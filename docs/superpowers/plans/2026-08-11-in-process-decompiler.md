# 实施计划：反编译路径从子进程 ilspycmd 改为进程内 ICSharpCode.Decompiler

## 背景与目标

`ilspymcp` 目前的反编译文本工具（decompile/decompile_member/decompile_to_dir/decompile_to_project）以子进程调用全局安装的 `ilspycmd`（dotnet tool）实现，前置要求用户手动安装、版本不可控、维护一套进程管理代码。本次重构将反编译改为**进程内调用 `ICSharpCode.Decompiler` 11.0.0.9335-rc**（与现有 ilspycmd 同源同版本，反编译行为一致），实现零前置安装、版本随包锁死、自包含。

纯元数据工具（list_types/signature/hierarchy/dependencies/call_graph）**完全不改**——它们走 PEReader 元数据层，与本次重构无关。

**重写/修改判断**：逐文件判断——若重写效率高于修改（改动面大、结构需重排、旧实现与新架构差异大）则删掉重写；改动小则直接修改。不预设任何文件必须重写或必须修改。

## 全局约束（所有任务必须遵守）

- **全程中文**：工具 `[Description]`、参数校验提示、错误提示、代码注释全部用简体中文。
- **工具方法返回 `Task<string>`**：一切错误（参数校验、反编译异常）返回中文提示文本，不抛异常。
- **MCP 工具参数带默认值**（如 `string assembly = ""`，不声明可空），`[Description]` 面向 agent、注明默认值。
- **stdout 只承载 MCP 协议消息**；日志必须走 stderr（`Program.cs` 已配置，勿改）。
- **每个工具方法保留 `CancellationToken cancellationToken = default` 参数**（元数据类放末尾，反编译类在 timeoutSeconds 之后）。
- 参数校验集中在共享 `ArgumentValidators` 静态类，返回 `bool` + `out string? error`。
- **缓存、行号标注、200 行默认截断、lines 分页、头部信息块、decompile_member 多匹配合并（`=== 名字 (token) ===` 分隔行）、>20 匹配仅签名清单、相近成员名提示、to_dir/to_project 写盘文件计数提示**——全部保留，行为不变。
- 工具代码/参数变更必须**同步更新 README.md 与 CHANGELOG.md（[Unreleased] 段），与代码改动同 commit**。
- 提交信息用中文。
- 每次修改后 `dotnet build -c Release src/ILSpyMcp/ILSpyMcp.csproj` 必须通过。

## 技术基线

- 库：`ICSharpCode.Decompiler` 11.0.0.9335-rc（本机 NuGet 缓存已就位）。
- 参考实现：`E:\Code\Projects\Externals\ILSpy\ICSharpCode.ILSpyCmd\IlspyCmdProgram.cs`（行为对齐依据）。
- 关键 API（已从源码确认）：
  - `new PEFile(assemblyFileName)`
  - `new UniversalAssemblyResolver(assemblyFileName, false, module.Metadata.DetectTargetFrameworkId())`
  - `new DecompilerSettings { ThrowOnAssemblyResolveErrors = false, UseNestedDirectoriesForNamespaces = ... }`
  - `new CSharpDecompiler(assemblyFileName, resolver, settings)`
  - `decompiler.DecompileWholeModuleAsString()`
  - `decompiler.DecompileAsString(EntityHandle)`（成员级；token → `MetadataTokens.EntityHandle(0x06xxxxxx)`，校验 row 数防越界）
  - `new WholeProjectDecompiler(settings, resolver, null, resolver, debugInfo)` + `DecompileProject(module, outputDirectory, projectFileWriter)`（项目文件 `{程序集名}.csproj`）
- 类型定位：复用 `MetadataNaming.FindType`（纯元数据，返回 `TypeDefinitionHandle`）→ `decompiler.DecompileAsString(handle)`。
- **超时/取消语义（已确认）**：进程内同步反编译无法强杀。同步反编译放 `Task.Run`，`Task.WhenAny(反编译, Task.Delay(timeoutSeconds, ct))`——超时/取消立即返回中文提示（建议增大 timeoutSeconds 或改用 decompile_to_dir），后台线程跑完即弃。`timeoutSeconds` 参数保留，默认 30。
- **to_dir 布局（已确认）**：非项目模式保持单文件 `{typeName 空 ? 程序集名 : typeName}.decompiled.cs`；`nestedDirectories` 参数保留但非项目模式不生效。
- **MaxOutputBytes 语义变化**：从「stdout 流式截断」改为「单次反编译生成文本字符数上限」——生成后检查，超限返回「建议改用 decompile_to_dir」且不入缓存（`AppConfig.MaxOutputBytes` 常量保留，取值不变 64MB，单位改字符数）。

## 任务分解

### Task 1: csproj 加包引用并验证编译

