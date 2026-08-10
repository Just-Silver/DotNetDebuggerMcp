# AGENTS.md

基于 [ilspycmd](https://github.com/icsharpcode/ilspy) 的反编译 MCP 服务器：通过 stdio 暴露四个 MCP 工具，内部以子进程调用 `ilspycmd`（不内置反编译库），stdout 输出带行号。

## 关键约束

- **所有 MCP 工具参数必须带默认值（如 `string assembly = ""`，不声明为可空）**。SDK 依据参数是否有默认值判断必填：无默认值的参数缺参时会在绑定阶段直接抛异常，返回 Tool Error（`The arguments dictionary is missing a value for the required parameter ...`），agent 拿不到错误原因。带默认值后缺参进入方法体，由校验返回中文提示。校验集中在共享 `ArgumentValidators` 静态类（`ValidateAssembly`/`ValidateRequired`/`ValidateMemberNameSearch`/`ValidateList`/`ValidateOutputDir`/`ValidateLanguageVersion`/`ValidateTimeoutSeconds`），方法返回 `bool` + `out string? error`，失败时返回中文提示文本。
- 工具方法返回 `Task<string>`，一切错误（参数校验、ilspycmd 退出码非 0）均返回提示文本，不抛异常。
- stdout 只承载 MCP 协议消息；日志必须走 stderr（`Program.cs` 已配置，勿改）。
- 工具的 `[Description]` 与所有提示用中文，必填参数标注「（必填）」。
- **更新版本号必须同步改三处**：`src/ILSpyMcp/ILSpyMcp.csproj` 的 `<Version>`、`src/ILSpyMcp/.mcp/server.json`（顶层 `version` 与 `packages[0].version` 两处都要改一致）、`CHANGELOG.md`（新增 `## [<version>] - <date>` 段落记录变更）。`-v/--version` 输出版本取程序集版本（由 csproj 生成），但 NuGet MCP 注册信息读 server.json，两处不同步会导致发布后展示的版本不一致；`PackageReleaseNotes` 由 CI（`build.yml` 打 `v*` tag 时）从 CHANGELOG.md 提取当前版本段注入，CHANGELOG 缺段会导致发布失败（防静默无说明）。

## 结构

- `src/ILSpyMcp/` — MCP 服务器（net10.0、PackAsTool、框架依赖；运行期需 .NET 10 运行时）
  - `Tools/` — DecompileTool / DecompileMemberTool / ListTypesTool / DecompileToDirTool（`ILSpyMcp.Tools`）：`[McpServerToolType]` 静态类，只做参数校验与命令组装，经共享服务执行。每个工具都带 `timeoutSeconds` 参数（默认 30s，大程序集可调大，校验仅要求 ≥1 的正整数，无上限）。注意 `decompile_to_dir` 不经缓存管道，直接经 `AppServices.Process` 执行（`ToolPipelineResult` 无 Oversized 字段，仅 `Text`）；另三个工具走 `ToolPipeline`。`decompile` 仅类型级反编译（`typeName` 必填，成员级由 decompile_member 承接）；`decompile_member` 按成员名子串在指定类型内搜索并反编译（纯元数据读取定位，**只传 `-m <token>`**——ilspycmd 的 `-t` 与 `-m` 互斥，token 全局唯一；多匹配合并行号连续）
  - `Infrastructure/`（`ILSpyMcp.Infrastructure`）— 执行基础设施：
    - `AppServices.cs` — 进程级共享单例（进程执行器、缓存、执行管道、安装检测），避免各工具独立持有实例；测试经 `ConfigureForTest`/`ResetForTest` 注入 fake
    - `ProcessRunner.cs` — 通用子进程执行（args[0] 为可执行名，超时终止进程树，失败返回提示不抛异常）；stdout 流式读取并有 `AppConfig.MaxOutputBytes`（=64MB）上限，超过即终止并返回"建议改用 decompile_to_dir"提示，防 OOM
    - `ToolPipeline.cs` — 共享执行管道：缓存命中 → 回源反编译（同 key 并发单飞）→ lines 分页格式化；`ExecuteMergedAsync` 合并多条命令（decompile_member 多匹配）为一个大行列表后统一格式化
    - `DecompileCache.cs` — 线程安全 LRU 缓存（默认 64MB，结构化 CacheKey 含程序集指纹，dll 更新自动失效）
    - `MemberResolver.cs` — 纯元数据读取（PEReader+MetadataReader，不加载程序集）定位类型并枚举方法，按名字子串匹配返回 `[{名字, token}]`；token 格式 `0x06000005` 直用于 `ilspycmd -m`
    - `OutputFormatter.cs` — 行号标注与 `lines` 分页；`InstallChecker.cs` — 会话内缓存一次检测结果；`AppConfig.cs` — 全局配置常量
  - `Validation/`（`ILSpyMcp.Validation`）— `ArgumentValidators.cs` 共享参数校验；`ToolPreflight.cs` 安装检测 + assembly 校验的前置检查
- `src/ILSpyMcp.Client/` — 端到端验证客户端：场景拆分为 `DecompileCases` / `DecompileMemberCases` / `ListTypesCases` / `DecompileToDirCases`（各工具全参数覆盖）与 `ClientRunner`（连接/执行/输出）、`TestDataHelper`（自动发现测试 dll 并共享类型/成员标识），`Program.cs` 仅做入口
- `tests/ILSpyMcp.Tests/` — xUnit 单元测试（缓存/管道/格式化/校验/进程执行，fake 注入 `IProcessRunner`）
- `tests/TestData/` — 验证用程序集（生成的 `ILSpyMcp.TestSamples.dll`：601 个 class 触发 list_types 的 500 行上限截断，`BigClass`（含 BigMethod 600+ 行与 BigHelper/BigHelper2）触发 decompile 截断/分页与 decompile_member 多匹配；dll 与生成脚本 `generate-testdata.ps1` 入库，可重新生成；Client 经 `TestDataHelper` 自动发现目录下 dll 并对全部工具参数做端到端验证）

## 命令

```bash
dotnet build -c Release src/ILSpyMcp/ILSpyMcp.csproj
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj   # 单元测试
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj --filter "FullyQualifiedName~DecompileCache"   # 只跑单套测试
dotnet run -c Release --project src/ILSpyMcp.Client/ILSpyMcp.Client.csproj   # 调全部工具做端到端验证
```

- CLI 调试（改完 server 代码用 Debug 构建的 exe 快速验证，行为与 MCP 工具一致，是验证新行为的主要手段）：
  ```bash
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName>      # 反编译类型
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -mn <成员名子串>  # 按名搜成员
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -l c               # 列 class（c/i/s/d/e 可组合）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -o <dir>           # 写盘（-p 项目形式）
  ```
  其他选项：`-ln start-end` 按行分页、`-lv` 语言版本、`--timeout` 秒数
- 运行期依赖 `ilspycmd` 需全局安装（`dotnet tool install --global ilspycmd`），未安装时工具返回安装提示
- 修改逻辑后：build 通过 + 单元测试通过 + 运行 Client 确认输出样式
- 重新生成测试程序集：`powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`（注意 `BigMethod` 用数组链而非常量链——否则 Release 编译常量折叠会让方法只剩几行，无法触发 600 行截断）
- 本地调试注意：根 `opencode.json` 把本仓库自身的 MCP server 绑定到 `src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe`，改完 server 代码需重新 build 并重启 opencode 才生效；会话内 `ilspy_*` 工具反映旧二进制，验证新行为请以 Client 输出为准

## 输出约定

- 结果前置头部信息块（`程序集/目标` 两行 + 总量字段 + `当前输出` 字段 + `---` 分隔线，纯文本不带行号），由工具经 `FormatContext` 传入、`OutputFormatter` 生成，给 agent 明确的代码归属、总体规模与当前切片位置。**不展示参数行**——agent 面对的是 MCP 命名参数，ilspycmd 内部命令行参数（如 `-m token`、`-t`、`-l`）会误导 agent；`decompile_to_dir` 成功提示含「来源 <assembly>」
- 总量：反编译为 `总行数: N 行`；列类型同时给出 `匹配实体: N 个` 与 `总行数: N 行`（每行一个实体，行数=实体数）。`当前输出` 统一按行（如 `1-200（200 行，已截断）`），空结果为 `无`、越界为 `无效（起始行 X 超出总行数 Y）`
- `decompile_member` 头部目标描述为 `类型 X 的成员 <memberName>（N 个匹配）`；多成员匹配合并输出（行号连续、总行数基于合并结果），无匹配返回「类型 X 中未找到名称包含 Y 的成员」、类型不存在返回「未找到类型 X」
- 头部之下按行号标注（`行号\t内容`），切片时行号基于原始位置
- 默认返回前 200 行，`lines="start-end"` 按行号范围分页（单次最多 500 行）
- stdout 反编译结果超过 `AppConfig.MaxOutputBytes`（64MB）时 `ProcessRunner` 直接返回「超过上限，建议改用 decompile_to_dir」错误提示，不入缓存；只有 `decompile_to_dir` 能拿到完整结果。测试超限行为可临时调小该常量（记得还原）

## 验证注意

- `ProcessRunnerTests` 覆盖 ReadCappedAsync 超限/取消/边界；真实超限验证可用 xUnit 直连 `ToolPipeline` + `ProcessRunner` 反编译 `tests/TestData` 下 dll 的 `ILSpyMcp.Samples.BigClass`（600+ 行，调小上限即触发）
- 单测里经 `ToolPipeline` 的 assembly 路径解析基准是测试进程 CWD（`bin/Debug/net10.0`）；访问 `tests/TestData` 下 dll 用 `TestDataPaths.TestSamplesDll` 帮助类（`tests/ILSpyMcp.Tests/TestDataPaths.cs`，自动上溯仓库根 5 层 `..`），MemberResolver 单测则直接用 `typeof(OutputFormatter).Assembly.Location`（主项目程序集，无需 TestData）
