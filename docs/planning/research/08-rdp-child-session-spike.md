<!-- 职责：记录 RDP Child Session（loopback 子会话）方案的实测验证结果——环境硬条件、投送/UIA/真实输入/截图/调试的实测表现、时序与参数、硬限制；
     不写：桌面隔离方案的全景比较与选型（见 research/07）、产品接入形态设计（见 specs/ 与 decisions）。 -->

# RDP Child Session 实测（spike 记录）

> 结论随实测追加。每条都标注「怎么测的 + 观测到什么」，无法复现的推断不写入。

**2026-11-14 更新**：维护者更正目标为“补齐 UIA 不足而不干扰用户”，不是为隔离而隔离；本轮新代码与实测在 §6。早期“真实输入、跨 Session 调试待测”是当时状态，不回填成当时已通过。

## 1. 目的与 demo 代码

目标：验证「agent 全权操作 UI 自动化、不抢用户操作」的隔离载体是否可用。demo 在 **仓库外**：`..\..\Demo\ChildSessionSpike`（相对本仓库根向上两级），
net10.0-windows WinForms + RDP ActiveX（`MsRdpClient10`，CLSID `A0C63C30-F08D-4AB4-907C-34905D770C7D`）。

| 命令 | 作用 |
|---|---|
| `ChildSessionSpike.exe probe` | 只读探测：RDP 状态、child sessions 开关、子会话 ID |
| `probe` / `enable` / `disable` | 开关 child sessions（`WTSEnableChildSessions`，需管理员） |
| `connect` | 打开 RDP ActiveX 窗口，`ConnectToChildSession` 建/接子会话；连上后自动投送 worker |
| `launch --session N` | 用计划任务把 worker 投送到会话 N |
| `worker --report <path> --hold N` | 子会话内自检：会话环境 + UIA + 截图，写报告文件 |

复用的生产代码：截图走本仓库 `src/SharpSight.Capture`（项目引用），UIA 走 FlaUI 5.0.0（与 `SharpSight.UiAutomation` 同一引擎）。

## 2. 实测环境

- Windows 10 Pro 26H2（`10.0.26300`），普通用户账号（非管理员）
- `TermService` RUNNING、`fDenyTSConnections=0`、3389 监听（用户经系统设置开启，未脚本改注册表）
- `DevicePasswordLessBuildVersion=2`（仅允许 Windows Hello 登录）
- 显示器 3440×1440（父会话）；子会话虚拟屏初始尺寸沿用上次 RDP 尺寸

## 3. 已实测结论

| # | 项目 | 方法 | 结果 |
|---|---|---|---|
| 1 | 启用 child sessions | `WTSEnableChildSessions(true)` | **非管理员 → `ERROR_ACCESS_DENIED(5)`**；管理员 → 成功，`WTSIsChildSessionsEnabled` 立即为 true。官方文档未写这条权限要求 |
| 2 | 建子会话前置条件 | 观察 | 需 `TermService` 运行 + `fDenyTSConnections=0`（RDP 主机开启）；缺一不可 |
| 3 | 自动登录 | `ConnectToChildSession` + `Connect()` | 本机 Hello-only ⇒ **必须人工输入密码**（官方默认自动登录，这是例外配置） |
| 4 | 取子会话 ID | 连接后立刻 / 延迟查 `WTSGetChildSessionId` | 立刻查返回 `1168`；**约 2 s 后**返回 `session=2` |
| 5 | 画面自适应 | 拖拽窗口 → `IMsRdpClient9::UpdateSessionDisplaySettings(w,h,物理w,h,0,100,100)` | 成功，多次生效，画面 **1:1 填满且不变形**；`SmartSizing=false`。无需 `Disconnect/Connect`（后者会白屏） |
| 6 | 投送进程进子会话 | 计划任务 `RegisterTaskDefinition(TASK_LOGON_INTERACTIVE_TOKEN, LUA)` + `RunEx(null, TASK_RUN_USE_SESSION_ID(0x4), sessionId, null)` | **成功，且不需要管理员**。`WTSQueryUserToken` 路线要 `SeTcbPrivilege`（管理员令牌没有），故不用 |
| 7 | 子会话内 UIA | 投送 worker（`ui_find` 同类调用：FlaUI UIA3 按 pid 找窗 → AutomationId 找控件 → ValuePattern 写 → InvokePattern 点） | **全部成功**（会话状态 `Disconnected` 时也成功） |
| 8 | 子会话内截图（断开态） | `ScreenCapture.CaptureScreen` / `CaptureWindow` | 全屏 BitBlt **失败**（`CaptureException`）；整窗 WGC 失败 → **PrintWindow 成功**（非纯黑） |
| 9 | 父会话内同一条代码路径（Active） | `launch --session 1` 投送同一 worker | **8/8 通过**：UIA 三项 + 显示器枚举 + 全屏 BitBlt + 整窗 WGC |
| 10 | 会话连接状态 | `WTSQuerySessionInformation(WTSConnectState)` | 子会话断开后仍存在（`query session` 显示 `Disc`，`WTSGetChildSessionId` 仍返回 2），进程照常运行 |
| 11 | 父会话驱动子会话（命令通道） | 命名管道一行一条 JSON：`info/find/input/click/get/capture/exit` | **全通**：`find` 返回子会话窗口的 UIA 树（含 AutoId/矩形），`input` 写入并读回，`click` 触发子会话窗口的按钮，`get` 读回回调结果，`capture` 落盘 PNG |
| 12 | 免密登录的候选原因 | 读策略键 | `HKLM\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation` **不存在**；当时推测凭据委派相关，但没有改变单一变量后验证成功，不能仅凭键不存在定根因。修正依据见 §4.5 |
| 13 | **Active 子会话内截图**（人工输密码连上后，会话 3，1008×729） | 投送 worker 后就地 `CaptureScreen` + `CaptureWindow` | **全通**：全屏 BitBlt 1008×729、整窗 **WGC 706×413**，均非纯黑；8/8 项 PASS。父会话写入的文本与点击结果都能在截图上看到 |