在 `src/ILSpyMcp/ILSpyMcp.csproj` 添加：
```xml
<PackageReference Include="ICSharpCode.Decompiler" Version="11.0.0.9335-rc" />
```
验证 `dotnet build -c Release src/ILSpyMcp/ILSpyMcp.csproj` 通过，且 `typeof(ICSharpCode.Decompiler.CSharp.CSharpDecompiler)` 可用（写一个临时验证或直接编译通过即视为 API 可用）。

**产出**：csproj 含包引用，build 通过。

### Task 2: 新增 Decompiler/InProcessDecompiler.cs

新建 `src/ILSpyMcp/Decompiler/InProcessDecompiler.cs`（命名空间 `ILSpyMcp.Decompiler`），进程内反编译服务，API：

```csharp
public sealed class InProcessDecompiler
{
    // 超时包装：同步 work 放 Task.Run；超时/取消返回超时提示（后台继续），正常返回 work 结果。
    // timeoutHint 为超时时返回的中文提示文本。
    public static async Task<string> RunWithTimeoutAsync(Func<string> work, TimeSpan timeout, CancellationToken cancellationToken, string timeoutHint);

    // 反编译指定类型到文本；未找到类型返回中文提示。
    public static string DecompileType(string assemblyPath, string typeName);

    // 反编译指定成员（token，如 "0x06000005"）到文本；token 非法/越界返回中文提示。
    public static string DecompileMember(string assemblyPath, string token);

    // 反编译整个程序集到文本。
    public static string DecompileWholeModule(string assemblyPath);

    // 写盘单文件布局：目录内 {typeName 空 ? Path.GetFileNameWithoutExtension(assembly) : typeName}.decompiled.cs；
    // 返回成功提示（含文件数）或错误提示。
    public static string DecompileToDir(string assemblyPath, string outputDir, string? typeName);

    // 项目模式写盘：{程序集名}.csproj + 每类型一个文件；nestedDirectories 生效；返回成功提示（含文件数）或错误提示。
    public static string DecompileToProject(string assemblyPath, string outputDir, bool nestedDirectories);
}
```

实现要点：
- 每次调用独立构建 `PEFile` + `UniversalAssemblyResolver` + `DecompilerSettings` + `CSharpDecompiler`，用完释放（`using`）。
- `DecompileType` 用 `MetadataNaming.FindType`（`ILSpyMcp.Metadata`，现成）定位 `TypeDefinitionHandle` → `DecompileAsString(handle)`；`typeName` 兼容 `+`/`.` 嵌套分隔与泛型 arity 格式。
- `DecompileMember` 的 token 用 `MetadataTokens.EntityHandle` 解析，按 ilspycmd `TryResolveMember` 的 token 分支校验 row 数。
- 全部入口 try/catch（`IOException`/`UnauthorizedAccessException`/`BadImageFormatException` + `Exception` 兜底），返回中文提示。
- 生成文本超 `AppConfig.MaxOutputBytes` 字符数时返回「反编译输出超过上限，建议改用 decompile_to_dir」。
- 新增 `tests/ILSpyMcp.Tests/InProcessDecompilerTests.cs`：用 `tests/TestData/ILSpyMcp.TestSamples.dll`（经 `TestDataPaths.TestSamplesDll` 定位）验证 DecompileType 命中/未找到、DecompileMember token、DecompileToDir 单文件布局与文件计数、DecompileToProject 写盘、超时语义（小 timeout 返回提示）。

**产出**：InProcessDecompiler.cs + 单测，`dotnet build` 通过、单测通过。

### Task 3: 重写 Pipeline 层（ToolCommand/ToolPipeline/ToolExecutor/AppServices）

删掉重写以下文件（重写优先原则）：

- `src/ILSpyMcp/Pipeline/ToolPipeline.cs`：`ToolCommand` 不再持有 `Executable`/`Args`/`ToolParameter`，改为**反编译请求描述**：保留 `Assembly`/`DisplayName`/`Signature`（缓存 key），新增 `DecompileRequest`（`Kind`: Type/Member/WholeModule + `Target`: typeName 或 token）。`ExecuteAsync`/`ExecuteMergedAsync` 回源路径从「子进程 stdout」改为「`InProcessDecompiler` + `RunWithTimeoutAsync` + `OutputFormatter.SplitLines`」；缓存命中/并发单飞/错误转提示逻辑保留。
- `src/ILSpyMcp/Services/ToolExecutor.cs`：删 `RunProcessAsync`；`RunPipelineAsync`/`RunMergedAsync` 签名不变（仍经 `AppServices.Pipeline`）。
- `src/ILSpyMcp/Services/AppServices.cs`：删 `Process`（IProcessRunner）/`Installer`（InstallChecker）字段；加 `Decompiler = new InProcessDecompiler()`；`Pipeline = new ToolPipeline(Cache)`；`ConfigureForTest`/`ResetForTest` 去掉 process 注入（缓存注入保留）。

