# AGENTS.md

基于 [ilspycmd](https://github.com/icsharpcode/ilspy) 的反编译 MCP 服务器：通过 stdio 暴露八个 MCP 工具，内部以子进程调用 `ilspycmd`（不内置反编译库）做方法体反编译与写盘，结构类查询（列类型/签名/层级/引用）走 PEReader 元数据层，stdout 输出带行号。

## 关键约束

- **所有 MCP 工具参数必须带默认值（如 `string assembly = ""`，不声明为可空）**。SDK 依据参数是否有默认值判断必填：无默认值的参数缺参时会在绑定阶段直接抛异常，返回 Tool Error（`The arguments dictionary is missing a value for the required parameter ...`），agent 拿不到错误原因。带默认值后缺参进入方法体，由校验返回中文提示。校验集中在共享 `ArgumentValidators` 静态类（`ValidateAssembly`/`ValidateRequired`/`ValidateMemberNameSearch`/`ValidateList`/`ValidateOutputDir`/`ValidateTimeoutSeconds`），方法返回 `bool` + `out string? error`，失败时返回中文提示文本。
- 工具方法返回 `Task<string>`，一切错误（参数校验、ilspycmd 退出码非 0）均返回提示文本，不抛异常。
- stdout 只承载 MCP 协议消息；日志必须走 stderr（`Program.cs` 已配置，勿改）。
- 工具的 `[Description]` 与所有提示用中文，必填参数标注「（必填）」。
- **`[Description]` 面向 agent（MCP 调用方），不是给人类开发者看的**：只写 agent 决策需要的行为、默认值、必填、示例与限制；不得出现实现细节或设计动机类措辞（如「agent 友好」「供 agent 决策」等），这类说明写代码注释或本文件。
- **有默认值的工具参数，`[Description]` 必须注明默认值**（如「默认 30」「默认 true」「缺省返回前 200 行」「省略使用 ilspycmd 默认」），否则 agent 无从感知当前默认行为。
- **每个工具方法带 `CancellationToken cancellationToken = default` 参数**（反编译类放在 `timeoutSeconds` 之后，元数据类放在末尾）：SDK 识别为取消令牌并注入、**不暴露为 MCP 参数**（不要写 `[Description]`），客户端取消调用时沿 Pipeline/ProcessRunner 终止 ilspycmd 子进程。勿删。
- **更新版本号必须同步改三处**：`src/ILSpyMcp/ILSpyMcp.csproj` 的 `<Version>`、`src/ILSpyMcp/.mcp/server.json`（顶层 `version` 与 `packages[0].version` 两处都要改一致）、`CHANGELOG.md`（发布前把 `[Unreleased]` 内容转成 `## [<version>] - <date>` 段）。`-v/--version` 输出版本取程序集版本（由 csproj 生成），但 NuGet MCP 注册信息读 server.json，不同步会导致发布后展示版本不一致。**CHANGELOG 变更统一记在 `[Unreleased]` 段**；发布打 `v*` tag 时 CI 从 CHANGELOG.md 提取 `## [<version>]` 段落作为 GitHub Release 正文，NuGet 包的 `PackageReleaseNotes` 只注入指向该 Release 页的链接，缺段会导致发布失败（防静默无说明）。
- **CHANGELOG 面向包使用者（agent 与 CLI 用户）**，记录使用者可见的变更（新功能、行为变化、破坏性变更、可感知的修复、默认值/参数描述变化）以及本次发布的核心内容；发布后它是 GitHub Release 的正文，无需再写开发流水账（git 提交记录已足够）。某版本只有内部变更时写一行「内部重构与细节调整，无用户可见变化」占位，保住 CI「缺段即失败」的防静默机制。

## 结构

- `src/ILSpyMcp/` — MCP 服务器（net10.0、PackAsTool、框架依赖；运行期需 .NET 10 运行时），源码按功能划分命名空间（每目录一个）
  - `Tools/`（`ILSpyMcp.Tools`）— 8 个 `[McpServerToolType]` 静态类，分两类：**反编译类**（DecompileTool / DecompileMemberTool / DecompileToDirTool / DecompileToProjectTool，走 `ToolPreflight.CheckAsync` 安装检测 + 子进程，带 `timeoutSeconds` 默认 30s）与**纯元数据类**（ListTypesTool / SignatureTool / HierarchyTool / DependenciesTool，秒回、**免安装检测**、无 timeoutSeconds）。**执行样板统一走 `ToolExecutor`**（`ResolveAssembly`/`RunPipelineAsync`/`RunMergedAsync`/`RunProcessAsync`）；写盘类（to_dir/to_project）不经缓存管道（`RunProcessAsync` 直接调子进程）。`decompile` 仅类型级反编译（`typeName` 必填，成员级由 decompile_member 承接）；`decompile_member` 按成员名子串搜索（纯元数据定位，**只传 `-m <token>`**——`-t` 与 `-m` 互斥，token 全局唯一；默认排除访问器；多匹配合并输出且各成员前插 `=== 名字 (token) ===` 分隔行；匹配数 > `AppConfig.MaxMemberMatches`（20）仅返回签名清单不反编译；无匹配返回相近成员名提示）；`to_dir` 的 `typeName` 非空时仅反编译该类型，省略全量；`to_project` 恒项目形式；`signature`/`hierarchy`/`dependencies` 输出经元数据组件读取
  - `Services/`（`ILSpyMcp.Services`）— `AppServices.cs` 进程级共享单例（进程执行器、缓存、执行管道、安装检测、NuGet 查询、环境自检报告缓存 `StatusReport`），避免各工具独立持有实例；测试经 `ConfigureForTest`/`ResetForTest` 注入 fake。**本层不得反向引用 `ILSpyMcp.Tools`**（交叉依赖已消除）。`ToolExecutor.cs` 工具执行共享辅助（路径安全解析 + 管道/子进程调用样板，移入本层以消除 Pipeline→Services 循环）。`CheckTool.cs` 环境自检入口（**非 MCP 工具**——握手期已把完整报告注入 ServerInstructions，仅供 CLI `-c/--check` 调试）：检查 ilspycmd 安装与版本（>= `AppConfig.RequiredIlspyCmdVersion`=11，`-m` 单成员反编译所需）及 ilspymcp 是否有新版；**结果会话内缓存**（环境变化需重启 CLI 才生效，重复检查无意义），NuGet 段同步读磁盘缓存、无有效检查记录时留白
  - `Pipeline/`（`ILSpyMcp.Pipeline`）— `ToolPipeline.cs` 共享执行管道：缓存命中 → 回源反编译（同 key 并发单飞）→ lines 分页格式化；`ExecuteMergedAsync` 合并多条命令（decompile_member 多匹配）为一个大行列表后统一格式化。**`ToolCommand` 持有 `Assembly` 属性（程序集唯一数据源），`ExecuteAsync(ToolCommand command, ...)` 不再单独传 assembly——勿再造双份程序集参数**
  - `Processes/`（`ILSpyMcp.Processes`）— `ProcessRunner.cs` 通用子进程执行（args[0] 为可执行名，超时终止进程树，失败返回提示不抛异常；stdout 流式读取并有 `AppConfig.MaxOutputBytes`=64MB 上限，超过即终止并返回"建议改用 decompile_to_dir"提示，防 OOM）+ `IProcessRunner`/`ProcessResult`；`InstallChecker.cs` 会话内缓存一次检测，安装状态与版本号同源一次填充（从 `ilspycmd -v` 解析，可执行名取 `AppConfig.IlspyCmdExecutable`，不引用 Pipeline 层）
  - `Caching/`（`ILSpyMcp.Caching`）— `DecompileCache.cs` 线程安全 LRU 缓存（默认 64MB，结构化 CacheKey 含程序集指纹，dll 更新自动失效）
  - `Formatting/`（`ILSpyMcp.Formatting`）— `OutputFormatter.cs` 行号标注与 `lines` 分页
  - `Metadata/`（`ILSpyMcp.Metadata`）— 纯元数据读取组件（PEReader+MetadataReader，不加载程序集、不反编译 IL）：`MetadataNaming.cs`（类型全名渲染/定位，格式与 ilspycmd `-l` 对齐：命名空间.类型、嵌套用 `+`、泛型带 arity 如 `GenericBox\`1`；定位时 `+`/`.` 分隔均接受）、`CompilerGeneratedFilter.cs`（**全名**（含嵌套外层链）含 `<` 即编译器生成类型——嵌套的 `<PrivateImplementationDetails>+__StaticArrayInitTypeSize=NN` 短名不含 `<` 也命中；**刻意不用 `__` 前缀/特性兜底**——`__ComObject` 是合法类型、顶层语句 `Program` 带 CompilerGeneratedAttribute 但非编译器产物）、`SignatureRenderer.cs`（成员签名渲染，含 `RenderMemberSignature` 单成员；隐式接口实现不渲染 sealed、静态属性带 static、索引器渲染 `this[参数]`、泛型构造函数名去 arity、显式接口属性/事件访问器与隐式访问器一并排除）、`TypeLister.cs`（按类别枚举+过滤）、`Hierarchy.cs`（基类链/接口/后代；支持泛型基类/接口实例化——TypeSpecification 解码，泛型定义在程序集内时基类链继续上溯）、`ReferenceExtractor.cs`（成员签名内部类型引用）、`MemberResolver.cs`（成员名子串搜索，返回 `MemberSearchResult` 含相近名；token `0x06000005` 直用于 `-m`；显式接口访问器一并排除）
  - `UpdateCheck/`（`ILSpyMcp.UpdateCheck`）— `NuGetClient.cs` NuGet 最新稳定版查询（排除预发布，网络失败返回 null 供环境自检静默跳过）；`UpdateChecker.cs` NuGet 新版本检查的磁盘缓存与报告段组装（成功 TTL 24h、失败 1h 退避、失败保留旧值，落盘 `%LOCALAPPDATA%\ilspymcp\update-check.json` 跨进程共享；**查询经构造注入的委托 `queryLatest`**，生产由 AppServices 传共享 NuGetClient、测试传 fake，不反引用 Services 层）；`EnvironmentChecker.cs` 环境自检报告组装（依赖经参数传入 installer/updater，不反引用 Services 层）。NuGet 段经 `GetCachedNuGetLine` 同步读缓存（零网络，无有效检查记录时留白），**网络刷新由握手后台 `RefreshIfStaleAsync` 承担**（TTL/退避内不联网，失败静默降级；CLI `-c` 是主动调试入口，调用前先 await 刷新）；**版本比较共用 `IsNewerThanCurrent` 静态方法**（环境自检报告与握手注入两处调同一规则，防漂移），当前版本统一取 `AppConfig.CurrentVersion`
  - `Configuration/`（`ILSpyMcp.Configuration`）— `AppConfig.cs` 全局配置常量（含 `RequiredIlspyCmdVersion`=11）
  - `Validation/`（`ILSpyMcp.Validation`）— `ArgumentValidators.cs` 共享参数校验；`ToolPreflight.cs` 安装检测 + assembly 校验的前置检查（**仅反编译类工具用**；纯元数据工具直接 `ValidateAssembly`，免安装检测）
  - MCP 握手期**先执行完整环境自检**（`await AppServices.StatusReport.Value`，报告与 CLI `-c` 同源会话内缓存），经 `McpServerOptions.ServerInstructions` 注入完整环境自检报告（agent 会话起始即可见 ilspycmd 安装/版本 + NuGet 更新状态；NuGet 段无结果留白，由后台刷新供下次会话；`StatusReport.Value` 包 try/catch 降级为空注入，环境自检异常不阻断 MCP 启动）
- `src/ILSpyMcp.Client/` — 端到端验证客户端：场景拆分为 `DecompileCases` / `DecompileMemberCases` / `ListTypesCases` / `DecompileToDirCases` / `SignatureCases` / `HierarchyCases` / `DependenciesCases`（各工具全参数覆盖）与 `ClientRunner`（连接/执行/输出）、`TestDataHelper`（自动发现测试 dll 并共享类型/成员标识），`Program.cs` 仅做入口
- `tests/ILSpyMcp.Tests/` — xUnit 单元测试（缓存/管道/格式化/校验/进程执行，fake 注入 `IProcessRunner`）
- `tests/TestData/` — 验证用程序集（生成的 `ILSpyMcp.TestSamples.dll`：640 class + 5 interface + 3 struct + 1 delegate + 3 enum，list_types 默认过滤 `<Module>`/`<PrivateImplementationDetails>+__StaticArrayInitTypeSize=256` 等编译器生成类型；`Class0001-0600` 触发 200 行默认截断与 500 行分页上限；`BigClass` 含 BigMethod 600+ 行与 BigHelper/BigHelper2，触发 decompile 截断/分页与 decompile_member 多匹配；`BaseClass/DerivedClass/DerivedClass2` 与 `IAnimal/Dog` 供 hierarchy，`GenericBox\`1`/`IntComparer` 供泛型基类/接口/签名，`Props` 供静态属性/索引器，`ThingImpl` 供显式接口访问器排除，`ManyOverloads`（21 个 Do 重载）触发 decompile_member 超限仅签名，`AbstractShape/Circle/SealedCircle` 与 `Level1-4` 供 signature 修饰符与多层基类链，`Uses/UsesShared1-3/Shared` 供 dependencies 双向，`WithClosure/WithAsync/StaticArrayHolder` 触发编译器生成类型；dll 与生成脚本 `generate-testdata.ps1` 入库，可重新生成；Client 经 `TestDataHelper` 自动发现目录下 dll 并对全部工具做端到端验证）

## 命令

```bash
dotnet build -c Release src/ILSpyMcp/ILSpyMcp.csproj
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj   # 单元测试
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj --filter "FullyQualifiedName~DecompileCache"   # 只跑单套测试
dotnet run -c Release --project src/ILSpyMcp.Client/ILSpyMcp.Client.csproj   # 调全部工具做端到端验证
```

- Client 端到端会以 Release 自启动 server 项目（`dotnet run --project src/ILSpyMcp/ILSpyMcp.csproj -c Release`，无需预先单独构建 server），运行后自动清理写盘产物 `tests/.ilspymcp-client-out/`（已在 .gitignore）；本机运行需 ilspycmd 在 PATH（`%USERPROFILE%\.dotnet\tools`）
- CLI 调试（改完 server 代码用 Debug 构建的 exe 快速验证，行为与 MCP 工具一致，是验证新行为的主要手段）：
  ```bash
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName>      # 反编译类型
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -mn <成员名子串>  # 按名搜成员（多匹配含 === 分隔行，>20 仅返回签名清单）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -s   # 成员签名（API 地图，纯元数据）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -hc  # 继承/接口关系（纯元数据）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -d   # 成员签名内部引用（纯元数据）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -l c               # 列 class（c/i/s/d/e 可组合，过滤编译器生成类型）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -o <dir>           # 全量写盘；-p 组合为项目形式（to_project）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -c                         # 环境自检（无需 -a）
  ```
  其他选项：`-ln start-end` 按行分页、`--timeout` 秒数
- 运行期依赖 `ilspycmd` 需全局安装（`dotnet tool install --global ilspycmd`），未安装时反编译/写盘类工具返回安装提示（纯元数据工具 list_types/signature/hierarchy/dependencies 不依赖 ilspycmd）
- 修改逻辑后：build 通过 + 单元测试通过 + 本机运行 Client 确认输出样式（CI 的 build.yml 只做 build/test/发布，不跑端到端；端到端验证改为本机手动执行）
- 重新生成测试程序集：`powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`（注意 `BigMethod` 用数组链而非常量链——否则 Release 编译常量折叠会让方法只剩几行，无法触发 600 行截断）
- 本地调试注意：根 `opencode.json` 把本仓库自身的 MCP server 绑定到 `src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe`，改完 server 代码需重新 build 并重启 opencode 才生效；会话内 `ilspy_*` 工具反映旧二进制，验证新行为请以 Client 输出为准

## 输出约定

- 结果前置头部信息块（`程序集/目标` 两行 + 总量字段 + `当前输出` 字段 + `---` 分隔线，纯文本不带行号），由工具经 `FormatContext` 传入、`OutputFormatter` 生成，给 agent 明确的代码归属、总体规模与当前切片位置。**不展示参数行**——agent 面对的是 MCP 命名参数，ilspycmd 内部命令行参数（如 `-m token`、`-t`、`-l`）会误导 agent；`decompile_to_dir`/`decompile_to_project` 成功提示含「来源 <assembly>」；环境自检（CLI `-c/--check` 输出）无头部信息块（不涉及程序集），直接返回状态报告
- 总量：反编译为 `总行数: N 行`；列类型同时给出 `匹配实体: N 个` 与 `总行数: N 行`（每行一个实体，行数=实体数）。`当前输出` 统一按行（如 `1-200（200 行，已截断）`），空结果为 `无`、越界为 `无效（起始行 X 超出总行数 Y）`
- `decompile_member` 头部目标描述为 `类型 X 的成员 <memberName>（N 个匹配）`；多成员匹配合并输出（行号连续、总行数基于合并结果，各成员前有 `=== 名字 (token) ===` 分隔行、计入行号），匹配数 > `AppConfig.MaxMemberMatches`（20）时头部注明「超过上限，仅列出签名」并只返回签名清单（每行 `签名  [token]`）；无匹配返回「类型 X 中未找到名称包含 Y 的成员」、存在相近名时追加「相近成员：A、B、C」；类型不存在返回「未找到类型 X」
- 纯元数据工具（list_types/signature/hierarchy/dependencies）头部同样带信息块（IsListing：`匹配实体: N 个 + 总行数: N 行`，每行一个实体）。反编译输出含 `//IL_` 未解析注释时，头部追加「提示: 输出含 //IL_ 未解析注释（动态类型/异常路径），仅供结构参考」
- `hierarchy` 输出三段（基类链/接口/程序集内继承实现者），`dependencies` 输出两段（引用的内部类型/引用它的类型，空段输出（无）占位），段标题与实体均作为行标注行号
- 头部之下按行号标注（`行号\t内容`），切片时行号基于原始位置
- 默认返回前 200 行，`lines="start-end"` 按行号范围分页（单次最多 500 行）
- stdout 反编译结果超过 `AppConfig.MaxOutputBytes`（64MB）时 `ProcessRunner` 直接返回「超过上限，建议改用 decompile_to_dir」错误提示，不入缓存；只有 `decompile_to_dir` 能拿到完整结果。测试超限行为可临时调小该常量（记得还原）

## 验证注意

- `ProcessRunnerTests` 覆盖 ReadCappedAsync 超限/取消/边界；真实超限验证可用 xUnit 直连 `ToolPipeline` + `ProcessRunner` 反编译 `tests/TestData` 下 dll 的 `ILSpyMcp.Samples.BigClass`（600+ 行，调小上限即触发）
- 单测里经 `ToolPipeline` 的 assembly 路径解析基准是测试进程 CWD（`bin/Debug/net10.0`）；访问 `tests/TestData` 下 dll 用 `TestDataPaths.TestSamplesDll` 帮助类（`tests/ILSpyMcp.Tests/TestDataPaths.cs`，逐级上溯找 `ILSpyMcp.slnx`），MemberResolver 单测则直接用 `typeof(OutputFormatter).Assembly.Location`（主项目程序集，无需 TestData）