## 4. 硬限制与设计含义

1. **UIA 按会话隔离**：父会话里的 MCP host 看不到子会话的 UIA 树，所以必须「会话内 actuator」——host 负责持有 RDP ActiveX 保活与 `debug_*`，actuator 在子会话内跑 `ui_*`/`screenshot`，两者用命名管道通信。
2. **截图能力取决于会话是否 Active**：客户端断开后无显示面，BitBlt 必败、WGC 也不出帧，只剩 PrintWindow（依赖目标窗口自绘，Chromium/游戏/部分 UWP 不稳）；客户端连着（Active）时 BitBlt 与 WGC 都正常。**保持 ActiveX 连接 = 保持 WGC/BitBlt 可用**，这是方案的前提而非可选项。
3. **开启子会话要管理员，投送不要**：前者一次性（系统级开关），后者每个子会话都要跑，用计划任务即可，不要求宿主提权。
4. **自适应用动态分辨率**：`UpdateSessionDisplaySettings` 必须在连接后约 1–2 s 调用（过早 `E_UNEXPECTED`），参数物理尺寸按 96 DPI 换算成毫米；`SmartSizing` 官方只写「缩放填满客户区」、未承诺保持纵横比，故不依赖它。
5. **密码**：当时本机出现人工认证；不能据此断言所有 Hello-only 配置必然无法免密，或关闭 Hello 后必然修复。官方 PiP 文档列多种认证/策略因素，未做闭环的候选均保留为未验证。

## 4.5 密码：当时的候选原因，未完成闭环

现象：官方语义是 `Connect()` 自动登录子会话、**不弹凭据**（例外只有「父会话用智能卡登录」与「启用 child sessions 之前就已登录」），
但本机实测：每次新建/重连子会话都停在 `Connected=2`，必须人工输入一次密码。

已排除：
- **不是 NLA 配置问题**：把 `EnableCredSspSupport` 设为 false → `Connect()` 直接 `E_INVALIDARG (0x80070057)`，即子会话必须走 CredSSP。
- **不是旧子会话脏**：`logoff 2` 清掉旧子会话、重建全新子会话后照样要凭据。
- **不是客户端代码的问题**：属性全部按子会话要求设好（`Server=localhost`、`EnableCredSspSupport=true`、`ConnectToChildSession=true`、`EnableZoom=true`，其余显示/音频/键盘参数齐全）后，行为不变——仍停在 `Connected=2` 等凭据。

当时假说：SSO 凭据委派未启用导致提示认证。但未验证客户端 `DisableCredentialsDelegation` 等独立开关，亦未做策略修改前后单变量闭环。
2026-11-14 复核 [Power Automate PiP 官方文档](https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-pip)：凭据策略明确 Disabled 可导致每次认证，同时文档写明 **certain policies and configurations** 均可能影响自动连接；不能把“不存在的注册表键”视为本机唯一根因。