**产出**：三层重写完成，build 通过。

### Task 4: 重写 4 个反编译类工具

删掉重写（或大改）以下文件，去掉 `ToolPreflight.CheckAsync` 调用（安装检测消失），改用进程内反编译：

- `src/ILSpyMcp/Tools/DecompileTool.cs`：构造 `DecompileRequest`（Kind=Type, typeName）→ `RunPipelineAsync`；`timeoutSeconds` 保留。
- `src/ILSpyMcp/Tools/DecompileMemberTool.cs`：`MemberResolver.FindMembers` 定位（保留）；匹配 ≤20 时构造多条 `DecompileRequest`（Kind=Member, token）→ `RunMergedAsync`；>20 仅签名清单逻辑（`RenderSignatureList`）保留；无匹配/相近名提示保留。
- `src/ILSpyMcp/Tools/DecompileToDirTool.cs`：调 `InProcessDecompiler.RunWithTimeoutAsync(() => InProcessDecompiler.DecompileToDir(...), ...)`；参数 `nestedDirectories`/`timeoutSeconds` 保留。
- `src/ILSpyMcp/Tools/DecompileToProjectTool.cs`：同上，调 `DecompileToProject`。

**产出**：4 个工具改造完成，build 通过。

### Task 5: 环境自检与配置（AppConfig/EnvironmentChecker/CheckTool/ILSpyMcpCmd）

- `src/ILSpyMcp/Configuration/AppConfig.cs`：删 `IlspyCmdExecutable`/`CheckTimeout`/`RequiredIlspyCmdVersion`；`MaxOutputBytes` 注释改为字符数上限语义。
- `src/ILSpyMcp/UpdateCheck/EnvironmentChecker.cs`：`BuildReportAsync` 去掉 installer 依赖（签名改为 `BuildReportAsync(Updater)`），报告改为两段：**内置反编译引擎版本**（`typeof(CSharpDecompiler).Assembly.GetName().Version`）+ **ilspymcp 包更新状态**（NuGet 段保留，`GetCachedNuGetLine` 逻辑不动）。
- `src/ILSpyMcp/Services/CheckTool.cs`：注释与 `CheckStatus` 不变（仍 `AppServices.StatusReport.Value`）。
- `src/ILSpyMcp/ILSpyMcpCmd.cs`：`-c/--check` 选项描述更新（去掉 ilspycmd 安装/版本检测措辞，改为内置引擎版本 + 更新检查）；其余分发不变。
- 同步更新 `tests/ILSpyMcp.Tests/CheckToolTests.cs` 与 `ILSpyMcpCmdTests.cs` 中断言（新报告内容）。

**产出**：配置与环境自检更新，build + 相关单测通过。

### Task 6: 删除子进程层并清理测试

- 删除 `src/ILSpyMcp/Processes/` 整个目录（`ProcessRunner.cs`/`IProcessRunner`/`ProcessResult`/`InstallChecker.cs`）。
- 删除 `src/ILSpyMcp/Validation/ToolPreflight.cs`。
- 删除测试：`ProcessRunnerTests.cs`/`InstallCheckerTests.cs`/`ToolPreflightTests.cs`/`FakeProcessRunner.cs`。
- 改造 `tests/ILSpyMcp.Tests/ToolPipelineTests.cs`：不再注入 fake `IProcessRunner`，改用 `AppServices.ConfigureForTest` 注入小缓存 + 真实 `InProcessDecompiler` 反编译 `TestDataPaths.TestSamplesDll` 验证管道（缓存命中/lines 分页/合并）。
- 若 `tests/ILSpyMcp.Tests/TestDataPaths.cs` 或其他测试引用已删类型，一并修正。
- 跑 `dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj` 全绿。

**产出**：子进程层清除，全部单元测试通过。

### Task 7: 文档同步（README/CHANGELOG）

- `README.md`：移除「需先安装 ilspycmd」的前置要求章节 → 改开箱即用；工具表/环境自检描述/CLI `-c` 说明同步更新。
- `CHANGELOG.md` `[Unreleased]`：记破坏性变更——不再依赖 ilspycmd 全局安装、反编译改进程内 ICSharpCode.Decompiler（版本）、`timeoutSeconds` 语义变为放弃等待。

**产出**：文档与代码一致。

### Task 8: 全量验证

- `dotnet build -c Release src/ILSpyMcp/ILSpyMcp.csproj` 通过。
- `dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj` 全部通过。
- 本机运行 Client 端到端：`dotnet run -c Release --project src/ILSpyMcp.Client/ILSpyMcp.Client.csproj`，确认 9 个工具全部通过且输出样式（头部块/行号/分页）与重构前一致。
- 检查无残留：`rg -n "ilspycmd|IProcessRunner|ProcessRunner|InstallChecker|ToolPreflight" src tests` 应仅剩文档中合理提及。

**产出**：全部验证通过，无残留引用。
