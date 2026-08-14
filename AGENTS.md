# AGENTS.md

基于 [ICSharpCode.Decompiler](https://github.com/icsharpcode/ilspy) 的进程内反编译 MCP 服务器：通过 stdio 暴露九个 MCP 工具，方法体反编译与写盘经内置 ICSharpCode.Decompiler（NuGet 包随包分发，无需外部安装反编译工具）在进程内完成，结构类查询（列类型/签名/层级/引用）走 PEReader 元数据层，stdout 输出带行号。

## Agent 优先设计

MCP 工具是 agent 的 API：参数、默认值、输出格式、提示文案等一切接口细节都面向 MCP 调用方（agent）设计——让 agent 能低成本、无歧义地理解、决策与解析，而非迁就人类阅读习惯。新增或修改工具时逐项自检：agent 看到这个参数/这段输出，能否不靠猜就决定下一步？

## 关键约束

- **所有 MCP 工具参数必须带默认值（如 `string assembly = ""`，不声明为可空）**。SDK 依据参数是否有默认值判断必填：无默认值的参数缺参时会在绑定阶段直接抛异常，返回 Tool Error（`The arguments dictionary is missing a value for the required parameter ...`），agent 拿不到错误原因。带默认值后缺参进入方法体，由校验返回中文提示。校验集中在共享 `ArgumentValidators` 静态类（`ValidateAssembly`/`ValidateRequired`/`ValidateMemberNameSearch`/`ValidateList`/`ValidateOutputDir`/`ValidateTimeoutSeconds`），方法返回 `bool` + `out string? error`，失败时返回中文提示文本。
- 工具方法返回 `Task<string>`，一切错误（参数校验、反编译失败）均返回提示文本，不抛异常。
- stdout 只承载 MCP 协议消息；日志必须走 stderr（`Program.cs` 已配置，勿改）。
- 工具的 `[Description]` 与所有提示用中文，必填参数标注「（必填）」。
- **`[Description]` 面向 agent（MCP 调用方），不是给人类开发者看的**：只写 agent 决策需要的行为、默认值、必填、示例与限制；不得出现实现细节或设计动机类措辞（如「agent 友好」「供 agent 决策」等），这类说明写代码注释或本文件。
- **有默认值的工具参数，`[Description]` 必须注明默认值**（如「默认 30」「默认 true」「缺省返回前约 8 KB」），否则 agent 无从感知当前默认行为。
- **每个工具方法带 `CancellationToken cancellationToken = default` 参数**（反编译类放在 `timeoutSeconds` 之后，元数据类放在末尾）：SDK 识别为取消令牌并注入、**不暴露为 MCP 参数**（不要写 `[Description]`），客户端取消调用时取消等待中的反编译（超时/取消统一按「放弃等待」处理：返回提示、结果不入缓存可重试，后台任务经协作式取消中断，不再跑完占 CPU）。勿删。
- **更新版本号必须同步改三处**：`src/ILSpyMcp/ILSpyMcp.csproj` 的 `<Version>`、`src/ILSpyMcp/.mcp/server.json`（顶层 `version` 与 `packages[0].version` 两处都要改一致）、`CHANGELOG.md`（发布前把 `[Unreleased]` 内容转成 `## [<version>] - <date>` 段）。`-v/--version` 输出版本取程序集版本（由 csproj 生成），但 NuGet MCP 注册信息读 server.json，不同步会导致发布后展示版本不一致。**CHANGELOG 变更统一记在 `[Unreleased]` 段**；发布打 `v*` tag 时 CI 从 CHANGELOG.md 提取 `## [<version>]` 段落作为 GitHub Release 正文，NuGet 包的 `PackageReleaseNotes` 只注入指向该 Release 页的链接，缺段会导致发布失败（防静默无说明）。
- **CHANGELOG 面向包使用者（agent 与 CLI 用户）**，记录使用者可见的变更（新功能、行为变化、破坏性变更、可感知的修复、默认值/参数描述变化）以及本次发布的核心内容；发布后它是 GitHub Release 的正文，无需再写开发流水账（git 提交记录已足够）。某版本只有内部变更时写一行「内部重构与细节调整，无用户可见变化」占位，保住 CI「缺段即失败」的防静默机制。
- **新增/变更 MCP 工具必须同步更新 `README.md`**（工具表、工具参数表、CLI 示例、使用示例）：README 是 NuGet 包内嵌的 `PackageReadmeFile`，用户从包页看到的是打包时的 README 快照——工具变更未同步 README 就发布，会导致用户看到的文档与包实际能力不符，必须再发一版修文档。因此**改工具代码（新增/删除/改名/加参/改默认值/改行为）时必须把 README 一并改到位再提交**，与代码改动同 commit，不依赖「下次顺手补」。
- **跨层/多处重复使用的字面量必须定义成常量**：新增代码不得在多文件散落同一字面量（如提示前缀「反编译失败：」、`#MEMBER` 分隔行、`（无）` 占位、token 格式化 `0x{:x8}`、缓存签名前缀与 `\u001F` 分隔符），否则后续修改文案/格式会遇到「改一处漏多处」。集中落点：`Configuration/AppText.cs`（用户可见文案常量：`DecompileFailurePrefix`/`OverLimitOnlySignatures`/`UnresolvedAssemblyAnnotation`）、`Configuration/CacheSignatures.cs`（缓存签名前缀 + `Separator`）、`MetadataNaming.FormatToken`（token 格式化）、`OutputFormatter.MemberLine`（`#MEMBER` 行）与 `SectionBuilder.EmptyPlaceholder`。新增工具/提示时先查上述常量类，能复用则复用；改文案只在常量类改一处，判重逻辑经常量方法（如 `AppText.StartsWithDecompileFailure`）同源感知。**缓存签名前缀改动必须同步 `CacheStatsTool.ToolNames` 映射表**（它与各工具签名生成点同源引用 `CacheSignatures`，勿各自手写）。

## 结构

- `src/ILSpyMcp/` — MCP 服务器（net10.0、PackAsTool、框架依赖；运行期需 .NET 10 运行时），源码按功能划分命名空间（每目录一个）
  - `Tools/`（`ILSpyMcp.Tools`）— 11 个 `[McpServerToolType]` 静态类，分三类：**反编译类**（DecompileTool / DecompileMemberTool / DecompileToDirTool / DecompileToProjectTool，经 `InProcessDecompiler` 进程内反编译，带 `timeoutSeconds` 默认 30s——超时/取消按「放弃等待」处理：返回提示、不入缓存、可调大重试）与**纯元数据类**（ListTypesTool / SignatureTool / HierarchyTool / DependenciesTool / CallGraphTool / AssemblyInfoTool，秒回、无 timeoutSeconds、带 `lines` 分页参数，结果经共享缓存（`ToolExecutor.RunMetadata`），命中时头部标注「缓存: 命中」、未命中回源后入缓存，错误/未找到提示不入缓存；`list_types` 另有 `nameContains` 名称子串过滤参数，忽略大小写、默认空=不过滤）与**缓存观察工具**（CacheStatsTool，`cache_stats` 无程序集参数：输出共享缓存当前占用/上限、条目数、命中率与逐条目占用明细，供评估缓存大小设置）。**执行样板统一走 `ToolExecutor`**（`ResolveAssembly`/`RunPipelineAsync`/`RunMergedAsync`/`RunMetadata`）。`decompile` 仅类型级反编译（`typeName` 必填，成员级由 decompile_member 承接）；`decompile_member` 按成员名子串搜索（纯元数据定位，token 全局唯一；默认排除访问器；多匹配合并输出且各成员前插 `#MEMBER {"name","token"}` JSON 分隔行；匹配数 > `AppConfig.MaxMemberMatches`（20）仅返回 `#MEMBER` 签名清单不反编译；提供 `token` 参数时按 token 直接反编译单个成员（忽略 memberName，typeName 可不填）；无匹配返回相近成员名提示）；`to_dir` 的 `typeName` 非空时仅反编译指定类型，支持逗号分隔多个类型批量写盘（每类型一个 `{TypeName}.decompiled.cs`，未找到的附「未找到：」提示、部分成功也算成功），省略全量（恒单文件写盘，嵌套目录输出请用 `to_project`）；`to_project` 恒项目形式；`signature`/`hierarchy`/`dependencies`/`call_graph` 输出经元数据组件读取；`hierarchy` 另有 `includeIndirect` 参数（默认 false），为 true 时后代段一次返回接口/基类的全部间接后代，`dependencies`/`call_graph` 另有 `includeExternal` 参数（默认 false），为 true 时追加输出跨程序集外部类型引用（带程序集归属）
  - `Services/`（`ILSpyMcp.Services`）— `AppServices.cs` 进程级共享单例（缓存、执行管道、进程内反编译服务、NuGet 查询、更新检查报告缓存 `StatusReport`），避免各工具独立持有实例；测试经 `ConfigureForTest`/`ResetForTest` 注入 fake。**本层不得反向引用 `ILSpyMcp.Tools`**（交叉依赖已消除）。`ToolExecutor.cs` 工具执行共享辅助（路径安全解析 + 管道调用样板 + 元数据共享缓存辅助 `RunMetadata`，移入本层以消除 Pipeline→Services 循环）。`CheckTool.cs` 更新检查入口（**非 MCP 工具**——握手期已把报告注入 ServerInstructions，仅供 CLI `-c/--check` 调试）：报告当前 ilspymcp 是否有新版本；**结果会话内缓存**（重复检查无意义），同步读磁盘缓存、无有效检查记录时返回空报告
  - `Pipeline/`（`ILSpyMcp.Pipeline`）— `ToolPipeline.cs` 共享执行管道：缓存命中 → 进程内反编译回源（同 key 并发单飞）→ lines 分页格式化；回源经 `InProcessDecompiler.RunWithTimeoutAsync`，超时/取消放弃等待、错误提示不入缓存（同 key 可重试）。`ExecuteMergedAsync` 合并多条命令（decompile_member 多匹配）为一个大行列表后统一格式化。**`ToolCommand` 持有 `Assembly` 属性（程序集唯一数据源），`ExecuteAsync(ToolCommand command, ...)` 不再单独传 assembly——勿再造双份程序集参数**
  - `Decompiler/`（`ILSpyMcp.Decompiler`）— `InProcessDecompiler.cs` 进程内反编译服务：以 ICSharpCode.Decompiler 库在进程内完成反编译（每次调用独立构建 PEFile + UniversalAssemblyResolver + CSharpDecompiler，用完即释放），类型/成员（token）/整模块反编译与写盘（单文件与项目模式）；`RunWithTimeoutAsync` 统一超时包装（后台线程执行，超时/取消把取消令牌注入引擎协作式中断、返回提示文本，不抛异常）
  - `Caching/`（`ILSpyMcp.Caching`）— `DecompileCache.cs` 线程安全 LRU 缓存（默认 64MB，结构化 CacheKey 含程序集指纹，dll 更新自动失效；反编译与元数据工具共用同一实例）
  - `Formatting/`（`ILSpyMcp.Formatting`）— `OutputFormatter.cs` 行号标注与 `lines` 分页
  - `Metadata/`（`ILSpyMcp.Metadata`）— 纯元数据读取组件（PEReader+MetadataReader，不加载程序集、不反编译 IL）：`MetadataNaming.cs`（类型全名渲染/定位，格式与反编译引擎一致：命名空间.类型、嵌套用 `+`、泛型带 arity 如 `GenericBox\`1`，list_types 等输出的名字可直接用于反编译工具定位；定位时 `+`/`.` 分隔均接受、行首类别前缀（如 `class Foo.Bar`）亦兼容；另有 `TypeReferenceFullName`/`TypeReferenceScope`/`FormatExternal` 供跨程序集外部类型渲染与归属判定——沿 ResolutionScope 上溯取 AssemblyReference.Name，纯元数据不加载外部程序集）、`CompilerGeneratedFilter.cs`（**全名**（含嵌套外层链）含 `<` 即编译器生成类型——嵌套的 `<PrivateImplementationDetails>+__StaticArrayInitTypeSize=NN` 短名不含 `<` 也命中；**刻意不用 `__` 前缀/特性兜底**——`__ComObject` 是合法类型、顶层语句 `Program` 带 CompilerGeneratedAttribute 但非编译器产物）、`SignatureRenderer.cs`（成员签名渲染，含 `RenderMemberSignature` 单成员；隐式接口实现不渲染 sealed、静态属性带 static、索引器渲染 `this[参数]`、泛型构造函数名去 arity、显式接口属性/事件访问器与隐式访问器一并排除）、`TypeLister.cs`（按类别枚举+过滤）、`Hierarchy.cs`（基类链/接口/后代；支持泛型基类/接口实例化——TypeSpecification 解码，泛型定义在程序集内时基类链继续上溯；`GetDescendantsIncludingIndirect` 一次返回全部间接后代——BFS 邻接表 + 全名去重，供 hierarchy includeIndirect）、`ReferenceExtractor.cs`（成员签名内部类型引用；`ExtractMemberSignatureReferencesWithExternal` 额外收集跨程序集外部类型，格式 `全名 [程序集名]`，程序集名取元数据 AssemblyReference.Name 不加载外部程序集；事件外部类型经 TypeReference case 补齐收集）、`CallGraphExtractor.cs`（**方法体调用图**：扫描类型全部方法体 IL 的调用指令（call/callvirt/newobj/ldftn/ldvirtftn/jmp/calli）提取内部被调用类型，方法体读取经 `PEReader.GetMethodBody(rva)`；IL 解码用 ECMA-335 操作数跳表（仅精确读 metadata token 操作数，其余按表跳过），同程序集调用通常发 MethodDef 直判内部、MemberRef 兜底沿 ResolutionScope 回溯、MethodSpec 泛型实参走签名解码收集；`ExtractMethodBodyCallTypesWithExternal` 额外收集跨程序集外部被调用类型（带程序集归属），反向 FindCallers 保持内部；编译器生成 target/source 一律过滤）、`MemberResolver.cs`（成员名子串搜索，返回 `MemberSearchResult` 含相近名；token `0x06000005` 直用于进程内成员反编译；显式接口访问器一并排除）
  - `UpdateCheck/`（`ILSpyMcp.UpdateCheck`）— `NuGetClient.cs` NuGet 最新稳定版查询（排除预发布，网络失败返回 null 供更新检查静默跳过）；`UpdateChecker.cs` NuGet 新版本检查的磁盘缓存与报告段组装（成功 TTL 24h、失败 1h 退避、失败保留旧值，落盘 `%LOCALAPPDATA%\ilspymcp\update-check.json` 跨进程共享；**查询经构造注入的委托 `queryLatest`**，生产由 AppServices 传共享 NuGetClient、测试传 fake，不反引用 Services 层）；`EnvironmentChecker.cs` 更新检查状态组装（仅 ilspymcp 更新状态，依赖经参数传入 updater，不反引用 Services 层）。状态经 `GetCachedNuGetStatus` 同步读缓存（零网络，返回 `NuGetUpdateStatus?` 含新版本标记与报告行，无有效检查记录时返回 null），**网络刷新由握手后台 `RefreshIfStaleAsync` 承担**（TTL/退避内不联网，失败静默降级；CLI `-c` 是主动调试入口，调用前先 await 刷新）；CLI `-c` 与握手注入分别组装文本——CLI 取状态行（`GetCachedNuGetLine`，供人阅读），握手注入走 `BuildHandshakeText`（有新版本时前缀明确指令要求 agent 会话开始主动告知用户并提供升级命令，已是最新仅状态行，无记录不注入）；**版本比较共用 `IsNewerThanCurrent` 静态方法**（报告与握手注入两处调同一规则，防漂移），当前版本统一取 `AppConfig.CurrentVersion`
  - `Configuration/`（`ILSpyMcp.Configuration`）— `AppConfig.cs` 全局配置常量（缓存/超时/匹配上限/NuGet 检查等可调参数集中维护）
  - `Validation/`（`ILSpyMcp.Validation`）— `ArgumentValidators.cs` 共享参数校验（assembly/必填/memberName/token/list/outputDir/timeoutSeconds，无安装前置）
  - MCP 握手期**始终注入 server 当前工作目录**（`BuildServerInstructions` 首行 `当前工作目录: <CWD>`，供 agent 解析 assembly/outputDir 相对路径）；随后**先执行更新检查**（`await AppServices.StatusReport.Value` 拿到 `NuGetUpdateStatus?`，与 CLI `-c` 同源会话内缓存），经 `EnvironmentChecker.BuildHandshakeText` 组装后附于 CWD 行之后一并注入 `McpServerOptions.ServerInstructions`：有新版本时注入指令式文本（要求 agent 在会话开始的第一条回复中主动告知用户并提供升级命令，陈述句会被 agent 当背景信息而不转述）；已是最新仅注入状态行；无有效检查记录时只注入 CWD 行；`StatusReport.Value` 包 try/catch 降级为空报告，更新检查异常不阻断 MCP 启动
- `src/ILSpyMcp.Client/` — 端到端验证客户端：场景拆分为 `DecompileCases` / `DecompileMemberCases` / `ListTypesCases` / `DecompileToDirCases` / `SignatureCases` / `HierarchyCases` / `DependenciesCases` / `CallGraphCases`（各工具全参数覆盖）与 `ClientRunner`（连接/执行/输出）、`TestDataHelper`（自动发现测试 dll 并共享类型/成员标识），`Program.cs` 仅做入口
- `tests/ILSpyMcp.Tests/` — xUnit 单元测试（缓存/管道/格式化/校验/进程内反编译/元数据组件/更新检查，经 `TestDataPaths` 与测试程序集验证）
- `tests/TestData/` — 验证用程序集（生成的 `ILSpyMcp.TestSamples.dll`：653 class + 6 interface + 3 struct + 1 delegate + 3 enum，list_types 默认过滤 `<Module>`/`<PrivateImplementationDetails>+__StaticArrayInitTypeSize=256` 等编译器生成类型；`Class0001-0600` 触发默认预算截断与分页上限；`BigClass` 含 BigMethod 600+ 行与 BigHelper/BigHelper2，触发 decompile 截断/分页与 decompile_member 多匹配；`BaseClass/DerivedClass/DerivedClass2` 与 `IAnimal/Dog` 供 hierarchy，`IWorker/WorkerBase/WorkerDerived` 与 `GenericRoot\`1/GenericMid/GenericLeaf` 供 hierarchy includeIndirect 间接后代，`GenericBox\`1`/`IntComparer` 供泛型基类/接口/签名，`Props` 供静态属性/索引器，`ThingImpl` 供显式接口访问器排除，`ManyOverloads`（21 个 Do 重载）触发 decompile_member 超限仅签名，`AbstractShape/Circle/SealedCircle` 与 `Level1-4` 供 signature 修饰符与多层基类链，`Uses/UsesShared1-3/Shared` 供 dependencies 双向，`Caller/Callee/GenericCaller/GenericHelper/PropReader/PropHolder/FieldUser/FieldHolder` 供 call_graph 双向（内部方法/构造/泛型实例化/属性访问器调用、跨程序集排除、字段访问不计、编译器生成过滤），`WithClosure/WithAsync/StaticArrayHolder` 触发编译器生成类型；dll 由生成脚本 `generate-testdata.ps1` 生成且已被 git 忽略（`tests/TestData/*.dll`），改脚本后需重新生成并保证本机存在；Client 经 `TestDataHelper` 自动发现目录下 dll 并对全部工具做端到端验证）

## 命令

```bash
dotnet build -c Release src/ILSpyMcp/ILSpyMcp.csproj
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj   # 单元测试
dotnet test tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj --filter "FullyQualifiedName~DecompileCache"   # 只跑单套测试
dotnet run -c Release --project src/ILSpyMcp.Client/ILSpyMcp.Client.csproj   # 调全部工具做端到端验证
```

- Client 端到端会以 Release 自启动 server 项目（`dotnet run --project src/ILSpyMcp/ILSpyMcp.csproj -c Release`，无需预先单独构建 server），运行后自动清理写盘产物 `tests/.ilspymcp-client-out/`（已在 .gitignore）
- CLI 调试（改完 server 代码用 Debug 构建的 exe 快速验证，行为与 MCP 工具一致，是验证新行为的主要手段）：
  ```bash
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName>      # 反编译类型
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -mn <成员名子串>  # 按名搜成员（多匹配含 #MEMBER JSON 分隔行，>20 仅返回签名清单）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -s   # 成员签名（API 地图，元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -hc  # 继承/接口关系（元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -hc -i  # 继承/接口关系含全部间接后代（元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -d   # 成员签名内部引用（元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -d -x  # 成员签名引用含跨程序集外部类型（元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -cg  # 方法体调用关系（元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -cg -x  # 方法体调用含跨程序集外部类型（元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -l c               # 列 class（c/i/s/d/e 可组合，过滤编译器生成类型）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -l c -nc Box       # 列 class 且名称含 Box 的类型（忽略大小写，配合 -l）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -l c -ns ILSpyMcp.Samples  # 列 class 且命名空间含 ILSpyMcp.Samples（忽略大小写，配合 -l）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -ss <子串>         # 按字符串字面量子串反查成员（可选 -t 限定类型，元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -fa -t <TypeName> -fn <字段名>  # 追踪字段读写点（可选 -tk 按字段 token，元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -iu -t <InterfaceName>  # 接口实现者与调用点（-i 含全部间接实现者，元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -gi -t <GenericTypeName>  # 泛型实例化使用点（元数据秒回）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -cc -t <TypeName> -mn <方法名>  # 起始方法调用序列 + 被调成员反编译（-tk 按方法 token 定位；-x 展开跨程序集外部调用）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -t <TypeName> -mn <成员名> -tt <typeToken>  # typeName 有歧义时按类型 token（0x02）精确定位类型后搜成员
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -a <dll> -o <dir>           # 全量写盘；-p 组合为项目形式（to_project）
  ./src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe -c                         # 检查 ilspymcp 是否有新版本（无需 -a）
  ```
  其他选项：`-ln start-end` 按行分页、`--timeout` 秒数
- 反编译引擎（ICSharpCode.Decompiler）随 NuGet 包内置，安装 ilspymcp 后开箱即用，无需额外安装反编译工具
- 修改逻辑后：build 通过 + 单元测试通过 + 本机运行 Client 确认输出样式（CI 的 build.yml 只做 build/test/发布，不跑端到端；端到端验证改为本机手动执行）
- 重新生成测试程序集：`powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`（注意 `BigMethod` 用数组链而非常量链——否则 Release 编译常量折叠会让方法只剩几行，无法触发截断）
- 本地调试注意：根 `opencode.json` 把本仓库自身的 MCP server 绑定到 `src/ILSpyMcp/bin/Debug/net10.0/ILSpyMcp.exe`，改完 server 代码需重新 build 并重启 opencode 才生效；会话内 `ilspy_*` 工具反映旧二进制，验证新行为请以 Client 输出为准

## 输出约定

- 结果前置头部信息块（`程序集/目标` 两行 + 总量字段 + `当前输出` 字段 + `剩余` 字段 + `---` 分隔线，纯文本不带行号），由工具经 `FormatContext` 传入、`OutputFormatter` 生成，给 agent 明确的代码归属、总体规模与当前切片位置。反编译与元数据工具结果命中缓存时在 `目标` 行后追加 `缓存:   命中（重复查询成本低）` 行（`FormatContext.IsCached`，由 `ToolPipeline` 或 `ToolExecutor.RunMetadata` 在缓存命中时经 `with` 注入；未命中/写盘工具（结果已在磁盘）不标注）。`剩余` 行仅截断时输出，告知剩余行数/数据量与「可一次获取」/「需分次获取」的建议 `lines` 范围。**不展示参数行**——agent 面对的是 MCP 命名参数，暴露内部参数/反编译细节（如成员 token）会误导 agent；`decompile_to_dir`/`decompile_to_project` 成功提示含「来源 <assembly>」；更新检查（CLI `-c/--check` 输出）无头部信息块（不涉及程序集），直接返回更新状态行
- 总量：反编译为 `总行数: N 行`；列类型同时给出 `匹配实体: N 个` 与 `总行数: N 行`（每行一个实体，行数=实体数）。`当前输出` 含返回量 KB 与截断原因：截断如 `1-78（78 行，7.9 KB，已截断：超过默认预算约 8 KB）`、未截断如 `1-3（3 行，0.0 KB）`；`剩余` 行如 `剩余:     122 行 / 约 12.5 KB，可一次获取：lines="79-200"`、剩余超单次预算时如 `剩余:     922 行 / 约 94.5 KB，超过单次预算（约 32 KB），需分次获取：先用 lines="79-390"`。空结果为 `无`、越界为 `无效（起始行 X 超出总行数 Y）`
- `decompile_member` 提供 `token` 参数时按 token 直接反编译单个成员，头部目标描述为 `类型 X 的成员 <token>（按 token 反编译）`（typeName 缺省时为 `成员 <token>（按 token 反编译）`）；按名搜索时头部目标描述为 `类型 X 的成员 <memberName>（N 个匹配）`；多成员匹配合并输出（行号连续、总行数基于合并结果，各成员前有 `#MEMBER {"name","token"}` JSON 分隔行、计入行号——agent 可直接解析取 token），匹配数 > `AppConfig.MaxMemberMatches`（20）时头部注明「超过上限，仅列出签名」并只返回 `#MEMBER` 签名清单（每行 `#MEMBER {"name","token","signature"}`）；无匹配返回「类型 X 中未找到名称包含 Y 的成员」、存在相近名时追加「相近成员：A、B、C」；类型不存在返回「未找到类型 X」
- 纯元数据工具（list_types/signature/hierarchy/dependencies/call_graph）头部同样带信息块（IsListing：`匹配实体: N 个 + 总行数: N 行`，每行一个实体），默认返回前约 8 KB、同样支持 `lines` 分页。反编译输出含 `//IL_` 未解析注释时，头部追加「提示: 输出含 //IL_ 未解析注释（动态类型/异常路径），仅供结构参考」
- `signature` 每行行尾附成员 token（`  0x06000505`，两空格分隔；方法 `0x06`/字段 `0x04`/属性 `0x17`/事件 `0x14` 高字节区分），agent 看中某成员可直接取行尾 token 用于 `decompile_member` 的 `token` 参数反编译，API 地图与成员反编译闭环
- `hierarchy` 输出三段（基类链/接口/程序集内继承实现者；`includeIndirect=true` 时继承实现者段含全部间接后代——接口的所有实现者及其子类、基类的所有子孙），`dependencies` 输出三段（引用的内部类型/引用的外部类型（仅 includeExternal=true，条目格式 `全名 [程序集名]`，空段输出（无）占位）/引用它的类型），`call_graph` 输出三段（方法体调用的内部类型/方法体调用的外部类型（仅 includeExternal=true，条目格式 `全名 [程序集名]`）/程序集内方法体调用此类型的类型），空段均输出（无）占位，段标题与实体均作为行标注行号
- 头部之下按行号标注（`行号\t内容`），切片时行号基于原始位置
- 默认返回前约 8 KB，`lines="start-end"` 按行号范围分页（单次最多约 32 KB）
- stdout 反编译结果超过 `AppConfig.MaxOutputBytes`（64MB）时 `InProcessDecompiler` 直接返回「超过上限，建议改用 decompile_to_dir」错误提示，不入缓存；只有 `decompile_to_dir` 能拿到完整结果。测试超限行为可临时调小该常量（记得还原）

## 验证注意

- 超限/超时/取消行为可用 xUnit 直连 `ToolPipeline` 反编译 `tests/TestData` 下 dll 的 `ILSpyMcp.Samples.BigClass`（600+ 行，调小上限即触发）
- 单测里经 `ToolPipeline` 的 assembly 路径解析基准是测试进程 CWD（`bin/Debug/net10.0`）；访问 `tests/TestData` 下 dll 用 `TestDataPaths.TestSamplesDll` 帮助类（`tests/ILSpyMcp.Tests/TestDataPaths.cs`，逐级上溯找 `ILSpyMcp.slnx`），MemberResolver 单测则直接用 `typeof(OutputFormatter).Assembly.Location`（主项目程序集，无需 TestData）