当时提出的两条未验证候选（仅保留历史，不作为当前推荐修改；本轮不执行）：

1. **改策略候选（未闭环，不能称一劳永逸）**：当时 demo 提供 `ChildSessionSpike.exe policy --apply`，写入
   `AllowDefaultCredentials` / `ConcatenateDefaults_AllowDefault` / `AllowDefCredentialsWhenNTLMOnly` / `ConcatenateDefaults_AllowDefNTLMOnly`
   = 1，并在两个子键里只列 `TERMSRV/localhost`、`TERMSRV/127.0.0.1`。**策略在下次登录时生效**，改完要注销/重启一次。
2. **不动策略**：`ChildSessionSpike.exe savecred` 输一次密码 → 存进 Windows 凭据管理器 `TERMSRV/localhost`
   → `connect` 时 `CredRead` 读出来，经 `IMsTscAx.UserName/Domain` + `IMsRdpClientAdvancedSettings.ClearTextPassword` 注入
   （ActiveX 自己不读凭据管理器，必须调用方喂）。属性注入通道已用假密码验证不报错。

另注：本机 `DevicePasswordLessBuildVersion=2`（仅允许 Windows Hello 登录），该配置下 Microsoft 帐户在本机没有可用的密码凭据。
本机实测可以输对密码，所以这条不是当前根因；但若注销后仍弹凭据，需要一并处理。

上句“不是根因”也仅为当时判断：能输入有效密码不排除 Hello/PIN/默认凭据可用性与自动登录相关，不能把交互认证成功推成已排除 SSO 配置问题。本轮新 PoC 未改这些策略，连接结果独立登记于 §6。

## 5. 待验证

- 跨会话 ICorDebug attach（`.NET` 目标进程在子会话内，调试器在父会话）。
- 子会话内 `SendInput` 物理输入（`clickPoint`）是否落到子会话输入队列。
- 免密：三条候选路径（委派策略 / 凭据注入 / 关闭 Hello-only）都尚未在本机实际生效验证过——见 §4.5。

## 6. 2026-11-14： 原生输入补充 PoC

### 6.1 环境、源码与授权范围

- 当前机器只读查询：Windows 11 专业版 `10.0.26300`、console SessionId=1、RDP ActiveX 已注册。旧 §2 的 “Windows 10 Pro 26H2” 为历史记录中的版本称呼错误；当前以本轮 CIM Caption/Version 为准。
- `WTSIsChildSessionsEnabled` 初值 false；`WTSGetChildSessionId` 初始失败、error=1168、id=0xffffffff。维护者明确授权“允许启用并验证”；经一次 UAC 调 `WTSEnableChildSessions(true)` 返回成功，未改凭据委派/Hello/证书/安全策略。
- 新源码在 [`tests/Fixtures/NativeInputSpike/`](../../../tests/Fixtures/NativeInputSpike/README.md)，独立于 slnx/CI。Debug 框架依赖构建直接加载宿主 `C:\Program Files\dotnet\shared\Microsoft.NETCore.App\10.0.14\System.Private.CoreLib.dll`；没有自包含发布、额外 Runtime 安装或复制部署环境。
- 旧 `../../Demo/ChildSessionSpike` 在当前设备不存在，不能据旧说明假定可执行。新 ActiveX/计划任务实现参考了经 `gh` 阅读的 `babalae/better-genshin-impact` 的 `RdpActiveXHost.cs` / `ChildSessionProcessLauncher.cs`，但实验进程不引用整个 BetterGI 或 MCP 宿主。
- 当前一次主实例：`child-b7a17595456542428d8667270db5320f`，父 holder PID 22276，child SessionId=2。连接未改认证策略即成功；`query session` 同时列 console 与 child Active。这不解释旧机/旧轮的认证问题，也不保证其他配置同样免密。
- 通过 `TASK_LOGON_INTERACTIVE_TOKEN` + `RunEx(..., TASK_RUN_USE_SESSION_ID, 2, ...)` 投送普通权限 worker；临时任务随即删除。多次重新构建/投送 worker，不等于多次新建 Session。

### 6.2 真正经过输入队列的动作与 IME 边界

