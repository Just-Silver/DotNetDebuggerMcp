# Changelog

本文件记录 ILSpyMcp 各版本的变更，格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循[语义化版本](https://semver.org/lang/zh-CN/)。

版本号与 `src/ILSpyMcp/ILSpyMcp.csproj` 的 `<Version>` 保持一致；发布时 CI 从本文件提取当前版本对应段落作为 GitHub Release 正文（NuGet 包的 `PackageReleaseNotes` 只放指向该 Release 页的链接）。未发布的变更统一记录在 `[Unreleased]`，发布时再转为带日期的版本段落。

本文件面向包使用者（agent 与 CLI 用户），只记录使用者可见的变更（新功能、行为变化、破坏性变更、可感知的修复、默认值/参数描述变化）；内部重构、实现细节、测试改动等一律不记录，请查阅 git 提交历史。

## [Unreleased]

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
