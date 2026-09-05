# Changelog

本文件记录 ILSpyMcp 各版本的变更，格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循[语义化版本](https://semver.org/lang/zh-CN/)。

版本号与 `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` 的 `<Version>` 保持一致；发布时 CI 从本文件提取当前版本对应段落作为 GitHub Release 正文（NuGet 包的 `PackageReleaseNotes` 只放指向该 Release 页的链接）。未发布的变更统一记录在 `[Unreleased]`，发布时再转为带日期的版本段落。

本文件面向包使用者（agent 与 CLI 用户），只记录使用者可见的变更（新功能、行为变化、破坏性变更、可感知的修复、默认值/参数描述变化）；内部重构、实现细节、测试改动等一律不记录，请查阅 git 提交历史。

## [Unreleased]

### Added

- **Web 调试展示面（`--web`，P4）**：宿主新增 `--web` 启动 Blazor Server 网页（内嵌 Kestrel，`--web-port` 指定端口，缺省自动选空闲端口并拉起默认浏览器）。页面提供反编译代码视图（Monaco 编辑器，语法高亮 + 断点/当前执行行装饰 + 随明暗主题切换）+ 动态调试面板（调用栈/局部变量/线程，BB 组件）+ 最小控制（启动并附加/断点/继续/单步/断开），与 MCP agent 共享同一调试会话（agent 经 `debug_*` 工具调试时浏览器可实时观看）；默认暗色 Fluent 主题。反编译 IL→行映射基于 ICSharpCode.Decompiler 序列点（无 PDB 亦可语句级定位）
- **动态调试 MCP 工具面（P3）**：新增 `dotnetdebugger_debug_*` 系列工具——会话（`debug_launch`/`debug_attach`/`debug_disconnect`/`debug_state`）、控制（`debug_continue`/`debug_step` into/over/out）、断点（`debug_breakpoint_set`/`_remove`/`_clear`，按 模块+方法 token+IL offset）、观察（`debug_stack`/`debug_threads`/`debug_variables`）、异常断点（`debug_exceptions`/`_clear`）。控制工具**异步返回（带默认超时）**，停点信息经查询工具获取。新增 `DotNetDebugger.Session` 库（会话管理 + 停点事件缓冲 + agent 轨迹日志）
- **动态调试引擎 v1（`DotNetDebugger.Engine` 库）**：进程内 .NET 调试引擎（ICorDebug 通道，ClrDebug + DbgShim），支持启动/附加目标进程、按方法 token+IL offset 下断点、continue、单步、读线程/调用栈/局部变量（标量）、first-chance 异常断点、统一 `DebugEvent` 事件流；宿主另有 `-dbg` CLI 一次性调试命令
- **测试设施**：`tests/TestData` 追加可执行调试目标 `DebugTarget.exe`；新增 `DotNetDebugger.Engine.Tests`（真实子进程 attach/断点/单步/状态/异常）与 `DotNetDebugger.Session.Tests`（会话管理/轨迹）集成测试，串行执行避免 ICorDebug 会话干扰

### 重构

- **仓库/包改名：ILSpyMcp → DotNetDebuggerMcp**：NuGet 包 id / CLI 命令 `ilspymcp` → `DotNetDebuggerMcp`；MCP 服务器注册名建议 `dotnetdebugger`（工具前缀 `dotnetdebugger_*`）；解决方案拆为五项目（`DotNetDebugger.Decompiler` 反编译能力库 + `DotNetDebugger.Engine/Session/Web` 预留库 + `DotNetDebuggerMcp` 宿主 exe）。**行为不变**：16 个反编译工具名/参数/输出格式、CLI 参数与握手简介均保持（简介文字已更新产品定位）
- **反编译/静态分析能力抽为 `DotNetDebugger.Decompiler` 库**：`Metadata`（纯元数据组件）+ `InProcessDecompiler` + 自建 `DecompilerConfig`/`DecompilerText` 常量；宿主 exe 引用该库。对外行为零变化
- **包安装/更新命令变更（破坏性）**：`dotnet tool install --global ilspymcp` → `dotnet tool install --global DotNetDebuggerMcp`；旧 `ilspymcp` 包不再更新（新包从 1.5.0 起独立发版）

### 文档

- **README 增加 opencode v2 接入配置**：「接入 opencode」分 v2 / v1 两小节——v2 服务器名称位于 `mcp.servers` 下（附 `opencode.jsonc` 示例），v1 写法保留并注明 v2 仍兼容；工具名前缀说明对齐 v2 的 `<服务器名>_<工具名>` 组合规则；同时修正已过时的「握手注入工作目录」描述（1.4.0 起不再注入，相对路径以 opencode 会话工作目录解析）

## [1.4.0] - 2026-08-28

### Added

- **MCP 握手注入 Markdown 功能简介（行为变化）**：`ServerInstructions` 全面 Markdown 化——「## 服务器简介」（含触发条件：反编译/分析 .NET 程序集时使用）、「## 工具一览」（全部 16 个工具的简短用途索引，agent 握手期即可见全量工具清单，无需按需搜索）、「## 使用约定」（相对路径与 `lines` 分页）；更新检查报告并入「## 更新状态」段（自带标题），无新版本时仅状态行、无检查记录时整段不注入。工作目录行不再注入（客户端系统环境已提供）。opencode 2 等客户端握手期只注入 `ServerInstructions` 且工具目录仅部分常驻，此前 agent 对 ilspy 服务器能力与触发时机一无所知；简介刻意保持简短以防上下文截断，**新增工具必须同步 `AppText.HandshakeFeatureIntro` 的工具一览**

### 依赖更新

- `ModelContextProtocol` 2.1.0 → 2.2.0（上游为小版本更新：HTTP 扩展包新增混合会话模式与 header 解码边界修复，stdio 场景行为无变化）

## [1.3.2] - 2026-08-26

### Fixed

- **并发请求挂死修复**：MCP 模式下 `Host.CreateApplicationBuilder` 默认注册的 Console 日志写 stdout，与 JSON-RPC 响应共用同一条管道且互不协调；多个请求并发时日志行与响应字节交错可能撕坏响应帧，客户端永远等不到对应 id 的结果（表现为调用长时间无返回，12 路并发下必现）。现启动时清除默认日志提供者并把全部日志显式路由到 stderr（`ClearProviders` + `LogToStandardErrorThreshold = Trace`），stdout 只承载协议消息——与「日志必须走 stderr」的既有输出约定对齐

## [1.3.1] - 2026-08-25

### Changed

- **缓存固定 30 分钟滑动过期 + 5 分钟定时清理（行为变化）**：`DecompileCache` 原为仅容量 LRU（64 MB 满时驱逐），MCP 以 daemon 常驻时空闲仍占满内存；现固定 30 分钟滑动过期（`Get` 命中刷新过期时间，`Put` 顺带清理过期条目）+ 5 分钟后台定时扫描清理过期条目，常驻空闲后内存自动回落；容量 LRU 与指纹失效保持不变
- **`hierarchy` 空段占位（行为变化）**：基类链/接口/继承实现者三段中空段由「整段省略」改为输出 `（无）` 占位，与 `dependencies`/`call_graph`/`interface_usage` 等同族工具一致——此前 agent 无法区分「确实没有」与「输出不完整」，且容易按同族工具占位惯例类比推断而误判
- **`interface_usage` 非接口校验（行为变化）**：`typeName` 定位到非接口类型时返回中文提示（「X 不是接口类型，interface_usage 仅适用于接口；查类的继承/后代请用 hierarchy」），不再对普通类输出貌似有效的三段伪结果，避免 agent 误以为该类型是接口
- **工具描述修正（行为无变化）**：`decompile_member` 无匹配提示改为「未找到；存在相近成员名时附相近列表」（此前措辞暗示总有相近名）；`call_chain` 区分两处 `#MEMBER` 清单触发条件——起始方法名多匹配返回清单是「定位步骤（不反编译）」，被调内部成员超过 20 个才仅返回其签名清单；`decompile_member` 的 `typeToken` 说明优先级（提供时按 typeToken 定位、忽略 typeName）；`decompile_to_dir` 注明文件名规则（`{TypeName}.decompiled.cs`，嵌套类型保留 `+` 分隔）；`list_types` 措辞「显示类」改为「闭包/显示类」

## [1.3.0] - 2026-08-17

### Added

- `list_types` 新增 `namespaceContains` 命名空间子串过滤参数（忽略大小写，默认空=不过滤），嵌套类型按其最外层声明类型的命名空间归属；可与 `nameContains` 组合使用，按命名空间定位类型免分页扫全量；CLI 同步提供 `-ns|--namespacecontains` 选项（配合 `-l`）
- `decompile_member` 新增 `typeToken` 参数（CLI `-tt`）：`typeName` 存在歧义（命名空间与嵌套分隔的多种解释均命中同一名字）时返回歧义提示并列出候选类型（附类型定义 token `0x02` 开头），可用 `typeToken` 精确定位类型后再按 `memberName` 搜索成员；提供 `typeToken` 时 `typeName` 可不填
- 新增 `search_string` 工具（CLI `-ss|--searchstring`）：按字符串字面量子串（忽略大小写）在方法体 `ldstr` 指令中反查成员，输出每行 `类型全名::成员签名` + 转义后的字符串值 + 成员 token（可直接用于 `decompile_member` 的 `token` 参数反编译对应成员）；`typeName` 非空时仅在指定类型内反查，省略时跨程序集全部类型；适用于按业务文案/SQL 片段/配置 Key 反查代码位置，无需反编译全文
- 新增 `field_access` 工具（CLI `-fa|--fieldaccess` + `-fn|--fieldname`）：追踪指定字段的读取/写入/取地址位置——按 `fieldToken`（`0x04` 开头字段 token，取 `signature` 行尾或 `#MEMBER` 分隔行的 token）或 `typeName`+`fieldName`（忽略大小写，省略 `typeName` 时跨程序集搜索）定位字段后，反向扫描全部类型方法体的字段访问指令（`ldfld`/`ldsfld` 读取、`stfld`/`stsfld` 写入、`ldflda`/`ldsflda` 取地址），输出三段 `类型全名::成员签名` 来源成员（空段 `（无）` 占位）；字段名匹配多个字段时返回 `#MEMBER` 签名清单，用其中 token 作 `fieldToken` 精确定位；适用于追踪字段读写点、判断字段是否仍被使用
- 新增 `call_chain` 工具（CLI `-cc|--callchain`）：输出起始方法的方法级正向调用序列 + 被调用成员反编译组合视图——按 `token`（`0x06` 开头方法 token）或 `typeName`+`memberName`（忽略大小写，匹配多个方法时返回 `#MEMBER` 签名清单，用其中 token 精确定位）定位起始方法，扫描其方法体调用指令按 IL 序列出调用序列（每行 `序号. 类型::成员()` + 内部成员 token，token 可直接用于 `decompile_member` 反编译）；`includeExternal=true` 时保留跨程序集外部调用行（格式 `全名::成员名 [程序集名]`，默认 `false` 过滤）；对去重后的唯一内部成员（最多 20 个）逐条反编译，各成员体前有 `#MEMBER` JSON 分隔行（含 name/token/type），超过 20 个时仅返回 `#MEMBER` 签名清单；适用于追踪单个方法直接调用了哪些方法及其实现体
- 新增 `interface_usage` 工具（CLI `-iu|--interfaceusage`，`-i` 传入 includeIndirect）：接口使用情况组合视图——程序集内实现该接口的类型（`includeIndirect=true` 时含全部间接实现者，如接口的子接口、实现者及其子类，默认 `false`）、方法体调用接口成员的调用点（每行 `类型全名::成员名 → 接口成员名`，反扫全部非编译器生成类型方法体调用指令，内部接口 MethodDef 直判、跨程序集外部接口 MemberRef 沿 parent TypeRef 全名判定，含泛型实例化 MethodSpec 解包）与成员签名引用该接口的类型（签名级引用，与调用点互补）；三段空段输出 `（无）` 占位，元数据秒回；适用于回答「哪些类型实现了这个接口、程序集内哪里调用了它的成员」的接口使用全景
- 新增 `generic_instantiations` 工具（CLI `-gi|--genericinstantiations`）：泛型实例化使用点组合视图——输出指定泛型类型在程序集内被具体实例化的位置两段：成员签名中的泛型实例化（扫描全部非编译器生成类型的字段/方法/属性/事件签名，凡签名用具体类型参数实例化该泛型类型时输出 `类型全名::成员签名 → GenericType<arg, arg>` 行，如 `ILSpyMcp.Samples.GenericUser::public GenericBox<int> BoxInt; → ILSpyMcp.Samples.GenericBox<int>`，int/string 等不同具体参数各一行）与方法体调用中的泛型实例化（扫描全部类型方法体调用指令，凡调用该泛型类型的泛型方法输出 `来源类型::来源方法签名 → Echo<int>` 行、经成员引用实例化该泛型类型如 `new GenericBox<int>()` 时输出对应行）；两段空段输出 `（无）` 占位，元数据秒回；`typeName` 兼容带 arity 全名（`GenericBox`1`）与省略 arity/短名（`GenericBox`）两种输入；适用于回答「这个泛型类型在程序集内哪里被用什么具体类型参数实例化了」

### Changed

- **输出可靠性标注（行为变化）**：反编译输出含 `//IL_` 未解析注释时，头部提示由定性描述改为计数（「提示: 输出含 N 处 //IL_ 未解析注释（动态类型/异常路径），仅供结构参考」）；`call_graph` 方法体 IL 解码降级（部分方法体因 IL 损坏中止解码）时头部追加「提示: 本结果含 N 处降级解析（部分方法体 IL 未完全解码，仅供结构参考）」（仅新鲜扫描显示，命中缓存不标注），agent 可感知结果的解码完整性
- **`call_chain` 跨程序集调用链展开（行为变化）**：`includeExternal=true` 时，跨程序集外部调用行保留并展开——可解析的外部调用（主 dll 同目录/CWD/NuGet 缓存/共享框架/GAC，经 UniversalAssemblyResolver 定位磁盘程序集）在外部调用行后缩进输出 `程序集::类型::成员 调用:` + 被调方法体子序列（子序列内跨程序集调用递归展开、防环），解析失败的框架/外部调用在行尾标注 `（未找到程序集 X，视为框架/外部调用未展开）`；纯元数据读取，不加载外部程序集
- **全部工具描述精简（行为无变化）**：MCP 工具与参数的 `[Description]` 全面压缩并删除实现细节（IL 指令名、UniversalAssemblyResolver/MethodDef/MemberRef 等内部机制、`#MEMBER` 精确 JSON 格式），统一为「做什么 → 定位参数与默认 → 输出要点」三段式；重复的参数描述（`assembly`/`lines`/`timeoutSeconds`/`includeExternal`/`includeIndirect` 及分页页脚）抽为共享模板常量（`Configuration/ToolParameterText.cs`）一处维护；README 工具表与参数表同步重写

### Fixed

- **类型名歧义提示按工具给出解法（行为变化）**：`typeName` 存在歧义时，`decompile_member` 提示用 `typeToken` 精确定位、`field_access` 提示用 `fieldToken` 精确定位；`search_string`/`interface_usage`/`generic_instantiations` 无 token 参数，提示改为「该类型名在归一化后存在同名类型，请换用不含歧义的完整类型名」
- `call_chain` 跨程序集调用展开增加深度与节点上限（默认 5 层 / 200 节点）：超限的外部调用子树不再展开（按未展开处理），防 BCL 密集方法体在 `includeExternal=true` 时展开数百节点拖慢查询
- `generic_instantiations` 修复类型参数过滤漏洞：泛型方法内以类型参数调用（如 `Echo<T>`）不再产出虚假 `Echo<T0>` 命中；嵌套部分具体化实参（如 `GenericBox<SomeGeneric<T>>`）不再被误判为具体化实例化
- `cache_stats` 修复新工具来源名显示：缓存条目来源工具名映射此前缺 `search_string`/`field_access`/`call_chain`/`interface_usage`/`generic_instantiations` 五个新工具前缀，明细中会显示原始签名前缀而非工具名；现已补录（映射与各工具签名生成同源引用 `CacheSignatures` 常量，后续改前缀不再失同步）

## [1.2.3] - 2026-08-14

### Added

- `list_types` 新增 `nameContains` 名称子串过滤参数（忽略大小写，默认空=不过滤），大型程序集按名定位类型；CLI 同步提供 `-nc|--namecontains` 选项（配合 `-l`）
- `hierarchy` 新增 `includeIndirect` 参数（默认 `false`）：为 true 时一次返回接口/基类的全部间接后代（如接口的所有实现者及其子类、基类的所有子孙），免 agent 递归多次调用
- `dependencies`/`call_graph` 新增 `includeExternal` 参数（默认 `false`，CLI `-x`）：同时输出跨程序集外部类型引用（格式 `全名 [程序集名]`，如 `System.Console [System.Console]`），真实依赖链可见；CLI `-hc` 同步提供 `-i|--indirect` 选项传入 hierarchy 的 includeIndirect
- `decompile_to_dir` 的 `typeName` 支持逗号分隔多个类型批量写盘（默认空=全量）：一次调用写入多个指定类型的源码文件（每个类型一个 `{TypeName}.decompiled.cs`），如 `"A.B.C1,A.B.C2"`；找到的类型写盘、未找到的类型在结果中提示（附「未找到：」清单），部分成功也算成功；CLI 写盘（`-o` + `-t`）同步支持
- 新增 `assembly_info` 工具：输出程序集概览（程序集名与版本、目标框架、引用的程序集清单、实体类型计数（class/interface/struct/delegate/enum，过滤编译器生成类型）与入口点），元数据秒回，适合作为接触陌生程序集的第一站；CLI 同步提供 `-ai|--assembly-info` 选项
- `call_graph` 新增 `token` 参数（CLI `-tk`）：按方法 token 反向定位程序集内哪些方法体调用了这个具体方法（方法级调用点），输出 `类型全名::成员签名` 调用点行（含泛型实例化调用解包、编译器生成类型过滤）；token 取 `signature` 行尾或 `#MEMBER` 分隔行的 token，提供时 `typeName` 可不填、`includeExternal` 忽略
- 新增 `cache_stats` 工具（无参数）：输出进程内共享缓存状态——当前占用/上限（据此判断缓存大小设置是否合适）、条目数、命中率（会话启动以来的累计命中/未命中）与逐条目占用明细（按占用降序，含来源工具、参数与程序集），定位缓存大头；明细支持 `lines` 分页

### Changed

- MCP 握手注入 server 当前工作目录：agent 可据此解析 assembly/outputDir 相对路径，消除路径基准盲区
- MCP 握手更新提示改为指令式：检测到 ilspymcp 有新版本时，注入文本明确指示 agent 在会话开始的第一条回复中主动告知用户并提供升级命令（仅新版本时主动转述；已是最新时仍为背景状态行，不打扰用户）。CLI `-c/--check` 输出保持朴素状态行不变
- 反编译与元数据工具结果命中缓存时，头部信息块在「目标」行后追加「缓存: 命中（重复查询成本低）」行，agent 可感知重复查询低成本、放心多问；未命中或写盘工具（结果已在磁盘）不标注
- **元数据工具结果纳入全局共享缓存（行为变化）**：`list_types`/`signature`/`hierarchy`/`dependencies`/`call_graph`/`assembly_info` 此前每次调用都重新读取元数据，现与反编译工具共用同一内存缓存（按「程序集 + 参数」区分，程序集更新后自动失效），重复查询命中缓存并在头部标注「缓存: 命中」；写盘工具 `decompile_to_dir`/`decompile_to_project` 不缓存（结果已落盘）
- 各工具未找到类型时附相近类型名提示（行为变化）：decompile/signature/hierarchy/dependencies/call_graph/decompile_to_dir/decompile_member 在类型未找到时返回「未找到类型 X。相近类型：A、B、C」（相近类型为全名，可直接复制定位），agent 免自行猜测拼写
- `decompile`/`decompile_member` 描述点明入口粒度：`decompile` 为类型级入口（整类型源码）、`decompile_member` 为成员级入口（单个或多个成员实现体），消除「按成员反编译该用哪个工具」的歧义，无行为变化
- **`decompile_member` 多成员分隔行与超限签名清单改为 `#MEMBER` JSON 结构化行（破坏性变更）**：分隔行由 `=== 名字 (token) ===` 改为 `#MEMBER {"name":"...","token":"0x..."}`（跨程序集搜索时另带 `type` 字段标注成员所属类型），超限签名清单由每行 `签名  [token]` 改为 `#MEMBER {"name","token","signature","type"}`——agent 免解析文本分隔线，直接按行首 `#MEMBER ` 识别并解析 JSON 取 token（token 仍可直接用于 `token` 参数反编译）；`token` 单成员反编译输出不变
- **`decompile_member` 的 `typeName` 变为可选（行为变化）**：省略 `typeName` 时跨程序集按成员名搜索全部类型的成员（默认仍排除属性/事件访问器），头部目标描述相应改为「成员 Y（跨程序集，N 个匹配）」
- `decompile_member` 按名搜索范围扩展为字段/方法/属性/事件（行为变化）：此前 `memberName` 只命中方法，现字段、属性、事件同样按名子串匹配（属性/事件访问器仍默认排除）
- `signature` 工具每行行尾附成员 token（如 `public void Do(int);  0x06000505`，可直接用于 `decompile_member` 的 `token` 参数反编译对应成员），API 地图与成员反编译闭环
- `decompile_to_dir` 成功提示列出实际写盘的文件名（如 `已写入 2 个文件至 <dir>：A.decompiled.cs、B.decompiled.cs（来源 <assembly>）`），agent 免推导即可直接读取产物；文件名过多时列前 3 个 + 等 N 个
- 内部重构：统一工具执行样板（RunMetadataPe/RunToDisk/SectionBuilder）、拆分 decompile_member 与 call_graph 方法，并将 decompile_member 超限签名清单纳入共享缓存（重复查询命中时头部标注「缓存: 命中」）

### Removed

- **`decompile_to_dir` 移除 `nestedDirectories` 参数（破坏性变更）**：该工具恒为单文件写盘，参数此前即不产生效果；按命名空间嵌套目录输出请改用 `decompile_to_project`

## [1.2.2] - 2026-08-12

### Added

- `decompile_member` 新增 `token` 参数：按元数据 token 直接反编译单个成员（匹配超限清单与多成员分隔行中的 token 可直接用于反编译，闭环使用）
- `typeName` 参数兼容 `list_types` 行首类别前缀（如 `class Foo.Bar` 可直接复制使用，无需手动去掉前缀）

## [1.2.1] - 2026-08-12

### Changed

- 反编译引擎升级至 v11 正式版（ICSharpCode.Decompiler 11.0.0.9375）：修复 reference assembly 反编译崩溃、项目导出遇成员失败时中止等若干反编译问题，输出更稳定

## [1.2.0] - 2026-08-12

### Added

- 新增 `call_graph` 工具：扫描指定类型全部方法体 IL 的调用指令（call/callvirt/newobj/ldftn/ldvirtftn 等），输出被调用的程序集内部类型，以及程序集内方法体调用了它的类型（反向）。与 `dependencies` 的签名级引用互补，反映执行流中的实际调用；跨程序集类型与编译器生成类型（闭包/状态机）自动过滤。元数据秒回，支持 `lines` 分页；CLI 对应新增 `-cg` 选项

### Changed

- **不再依赖 ilspycmd（破坏性变更）**：反编译改为进程内 [ICSharpCode.Decompiler](https://github.com/icsharpcode/ilspy)（内置版本 11.0.0.9335-rc，随 NuGet 包分发），安装 ilspymcp 后开箱即用，无需再全局安装 `ilspycmd`
- **`timeoutSeconds` 语义变为「放弃等待」（破坏性变更）**：超时/取消返回提示文本且结果不入缓存，可调大 `timeoutSeconds` 后重试；进程内无法强杀，后台反编译任务经协作式取消中断
- **标准输出截断从行数改为数据量**：默认返回前约 8 KB、`lines` 参数单次最多约 32 KB（`lines="start-end"` 行号分页不变）；头部「当前输出」增加返回量 KB 与截断原因，并新增「剩余」行告知剩余数据量与「可一次获取/需分次获取」的建议 `lines` 范围
- 握手报告（CLI `-c/--check` 与 MCP 握手注入）不再报告 ilspycmd 安装/版本，改为仅报告 ilspymcp 更新状态（无有效检查记录时不注入）
- `decompile_to_dir` 的 `nestedDirectories` 参数当前不产生效果（写盘为单文件布局，保留参数仅为向前兼容）
- 反编译超时/取消后，后台任务经协作式取消真正中断，不再继续占用 CPU（内部行为改进，无参数变化；`timeoutSeconds` 默认值与语义不变）
- 工具描述与握手报告精简冗余措辞（移除「纯元数据」「内置」「结果缓存在内存」等实现细节表述），无功能变化

## [1.1.4] - 2026-08-11

### Added

- `signature`/`hierarchy`/`dependencies` 新增 `lines` 分页参数：结果超过 200 行时可按行号范围拉取后续（格式 `start-end`，单次最多 500 行），CLI 对应 `-s/-hc/-d` 同步支持 `-ln`

### Fixed

- `signature`/`hierarchy`/`dependencies` 超过 200 行被截断后无法续读（此前未暴露 `lines` 参数，截断即不可达），现可用 `lines` 分页拉取完整结果
- `decompile_member` 匹配数超限（>20）时返回的成员签名清单超过 200 行同样无法续读，现超限清单也支持 `lines` 分页

## [1.1.3] - 2026-08-11

### Added

- 新增元数据层结构查询组件与工具（纯 PEReader 秒回、无需 ilspycmd 安装）：
  - `signature`：输出类型全部成员（字段/方法/属性/事件）每成员一行 C# 签名，作 API 地图
  - `hierarchy`：输出基类链（上溯到 System.Object）/实现的接口/程序集内直接继承或实现它的类型
  - `dependencies`：输出成员签名（方法参数/返回、字段/属性/事件类型）引用的程序集内部类型及反向引用（不做 IL 方法体扫描）
  - `decompile_to_project`：以可编译项目形式反编译整个程序集写盘（从 decompile_to_dir 拆出）
- `decompile_member` 合并输出各成员前插入 `=== 名字 (token) ===` 分隔行，消除「散落方法体片段」无法归属的问题；匹配数超过上限（20）时仅返回成员签名清单、不启动子进程；无匹配时返回相近成员名提示；默认排除属性/事件访问器
- 输出含 `//IL_` 未解析注释时，头部信息块追加「动态类型/异常路径，仅供结构参考」提示，避免 agent 误当源码
- `list_types` 改用元数据层实现：默认过滤编译器生成类型（`<Module>`、async 状态机、lambda 显示类等），不再依赖 ilspycmd 安装

### Changed

- **工具参数瘦身（破坏性变更）**：全部工具移除 `languageVersion` 参数；`decompile_to_dir` 移除 `project` 参数（改由 `decompile_to_project` 承接）；`list_types` 移除 `timeoutSeconds`（纯元数据秒回）
- `list_types` 输出不再包含编译器生成类型（行为变化）
- `decompile_member` 默认不再返回属性/事件访问器（行为变化）
- 元数据工具（list_types/signature/hierarchy/dependencies）不再要求 ilspycmd 已安装
- 类型全名统一与 ilspycmd `-l` 输出对齐：命名空间.类型、嵌套用 `+`、泛型带 arity（如 `GenericBox\`1`），`-t` 同时接受 `+` 与 `.` 嵌套分隔符
- CLI 新增 `-s/--signatures`、`-hc/--hierarchy`、`-d/--dependencies`；移除 `-lv/--languageversion`；`-p` 改走 decompile_to_project

### Fixed

- `hierarchy` 修复泛型基类/泛型接口（TypeSpecification 实例化）丢失：泛型类型现可正确输出基类链（泛型定义在程序集内时继续上溯）、实现的泛型接口与程序集内实现者
- `signature` 修复多项渲染错误：接口实现方法不再误渲染 `sealed`（编译器将隐式接口实现标为 sealed virtual newslot，源码为普通方法）；静态属性补齐 `static`；泛型类构造函数名去掉 arity（`SimpleClient`1<T>` → `SimpleClient<T>`）；索引器渲染为 `this[参数]` 而非 `Item`
- `signature`/`decompile_member` 不再重复渲染显式接口属性/事件访问器（方法名为 `Ns.IFoo.get_Value` 时此前既当方法行又当属性行重复输出）
- `list_types` 修复嵌套编译器生成类型漏网：`<PrivateImplementationDetails>` 的嵌套 `__StaticArrayInitTypeSize` 短名不含 `<`，此前会混入输出；现按全名（含嵌套外层链）判定

## [1.1.2] - 2026-08-11

### Added

- 环境自检新增 NuGet 更新检查：检查 ilspymcp 是否有新版本，结果落盘跨进程共享（成功 TTL 24h、失败 1h 退避、失败保留旧值），避免每次会话都联网复查
- MCP 握手期经 `ServerInstructions` 注入完整环境自检报告（ilspycmd 安装/版本 + NuGet 更新状态），agent 会话起始即可感知环境；握手后台异步刷新 NuGet 磁盘缓存供下次会话

### Changed

- **check_status 不再暴露为 MCP 工具**（破坏性变更）：环境自检报告改由握手期注入 `ServerInstructions`，agent 无需手动调用；`CheckStatus` 保留供 CLI `-c/--check` 调试
- CLI `-c/--check` 调用前先刷新 NuGet 缓存（TTL/退避内不联网），无缓存记录时 NuGet 段不再永远留白

### Fixed

- 握手期 `StatusReport` 环境自检异常不再阻断 MCP 启动：降级为空注入提示，核心反编译功能不受影响

## [1.1.1] - 2026-08-10

### Changed

- decompile_to_dir 的 `nestedDirectories` 默认改为 `true`（省略即按命名空间嵌套目录输出）
- 全部工具参数描述补齐默认值说明（`lines` 缺省返回前 200 行、`project` 默认 false、`nestedDirectories` 默认 true、`languageVersion` 省略使用 ilspycmd 默认），消除 agent 对默认行为的盲区
- decompile_to_dir 的 `typeName` 参数描述注明「仅 project=false 时生效；project=true 时项目模式忽略并全量输出」，消除描述与实现不符（ilspycmd 项目模式会静默忽略 `-t`）

## [1.1.0] - 2026-08-10

### Added

- 新增 check_status 工具（CLI 同步 `-c/--check`）：环境自检——ilspycmd 是否安装、版本是否满足要求（>= 11.0，-m 单成员反编译所需）、当前 ilspymcp 是否有新版本；结果会话内缓存（仅首次真实检查），NuGet 网络失败/超时静默跳过该检查项
- MCP 工具（decompile / decompile_member / list_types / decompile_to_dir）调用支持取消：客户端取消一次调用时立即终止 ilspycmd 子进程，避免孤儿进程占用资源（内部逐层传递 CancellationToken）
- MCP 工具（decompile / list_types）输出前置头部信息块（程序集/目标/内容），明确代码归属与当前切片位置，缓存命中时同样携带
- decompile_to_dir 成功提示并入来源程序集

### Fixed

- 移除头部信息块的「参数」行：agent 面对的是 MCP 命名参数，ilspycmd 内部命令行参数（如 `-m token`、`-t`、`-l`）对 agent 无意义且会误导
- 修复 list_types 空结果（如列出不存在的实体类别）静默无提示的问题
- Program.cs 增加 MCP 装配期异常兜底（启动失败时 stderr 中文提示 + 非零退出码，不再暴露崩溃堆栈）
