# AGENTS.md

基于 [ilspycmd](https://github.com/icsharpcode/ilspy) 的反编译 MCP 服务器：通过 stdio 暴露三个 MCP 工具，内部以子进程调用 `ilspycmd`（不内置反编译库），stdout 输出带行号。

## 关键约束

- **所有 MCP 工具参数必须带默认值（如 `string assembly = ""`，不声明为可空）**。SDK 依据参数是否有默认值判断必填：无默认值的参数缺参时会在绑定阶段直接抛异常，返回 Tool Error（`The arguments dictionary is missing a value for the required parameter ...`），agent 拿不到错误原因。带默认值后缺参进入方法体，由校验返回中文提示。校验集中在共享 `ArgumentValidators` 静态类（`ValidateAssembly`/`ValidateDecompileTarget`/`ValidateList`/`ValidateOutputDir`/`ValidateLanguageVersion`/`ValidateTimeoutSeconds`），方法返回 `bool` + `out string? error`，失败时返回中文提示文本。
- 工具方法返回 `Task<string>`，一切错误（参数校验、ilspycmd 退出码非 0）均返回提示文本，不抛异常。
- stdout 只承载 MCP 协议消息；日志必须走 stderr（`Program.cs` 已配置，勿改）。
- 工具的 `[Description]` 与所有提示用中文，必填参数标注「（必填）」。

## 结构

- `src/ILSpyMcp/` — MCP 服务器（net10.0、PackAsTool、框架依赖；运行期需 .NET 10 运行时）
  - `Tools/` — DecompileTool / ListTypesTool / DecompileToDirTool：`[McpServerToolType]` 静态类，只做参数校验与命令组装，经共享服务执行。每个工具都带 `timeoutSeconds` 参数（默认 30s，大程序集可调大，校验仅要求 ≥1 的正整数，无上限）。注意 `decompile_to_dir` 不经缓存管道，直接经 `AppServices.Process` 执行（`ToolPipelineResult` 无 Oversized 字段，仅 `Text`）；另两个工具走 `ToolPipeline`
  - `AppServices.cs` — 进程级共享单例（进程执行器、缓存、执行管道、安装检测），避免各工具独立持有实例；测试经 `ConfigureForTest`/`ResetForTest` 注入 fake
  - `ProcessRunner.cs` — 通用子进程执行（args[0] 为可执行名，超时终止进程树，失败返回提示不抛异常）；stdout 流式读取并有 `AppConfig.MaxOutputBytes`（=64MB）上限，超过即终止并返回"建议改用 decompile_to_dir"提示，防 OOM
  - `ToolPipeline.cs` — 共享执行管道：缓存命中 → 回源反编译（同 key 并发单飞）→ lines 分页格式化
  - `DecompileCache.cs` — 线程安全 LRU 缓存（默认 64MB，结构化 CacheKey 含程序集指纹，dll 更新自动失效）
  - `OutputFormatter.cs` — 行号标注与 `lines` 分页；`ArgumentValidators.cs` — 共享参数校验；`InstallChecker.cs` — 会话内缓存一次检测结果
- `src/ILSpyMcp.Client/` — 端到端验证客户端：场景拆分为 `DecompileCases` / `ListTypesCases` / `DecompileToDirCases`（各工具全参数覆盖）与 `ClientRunner`（连接/执行/输出），`Program.cs` 仅做入口
- `tests/ILSpyMcp.Tests/` — xUnit 单元测试（缓存/管道/格式化/校验/进程执行，fake 注入 `IProcessRunner`）
- `tests/TestData/System.Linq.dll` — 验证用程序集（.NET 的 ref 版 System.Linq.dll，入库跟踪；Client 用它对全部工具参数做端到端验证）

## 命令

```bash
dotnet build -c Release src/ILSpyMcp/ILSpyMcp.csproj
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj   # 单元测试
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj --filter "FullyQualifiedName~DecompileCache"   # 只跑单套测试
dotnet run -c Release --project src/ILSpyMcp.Client/ILSpyMcp.Client.csproj   # 调全部工具做端到端验证
```

- 运行期依赖 `ilspycmd` 需全局安装（`dotnet tool install --global ilspycmd`），未安装时工具返回安装提示
- 修改逻辑后：build 通过 + 单元测试通过 + 运行 Client 确认输出样式
- 本地调试注意：根 `opencode.json` 把本仓库自身的 MCP server 绑定到 `src/ILSpyMcp/bin/Debug/net10.0/win-x64/ilspymcp`，改完 server 代码需重新 build 并重启 opencode 才生效；会话内 `ilspy_*` 工具反映旧二进制，验证新行为请以 Client 输出为准

## 输出约定

- 结果按 codegraph 风格标注行号（`行号\t内容`），切片时行号基于原始位置
- 默认返回前 200 行，`lines="start-end"` 按行号范围分页（单次最多 500 行）
- stdout 反编译结果超过 `AppConfig.MaxOutputBytes`（64MB）时 `ProcessRunner` 直接返回「超过上限，建议改用 decompile_to_dir」错误提示，不入缓存；只有 `decompile_to_dir` 能拿到完整结果。测试超限行为可临时调小该常量（记得还原）

## 验证注意

- `ProcessRunnerTests` 覆盖 ReadCappedAsync 超限/取消/边界；真实超限验证可用 xUnit 直连 `ToolPipeline` + `ProcessRunner` 反编译 `tests/TestData/System.Linq.dll` 的 `System.Linq.Enumerable`（输出 ~20KB，调小上限即触发）
- 单测里经 `ToolPipeline` 的 assembly 路径解析基准是测试进程 CWD（`bin/Debug/net10.0`），相对路径需从 `AppContext.BaseDirectory` 上溯仓库根再拼 `tests\TestData\System.Linq.dll`（5 层 `..`）
