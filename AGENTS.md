# AGENTS.md

基于 [ilspycmd](https://github.com/icsharpcode/ilspy) 的反编译 MCP 服务器：通过 stdio 暴露五个 MCP 工具，内部以子进程调用 `ilspycmd`（不内置反编译库），stdout 输出带行号。

## 关键约束

- **所有 MCP 工具参数必须带默认值（如 `string assembly = ""`，不声明为可空）**。SDK 依据参数是否有默认值判断必填：无默认值的参数缺参时会在绑定阶段直接抛异常，返回 Tool Error（`The arguments dictionary is missing a value for the required parameter ...`），agent 拿不到错误原因。带默认值后缺参进入方法体，由校验返回中文提示。校验集中在共享 `ArgumentValidators` 静态类（`ValidateAssembly`/`ValidateRequired`/`ValidateMemberNameSearch`/`ValidateList`/`ValidateOutputDir`/`ValidateLanguageVersion`/`ValidateTimeoutSeconds`），方法返回 `bool` + `out string? error`，失败时返回中文提示文本。
- 工具方法返回 `Task<string>`，一切错误（参数校验、ilspycmd 退出码非 0）均返回提示文本，不抛异常。
- stdout 只承载 MCP 协议消息；日志必须走 stderr（`Program.cs` 已配置，勿改）。
- 工具的 `[Description]` 与所有提示用中文，必填参数标注「（必填）」。
- **每个反编译工具方法带 `CancellationToken cancellationToken = default` 参数**（放在 `timeoutSeconds` 之后）：SDK 识别为取消令牌并注入、**不暴露为 MCP 参数**（不要写 `[Description]`），客户端取消调用时沿 Pipeline/ProcessRunner 终止 ilspycmd 子进程。勿删。
- **更新版本号必须同步改两处**：`src/ILSpyMcp/ILSpyMcp.csproj` 的 `<Version>` 与 `src/ILSpyMcp/.mcp/server.json`（顶层 `version` 与 `packages[0].version` 两处都要改一致）。`-v/--version` 输出版本取程序集版本（由 csproj 生成），但 NuGet MCP 注册信息读 server.json，不同步会导致发布后展示版本不一致。**CHANGELOG 变更统一记在 `[Unreleased]` 段**（当前 1.1.0 从未发布）；发布打 `v*` tag 时 CI 从 CHANGELOG.md 提取 `## [<version>]` 段落注入 `PackageReleaseNotes`，故发布前须把 Unreleased 内容转成 `## [<version>] - <date>` 段，缺段会导致发布失败（防静默无说明）。

## 结构

- `src/ILSpyMcp/` — MCP 服务器（net10.0、PackAsTool、框架依赖；运行期需 .NET 10 运行时）
  - `Tools/` — DecompileTool / DecompileMemberTool / ListTypesTool / DecompileToDirTool / CheckTool（`ILSpyMcp.Tools`）：`[McpServerToolType]` 静态类，只做参数校验与命令组装，经共享服务执行。除 `check_status` 外每个工具都带 `timeoutSeconds` 参数（默认 30s，大程序集可调大，校验仅要求 ≥1 的正整数，无上限）。**执行样板统一走 `ToolExecutor`**（`ResolveAssembly`/`RunPipelineAsync`/`RunMergedAsync`/`RunProcessAsync`），新工具复用勿复制；注意 `decompile_to_dir` 不经缓存管道（`ToolExecutor.RunProcessAsync` 直接调子进程，`ToolPipelineResult` 无 Oversized 字段，仅 `Text`），其余走 `ToolPipeline`。`decompile` 仅类型级反编译（`typeName` 必填，成员级由 decompile_member 承接）；`decompile_member` 按成员名子串在指定类型内搜索并反编译（纯元数据读取定位，**只传 `-m <token>`**——ilspycmd 的 `-t` 与 `-m` 互斥，token 全局唯一；多匹配合并行号连续）。`check_status` 环境自检（无参数）：检查 ilspycmd 安装与版本（>= `AppConfig.RequiredIlspyCmdVersion`=11，`-m` 单成员反编译所需）及 ilspymcp 是否有新版；**结果会话内缓存**（环境变化需重启 CLI 才生效，重复检查无意义），NuGet 网络失败/超时静默跳过该检查项
  - `Infrastructure/`（`ILSpyMcp.Infrastructure`）— 执行基础设施：
    - `AppServices.cs` — 进程级共享单例（进程执行器、缓存、执行管道、安装检测、NuGet 查询、check_status 报告缓存 `StatusReport`），避免各工具独立持有实例；测试经 `ConfigureForTest`/`ResetForTest` 注入 fake。**本层不得反向引用 `ILSpyMcp.Tools`**（交叉依赖已消除，check_status 报告组装在下述 `EnvironmentChecker.cs`）
    - `ProcessRunner.cs` — 通用子进程执行（args[0] 为可执行名，超时终止进程树，失败返回提示不抛异常）；stdout 流式读取并有 `AppConfig.MaxOutputBytes`（=64MB）上限，超过即终止并返回"建议改用 decompile_to_dir"提示，防 OOM
    - `ToolPipeline.cs` — 共享执行管道：缓存命中 → 回源反编译（同 key 并发单飞）→ lines 分页格式化；`ExecuteMergedAsync` 合并多条命令（decompile_member 多匹配）为一个大行列表后统一格式化。**`ToolCommand` 持有 `Assembly` 属性（程序集唯一数据源），`ExecuteAsync(ToolCommand command, ...)` 不再单独传 assembly——勿再造双份程序集参数**
    - `DecompileCache.cs` — 线程安全 LRU 缓存（默认 64MB，结构化 CacheKey 含程序集指纹，dll 更新自动失效）
    - `MemberResolver.cs` — 纯元数据读取（PEReader+MetadataReader，不加载程序集）定位类型并枚举方法，按名字子串匹配返回 `[{名字, token}]`；token 格式 `0x06000005` 直用于 `ilspycmd -m`
    - `OutputFormatter.cs` — 行号标注与 `lines` 分页；`InstallChecker.cs` — 会话内缓存一次检测，安装状态与版本号同源一次填充（从 `ilspycmd -v` 解析）；`NuGetClient.cs` — NuGet 最新稳定版查询（排除预发布，网络失败返回 null 供 check_status 静默跳过）；`EnvironmentChecker.cs` — check_status 报告组装；`ToolExecutor.cs` — 工具执行共享辅助（路径安全解析 + 管道/子进程调用样板）；`AppConfig.cs` — 全局配置常量（含 `RequiredIlspyCmdVersion`=11）
  - `Validation/`（`ILSpyMcp.Validation`）— `ArgumentValidators.cs` 共享参数校验；`ToolPreflight.cs` 安装检测 + assembly 校验的前置检查
- `src/ILSpyMcp.Client/` — 端到端验证客户端：场景拆分为 `DecompileCases` / `DecompileMemberCases` / `ListTypesCases` / `DecompileToDirCases`（各工具全参数覆盖）与 `ClientRunner`（连接/执行/输出）、`TestDataHelper`（自动发现测试 dll 并共享类型/成员标识），`Program.cs` 仅做入口
- `tests/ILSpyMcp.Tests/` — xUnit 单元测试（缓存/管道/格式化/校验/进程执行，fake 注入 `IProcessRunner`）
- `tests/TestData/` — 验证用程序集（生成的 `ILSpyMcp.TestSamples.dll`：601 个 class = Class0001-0600 + BigClass，list_types 输出还含编译器生成的 `<Module>`，共 602 行，同时触发 200 行默认截断与 500 行分页上限；`BigClass` 含 BigMethod 600+ 行与 BigHelper/BigHelper2，触发 decompile 截断/分页与 decompile_member 多匹配；dll 与生成脚本 `generate-testdata.ps1` 入库，可重新生成；Client 经 `TestDataHelper` 自动发现目录下 dll 并对全部工具参数做端到端验证）

## 命令

```bash
dotnet build -c Release src/ILSpyMcp/ILSpyMcp.csproj
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj   # 单元测试
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj --filter "FullyQualifiedName~DecompileCache"   # 只跑单套测试
dotnet run -c Release --project src/ILSpyMcp.Client/ILSpyMcp.Client.csproj   # 调全部工具做端到端验证
```

- Client 端到端会以 Release 自启动 server 项目（`dotnet run --project src/ILSpyMcp/ILSpyMcp.csproj -c Release`，无需预先单独构建 server），运行后自动清理写盘产物 `tests/.ilspymcp-client-out/`（已在 .gitignore）；运行期需 ilspycmd 在 PATH（CI 显式把 `%USERPROFILE%\.dotnet\tools` 前置到 PATH）
- CLI 调试（改完 server 代码用 Debug 构建的 exe 快速验证，行为与 MCP 工具一致，是验证新行为的主要手段）：
  ```bash
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName>      # 反编译类型
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -mn <成员名子串>  # 按名搜成员
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -l c               # 列 class（c/i/s/d/e 可组合）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -o <dir>           # 写盘（-p 项目形式）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -c                         # 环境自检（check_status，无需 -a）
  ```
  其他选项：`-ln start-end` 按行分页、`-lv` 语言版本、`--timeout` 秒数
- 运行期依赖 `ilspycmd` 需全局安装（`dotnet tool install --global ilspycmd`），未安装时工具返回安装提示
- 修改逻辑后：build 通过 + 单元测试通过 + 运行 Client 确认输出样式（CI 的 build.yml 已含端到端步骤：master push 时自动 Install ilspycmd + 运行 Client，不再只靠手工）
- 重新生成测试程序集：`powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`（注意 `BigMethod` 用数组链而非常量链——否则 Release 编译常量折叠会让方法只剩几行，无法触发 600 行截断）
- 本地调试注意：根 `opencode.json` 把本仓库自身的 MCP server 绑定到 `src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe`，改完 server 代码需重新 build 并重启 opencode 才生效；会话内 `ilspy_*` 工具反映旧二进制，验证新行为请以 Client 输出为准

## 输出约定

- 结果前置头部信息块（`程序集/目标` 两行 + 总量字段 + `当前输出` 字段 + `---` 分隔线，纯文本不带行号），由工具经 `FormatContext` 传入、`OutputFormatter` 生成，给 agent 明确的代码归属、总体规模与当前切片位置。**不展示参数行**——agent 面对的是 MCP 命名参数，ilspycmd 内部命令行参数（如 `-m token`、`-t`、`-l`）会误导 agent；`decompile_to_dir` 成功提示含「来源 <assembly>」；`check_status` 无头部信息块（不涉及程序集），直接返回状态报告
- 总量：反编译为 `总行数: N 行`；列类型同时给出 `匹配实体: N 个` 与 `总行数: N 行`（每行一个实体，行数=实体数）。`当前输出` 统一按行（如 `1-200（200 行，已截断）`），空结果为 `无`、越界为 `无效（起始行 X 超出总行数 Y）`
- `decompile_member` 头部目标描述为 `类型 X 的成员 <memberName>（N 个匹配）`；多成员匹配合并输出（行号连续、总行数基于合并结果），无匹配返回「类型 X 中未找到名称包含 Y 的成员」、类型不存在返回「未找到类型 X」
- 头部之下按行号标注（`行号\t内容`），切片时行号基于原始位置
- 默认返回前 200 行，`lines="start-end"` 按行号范围分页（单次最多 500 行）
- stdout 反编译结果超过 `AppConfig.MaxOutputBytes`（64MB）时 `ProcessRunner` 直接返回「超过上限，建议改用 decompile_to_dir」错误提示，不入缓存；只有 `decompile_to_dir` 能拿到完整结果。测试超限行为可临时调小该常量（记得还原）

## 验证注意

- `ProcessRunnerTests` 覆盖 ReadCappedAsync 超限/取消/边界；真实超限验证可用 xUnit 直连 `ToolPipeline` + `ProcessRunner` 反编译 `tests/TestData` 下 dll 的 `ILSpyMcp.Samples.BigClass`（600+ 行，调小上限即触发）
- 单测里经 `ToolPipeline` 的 assembly 路径解析基准是测试进程 CWD（`bin/Debug/net10.0`）；访问 `tests/TestData` 下 dll 用 `TestDataPaths.TestSamplesDll` 帮助类（`tests/ILSpyMcp.Tests/TestDataPaths.cs`，逐级上溯找 `ILSpyMcp.slnx`），MemberResolver 单测则直接用 `typeof(OutputFormatter).Assembly.Location`（主项目程序集，无需 TestData）