自绘 Canvas 可被 UIA 找到，但 Invoke/Value 不支持（后台对照见 research/07）。测试目标包含普通消息区域与“本窗是 child 前台 + `GetAsyncKeyState(VK_LBUTTON)<0`”才计数的区域；拖拽通过鼠标 down→多步 move→up，文本与快捷键由目标正常消息泵处理。不是通过调用业务 handler 或 UIA Invoke 完成。

- child worker 的 `SendInput` 点击使严格区域计数增加，拖拽终点为 `(230,190)`；目标在 child 内为前台。父会话不激活 RDP 客户端来协助操作。
- 初始键码输入后 Text 为空、KeyDown 为 `ProcessKey`；扫描码对照亦未直接得到已提交文本。**随后父侧截图实际看到 IME 的拼音组合串与候选栏**，所以这证明输入到达而仍在 composition，不是“子会话收不到键盘”。原始证据为 `keyboard-initial.json`、`keyboard-probe.json`、`scan-probe.json`。
- 正常发送 Enter 提交后，Text=`realinput`，随后 Ctrl+K 计数增加；再走 SendInput/KEYEVENTF_UNICODE 得 `realinput隔离Input`，Ctrl+F6 也增加。未关闭 IME，未使用 SetText/ValuePattern 替代。记录 `ime-commit-comparison.json`。
- `KEYEVENTF_UNICODE` 是 VK_PACKET 系统输入，不等同物理 HID/扫描码；本轮分别记录两者。结果说明应用脚本要正确处理 IME 提交，不是自动获得“所有中文输入法/键盘布局都可靠”的保证。

### 6.3 约一分钟循环与父侧观测

16:33:57–16:35:07 的父侧观测约 70s，共 1115 个采样；输入循环约 60s、**25 轮全部通过本轮业务断言**：严格前台点击、拖拽、IME 提交文本、Unicode 文本、Ctrl+K、Ctrl+F6 与 WGC 返回。始终使用 worker PID 23480，循环结束严格点击/拖拽/两类快捷键均为 26（包括先前单轮）。

- `live-rounds.json` 保存每轮输入前后状态与截图路径；当时读取 `parent-observations.json` 得父侧 `markedKeyboard=0`、`markedMouse=0`，1115 个样本的前台 HWND 相同，光标仅一个位置。**原始 70s 观测文件随后被早期脚本的最终短复验覆盖，目前仅保留当时工具输出中的统计证据，不声称完整原始采样仍在。** 已修正脚本，每次 Exercise 使用新 `run-<id>/` 保留历史。
- 本轮最后窗口 PNG 已实际读取，能看到计数 26、拖拽终点和 `realinput隔离Input`；不是仅看哈希/非黑就签收画面。未对所有 25 张做 OCR/逐像素验算。
- **人工反馈与采样分别记录**：最初只有机器证据；随后维护者明确回复“不会抢我操作，这个项目之前好像已经测试过了”，并要求不重复同类验收安排。本轮据此取得正面现场反馈，不再重复安排人工循环；但不能把这句话扩写成 70s 每秒都在打字/拖拽，也不能把只有一个光标位置的采样说成完整人工动作轨迹证明。
- 本轮未测 10 分钟压力、多种框架、其他应用、自定义游戏输入、物理 HID 依赖或全部硬件。25 轮仅支持该 fixture 与当时连接状态。

### 6.4 外层客户端状态与 child 内目标状态

原始记录：`client-view-matrix.json`、`target-view-matrix.json`。

| 状态 | 观测 | 判定边界 |
|---|---|---|
| client 可见、父侧失焦 | 上述 25 轮输入成功，父前台未转入测试客户端 | 本机本轮通过，不等于人工持续并行已签收 |
| **client 最小化** | 所有四类输入命令返回“SendInput 未成功提交”，child 目标不在前台；同轮 WGC 仍返回非黑 642×382 | **输入失败**；截图返回不能证明输入可用或帧新鲜，不能推荐最小化保活 |
| client 隐藏（ShowWindow SW_HIDE，不 Disconnect） | 严格点击/拖拽/Ctrl+K 各增 1，Unicode 文本正确，WGC 返回 | 单次通过；不是隐藏目标窗、不是断开会话；还需长循环验证 |
| child 内目标最小化 | PrintWindow 返回 160×28，仅最小化外形；消息区域仍响应 | 非黑不等于完整目标截图；未签收保持最小化时真实命中 |
| child 内目标隐藏、Normal | 截图回退 BitBlt，实际读图为后方 console/桌面内容，而非隐藏目标 | **目标截图失败**，即使 API 返回非黑；消息区域仍可改变状态 |
| 将 child 内目标显式还原并激活后 | 真实动作与 IME 提交重新成功，父会话不需切前台 | 允许作为 child 内恢复步骤，但不冒充“隐藏/最小化原状态也能真实命中” |

