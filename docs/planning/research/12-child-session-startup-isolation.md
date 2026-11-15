<!-- 职责：记录Child Session重复登录启动项的来源取证、官方机制、会话复用可行性与候选验证门槛，面向维护者；不写：已签收的生产设计、全局启动项修改方案或前一轮功能验收细节。 -->

# Child Session 登录启动项隔离研究

日期：2026-11-15。**状态：PoC-1失败并已清理（§9）；SessionInfo候选保留、不接入生产（§10）。最新只读研究转向保持连接、跨MCP/CLI复用（§15–17）：已有基础能力，但长期存活与重连尚未验收。未修改生产或用户启动配置，尚无满足全部隔离约束的已验证方案。**

## 1. 用户观察与约束

维护者发现每次创建child会重新运行Windows用户启动项，使父会话已有后台软件在child重复启动。目标是父Session的开机与登录启动完全正常，child尽量只运行自动化Runner、被测GUI及其必要系统组件。

不修改/禁用用户现有全局启动项，不要求额外用户，不临时删除注册表条目，不改全局/用户Shell配置或策略，不以登录后批量结束进程掩盖启动。现有GUI、UIA、SendInput、截图、IPC与所有权清理必须保持。先定位实际来源，再最小PoC；成功后才决定生产接入。

前一轮安装产物三轮验证归[正式接入验证记录§8](11-child-session-integration-results.md#8-最终安装产物三轮业务验收通过2026-11-15)，其通过不等于登录启动项隔离已通过。当前合并暂停，生产源码维持已验证版本。

### 已知具体现场（维护者反馈）

目前明确看到的是 **Steam 与 Clash Verge**，其他程序尚未确认。Clash Verge有单实例/防重开行为：child登录时，父Session已经运行的Clash Verge界面会显示一下。因此“child里没有持久Clash进程”不能作为“没有执行启动入口”的证明；还需要观察瞬时启动与可能的跨Session单实例通知。反馈当时尚未核对启动入口；后续独立证据见§4，不把未抓取的完整IPC因果链当作既定事实。

取证优先对应这两个软件的Run/Startup/登录任务配置及实际映像/参数。验证增加“父会话既有单实例软件不被唤起”的条件；不要求退出父侧软件、不禁用其启动项。在来源与候选机制未查明前，不为复现而反复创建默认桌面child再次触发父侧界面。

## 2. 当前实现已核实的落点

`RdpActiveXHost.ConnectChild`使用`ConnectToChildSession=true`连接localhost，配置显示尺寸与认证参数，未指定初始程序/替代Shell。登录完成与WTS身份核对后，`ChildWorkerLauncher`通过临时计划任务`RunEx`明确指定child Session投送worker。不能仅凭此代码推断每个重复后台应用来自Explorer、Run或登录任务。

## 3. 取证与验证门槛

1. 静态盘点只产生候选：注册表/Startup/计划任务存在不等于本次确实触发。需要结合实际child进程创建时间、Session、父进程与触发日志归因；服务代理启动时不能只看PPID。
2. 官方RDP初始程序接口的存在不等于Windows Child Session支持或实际采用；RemoteApp/RDS Server行为不能直接外推。记录接口/属性、适用范围、实际设置与效果。
3. “不启动Explorer”与“完全不执行登录启动项”不是同一结论；独立登录任务及输入法/图形必要组件需分开核实。
4. PoC须对照默认桌面与仅当前child的候选模式，不改主Session配置；记录实际启动进程及GUI/UIA/真实输入/PNG像素/IPC结果，再决定生产最小改动。失败或无法覆盖的启动源如实保留。

## 4. 本机来源盘点与独立核对

取证未提权、未启用审计、未运行新child。主会话于19:38只读复核以下入口，原文归本研究工作区`controller-source-check.json`；没有修改注册表、任务或应用配置。

| 程序 | 已确认的配置入口 | 已有触发相关记录 | 不能据此断定的部分 |
|---|---|---|---|
| Steam | `HKCU\Software\Microsoft\Windows\CurrentVersion\Run`，值名`Steam`，命令为`steam.exe -silent`（实际路径已只读核对）；未发现Steam Startup链接或登录任务 | Shell-Core 9707于19:12:17开始执行该命令，9708于19:12:19记录PID63368；Steam自身日志另在18:58:46、19:12:25出现`-silent`启动模式，与child6/10登录相隔约19/17秒 | Shell事件直接证明Shell执行命令，但事件未提供Session ID；`-silent`不是注册表专属标志，不能仅凭参数和时间证明唯一启动来源或退出因果 |
| Clash Verge | 计划任务`\Clash Verge`：用户登录触发、交互令牌、Parallel、无延迟，动作`clash-verge.exe`无参数；盘点未发现其Run/Startup入口 | 任务最近运行19:12:09、结果0，与child10登录相隔约1秒；应用日志在19:11:03.800、19:11:33.773、19:12:09.069均记录`Restoring the application window from a secondary instance`，随后记录“窗口激活成功”，与child8/9/10登录时间吻合 | 日志证明窗口恢复路径执行，支持用户观察；仍没有次级进程实际Session ID、完整通知链和退出寿命，不能由LastRun推得这些细节 |

主会话19:45另行读取`%APPDATA%\io.github.clash-verge-rev.clash-verge-rev\logs\latest.log`，当时129942字节，取得上述三组窗口恢复记录，原文归`controller-activation-shell-evidence.json`。这更正了较早库存报告“没有11-15 Clash日志”的判断，不删除旧采集原文。

父侧Clash进程拥有本机回环监听，且存在同用户共享的单实例通道配置，结合应用日志，与用户描述的界面显示现象一致；本轮未抓取第二实例及通知流量，尚不能重建完整跨Session链路，不发布通道token或应用敏感配置。无child常驻副本不代表启动项没执行。

现有日志缺口：Security进程创建事件不可读，TaskScheduler Operational未启用，未取得child登录时完整进程树。现有三轮证据只记录Runner/被测程序，不记录所有登录派生进程。因此后续最小PoC需要补瞬时/实际Session证据，不能用配置盘点代替动态结论；也不为取证改变全局审计设置。

两种入口须分开验证：即使候选模式确实跳过Explorer/Run，也不能因此宣称`\Clash Verge`登录任务被抑制。候选初始程序与计划任务触发的关系正在按官方资料核实，尚未批准或实施生产改动。

## 5. 最小PoC的取证要求（机制未定，尚未执行）

- 复用已有默认登录的日志作为基线，不为对照反复新建默认桌面child；实际候选模式先核实官方支持范围及设置是否被采用。
- 观察窗口应覆盖已见Steam延迟启动的17–19秒，而不只在Runner刚Ready时取一张快照；当前Clash任务无延迟，需在认证前准备观测，不能只看登录完成后的长期进程。
- 记录真实child Session、候选初始程序实际PID/映像、进程创建/父子关系；非管理员事件源不可用时明确降级与盲区，不擅自开启全局审计。
- Clash的验证同时看任务最近运行变化、瞬时启动与父侧既有窗口显示/前台事件；不采集键盘内容、不发送父侧输入。缺少第二进程记录时不能由“无长期副本”签收抑制成功。
- GUI兼容需在已核实的自有child目标上实际走UIA、真实键鼠/Unicode/提交、截图可解码与画面变化、IPC请求/取消/停止；必要系统图形/输入组件与不必要用户自启动应用分别列出，不追求虚假的“只有两个进程”。
- 比较PoC前后父会话Run/Startup/登录任务/Shell配置，确认未作全局变更；主会话软件与网络保持运行。不把“未改配置”夸写成已重新登录主会话的实测，后者本轮不应擅自进行。
- 候选配置若被忽略、任务仍触发或GUI退化，就记录失败/部分有效及覆盖缺口，不接入生产、不以批量杀进程或更改原启动项绕过。

## 6. 官方机制核对与边界

| 机制 | 核实结论 | 官方依据 |
|---|---|---|
| RDP初始程序 | `IMsTscSecuredSettings.StartProgram`指定连接后运行的程序；未设时读取会话用户Shell。`WorkDir`指定工作目录，须在连接前设置。这是本次连接的内存属性，不要求改全局Winlogon | [StartProgram](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscsecuredsettings-startprogram)、[WorkDir](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscsecuredsettings-workdir)、[接口设置时机](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientsecuredsettings-interface) |
| Child Session支持范围 | 官方说明其为绑定父会话的特殊本地回环RDP会话，但没有承诺或拒绝`StartProgram`。接口存在、属性写入成功、普通RDS支持都不证明child实际采用 | [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions) |
| 登录计划任务 | `LogonTrigger`按用户登录触发，区别于Explorer启动项；文档没有给当前child连接临时抑制现有任务的开关。预计仅替换Shell仍有覆盖缺口，**尚未在替代Shell下实测，不写成已证必然失败** | [LogonTrigger](https://learn.microsoft.com/en-us/windows/win32/taskschd/logontrigger) |
| Shell Launcher | 官方明确在自定义Shell前处理Run/RunOnce，且配置面按用户/设备，不是本需求的临时会话隔离器；“没有Explorer”本身不足以证明不处理Run | [Shell Launcher](https://learn.microsoft.com/en-us/windows-hardware/customize/desktop/unattend/microsoft-windows-embedded-shelllauncher) |
| 微软PiP产品的限制 | Power Automate明确采用Child Sessions，并将同用户启动应用会在child再开、可能冲突列为已知限制。它建议取消应用自启，但违反本任务约束，不采纳；产品限制也不是所有Windows候选机制均无解的证明 | [PiP：Limitations of child session mode](https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-pip#limitations-of-child-session-mode) |

本次官方文档与公开实现检索**未找到满足全部约束、经验证可按Session抑制所有既有启动源的机制**；不等同于“已证明不存在”。RDP初始程序值得做一次有界实验，但即使减少Run类启动，也不能签收完整解决Steam和Clash。无Explorer时GUI渲染、前台、真实输入、UIA、WGC、输入法及Shell退出语义仍须实测。

## 7. PoC-0：不连接的ActiveX属性探针（已执行）

2026-11-15 19:51:23–24，未提权，在父Session1的STA中运行一次独立丢弃式探针；构建0警告/0错误，执行退出0。未改`src/`、解决方案或正式测试；未调用连接、认证、窗口显示或输入操作。

探针步骤：

1. 创建隐藏WinForms父窗与和生产相同CLSID的AxHost（`A0C63C30-F08D-4AB4-907C-34905D770C7D`），仅创建句柄，不调用`Show`或`Connect`。
2. 核对`SecuredSettingsEnabled`，将`ConnectToChildSession=true`写入并读回。
3. 经`SecuredSettings`把`StartProgram`写为探针自身exe路径、`WorkDir`写为探针输出目录，分别读回比较；这里只是属性字符串，**没有执行该初始程序**。
4. Dispose控件及父窗；前后读取WTS child、Connected与前台句柄，退出后核对探针进程及Clash任务最近运行时间。

| 观测 | 结果 |
|---|---|
| `SecuredSettingsEnabled` / child标志读回 | true / true |
| `StartProgram` / `WorkDir` | 两项写读完全一致 |
| 控件Connected / 父窗Visible | 前后均0 / false |
| WTS child | 前后均error1168（未找到），未出现child编号 |
| 父侧前台句柄 | 两端相同；仅端点观察，不宣称全程无瞬态 |
| Clash任务最近运行时间 | 仍为§4记录的值，未推进 |
| 探针进程 | 已退出 |

原始记录：本研究忽略工作区`poc0/`、`poc0-result.json`、`poc0-controller-verification.json`，不承诺这些临时文件随源码分发。探针DLL SHA256=`1D63149BEF7156B1E9FD454A251B3F62BA510238394135080A755BC03124C48E`。

**仅证实：当前桌面AxHost可在连接前写入这些属性。未证实：child采用初始程序、替代Explorer、Run被跳过或登录任务被抑制。**

## 8. 候选登录的受控边界（当次实验约定）

只做一次无参数探针exe作为初始程序的候选连接，不再重跑默认桌面基线，不以worker直接充当Shell，不修改用户启动项。认证仍由维护者在系统连接窗口完成。登录前准备瞬时进程/Session观测、Clash任务与应用日志端点、父侧窗口显示事件；登录后观察至少30秒以覆盖已见Steam延迟。

任何轮询均可能漏掉短命进程；若事件订阅权限不足或证据不全，保留盲区，不把“没有抓到”写成“从未启动”。初始程序未采用、任务仍推进或父侧窗口被唤起均为有效结果，不能为了保住假设重解释为成功。任务不推进也须记录并检查触发条件，不能预先排除候选有效的可能。

**风险：该次登录仍可能再次唤起父侧Clash窗口或重复启动Steam；不能承诺零扰动。** 结束只核对并注销本次自有child，保留原系统开关，不碰父侧应用。先裁决初始程序是否实际生效与启动项覆盖，再决定是否值得进行GUI/UIA/键鼠/截图/IPC兼容性验证；本阶段不接入生产或恢复合并。

维护者在获知上述风险后明确回复“可以开始”，批准这一次候选实验。独立丢弃式宿主与无参数`ShellAnchor.exe`只放忽略工作区，不改生产；与既有连接参数相比增加初始程序/工作目录，并关闭控件自动重连以保证不自动重复登录。窗口出现后只由维护者认证，不注入凭据或更改认证策略。初始程序出现后观察40秒；未出现则在登录事件确认后观察60秒，再请求注销本次登记且实时匹配的child；整体上限240秒，编号变化或所有权不符则拒绝注销。

瞬时进程WMI临时订阅前置探测失败（`Win32_ProcessStartTrace`，CimException“调用已取消”），未提权或开启审计，不推断特定权限根因；本轮降级为会话进程轮询、父侧窗口事件、任务与应用日志交叉核对，保留短命进程漏采样盲区。两份独立探针最终构建0警告/0错误，运行中不改探针二进制或源码；实际结果单列下节，不回写当时约定为已证明结论。

## 9. PoC-1：一次真实候选登录，未实现隔离

### 身份与实验边界

2026-11-15 20:14:19–20:15:42（UTC+8），父Session1、探针holder PID45748，首次登记自有child11。连接前读回的`StartProgram`为本轮`ShellAnchor.exe`绝对路径，`WorkDir`为其目录；入口检查过文件、路径长度、无参数配置中的父Session与输出目录。仅调用一次`Connect()`，无自动重连或后续补跑。

原始产物在忽略工作区`poc1/run-20261115-201418/`：`holder.jsonl`、配置指纹、应用/任务端点、`steam-shell-events.json`、`controller-analysis.json`、`controller-post-cleanup.json`。运行时holder DLL SHA256=`E859792A916366899E372A03FDD197C5FFAA4920A265067E6E19126D36E87A83`，anchor DLL=`A577D1DD4DB51FB07D4B56BD0BD371C88C72471AE085DAFDC805EC0E8325A47A`，源文件哈希归`build.json`。这些是丢弃式探针，不属于生产构建或安装验收包。

### 直接观察

| 时间 | 观测与证据 |
|---|---|
| 20:14:20 | 连接前初始进程快照只有父侧对照Steam44244/Clash23616，无其他用户Session进程；属性写读一致 |
| 20:14:38.770 | 同一holder首次登记child11 |
| 20:14:39.440 | 收到`OnLoginComplete`；其前`OnLogonError(-2)`按[官方定义](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onlogonerror)是继续登录通知，不是登录失败或初始程序被拒绝的证据 |
| 20:14:39 | 在Session11捕获Clash进程52056，创建时间20:14:39.526、PPID2780；随后不再出现在采样。未把采样跨度当作精确寿命 |
| 20:14:39 | 在Session11捕获`userinit.exe`52904、`explorer.exe`34872（PPID52904），明确仍进入Explorer桌面链 |
| 20:14:39.676–.770 | Clash应用记录secondary-instance恢复窗口、激活成功；父侧PID23616的窗口收到SHOW及FOREGROUND事件，任务最近运行推进到20:14:39、结果0 |
| 20:14:52–53 | Shell-Core9707/9708的provider PID34872执行`steam.exe -silent`并返回PID64092；WTS同时捕获Steam64092属于Session11、PPID34872，闭合本轮Shell→Steam→实际Session证据 |
| 登录后60秒内 | 没有`ShellAnchor`进程采样、最早入口文件或anchor业务标记；不只看一张登录后快照 |
| 20:15:39.528 | 到达观测期后，仅对实时匹配的自有child11请求注销 |
| 20:15:40–41 | 控件断开，WTS child消失，观测器异步结束、holder退出 |

进程采样共2301次、WTS读取失败0次，目标25ms但实测平均间隔35.5ms、最大239.7ms；部分新进程补创建时间有拒绝访问，原样记录。窗口两个OUTOFCONTEXT钩子均安装成功，但仍受消息泵及PID过滤范围限制。日志中的采样时间有“查询开始”与“记录完成”两个时点，不能据其微小先后推导比原生查询更精细的进程启动顺序。

**结论：本次连接配置没有实现指定初始程序替代Explorer；Steam和Clash仍被启动，父侧Clash仍被唤起。** 属性可写不等于控件已发送，更不等于服务端采用。未抓Client Info PDU或初始程序服务端决策，尚不能区分“未发送”“发送后忽略”“策略/版本限制”“启动失败后回退”等内部原因；也不能由本轮推出所有Windows版本或所有官方机制均不支持。

尤其不能用本轮来证明“真正无Explorer的替代Shell仍会触发计划任务”——本轮没有进入那个模式。只证明这一次设置未能绕过既有登录链，候选不满足接入条件；GUI/UIA/SendInput/截图/IPC的无Shell兼容性本轮没有验证，不宣称通过。

### 父侧Steam的额外变化

父Session1原Steam44244最后一次被采样到在20:15:02.305，之后消失；这比请求注销child早约37秒，不能归因于本轮随后执行的`WTSLogoffSession`。Steam共享bootstrap日志同秒记录`Shutdown`和另一次`-silent`启动，console日志亦记录该启动模式，但应用日志没有PID/Session，不能单靠这些行证明是谁要求退出或完整跨会话因果。

维护者经专门询问明确确认：**没有手动退出Steam**。因此不能把此变化归为用户操作；列为与child重复启动强相关、原因仍待定位的父侧副作用。20:16独立核对时父Steam不在，Clash23616仍为原进程；本轮未自动重启Steam，也未操作/结束父侧应用。

### 配置差异解释与独立清理核验

最初脚本给出`configurationUnchanged=false`，没有因此回写配置或把它隐藏为通过。逐项核对发现：

- Run/RunOnce、Policies Run、Startup文件、Clash任务XML及RDP-Tcp指纹前后一致。
- 唯一整键差异为HKLM Winlogon。将当前属性集合**仅在内存**把`LastLogOffEndTimePerfCounter`换回早先库存值，重算即完全匹配本轮连接前指纹；未换时又完全匹配本轮结束指纹。由此将差异限定为该注销计时记录，不是Shell/Userinit/启动项定义变化。证明归`controller-winlogon-hash-explanation.json`，原始`summary.json`的false保留。
- 全局Shell仍为`explorer.exe`，Userinit仍为`C:\WINDOWS\system32\userinit.exe,`；没有执行注册表、任务定义、策略或启动项写入。
- 主会话20:16另起只读原生查询：`WTSGetChildSessionId`返回1168（未找到），`WTSIsChildSessionsEnabled=true`；无Session11、Poc1或ShellAnchor进程。只证本次清理及保留原开关，不外推全部故障路径。

20:25最终复核仍无child11及探针进程、开关true，探针源码哈希与运行冻结记录一致；父Clash仍为原PID23616。另外观察到父侧新的Steam3208（20:21:57创建），本agent没有启动它，也没有足够证据判定其启动来源；不将新进程当作原进程从未退出或候选隔离有效的证明。

**本轮结果不是“exit0所以隔离通过”**：exit0仅表明观测和清理正常结束。没有验证成功的生产修复，不改现有实现、不恢复合并、不再以默认或同一候选重复登录。后续若继续，只读核查初始程序未生效原因与任务面可控边界；任何新真实候选实验须另行明确范围。

独立只读复核报告`poc1-evidence-review.md`与上述可观察结论一致。另保留两项记录限制：控制日志中的`observeAfterLoginSeconds=40`是未随无anchor分支更新的元数据，真实状态机与事件时间证明该分支观察约60.086秒，不能据旧字段写40秒；本轮发出两次`OnConnecting`通知、只调用一次`Connect()`，没有采集足以解释两次通知的认证/仲裁事件，不把通知数当作调用或登录次数，也不凭猜测归因。

## 10. BgDesk源码核对：独立的Explorer会话状态标记候选

维护者新增参考[enwong93-sketch/background-computer-use](https://github.com/enwong93-sketch/background-computer-use)。本轮固定commit **`c25e1bcb0e2b54a0dbe5d881528a50017daaa65f`**，通过GitHub API取得完整未截断文件树并读取实际C#源码，没有安装、构建或运行该项目；以下判断不是仅据README。

### 实际写入与调用时机

| 落点 | 源码实际行为 | 对本项目的意义 |
|---|---|---|
| [`Common.cs:12–17`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/Shared/Common.cs#L12-L17) | 定义用户Run、`Environment\UserInitMprLogonScript`和`Explorer\SessionInfo`路径 | 必须区分全用户配置写入与某Session状态标记 |
| [`HostForm.Connect:156–166`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/bgdeskw/HostForm.cs#L156-L166) | 先保存原登录脚本，再把它设为agent命令，并写入`Run\BgDeskAgent`作为引导 | **不复用**：会临时改变用户级配置，不符合本任务约束 |
| [`HostForm.Connect:168–182`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/bgdeskw/HostForm.cs#L168-L182) | 在调用`Connect()`前启动50ms WinForms定时器；取得`WTSGetChildSessionId`后，在`HKCU\Software\Microsoft\Windows\CurrentVersion\Explorer\SessionInfo\<id>`创建`StartupHasBeenRun`、`RunStuffHasBeenRun`两个**空子键**，选项为`RegistryOptions.Volatile`；不是写DWORD值 | 这是与`StartProgram`不同的新候选，不依赖替换Explorer |
| 同一回调 | 每tick重试；tick数超过1200或closing时停止；先尝试写两键，再检查`_loggedIn`决定停止；写入异常经`Try`静默忽略 | 大约60秒的名义窗口不是启动屏障；没有“已赶在Explorer前成功写入”的可核验保证 |
| [`OnLoginComplete/LoggedIn:208–222`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/bgdeskw/HostForm.cs#L208-L222)、[`poll:184–203`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/bgdeskw/HostForm.cs#L184-L203) | 登录事件或核对到该child的agent应答可置logged-in；agent应答后删除自加Run项、恢复原登录脚本 | 没有在marker定时器中创建或等待会话内worker；因此marker本身可与其全局引导代码拆开分析 |
| [`RemoveAutostart:224–233`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/bgdeskw/HostForm.cs#L224-L233) | 删除自己的Run值并恢复登录脚本，**没有删除SessionInfo marker的对应逻辑** | 不能把配置恢复函数当作marker清理证明 |

原理假说是利用Explorer“本Session的某类登录启动已处理”的去重状态，使其跳过相应启动阶段，而不是修改Steam的Run值、启动批准列表或全局Shell。第三方[Hexacorn 2019分析](https://www.hexacorn.com/blog/2019/02/23/beyond-good-ol-run-key-part-104/)把`RunStuffHasBeenRun`关联到传统Run类启动，把`StartupHasBeenRun`关联到Startup文件夹；本轮取得搜索摘要，原站正文抓取403，故仅作辅助，**不是Windows 11的官方契约或独立实测**。

### Steam覆盖与兼容性判断

- Steam的已证实入口和本轮实际Shell执行链见§4、§9，正落在该候选希望跳过的Explorer Run阶段。因此它是**值得保留的Steam/Startup部分缓解候选**，而不是已证明能挡住本机Steam的方案。
- BgDesk保留普通Explorer桌面，没有在此路径设置`StartProgram`；PoC-1的失败不能用来否定这个不同机制。反之，BgDesk中有两次`CreateSubKey`也不能证明当前构建会读取它们并跳过Run。
- 定时器与Explorer并发；`WTSGetChildSessionId`出现、Userinit建立/重置SessionInfo、Explorer检查标记之间没有源码级顺序保证。认证慢于定时器窗口、STA忙、写入失败、marker已有等均可能导致漏过或误判；补写不能撤销已启动的Steam或已向父程序发出的通知。
- README作者自述开发/验证环境为Windows 11 Enterprise 25H2 build26200，并明确“其他版本/构建未测”、跳过启动项依赖未文档化Explorer行为（[README:169–217](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/README.md#L169-L217)）。这些是作者声明，不是当前Pro build26300的兼容性证据；未发现微软将这两个marker定义为稳定、可配置的抑制API。
- 当前worker由本库[`ChildWorkerLauncher.LaunchAsync`](../../../src/SharpSight.ChildSession/Connection/ChildWorkerLauncher.cs)经`RunEx(..., TASK_RUN_USE_SESSION_ID, childId, ...)`投送，不依赖用户Run或登录脚本。因此**可以只研究marker而不照搬BgDesk的全用户引导写入**；尚未做任何生产修改。

另只读核对到[BackDesk同类实现的固定源码](https://github.com/sealpp/BackDesk/blob/54ced6d11113f0a1dd63598c23d784fa58bfc8fd/src/BackDesk/Runtime/ExplorerStartupSuppression.cs)：对已有child不操作，校验父/console及账户，任一marker已存在则记`already_processed`，保留原键句柄及创建disposition，仅清理自己创建的空键。它提供了作用域/竞态/清理的工程防护参考，但仍明确标注marker未公开；代码存在不证明本机有效，也不补上Clash任务抑制能力。

### Volatile不等于“注销child即消失”

官方[RegCreateKeyExW](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regcreatekeyexw)规定，volatile信息在内存中、对应hive卸载时不保留，且该选项对**已存在键不生效**；它不是“绑定SessionId的隔离权限”或“关闭句柄即删除”。HKCU仍是同一用户共享hive，会话作用域来自路径中的ID及消费方逻辑。

本轮20:38仅以只读方式枚举现有SessionInfo：父Session1和已注销的child10/11节点及两marker仍存在，原文`phase2-sessioninfo-observation.json`；未创建/删除/写任何键。该现状不证明预创建有效，但足以禁止承诺“volatile保证child注销后自动清空”。未来若研究写入，需分别核对创建/打开的disposition、旧状态、所属child及编号复用/清理，不能清空整个SessionInfo或触碰父会话节点。

### Clash任务与禁止照搬的事后处理

BgDesk的实际源码没有通过这些marker配置Task Scheduler。相反，[`Containment.cs:94–105,168–175`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/bgdeskw/Containment.cs#L94-L105)在登录后枚举窗口并发送`WM_CLOSE`关闭无归属控制台；[`SweepSessionStart:30–46`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/bgdeskw/Containment.cs#L30-L46)还会结束本Session的`jabswitch`进程。agent在[`Agent.Run:25–34`](https://github.com/enwong93-sketch/background-computer-use/blob/c25e1bcb0e2b54a0dbe5d881528a50017daaa65f/src/bgdeskw/Agent.cs#L25-L34)中启动这些事后处理。

作者README也明确承认LogonTrigger仍会触发，随后关闭其控制台窗口。**这是先启动后处理，不是任务抑制，不能复用来解决Clash。** §9中Clash第二实例先于Explorer创建，随后父窗口被唤起；之后关窗、杀进程或没有常驻副本都不能抹去这次副作用。

故BgDesk整体不满足本项目约束；仅抽出的SessionInfo标记可能减少Steam/Startup启动，尚不能覆盖Clash，也不能承诺父侧程序零影响。本阶段不写这些键、不做新登录、不运行BgDesk。

## 11. LocalRDP源码对照：投送通道不同，不是启动项隔离

核对的是维护者给出的 **[`charles-cty/LocalRDP@eb555fc9a8a2c2b0afe44f7380470d26c66443c0`](https://github.com/charles-cty/LocalRDP/tree/eb555fc9a8a2c2b0afe44f7380470d26c66443c0)**，不是此前资料中的`KiLiMe/LocalRDP-go`。本轮只读取固定commit源码，未构建、安装或运行。

| 阶段 | 实际源码 | 与当前项目的关系 |
|---|---|---|
| 创建前置 | [`start.cpp:3–44`](https://github.com/charles-cty/LocalRDP/blob/eb555fc9a8a2c2b0afe44f7380470d26c66443c0/src/start.cpp#L3-L44)检查是否在child内部、系统是否可用、是否已有child，再写目标job文件并运行连接宿主 | 仍是Windows Child Session，不是干净用户或另一套启动管线 |
| RDP连接 | [`rdp_host.cpp:748–886`](https://github.com/charles-cty/LocalRDP/blob/eb555fc9a8a2c2b0afe44f7380470d26c66443c0/src/rdp_host.cpp#L748-L886)设置localhost、尺寸、重定向、认证参数、关闭自动重连及`ConnectToChildSession=true`；1071行调用`Connect` | 没有设置`StartProgram`/`WorkDir`；`SecuredSettings*`在此只设键盘模式 |
| 登录与投送时机 | [`OnLogin:693–710`](https://github.com/charles-cty/LocalRDP/blob/eb555fc9a8a2c2b0afe44f7380470d26c66443c0/src/rdp_host.cpp#L693-L710)登录完成后启动定时器，有job时约1500ms后投送；注释为等待Explorer | 保留正常登录流程；登录后投送不是拦截启动项的钩子。等待Explorer也不等于所有延迟Run项已经结束 |
| 进程启动 | [`session_launch.cpp:9–22,142–253`](https://github.com/charles-cty/LocalRDP/blob/eb555fc9a8a2c2b0afe44f7380470d26c66443c0/src/session_launch.cpp#L142-L253)依次尝试WTS用户令牌、复制child中进程令牌、父进程属性创建；选进程优先Explorer，也可退到Userinit等 | 与本库临时任务`RunEx`不同，但只改变“如何把自己的程序放进child”，不改变Run/Startup/LogonTrigger触发；不能由更换投送方式推得隔离 |
| child内helper | [`session_entry.cpp:76–114`](https://github.com/charles-cty/LocalRDP/blob/eb555fc9a8a2c2b0afe44f7380470d26c66443c0/src/session_entry.cpp#L76-L114)校验实际Session、读取job、启动目标并等待；非keep-session时目标退出后注销 | 生命周期便利性，不是启动源过滤 |
| 停止/宿主退出 | [`start.cpp:47–59`](https://github.com/charles-cty/LocalRDP/blob/eb555fc9a8a2c2b0afe44f7380470d26c66443c0/src/start.cpp#L47-L59)注销当前child；[`rdp_host.cpp:1111–1118`](https://github.com/charles-cty/LocalRDP/blob/eb555fc9a8a2c2b0afe44f7380470d26c66443c0/src/rdp_host.cpp#L1111-L1118)退出时按keep-session决定是否注销 | 没有本项目的实例/SID/holder所有权模型，不照搬其“当前child即目标”的处理 |

当前源码没有SessionInfo marker、Run/Startup过滤、LogonTrigger配置或会话级任务抑制。README所说“不用Run/RunOnce”指其**自身引导不写这些键**，不能解释为Windows不会运行用户已有项。投送代码中结束进程的分支仅处理自己刚启动却落错Session的进程，不是用户启动项抑制，亦不作为本项目绕过方案。

LocalRDP另有`AuthenticationLevel=0`、关闭凭据提示等连接参数；这不能证明无需Windows登录，更不能证明启动项不运行。本轮不采纳认证降级或改变手动认证约定。`WTSQueryUserToken`还受[官方SYSTEM与SeTcb权限要求](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken)限制，不能假定它的全部投送分支在本机普通身份下可用。

**判定：没有从LocalRDP发现能覆盖Steam或Clash的新抑制机制，不值得为此换投送实现或再建child。** 这只是该仓库的能力边界，不是Windows不存在其他机制的证明。

## 12. StartProgram未生效的进一步只读判断

本节只补§9失败的原因边界，不重新执行该实验，也不把§10的SessionInfo候选混进初始程序机制。

| 层次/候选原因 | 目前能确认的内容 | 不能确认的内容 |
|---|---|---|
| ActiveX本地设置 | PoC-1写入发生在`Connect`前，`Connected=0`且路径/目录读回一致；安全访问检查通过 | 读回不证明控件把字段发送到了有效连接 |
| 客户端连接/传输 | `TS_INFO_PACKET`定义`AlternateShell`、`WorkingDir`；[协议字段规范](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/732394f5-e2b5-4ac5-8a0a-35345386b0d1)限制填写条件与512字节上限；`INFO_MAXIMIZESHELL`描述最大化状态，不是child必须采用的证明 | 未取得该次Client Info PDU，不能证明字段未发送或丢失；两次OnConnecting只支持“内部状态变化待查”，不证明发生两次Connect或第二次属性丢失 |
| 服务端处理 | [Processing Client Info PDU](https://learn.microsoft.com/en-us/openspecs/windows_protocols/ms-rdpbcgr/ef9db6a5-36f4-4613-84b3-97501679d210)规定解密、解析、存储及长度处理，该章节未定义如何将初始程序应用到Windows登录Shell | 仅据该协议章节不能推出服务端必须采用，也不能证明忽略是本轮的根因；更高层child会话配置/登录流程仍未观测 |
| 配置与策略 | 可读`RDP-Tcp`及`DefaultUserConfiguration`为`fInheritInitialProgram=1`、初始程序/工作目录为空；复读HKLM/HKCU可见策略未发现对应覆盖值；[WTS_CONFIG_CLASS](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ne-wtsapi32-wts_config_class)给出客户端指定的门控语义 | 不能证明child采用这两份配置；没有child有效配置或策略应用日志，不能排除其他层限制，也不据查询失败推断配置内容 |
| SKU/build/会话模式 | 官方[支持的RDP属性](https://learn.microsoft.com/en-us/azure/virtual-desktop/rdp-properties)将alternate shell列用于Remote PC等场景；本机child确能登录 | 不能以“Windows是客户端版”一概断言不支持；同样不能由Remote PC适用性推出Pro26300的child模式必支持。SKU/build/模式限制仍是未证原因 |
| 路径/加载/回退 | 本轮exe路径存在、长度合规、读回一致，登录成功且Explorer为Userinit直接子进程 | RDSH文档的路径错误通常导致连接失败，但不能外推child不会静默回退。原生加载失败、极短命进程未到托管入口等仍未完全排除；现有采样不能证明所有加载路径 |

**技术结论仍是“发送侧与采用侧未能区分”，不是“已定位服务端忽略”。** 旧失败证据不足以选择一个修复点，不应凭两个OnConnecting去补重设属性或重连。

[`WTS_INFO_CLASS`](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/ne-wtsapi32-wts_info_class)提供`WTSInitialProgram`、`WTSWorkingDirectory`等文档化的只读会话信息。它们可在未来另获批准的观测中辅助检查有效会话配置，但空值也可能来自配置覆盖/丢弃，**不能单独证明客户端未发送**；当前父Session的空值不代表已注销child11。仅为了补这些字段而重跑StartProgram没有充分依据。

官方边界对照：

- Child Sessions与ExtendedSettings公开的是“连接本地回环child”等能力，未给`SuppressStartup`/按child关闭登录任务的公开属性；不以文档未提就断言Windows内部无相关状态。
- [`ITSRemoteProgram.ServerStartProgram`](https://learn.microsoft.com/en-us/windows/win32/termserv/itsremoteprogram-serverstartprogram)明确是连接后启动RemoteApp的API；它与StartProgram不同，但不是现有独立child桌面的启动项抑制开关，不能把RemoteApp行为直接外推。
- Power Automate PiP的启动应用冲突限制见§6。其另一个virtual-desktop预览模式还明确不支持物理UI自动化和截图，不能作为保持本项目能力不变的直接替代。

**停止继续重复StartProgram连接。** 只有新增可核验的发送/有效会话配置/服务端决策证据，且另获实机授权，才有理由重开原因验证；这不否定独立的SessionInfo候选，也不授权新登录。

## 13. Clash的独立任务面：所查官方控制均不满足child-only约束

对照的是既有`\Clash Verge`的用户登录触发器，不再重新盘点全机任务，也不修改其定义。

| 官方控制面 | 能做什么 | 为什么不能复用为本需求 |
|---|---|---|
| [`LogonTrigger` schema](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-logontrigger-triggergroup-element) | 根据`UserId`等条件在用户登录时触发；公开子元素是UserId、Delay及通用触发器时间/启用设置 | 没有按SessionId排除child的字段；父子为同一用户，改UserId/Enabled/Delay也会改现有任务并影响父侧规则 |
| [`ITaskSettings2.DisallowStartOnRemoteAppSession`](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-itasksettings2-get_disallowstartonremoteappsession) | 不在RAIL（Remote Applications Integrated Locally）会话启动该任务 | 是**单个任务定义**中的设置，需修改Clash任务；文档限定RAIL，不能套成普通child过滤器 |
| [`IRegisteredTask.RunEx`](https://learn.microsoft.com/en-us/windows/win32/api/taskschd/nf-taskschd-iregisteredtask-runex)的`sessionID` | 立刻把这一次显式请求的任务运行投向指定Session；须带`TASK_RUN_USE_SESSION_ID` | 不是给自动LogonTrigger注册排除器，也不改变其他任务的触发条件。本库投送worker不意味着能控制Clash |
| [`settingsType`](https://learn.microsoft.com/en-us/windows/win32/taskschd/taskschedulerschema-settingstype-complextype)的并发/启用/空闲/网络条件 | 控制该任务的运行策略 | 无专门child ID过滤设置；改成IgnoreNew、禁用或依赖现有运行实例均需改任务，且不保证父侧正常规则不变 |
| 用户/设备Run抑制策略及Startup开关 | 限制用户或机器的相应启动列表；Run/Startup文件夹的作用域本来就是用户/公共 | 不是Task Scheduler过滤，也没有本次child的临时作用域；不采用改`StartupApproved`、Run条目或Startup内容的方案 |
| 在holder/worker中覆盖注册表或环境 | [RegOverridePredefKey](https://learn.microsoft.com/en-us/windows/win32/api/winreg/nf-winreg-regoverridepredefkey)只改调用进程的预定义键映射；新进程环境块只用于被创建进程 | 不能让独立运行的Task Scheduler服务、Userinit、Explorer同步采用这个视图；不是对整个child登录的隔离层 |

官方[LogonTrigger对象](https://learn.microsoft.com/en-us/windows/win32/taskschd/logontrigger)将触发评估归于Task Scheduler服务。Explorer两枚标记没有公开契约将状态传给该服务；BgDesk实际代码和其任务仍触发的自述亦不提供这种能力。

**结论：本轮没有找到不改任务定义/用户配置、仅对目标child抑制Clash LogonTrigger的官方方案。** 这是所查公开控制面的结构性限制与目前证据结论，不是“证明Windows内部不存在任何办法”。不能把“未找到”升级为不存在，也不能把未文档化标记包装为官方支持。

## 14. 综合裁决与最小PoC门槛（只设计，不执行）

### 覆盖与项目约束

| 来源/目标 | BgDesk独立SessionInfo标记 | LocalRDP | 本轮判断 |
|---|---|---|---|
| Steam的HKCU Run | 有具体源码、已知Shell去重状态的旁证；必须抢在Explorer之前，当前Pro26300未验证 | 无抑制机制，换投送通道不改变该链 | **存在值得保留的新部分候选**，尚不能说能可靠阻止Steam |
| Explorer Startup文件夹 | 同属候选覆盖范围，具体版本/阶段待验证 | 无处理 | 不能外推RunOnceEx、Active Setup、UWP StartupTask或所有启动源都被挡住 |
| Clash的LogonTrigger | 没有任务服务过滤；BgDesk仍让任务运行并事后关部分窗口 | 无任务过滤 | **没有符合约束的新候选**；不以关窗/杀进程替代抑制 |
| 父会话已有程序不受影响 | 正确child路径预计不直接改变父Explorer状态，但同用户共享hive、时序失败及Clash仍启动的风险都在 | 同用户、正常登录，不增加该保证 | **不能保证**；§9已有父侧副作用，不能忽略 |
| 保留GUI/UIA/真实输入/截图/IPC | 保留Explorer意味着不需要重做替代Shell，但marker仍可能跳过所需用户辅助程序，必须验证 | 提供GUI程序投送，不等于完整能力验收 | 本轮无兼容性实测；不能把第三方测试声明当本项目通过 |

**是否满足全部约束：否。** BgDesk整体含禁止的用户级引导改动与事后处理；独立marker虽然可以不碰原Run值/任务/Shell，但仍是未公开的、版本及时序敏感的HKCU状态写入，且不能覆盖当前Clash问题。LocalRDP没有增加抑制能力。没有可靠完整方案，不应修改正式实现或恢复合并。

**是否停止研究：**

1. 停止重复StartProgram实验；不为LocalRDP更换投送方式再登录。
2. 对“当前约束下，Steam和Clash都不重启且父桌面不受影响”的完整交付方向，**建议暂停实机与生产推进**，直到出现新的任务侧机制依据或维护者明确调整目标；不继续靠换属性、补计时重试。
3. 不应宣布所有研究都已无路：SessionInfo是新而独立的**部分缓解候选**。只有维护者接受先验证Steam/Startup、接受未公开行为与残余Clash风险，才值得单独验证。停止完整方向与保留这一候选并不矛盾。

### 有限PoC草案

本阶段仍为只读，**没有创建child或marker，以下不是执行授权，也不是生产设计**。若维护者单独同意部分目标，可用一次独立探针验证一个假设：

> 在当前Windows构建，父侧在已核实自有child的Explorer开始处理启动项前，预创建两枚SessionInfo去重子键，能否使Steam的Run执行记录及进程启动不再出现，同时保留普通Explorer桌面？

实验约束及可证伪条件：

1. 复用§9作历史基线，不再跑默认登录或StartProgram对照。禁止采用BgDesk的Run/登录脚本写入、凭据存储、事后扫窗/结束程序；本项目原有worker投送方案无需为此替换。
2. 从连接前开始观测，仅在无既有child、父账户/SID和本次child归属均确认后，操作精确的`SessionInfo\<ownedChildId>`。每次写前复核ID未变且不是父/console；不预猜ID、不枚举后随便选一个、不改父节点或整个SessionInfo。
3. 区分新建与已有键，记录原状态、`RegCreateKeyEx` disposition、成功/失败时间与Explorer/Steam时序。已有marker可能表示Explorer已经处理启动项，不能改写后宣称成功；一枚已创建而另一枚失败须记为部分变更。设置50ms或更短间隔本身不消除竞态。
4. 登录后至少观察60秒，联合会话进程、Shell-Core9707/9708、Steam日志和父侧进程/窗口事件；明确采样盲区。“两键存在”或“没有Steam常驻副本”均不单独算成功。Clash任务推进及父侧显示仍要独立记录，**不作为允许忽略的噪声**。
5. marker写入失败、晚于消费或Steam仍启动都为失败/未验证，不自动重放或追加登录。即便Steam未观察到，也只能形成一次构建下的有限证据；生产可靠性需要后续另批验证，不能一次即签收全部目标。
6. 正式接入前仍须验证普通child中的UIA、真实输入、可解码PNG/画面变化、IPC/取消/stop；本轮不补跑旧三轮。若部分启动项是被测应用的依赖，也需记录兼容性退化。
7. 结束仅注销已核实自有child，并确认无残留及开关保持。对marker单独设计清理：不依赖volatile自动删；不删除已有或由Windows替换的键、不清空SessionInfo根。编号复用或原键身份无法确认时，不沿路径盲删，并将未清理状态作为阻塞项，而不是篡改成功状态。

**具体风险**：Clash依旧可能再次启动并唤起父窗；marker未生效时Steam仍可能重复启动并影响父实例；键作用域/编号复用误判可能影响后续正常登录；未公开Shell行为可能随更新失效。若维护者要求这一次实验也必须保证父软件绝不受扰动，当前没有满足这一前提的实验方案，应不启动。

**重开完整方向的依据门槛**：微软可核验的child-only任务抑制契约/支持说明，或能解释作用域与登录触发链的新实际实现；不能只是另一个README、自身不写Run、事后关闭副本、或换一个同义StartProgram访问器。任何新依据仍须先做只读评估，再另行批准实机，不由本文自动授权。

## 15. 新方向：保持连接、跨调用复用已有实例

维护者随后要求优先减少重复登录，而不是继续尝试启动项抑制。本轮仅分析当前工作树源码、既有验收记录与官方文档；不创建child、不调用Connect/Disconnect、不写marker、不构建或运行探针。以下区分**源码已有行为、历史有限证据、未验证推断、待批准建议**，不改写§9及§14的历史结论。

### 15.1 三个不同的“连接/持久化”

1. **客户端复用**：多个MCP请求、多个CLI进程或新的MCP服务器进程，使用同一个instance，访问仍存活的holder/worker。正常情况下不需要再次调用ActiveX Connect，也不重新创建Windows登录会话。
2. **RDP断连后会话存留**：Windows child及其中程序可能仍在，但连接不可交互；不等于GUI/UIA/真实输入/截图仍可用。
3. **RDP恢复到原会话**：必须证明恢复的是原Windows登录与原worker，而不是新登录、编号复用或新worker。它不是“换一个MCP客户端”。

本文推荐先做第1类。这里的“长期”仅限**同一父登录会话内的进程持续存活**，不是保存桌面到磁盘、跨重启恢复、跨父会话注销恢复，也不是后台Windows服务。

### 15.2 当前进程与状态归属

```text
父Windows会话
  MCP进程A / CLI进程B / 后来的MCP进程C
       │ 每次请求读取同一实例记录，再建立独立IPC连接
       ▼
  独立holder进程 ── STA消息泵 / 连接窗 / RDP ActiveX
       │ 已建立的RDP连接                 │ 认证后的临时任务RunEx投送一次
       │                               ▼
       └──────────────────── Windows child中的worker
                                      ├─ 任务、Job、输出
                                      ├─ 请求记录、观察引用、GUI执行门
                                      └─ 被测GUI
```

| 源码落点（仓库相对路径） | 已有行为与限制 |
|---|---|
| [`ChildServices.cs:17–29`](../../../src/DotNetDebuggerMcp/Services/Child/ChildServices.cs) | 所有正常入口默认使用`%LOCALAPPDATA%\DotNetDebuggerMcp\child`；不是每个MCP进程各有一个私有实例根。跨入口复用仍受相同用户/父Session和协议兼容约束，不承诺任意版本混用。 |
| [`ChildCoordinator.cs:34–58,100–113`](../../../src/SharpSight.ChildSession/Lifecycle/ChildCoordinator.cs) | 生命周期锁内按SID+父Session查非Stopped实例。存在且holder存活就返回原实例；并不再次StartHolder。**现状不是完整健康探测**：Disconnected、Faulted、Stopping等也可能被返回；holder失效则拒绝，不自动接管/重建。无本产品实例但系统有外部child也拒绝。 |
| [`ChildSessionClient.cs:10–47`](../../../src/SharpSight.ChildSession/Lifecycle/ChildSessionClient.cs) | `start`后的等待只轮询状态，截止不注销child；其他操作读取登记、校验holder后通过IPC转发，不创建ActiveX。`completed`是查询完成，不代表返回的`state`一定Ready。 |
| [`Program.cs:10–14`](../../../src/DotNetDebuggerMcp/Program.cs)、[`ChildProcessRoles.cs:9–36`](../../../src/DotNetDebuggerMcp/Services/Child/ChildProcessRoles.cs) | holder/worker是同一exe的独立内部角色，在CLI/MCP分派前进入，令牌为`CancellationToken.None`；不随某次MCP请求的取消令牌结束，也不依赖CLI命令循环保持运行。没有订阅“创建者MCP退出即注销child”的逻辑。 |
| [`ChildHolderRuntime.cs:20–95`](../../../src/SharpSight.ChildSession/Connection/ChildHolderRuntime.cs)、[`ChildConnectionWindow.cs:10–20`](../../../src/SharpSight.ChildSession/Connection/ChildConnectionWindow.cs) | holder维持STA和服务循环，首次窗口Shown时Connect一次；轮询不因没有客户端或业务任务结束而注销，没有空闲自动stop。退出/异常finally会关闭连接窗，但不是Windows注销保证。 |
| [`ChildWorkerLauncher.cs:9–60`](../../../src/SharpSight.ChildSession/Connection/ChildWorkerLauncher.cs) | 登录/WTS门控后投送worker一次；自有临时任务没有登录触发器，设置`ExecutionTimeLimit=PT0S`，握手或失败后删除任务定义。不是每个业务调用都重新注册任务，也不是改Clash等用户任务。本轮不改变这条投送链。 |
| [`ChildWorkerRuntime.cs:11–42`](../../../src/SharpSight.ChildSession/Lifecycle/ChildWorkerRuntime.cs)、[`ChildOperationDispatcher.cs:6–24`](../../../src/DotNetDebuggerMcp/Services/Child/ChildOperationDispatcher.cs) | worker持有执行状态，监视holder身份及Stopping/Stopped；不监视最初客户端。holder失效会使worker退出/执行失败，并释放dispatcher；不能将当前worker视为可脱离holder长期运行。 |
| [`ChildTaskRunner.cs:50–69,116–130`](../../../src/SharpSight.ChildSession/Processes/ChildTaskRunner.cs) | 单个GUI/脚本任务结束不停止会话；显式cancel仅处理自有Job。终态任务仍留在字典，累计256个任务或日志配额满后拒绝新任务。现有配额/产物保留口径归[正式设计§8](../specs/2026-11-14-child-session-mcp-cli-design.md#8-任务输出与取消)，不能以“长期复用”承诺无限任务/存储。 |

**静态结论：正常连接期间跨调用复用已经存在，不需要再建一套持久化管理器，也不需要新增keep-alive请求来阻止本产品空闲注销。** `instance.json`只是定位/身份登记，不能恢复已退出的worker内存、Job、请求记录或GUI执行状态。外部宿主若使用进程树/Job整体终止，仍可能杀死holder；当前源码并没有提供脱离任意外部进程管理器的保证。

### 15.3 生命周期边界

| 动作/事件 | 当前结果 |
|---|---|
| 一次CLI退出、单次MCP请求结束/取消 | 不主动stop；已启动进程任务需显式cancel。正在执行的GUI请求仍按连接级取消处理，不保证动作继续完成或副作用回滚。 |
| 创建实例的MCP服务器退出，后来换MCP/CLI | 架构允许通过共享根重新发现并校验原holder；不需要Windows重登录。但创建者退出后的日志依赖见§15.4，完整MCP退出/再接入与长时可靠性未专项验收。调试Session仍属于原MCP进程，不因child复用而跨进程恢复。 |
| 仅连接窗失焦/被其他窗遮挡 | 不等于断连；无需为了换调用者关闭连接窗。最小化按已有GUI请求前不激活还原契约处理，不改为隐藏/销毁ActiveX。 |
| 手动关闭连接窗或WTS断连 | holder监视器记录Disconnected；worker存活时控制查询/取消仍可转发。GUI及新process start被拒绝；不是“后台保持全功能”。关闭窗后holder监视/IPC还可能继续，不能用holder PID存活证明ActiveX仍在。 |
| 单个任务正常结束或cancel | 不会自动结束holder、worker或Windows child。 |
| `child_session stop` | 按所有权注销整个child，再关闭holder；不是释放一个客户端。不能放进每条业务操作的finally。 |
| holder/worker故障 | 没有透明恢复；holder死亡后的stop拒绝仍是既有审阅I1，不能把去掉所有权检查当作复用优化。 |
| 父会话注销/系统重启 | 不属于复用范围，不能靠状态文件恢复旧实例。 |

### 15.4 长期运行的实际缺口：日志读取者仍在创建者中

[`ChildSessionPlatform.StartHolder/DrainAndDisposeAsync:13–23,43–69`](../../../src/SharpSight.ChildSession/Lifecycle/ChildSessionPlatform.cs)给holder独立重定向stdout/stderr，但**排空与写`holder.log`的Task运行在启动它的CLI/MCP进程里**。该客户端退出后，这个读取Task及其日志文件句柄不再存在；holder继续持有的写端是否出现丢日志、写入失败等，要由具体输出路径决定。本轮没有实机复现，不写成“已证明每次都会崩溃/卡死”，但它确实不是创建者无关的诊断链。

[`ChildStandardHandles.cs`](../../../src/SharpSight.ChildSession/Lifecycle/ChildStandardHandles.cs)已解决holder继承调用端协议/输出写端导致CLI不收到EOF的问题；**它没有把日志排空Task转移到holder**。不要把此前EOF回归通过当作长期日志可靠性证明，也不能仅取消重定向，让holder重新继承MCP的stdout。

历史安装三轮确有“CLI创建返回后，MCP/后续CLI操作同实例”的短时业务证据，归[接入验证记录§8](11-child-session-integration-results.md#8-最终安装产物三轮业务验收通过2026-11-15)。当前验收器[`ChildSessionCases.RoundAsync:52–77`](../../../src/DotNetDebuggerMcp.Client/ChildSessionCases.cs)每轮finally都stop，三轮是功能/清理验证，**不是持续数小时、创建者退出后持续日志、重启MCP后复用、或RDP断线恢复的验收**。

## 16. Windows断连/重连能力与当前实现的差距

### 官方能力不等于本产品已支持

| 官方依据 | 能确认的内容 | 本需求边界 |
|---|---|---|
| [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions) | child绑定父会话；可直接注销child，父会话终止也会终止child；RDP连接超时组策略对child无效；系统同时最多一个active且connected child | 支持“同一父登录内长期保持”的方向，但不是任意故障永不掉线的保证；不需要修改超时策略。 |
| [WTSDisconnectSession](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsdisconnectsession) | 断开用户而不关闭会话；文档描述在同一RDSH后续登录可连回原会话 | 不能据通用RDSH说明承诺本机Pro26300的child一定保留原登录/原worker；旧恢复现场还出现过child编号变化。 |
| [IMsTscAx.Disconnect](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscax-disconnect) | 断开当前连接；关闭承载控件也触发断连 | 关闭/销毁ActiveX不是注销API，也不是能继续真实输入的后台模式。 |
| [EnableAutoReconnect](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclientadvancedsettings2-enableautoreconnect) | 网络断连时控件自动重连开关，文档默认true；需在未连接时设置 | 当前`ConnectChild`没有显式设置或读取该属性。只能说产品未实现重连编排，**不能说已明确关闭控件内部自动重连**；本机child有效值/行为未采集。 |
| [OnAutoReconnecting](https://learn.microsoft.com/en-us/windows/win32/termserv/-imstscaxevents--onautoreconnecting)、[OnAutoReconnected](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onautoreconnected) | 提供控件自动重连的过程/完成事件，前者可控制后续尝试 | 当前只订阅登录完成/断连，未消费这些事件；事件本身也不证明worker/桌面已可安全交互。 |
| [IMsRdpClient8.Reconnect](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpclient8-reconnect) | 以新宽高重连，**要求控件已经连接** | 不是Disconnected状态的通用恢复入口，不可见到同名方法就用来恢复已关闭的窗口。 |

### 现有状态机没有恢复闭环

依据[`ChildHolderRuntime.EvaluateConnectionRoundAsync:101–115`](../../../src/SharpSight.ChildSession/Connection/ChildHolderRuntime.cs)：

- 已投送worker后失连，返回Disconnected并写实例状态；窗已关闭则始终走此分支，不重建窗口。
- 已投送worker后，即使后续读取`Connected=true`，也只返回Hold；没有“复核原worker后Disconnected→Ready”的分支。worker只在初次注册时写Ready，不会因RDP回来自动再握手。
- [`ChildForwarder.ForwardAsync:25–34`](../../../src/SharpSight.ChildSession/Connection/ChildForwarder.cs)仍要求实例Ready、ActiveX已连接、当前child一致且WTS Active。因此不能只增加一次Connect调用、或看到Connected就宣称GUI恢复。
- `start`只返回已有实例，不向holder发重连指令。调用多次start既不构成重连，也不应通过stop/start自动掩盖断连。

这是静态可达路径分析，不是本轮断线复现。原验收[`VerifyDisconnectedAsync`](../../../src/DotNetDebuggerMcp.Client/ChildSessionCases.Lifecycle.cs)只验证断连后的控制查询/取消与GUI拒绝，没有恢复连接/原登录保持证据。

真正重连若未来另立任务，至少要核对父会话、原child归属/登录身份、原worker PID+创建时间+IPC，重新确认图形/input desktop可用，并处理连接代际与旧观察引用；不能自动重投worker、重放未知GUI请求或遇身份变化便接管。还须单独观察Shell及Clash任务，不能用“SessionId没变”或控件事件推导“没有新登录/启动项触发”。这已超出本轮推荐的最小方案。

## 17. 最小改动建议与后续验证门槛（未批准、未实施）

### 方案对比

| 路线 | 收益与代价 | 建议 |
|---|---|---|
| A. 保留当前holder+ActiveX连接，MCP/CLI反复复用 | 复用基础已存在；正常业务批次间不再主动制造Windows登录。需补齐创建者无关的日志与长时证据 | **优先**；不新增常驻服务、不新增keepSession参数、不改用户配置。 |
| B. 空闲主动断开RDP、需要时恢复原child | 或可保留程序，但空闲期无完整GUI能力；恢复身份、认证、状态、图形及任务触发都需新验证 | 暂缓；不作为节省资源或避免登录的已验证手段。 |
| C. 新增服务/守护进程，holder/worker故障后接管 | 涉及权限、协议、所有权与新执行代际；父会话注销仍受Windows规则限制 | 不属于最小改动；现有跨客户端复用不需要它。 |

**建议的使用流**（流程说明，不是本轮执行脚本）：

```text
查当前实例
  ├─ Ready且身份/实际通道可确认 → 保存instance，继续操作
  ├─ Starting/AwaitingAuthentication → 查同一实例，必要时用户手动认证
  ├─ Disconnected/Faulted/Stopping → 报状态，保留证据；不自动stop/start
  └─ 无实例且无外部child → 仅在得到创建授权后start一次

同一instance上执行多批业务；单任务清理用cancel
切换CLI/MCP客户端不注销；明确结束整个工作区时才stop
```

仅保持正常连接的复用路径**不需要改生命周期核心逻辑**。若后续批准产品改动，最小候选范围为：

1. **先脱离创建者的日志生命周期**：让holder拥有自己的有界诊断输出；启动层给它独立于MCP协议的文件/NUL等标准句柄，或采用等效的角色内日志接管方案。具体句柄交付及早期失败路径需先做不创建child的进程测试，不能简单删重定向、写回父stdout或再起一个日志守护服务。候选落点为`ChildSessionPlatform`与内部holder角色入口；不动worker投送、用户启动配置或实例所有权。
2. **说明现有复用契约**：获批后同步工具Description、握手、README/CLI指引，明确start可返回原实例但不是重连、任务结束用cancel、stop是整体注销、跨客户端退出不等于持久存储。当前工具和握手未充分解释这一使用方式；本轮只更新研究文档，不抢先改这两个agent通道。
3. **用验证决定是否补健康探测**：当前status主要读登记，start也只先核holder存活。后续验证若需更强的“可复用”判断，应只读确认实际通道及原worker，不把增加心跳变成自动修复/重建机制。本轮不新增健康工具、重连action、空闲超时、全局策略或SessionInfo写入。

**长期复用不是启动项隔离**：首次创建仍可能运行Steam与Clash并扰动父程序；只有后续业务不再发生新登录时，才减少相应触发机会。父子共享账户数据与应用单实例通信的既有风险仍在。BgDesk marker保持独立候选，不与本路线捆绑，也不借复用方案声称Clash已解决。

### 验证顺序与通过条件

以下全部是待另行批准的验证，不由本研究自动授权；优先无child测试，且不重跑StartProgram或默认登录基线。

1. **无child进程边界验证**：短命创建者退出后，长命自有测试进程继续多次写诊断；新客户端能发现并请求它；stdout无协议外文本、CLI正常EOF、不靠原创建者排空、不因日志上限阻塞。此阶段不需要Windows登录。
2. **一次已授权实例上的多批复用**：若已有获准长期保留的自有实例，优先使用它；否则另批一次创建并明确首次启动风险。记录instance、holder/worker创建时间、Windows登录身份和Shell身份；在多个独立CLI与MCP进程间交替操作，并退出最初创建者、关闭再启动MCP客户端。不能通过登录次数增加来验证“复用”。
3. **联合确认未发生新登录**：比较角色/会话身份，结合TerminalServices登录与Shell启动事件、Steam执行链、Clash任务LastRun/应用日志和父侧窗口变化；身份相同或没有child常驻副本都不是单独充分证据。记录观察窗口和缺失日志，不能以未捕获宣称绝对没有副作用。
4. **长时与业务能力**：多批间保留空闲窗口，至少覆盖一段约定的小时级运行，再做UIA写读、真实输入与可解码/像素变化截图；同时检查日志持续可读、任务累计/产物增长与资源趋势。有限窗口通过不等于数周可靠性。先在可用任务/日志配额内验证，不借此擅自删历史任务或无限放宽配额。
5. **最终显式清理**：只注销已核实自有实例，确认holder/worker/目标结束及系统开关不变；长时故障触发I1时如实阻塞，不绕过所有权检查、不擅自结束父程序。该故障清理边界仍需恢复交付前另行裁决。

本轮结论是**“已有连接复用是最小、依据充分的下一研究方向；先验证并补齐日志独立性，不先建设断线重连”**。目前没有小时级、MCP退出再接入或RDP重连的新实机结果，不修改生产实现、不恢复合并或发布。
