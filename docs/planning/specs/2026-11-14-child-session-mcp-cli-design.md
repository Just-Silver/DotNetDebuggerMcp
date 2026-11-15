<!-- 职责：定义子会话正式接入 MCP/CLI 的首版范围、外部契约、进程边界和验收规则，面向实施者；不写：历史试验结果全文、逐任务实施计划或任意应用兼容性承诺。 -->

# 子会话 MCP / CLI 正式接入设计

日期：2026-11-14。批准时状态：**维护者已批准本设计正文；尚未实施。** 对应[实施计划](../plans/2026-11-14-child-session-mcp-cli.md)；当前执行进展与换设备接续见[验证记录](../research/11-child-session-integration-results.md#5-换设备接续背景2026-11-15按维护者要求暂停本机新验收)。

## 1. 已确认的目标

维护者确认使用 Windows RDP Child Session，并同意“父侧 MCP/CLI → 子会话执行进程”的接入方式。首版覆盖创建/查询/停止子会话、启动程序或脚本、UIA、真实键鼠及截图；MCP 与普通 CLI 共用同一执行实例。

目的：补齐现有 UIA 无法完成的 GUI 交互，同时不干扰用户主会话的鼠标、键盘与前台窗口。客户端保持连接、非最小化且失焦即可；不要求隐藏、最小化、断开或锁屏后仍可工作。

现有入口与默认行为保持兼容，调用方显式选择子会话。失败不得回退主会话输入。技术路线不再选型、不重复开展通用人工循环。

历史机制和实机证据见 [子会话研究](../research/08-rdp-child-session-spike.md)；旧[验证设计](2026-11-14-native-gui-input-isolation-poc-design.md)保留历史背景。本文件是正式接入的设计依据，不将验证夹具直接包装成产品。

## 2. 接入方式与取舍

| 方式 | 取舍 | 决定 |
|---|---|---|
| 父侧保持现有 MCP/CLI，按实例转发 GUI 请求到 child worker | 现有反编译与调试不迁移；需补生命周期、IPC 和 GUI 执行分离 | **维护者已选择** |
| 子会话内运行完整 MCP 服务，父侧代理完整协议 | 可整体复用工具面，但引入第二套服务/调试状态、协议转发与恢复问题 | 不采用 |
| 把现有全部操作默认切到子会话 | 调用简单，但改变现有脚本语义，且反编译不需要隔离 | 不采用 |

以下具体名称、限额与组件划分为设计者提出的实现选择，不冒称维护者逐项指定。

## 3. 范围与非目标

### 3.1 首版包含

- 同一父交互会话至多一个本产品创建的活动实例；多个 MCP/CLI 客户端可显式复用该实例。
- 一次显式管理员设置；普通权限建立连接和投送 worker；必要时用户在连接窗口正常认证。
- 直接从原路径启动宿主已有 exe、SDK/脚本解释器和脚本，指定工作目录、参数及必要环境。
- 子会话内执行现有五项 UIA 能力、现有截图模式及新增真实鼠标/键盘操作。
- 任务查询、增量输出、取消和明确停止；独立 CLI 结束不销毁实例或运行中的应用。
- MCP 工具说明、按能力族握手、CLI 帮助、README、CHANGELOG 与打包验证。

### 3.2 明确不包含

- 第二套 Windows、系统 DLL 补丁、驱动、多席位后端、自动修改认证/证书/Hello 策略。
- 任意代码的安全沙箱、恶意脚本隔离、多用户服务、网络服务、账户密码收集。
- 原始窗口消息通用后端、应用注入式兼容层、多种隔离路线的插件框架。
- 自动重放 GUI 动作、断连后静默重建实例、崩溃后恢复原按键序列。
- 跨会话 Main 前 `debug_launch`；首版用启动返回的 PID 调现有 `debug_attach`。不得把 attach 成功宣传为 Main 前调试通过。
- 修改原有无 `instance` 调用的焦点/提交语义，或宣称全部既有 `ui_*` 都是无干扰操作。

## 4. 进程与代码边界

```text
父会话
  现有 MCP stdio ─┐
  普通 CLI ──────┴→ 子会话客户端 → 连接持有进程（STA，生命周期协调）
                                    │
                                    ├→ RDP ActiveX 连接窗口
                                    └→ 私有 IPC → child worker
                                                   ├→ 原路径程序 / 脚本任务
                                                   ├→ 既有 UIA / Capture 库
                                                   └→ 真实键鼠输入
  既有反编译、父侧 debug_attach 等不迁移
```

### 4.1 组件划分

| 位置 | 负责 / 不负责 |
|---|---|
| 新 `src/SharpSight.ChildSession/` 能力库 | WTS/ActiveX、实例身份与生命周期、IPC、任务进程控制、受护栏保护的真实输入；零宿主依赖，不实现 MCP，不依赖调试 Session 库 |
| 现有 `DotNetDebuggerMcp` 可执行文件 | 保留 MCP/CLI 入口；增加内部连接持有/worker/一次性设置角色，提前分派，内部角色不启动 MCP、不向 stdout 写协议外日志 |
| 宿主 `Services` 中 GUI 执行组件 | 从工具类提取可复用的校验/定位/执行与结果格式化；父侧和 child worker 共用，服务不得反向调用 Tools |
| 宿主 MCP 工具与 child CLI | 参数绑定、调用上述具名用例、输出；不各自实现一套启动/清理流程 |
| 既有 UIA/Capture 库 | 复用现有实现，不复制代码，不公开所有内部类型；宿主 worker 仍通过宿主已有访问权限调用 UIA |

连接持有进程与 child worker 使用同一个安装产物的内部启动角色，避免额外 exe 的寻找/分发问题；能力库可启用 WinForms 以承载 ActiveX。DLL 在同一工具包中分发，必须实测 pack→本地 tool install→运行，不能只验证开发目录。

内部角色只接受经身份验证的实例上下文，不提供绕开前置检查的任意 SessionId 投送参数。默认反编译/MCP 启动路径不创建窗口，也不初始化 ActiveX。

按仓库既有组装方式显式构造、持有和释放对象，不为本功能引入 DI 容器或给每个类建接口。原生进程/IPC/时钟等真正外部边界可替换；连接、任务和请求分别拥有自己的状态，不放入万能静态 Manager。

## 5. 生命周期、权限与所有权

### 5.1 设置与建立

1. `probe` 只读返回开关、父会话、已有 child、ActiveX 和当前实例状态，不读取密码。
2. 功能关闭时 `start` 返回“需要显式启用”及 CLI 指引；**不自动触发 UAC**。CLI `child enable` / `child disable` 才能请求管理员授权，取消即失败，不改其他策略。MCP 不收集凭据。
3. `start` 通过当前用户 SID + 父 SessionId 范围的互斥协调并发。已有健康的本产品实例就返回其身份；已有非本产品 child 则拒绝，不接管或注销。
4. 创建普通权限 STA 持有器，连接窗口不主动激活、不置顶、不最小化。连接中/待认证可查询；用户需要认证时手动进入窗口，执行期间不强行把它带到父前台。
5. 连接、登录、WTS child 身份均确认后，以普通权限交互令牌计划任务投送 worker。任务名称唯一；握手成功或启动失败后删除本次任务，失败时保留错误与明确清理状态。
6. worker 回报并交叉核对 SID、父/child ID、PID+创建时间、映像及实例随机凭据。worker 可用后才标记 Ready；仅窗口存在或任务提交不能算就绪。

`instance` 是随机且不可复用的执行代际标识，不是数字 SessionId。持有器/worker 重启不沿用旧实例；首版不自动重启，需显式 stop 后 start。一次 CLI/MCP 退出只断开它自己的客户端。

`child disable` 只改系统开关，不隐含 stop。只要当前父会话存在任何 WTS child（本产品或外部），或本产品仍处于建立/待认证/停止中，就拒绝 disable；先显式 stop 自有实例，外部 child 由其所有者处理。检查与设置在本产品的生命周期互斥区内执行，提升权限后的设置角色在调用系统 API 前再次检查，避免利用旧快照绕过限制；仍不能宣称与其他产品的并发系统调用有原子性保证。已关闭且无活动 child 时幂等返回。

### 5.2 状态与停止

状态至少区分：Starting、AwaitingAuthentication、Ready、Disconnected、Faulted、Stopping、Stopped；状态输出带原因、实际父/child ID、持有器/worker 身份和创建时间。等待上限到达返回当前状态，不谎报 Ready。

`stop` 是显式注销本实例 child 的操作，会结束其中全部应用，**包括用户手动在该 child 打开的应用**；工具说明必须写明。不自动关闭全局 child-session 开关，也不影响父会话。

停止先禁止新动作、取消排队操作、释放本轮已按下状态、取消本实例任务，再核对当前 WTS child 与登记的身份，异步请求注销并有界等待，最后关闭匹配持有器。worker 不在线也可根据持有器及 WTS 的有效所有权证据停止；身份无法确认时只报告人工处理，不猜测注销。

同一 MCP 仍附加本实例目标进行调试时拒绝 stop，提示先 disconnect；外部调试器状态不承诺可检测。注销请求已提交而未确认完成时返回“停止中/结果未确认”，后续查 status，不把部分清理当成从未执行。

手动关闭连接窗不算 stop：立即禁止新 GUI 动作，保留 Disconnected 状态和显式清理入口。父会话注销后不承诺继续运行。

## 6. MCP 外部契约

所有新工具遵守仓库约定：参数有默认值、中文 Description、CancellationToken；错误为中文文本，不抛 Tool Error。下表未列通用取消令牌。

### 6.1 新增工具

| 工具 | 参数与默认值 | 语义 |
|---|---|---|
| `child_session` | `action="status"`、`instance=""`、`requestId=""`、`cursor=0`、`limit=20`、`timeoutSeconds=30` | action=probe/start/status/stop/requests/result；start 返回实例；status 空实例列当前父会话的本产品实例；stop/requests 必须给实例；result 必须给实例和 requestId，查询同一请求，不执行新 GUI 动作 |
| `child_process` | `action="status"`、`instance=""`、`taskId=""`、`executable=""`、`arguments="[]"`、`workingDirectory=""`、`environment="{}"`、`cursor=0`、`limit=8192`、`timeoutSeconds=5` | action=start/list/status/output/cancel；start 必须有实例和 executable；list 必须有实例，列该实例任务；status/output/cancel 必须有实例和 taskId |
| `child_pointer` | `instance=""`、`processId=0`、`hwnd=""`、`action=""`、`x=0`、`y=0`、`endX=0`、`endY=0`、`button="left"`、`delta=0`、`durationMs=300`、`observationId=""`、`timeoutSeconds=5` | action=move/click/doubleclick/drag/scroll；目标 PID 必填；hwnd 空时要求唯一可选顶层窗口；参数不适用时拒绝，非静默忽略 |
| `child_keyboard` | `instance=""`、`processId=0`、`hwnd=""`、`action=""`、`text=""`、`keys=""`、`observationId=""`、`timeoutSeconds=5` | action=text/key/chord；text 用 Unicode 系统输入，key/chord 用键码；不跨调用保留按下状态 |

`start` 默认 30 秒等待实例；认证未完成时返回实例和状态，后续 status 继续查询。stop 等待同样有上限；超时仅放弃等待，不把注销反向撤销。process start 只等待注册启动结果，不等应用退出；timeout 不充当进程运行时限。

`child_session(action=result)` 的 timeoutSeconds 为等候该请求结束的上限，默认 30、范围 0–300，0=立即查询；其他 action 使用 1–300。返回原请求操作、queued/running/completed/cancelled/not-executed/unknown、最终文本结果或产物路径、错误及时间。截图结果查询只回已保存的 PNG 路径/元数据，不重新截图，也不改变本工具的 Task<string> 契约。

`child_session(action=requests)` 不要求预先知道 requestId，列出本实例保留的请求摘要：序号、requestId、操作、目标 PID/taskId（适用时）、受理时间及状态，不包含输入文本、脚本参数或结果正文。cursor 为上次返回的单调序号（0=从当前最早保留记录开始），limit 默认 20、范围 1–100，返回 nextCursor 和是否还有记录；按文本预算提前截断时仍给可继续的 nextCursor。只有真正的 GUI 操作与进程变更请求入登记，status/list/output/requests/result 等控制查询不挤占记录。MCP 调用取消后没拿到原响应时，可由此发现请求再调用 result；只有实际被持有器接受的请求会出现，未找到不能反推未执行（也可能已淘汰/实例失效）。

### 6.2 扩展既有工具

- `ui_find/ui_action/ui_input/ui_get/ui_wait` 与 `screenshot` 增 `instance=""`。空值走原路径，非空经协调进程转发；路由判断在任何本地定位/焦点/输入动作之前完成。
- 子会话模式不使用父侧活动调试进程作缺省目标；UIA 必须显式 process，元素截图必须显式 processId。
- 原有参数含义保持；子会话目标解析限定实际 child Session，不让进程名匹配跑到其他会话。向既有定位器传已核验的 PID。
- screenshot 所有模式，包括 list-windows/list-displays/foreground，都在选定环境执行；绝不返回父侧清单冒充 child 清单。
- UIA 和截图返回头部标注实例、child SessionId、目标 PID+创建时间（若有）与原有帧/来源等信息。
- 新增真实输入工具**没有主会话模式**，缺实例即拒绝。不扩展原 `ui_action` 去默默采用新真实输入后端。

### 6.3 能力族与可发现性

新增 `child` 能力族，管理上述四个新工具；遵循现有“未指定 features 则全族启用”的规则，但注册不意味着启动子会话。

UIA/screenshot 仍归各自族。只有 `child` 与对应族都启用时，其 instance 路由可用；否则非空 instance 返回“子会话能力未启用”，绝不当空值处理。CLI 不受 MCP 工具注册开关限制，与既有 CLI 规则一致。

**共享实例的执行能力不取决于创建者。** 持有器/worker 固定支持本设计完整的 GUI 操作白名单；MCP 的 features 是每个 MCP 入口的可见性/调用门控，而不是实例权限。各 MCP 在发送前核对本入口启用族，不暴露任意转发工具；CLI 可调用完整白名单。同一实例不因另一客户端启用更多族而重启或扩权。worker 始终验证对端身份及固定操作白名单，不把某个 MCP 的 features 冒充同账户跨进程安全边界。

请求记录保存提交操作所需能力族；MCP 的 result 查询同样核对本入口具备该族（UIA=child+ui，截图=child+screenshot），否则只提示未启用，不借结果查询读到被禁用能力的内容。requests 只列本入口允许查看的操作摘要；CLI 无此 MCP 门控。

握手按实际启用族说明创建实例、原路径启动、截图/真实输入及 stop 的触发条件；不启用某族时不得提及该族工具或工作流。Description、握手、README 同批更新。

## 7. CLI 与脚本

保留现有 `-a/-dbg/-c` 等；新增明确的 `child` 子命令，不把反编译参数集继续堆大。

```text
DotNetDebuggerMcp child enable
DotNetDebuggerMcp child disable
DotNetDebuggerMcp child call <operation> --json <参数对象>
```

operation 为明确白名单：`child_session/child_process/child_pointer/child_keyboard/ui_find/ui_action/ui_input/ui_get/ui_wait/screenshot`。`--json` 是与 MCP 同名的参数对象；另支持 `--json-file <路径>`，二者互斥，便于长文本和避免 shell 转义。不得通过反射调用任意方法。

CLI 直接调用共享用例，不连接或启动 MCP 服务；同一实例可由前一条 CLI 创建、后一条 CLI 操作、MCP 查询。CLI call 的 UIA/截图也必须给 instance，操作主会话继续用原 MCP 入口，避免 `child` 命令名与实际执行环境相反。

CLI stdout 为单个 JSON 结果，包含成功/当前状态、实例、taskId、PID、消息及产物；stderr 为诊断；已确认失败退出 1，接受任务/当前状态正常返回退出 0。响应未知标明 `unknown`，不得作为已完成成功。screenshot 在 CLI 落 PNG 并输出元数据/路径，不向终端直接打印图片或 base64。

脚本通过 child_process 启动指定解释器（如现有 pwsh）并传数组参数；不自动拼接 shell，不安装解释器，不承诺解释器变量跨任务持久化。

## 8. 任务、输出与取消

- executable 相对路径及显式 workingDirectory 在调用端以其 CWD 转绝对路径；缺省工作目录为 executable 所在目录。裸命令名按调用端 PATH 解析，不因调用端不在 child 而擅自更换解释器。参数为 JSON 字符串数组，环境为 JSON 字符串字典，只覆盖显式提供项。
- worker 启动任务前再次验证路径与归属，以普通权限在自身 child 启动；绝不把一次任务退化为父侧 shell 执行。脚本共享宿主文件、注册表和用户权限，不是安全隔离。
- 任务获得随机 taskId，记录 PID+创建时间、会话、开始/结束、退出码和输出路径。GUI 程序可以长期运行；正常 start 不等待 GUI 退出。
- 启动即接管自有进程树的取消边界，避免子进程先跑后才登记；以 Windows Job Object 管理本任务可控制的进程树。单实例/broker 把请求转给既有进程时如实报告，不把对方登记为自有任务。
- list 返回任务 ID、状态、受控进程是否仍存活及退出码，不要求保存创建任务的那一个客户端；终态记录保留至实例停止，最多 256 个任务，满额拒绝新任务而非悄悄丢弃取消/输出句柄。
- 单实例/broker 转交通常无法通用检测：启动器退出就报告它及其 Job 的真实状态，并明确“这不证明可见应用已退出”。只有有证据时才标记转交，不能猜测外部进程属于任务；cancel 在 Job 已无进程时返回无自有进程可取消，不声称已关闭该应用。其他 child 应用可显式选择进行 GUI 操作；要结束整个 child 则使用有整体影响说明的 stop。
- stdout/stderr 持续排空，保存为独立流；每任务总日志上限 16 MiB，达到后继续排空并丢弃新增内容，标注截断。每实例任务日志总配额 256 MiB，超额拒绝新任务，不静默覆盖旧报告。
- output 的 cursor 是聚合输出文件的字节位置，返回 nextCursor；默认最多约 8 KiB，最大 32 KiB，在完整 UTF-8 字符边界切分。每条记录标出 stdout/stderr，不承诺两条流之间的绝对先后。
- 状态查询与取消不排在长 GUI 操作之后。cancel 只结束该 task 的自有 Job，已退出时幂等返回；不按进程名杀进程，也不注销整个 child。worker 异常退出时 Job 关闭结束其自有任务，实例失效。
- CLI 退出/MCP 请求取消/超时表示放弃等待，不自动取消进程任务；终止任务须显式 cancel。GUI 请求的取消则要求尽快中止后续事件并释放本轮按下状态，已经发生的业务副作用不回滚。

## 9. 真实输入、坐标与引用护栏

### 9.1 目标与输入序列

每次真实动作检查实例/worker 代际、child 连接态、当前进程实际 Session、可用 input desktop、目标 PID+创建时间及 HWND 所属关系。只在 child 内还原并激活目标，等待实际前台确认；不调用父侧前台/光标 API 帮助完成。

不带 observationId 时，调用者表达的是“操作此刻该 PID 对应的进程”，worker 在受理时打开进程并记录创建时间，以此为本次操作不可变化的基准；这能防止操作期间 PID 复用，**不能证明该 PID 仍是调用者早先看到的进程**。需要与先前观察绑定时必须传 observationId；工具说明与结果须区分这两种保证。

每个 SendInput 事件前复核目标前台和连接状态；中途变化就停止后续事件。释放本轮已提交的按下状态是唯一可在目标失焦后继续提交的输入，且仍须确认原 child/input desktop 有效。不得恢复或释放并非本轮按下的其他键。

鼠标/键盘动作是完整有界序列，没有对外暴露跨请求 mouse-down/key-down。drag 是 down→分段移动→up，chord 是按顺序压下、逆序释放；durationMs 范围 50–5000。输入序列在实例内串行；UIA 变更也参与同一 GUI 队列，避免 commit、focus、真实输入互相干扰。具体队列与等待规则见 §10.1。

键盘 key/chord 支持明确文法：Ctrl/Alt/Shift/Win 修饰键、A–Z、0–9、F1–F24、Enter/Tab/Escape/Space/Backspace/Delete/Insert/Home/End/PageUp/PageDown/Up/Down/Left/Right。chord 为 `Ctrl+K` 形式且恰有一个非修饰键；不支持名字返回合法提示。text 为 Unicode 输入，不自动追加 Enter、不关闭 IME；IME 提交由调用方显式发送 Enter。输入成功只说明事件已提交，不冒称业务已经消费，应通过 UIA/截图确认结果。

### 9.2 坐标与观察引用

- pointer 的 x/y/endX/endY 固定为 **child 虚拟屏物理像素坐标**，不是父侧连接窗坐标，也不是客户区坐标；允许负原点。截图像素点换算为 `origin + 图内像素`，保持原生 1:1。
- click/drag/move 的指定点必须属于目标窗口当前有效区域；其他窗口遮挡或命中对象不符就拒绝，不能为了到达坐标点击到别的应用。scroll 在 x/y 处执行，delta 为滚轮单位，可正可负；不支持的 button/action 组合拒绝。
- 保留既有 UIA frameId（0=不校验）的局部语义；它不跨实例/worker 使用，worker 重启后旧 instance 即失效。worker 内沿用单个 UIA 帧登记：ui_find 与成功的 element 截图推进，普通 screen/window/region/display 截图不推进。ui_find B 会替换 ui_find A 的 index 缓存，旧 A 的 frameId 被拒绝，不新增多目标 UIA 缓存平台。
- 子会话 UIA/截图另返回不透明 `observationId`，绑定 instance、worker 代际、目标 PID+创建时间、HWND（适用时）及采集时几何；屏幕截图绑定显示布局。UIA 清单/元素截图额外绑定局部 frameId。`ui_action/ui_input/ui_get/screenshot` 增 `observationId=""`，仅子会话模式非空可用，父侧非空明确拒绝。
- observation 保留最近 256 条，不是新截图覆盖全局唯一槽。普通窗口/屏幕截图不废弃先前 UIA 清单，所以 ui_find A→普通窗口截图→按原清单操作 A 可用；新 ui_find/element 截图使旧 UIA 清单引用失效。视觉引用不因无关窗口采集而失效，但目标创建时间/HWND/几何/显示布局不符或记录淘汰时拒绝。引用只保证来源与可检查身份/几何，不保证全部像素内容自采集后未变。
- frameId 与 observationId 同时非空时必须两者都有效且同源，不能让后者覆盖前者。index 定位只能消费 UIA 清单绑定的引用；纯屏幕/窗口视觉引用不能充当 index 清单。pointer/keyboard 可消费相应目标或屏幕来源的视觉引用，不能从另一个进程的窗口引用跨目标操作。
- pointer/keyboard 的 observationId 可省略，但省略后不保证坐标来自新画面，工具说明必须写明；实例/会话/目标/前台检查始终执行。
- 帧号和引用检查必须与实际 GUI 执行在同一队列内完成，避免检查后被另一个请求换掉缓存。

## 10. IPC、产物与故障语义

- 控制管道限定当前用户，实例目录仅当前用户/必要系统主体可访问，放 `%LOCALAPPDATA%\DotNetDebuggerMcp\child\<instance>`；不能依赖随机管道名本身作授权。
- 客户端↔持有器、持有器↔worker 均验证实际对端 PID/创建时间/SID/Session、协议版本、实例随机凭据。随机凭据不出现在公开结果、普通日志或命令行；通过私有实例目录完成引导。不宣称能防同用户恶意程序。
- IPC 使用有长度上限的 JSON 信封，含版本、requestId、instance、操作与 payload；单次请求/文本响应上限 1 MiB。明确允许的操作列表，不实现任意 C# 求值、任意类型反序列化或通用反射 RPC。
- GUI 调度与超时规则见 §10.1；控制通道保留独立读状态/取消能力，不因上层等待超时重复执行。
- requestId 由调用端发送前随机生成，所有子会话 GUI/进程请求结果均返回它；持有器收到后有界登记接收/开始/完成状态，保留当前实例最近 256 条。调用端失联或超时也能返回其原先生成的 requestId，按 child_session(action=result) 查询；记录不存在/淘汰时明确 unknown，不声称未执行，也不重执行。真正的进程崩溃不能获得 exactly-once 保证，禁止静默重试变更。
- 截图由 worker 在私有实例产物目录落 PNG，返回相对产物标识和来源/尺寸/原点等元数据；持有器/父侧验证产物属于该实例后读取。MCP 小图按既有门槛附 image，大图/指定 filePath 按既有规则输出路径；指定外部 filePath 由调用端物化，worker 不接受任意截图写盘路径。
- 启动和产物元数据原子写入；状态文件只作定位线索，操作前仍核对活进程/WTS/握手。损坏或旧记录不触发自动删除其他会话。
- worker 错误、断连、截图失败、任务启动失败、取消和“已提交但结果未知”分别报告；日志走 stderr/实例日志，不能污染 MCP stdout。
- 不自动清除本实例产物；stop 后保留报告与日志，README 说明人工清理位置。配额仅约束任务日志，截图失败/磁盘不足必须如实返回，不以旧图替代。

### 10.1 GUI 队列、等待与超时

| 操作 | 调度方式 |
|---|---|
| ui_find/ui_get、一次截图（含清单）、ui_action/ui_input、一次完整 pointer/keyboard 序列 | 共用实例 GUI 执行门；引用检查、目标绑定、执行和采集结果登记在同一门内 |
| ui_wait | 订阅/退订及每次探测分别短暂取门，事件/轮询睡眠时放门；不独占最长 300 秒等待，不推进清单帧或替换 index 缓存 |
| child_session status/requests/result/stop、child_process list/status/output/cancel | 控制通道旁路；stop/cancel 可发协作式取消，不在阻塞 GUI 调用之后排队 |
| child_process start | 进程启动/任务登记独立串行，不等待 GUI 队列；启动程序造成的 child 前台变化由输入护栏检测，不为并发启动豁免 |

子会话模式需提取最小的 UIA 分段执行/真实完成跟踪接缝，复用既有定位/pattern/事件等待算法。现有 RunGateAsync/UiaBoundAsync 在超时后会先返回、内部 Task 可能继续执行，因此**不能仅在公共异步方法外套一把锁**；需在实际后台 Task 完成后才释放 child GUI 执行门，UIA 客户端也须存活到真实完成。未选择 instance 的原调用口径不因本次设计被改成等待整个后台任务。

UIA COM 阻塞没有可靠的任意中断保证。超时后仍运行则保持 GUI Busy、拒绝新的 GUI 调用（包括截图与探测），但允许控制查询/stop；旧调用真正结束后才解除 Busy，若永不返回则显式 stop/start，不自动重放。正常 ui_wait 在两次探测之间不造成 Busy。

子会话各工具的 timeoutSeconds 是含排队的单请求执行预算；pointer/keyboard 范围 1–30 秒，UIA 沿用 1–300 秒，screenshot 沿用 0–30 秒（0=立即尝试一次）。截止时：

- 尚未进入执行门的请求取消排队，返回 not-executed，保证以后不会再执行。
- 已开始的 pointer/keyboard 协作式停止后续事件、尝试释放本轮按下状态；返回已确认取消或 running/unknown，不保证回滚先前输入。
- 已开始的 UIA/截图放弃等待、登记实际后台生命周期；返回运行中/结果未知，后续 result 查询最终状态。ui_wait 到期仍按原约定返回当前条件未命中，不当成调用异常。

调用端传输等待上限为该执行预算加 5 秒协议收尾宽限，不能提前于已知执行预算超时；连接阶段最多等待 5 秒，不能连接且未发送时明确 not-executed。接入层先传播取消/截止信息，持有器负责原子决定 queued→not-executed 或 queued→running；传输失联且未收到确认时只能报 unknown。MCP 客户端取消可能不接收最终响应，可按 instance 调 requests 找回已受理请求的 requestId，再以 result 对账；CLI 超时/取消返回包含 requestId 的 JSON。CLI unknown 使用退出码 1，正常 queued/running 的任务接受回执及状态查询使用退出码 0，completed 以实际成功/失败决定。

## 11. 测试与验收

只测本次业务行为；不为 DTO 字段、枚举值、私有结构和默认值另建结构测试。复用既有用例，不平行重复。

### 11.1 自动化业务护栏

- 并发 start 不创建两个实例；已有外部 child 不被接管；失效 PID/代际与错误会话请求被拒绝。
- 空 instance 原工具保持原行为；指定 instance 时不在父侧执行定位、提交、键鼠或屏幕清单。
- 参数数组、含空格路径、显式工作目录/环境在真实任务中生效；输出持续排空、截断可见、取消只影响自有任务；零轮或未启动不得假成功。
- 旧帧/观察引用、目标退出或窗口/布局变化拒绝操作；前台变化中止序列，释放本轮状态，不重放。
- 请求超时与任务取消不同；未知结果明确可见；worker 退出、客户端断开及 stop 的清理边界可验证。
- MCP 取消导致原响应未送达时，另一客户端能按实例列请求、取得 requestId 并查回同一结果，不重放变更；记录淘汰或实例失效时明确无法确认。
- 按各族组合验证实际可调用性与 agent 可发现性；沿用既有握手和 MCP stdio 并发回归护栏，不只做常量清单断言。

### 11.2 本机集成验收

1. Release 构建、受影响套件及现有 Client/CLI 回归；pack→临时 tool install→MCP/CLI 调用通过，内部角色从安装路径可启动。
2. 同一实例由 CLI 创建、MCP 启动真实 WinForms/WPF 目标，再由另一 CLI 查询、UIA 写读、真实点击/拖拽/键盘、截图、任务取消与 stop；使用原路径 Debug 产物。
3. 客户端非最小化、主会话失焦且保持连接；不需要重复让维护者执行已认可的人工循环，以既有正面反馈为背景，正式接入仍检查宿主标记输入与前台副作用。
4. 至少三次真实建立→操作→停止，覆盖断连与 worker 异常；确认无本轮遗留进程、临时计划任务或错误注销。需要修改系统开关时先明确征得授权，结束按约定恢复，不偷偷重新启用。
5. 父侧现有 debug_attach 对启动 PID 完成断点/读栈/断开；该结果不扩大到 Main 前 launch。
6. 画面实际读取确认目标与新内容，不以非黑/文件存在/输入 API 返回值代替业务结果。

有硬件、COM、WinUI 等应用相关限制时单列，不将首版接入通过宣传为任意应用兼容。

## 12. 留给实施者的选择

可自行决定小类/方法名、原生互操作封装、JSON 记录类型、日志轮转文件格式、测试夹具最小扩展及具体分任务顺序；不得改变显式 instance、原入口兼容、所有权保护、不回退宿主、未知动作不重放和正常连接失焦即可的验收口径。

本工作属于跨进程功能接入，而非单个参数小改动。设计批准后单独编写实施计划；在此之前不创建产品工程、不安装依赖、不改现有工具代码。已有未提交研究/夹具改动保持原状，不夹带提交。

## 13. 2026-11-15 范围收敛：只验收已确认的 exe 入口

维护者要求先确认使用场景，不为无使用场景的兼容性增加功能。核对当前发布与配置后：NuGet以.NET tool安装，README的MCP示例调用`DotNetDebuggerMcp`命令，仓库配置指向开发exe；MCP与CLI均为同一exe按参数分发。**未确认用户以`dotnet DotNetDebuggerMcp.dll`直接启动本工具的实际场景。**

初次范围裁决曾拟删除DLL直启分支；维护者随后澄清，必要性由实现方依据实际情况判断，并未要求删除。复核现有实现后决定：正式三轮统一使用安装exe承载父侧MCP/CLI，内部holder/worker也从同一安装exe启动；不新增DLL部署承诺，不增加DLL专项实机验收。保留已有的最小dotnet程序集路径前缀分支、通用启动参数和轻量IPC回归：它们已存在，删除会牵动启动与测试，却没有已证实的产品收益。该保留决定不是发现了已确认的用户DLL部署场景。构建/打包所需的托管DLL及其哈希记录同样保留。

已确认仍须覆盖的边界是：开发/安装目录中的合法exe客户端可按实例复用，不将客户端映像完整路径硬绑定为持有器路径；实际PID/创建时间/SID/Session/Image核实、凭据、白名单及worker固定holder身份检查保持。此处不新增第二套MCP宿主或可任意转发的入口。

本节是后续范围裁决，不改写前文及旧验证记录所记载的原始设计、实际DLL启动实验与失败现场。后续实施与验收以本节为准。

## 14. 连接窗最小化时自动不激活还原（2026-11-15 已确认）

维护者提出：连接窗若最小化，自动检查并还原为非最小化。历史输入失败依据归[research/08 §6.4](../research/08-rdp-child-session-spike.md#64-外层客户端状态与-child-内目标状态)，不泛化为所有能力均失效。

维护者通过“按此实施”确认以下按需恢复边界；原§1的非最小化工作前提不变，新增的是GUI请求前从最小化状态恢复该前提，不承诺维持最小化时执行输入。

**方案比较与实现方建议：** 选择在GUI请求转发前按需还原；不选200ms监视循环或SizeChanged中立即还原，后者会与用户Win+D/最小化拉锯，且仍不能避免两次检查间竞态。不使用可能激活父侧窗口的`Form.WindowState=Normal`/`SW_RESTORE`。

- 仅针对本实例holder拥有的父侧连接窗，在`ui_*`、`screenshot`、`child_pointer`、`child_keyboard`操作前由原STA读取最小化状态；最小化才调用`ShowWindow(SW_SHOWNOACTIVATE=4)`，随后`IsIconic`确认已还原。已正常时不改变窗口状态。
- 原有Ready/实例归属/真实child编号/连接与WTS校验均保留；不激活、不置顶、不向父侧发送输入。`child_session`、`child_process`（含start）不因该规则弹出或还原连接窗。
- STA读取/还原须有界；已关窗、取消、无法还原或未确认时返回中文未执行，不向worker投送GUI操作、不回退父侧。超时后的已排队回调在变更前检查取消，不能迟到后再还原。
- 不自动重连，不承诺还原后立即保证所有图形栈已恢复，不修改child内目标窗口既有最小化拒绝/激活语义；进入worker前的窗口还原不允许重放原请求。
- 工具Description、按child族握手、README同步这一可见行为；能力库保持零宿主依赖。
- 自动回归从生产转发入口驱动：还原成功后实际到达worker、还原失败无业务副作用、控制调用不依赖还原。原生窗口边界可替换，其余使用真实Store/IPC。
- 实机恢复验证单列于正常三轮之外：仅最小化本实例连接窗，预置状态记录后发一次GUI请求，确认还原且父前台/光标不变、业务实际可用。故意最小化放在隔离采样开始之前，采样本身仍坚持非最小化；不放宽隔离判据隐藏瞬态。

WPF源码对最小化→Normal且ShowActivated=false使用4提供参考，但真实Windows上不激活恢复、ActiveX重新可交互及父侧无变化必须由本轮安装验收核实，不能拿API文档代替实测。