### 6.5 跨 Session 调试实测

在父侧现有 MCP/Engine 对 child worker PID 23480：

1. `debug_attach(23480)` 成功，初始冻结态 `debug_frame(all=true)` 可读 8 个托管线程栈。
2. 以 `NativeInputSpike.ProbeForm::Snapshot` 设置断点（该构建 token=`0x060000dc`），continue 后由普通 CLI status 请求驱动，命中 UI 线程 38236。
3. `debug_frame` 读到 17 帧调用栈与 this 字段（父 Session=1、child=true、计数等）。
4. `debug_session(action=disconnect)` 成功，等待中的 CLI status 请求正常完成，目标继续运行。

这是当前用户/权限/.NET 10 下 **attach→断点→读栈/字段→detach** 的正面证据，不外推 Main 前跨 Session launch、所有 CLR/位数、提权目标或任意进程。实验时未并发输入，不把调试冻结造成的 GUI 等待算输入失败。

### 6.6 本轮尚待收口

- 父锁屏、完整 Session 重建压力与更广范围兼容性；已执行的断开/清理和最终源码短复验见 §6.7。
- 更长时间、更多人工操作组合与应用框架、实际 Windows App SDK/COM/专用驱动/硬件访问；已有维护者“不会抢我操作”的正面反馈，当前机器实测仅覆盖宿主 Runtime 和 fixture 的 GUI/调试。
- CLI 已有 PoC 控制入口；任意脚本投送、完整任务取消/恢复、正式 Runtime/MCP 路由均未实现。

**当前判断**：Child Session 是有实测支撑的前台真实输入补充候选，不是替换全部 UIA 的唯一执行方式。当前不能声明全部最终目标已验收，尤其不能承诺最小化客户端可靠或所有宿主设备无差别复用。

### 6.7 最终源码短复验与清理

- 输入护栏进一步改为每次 SendInput 前核对目标前台，停止误投；异常时仅释放本轮已经提交的按下状态。worker 同时交叉检查连接记录的 parent/child ID，Desktop 查询失败时拒绝。没有在 child 内调用 `WTSGetChildSessionId` 来验证“自己是 child”——该 API 查询的是调用方的子会话，不是反查父关系。
- 最终 Debug 构建 0 警告、0 错误；新版脚本 5s 短复验 **2 轮、allMachineChecksPassed=true、parentMarkedInputsAbsent=true、observerExitCode=0**。这是最终护栏版本的短复验，不把前述 25 轮当成最后代码逐字相同的全量回归。
- 最终脚本另做无 worker 的失败口径复验：退出码 1、rounds=0、allMachineChecksPassed=false，保留错误摘要/观测报告且观察器正常退出。原始目录 `failure-check-b3e891574a324f0c94d3531eeb02d397/run-a9bb59152e9f44d489b37496a8faf920/`。这验证“不把启动失败或零轮当成功”，不是输入交互通过。
- 最终后台消息复验仍得到普通点击/拖拽响应、严格前台点击/快捷键未通过；结果在 `background-final-ebe04a3694b54982983bdd695ccc84da/background-report.json`。这轮父前台从一个非测试窗口切换到另一个非测试窗口，不能继续沿用首轮“前台恒定”表述；尚无证据归因于自动化，且不把采样切换本身当成功或失败。
- 主动关闭匹配 holder 后，worker 仍在 Session 2 且可回复 status，目标 foreground=false；`real` 被护栏拒绝，记录 `disconnect-result.json`。这是正确拒绝与生命周期观察，**没有绕过护栏测 Disconnected 状态的原始 SendInput 成功性**。
- `stop-child` 在核对当前父/child ID、worker PID/创建时间后异步请求注销并有界等待，返回 `stoppedChildId=2, holderExited=true`，记录 `stop-result.json`；probe 确认没有 child。没有注销父会话或结束其他用户进程。
- 经 UAC 恢复 `WTSEnableChildSessions(false)`，probe 返回 enabled=false、hasChild=false，回到实验前状态。未安装驱动、未改认证/安全策略，没有自动化输入任务留在后台。
