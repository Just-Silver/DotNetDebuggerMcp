<!-- 职责：记录 RDP Child Session（loopback 子会话）方案的实测验证结果——环境硬条件、投送/UIA/截图在子会话内的实测表现、时序与参数、硬限制；
     不写：桌面隔离方案的全景比较与选型（见 research/07）、产品接入形态设计（见 specs/ 与 decisions）。 -->

# RDP Child Session 实测（spike 记录）

> 结论随实测追加。每条都标注「怎么测的 + 观测到什么」，无法复现的推断不写入。

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
| 12 | 免密登录的根因 | 读策略键 | `HKLM\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation` **不存在**（默认=不允许把默认凭据委派给任何服务器）→ 子会话（必须是 CredSSP）拿不到 SSO 凭据，只能弹登录界面。详见 §4.5 |
| 13 | **Active 子会话内截图**（人工输密码连上后，会话 3，1008×729） | 投送 worker 后就地 `CaptureScreen` + `CaptureWindow` | **全通**：全屏 BitBlt 1008×729、整窗 **WGC 706×413**，均非纯黑；8/8 项 PASS。父会话写入的文本与点击结果都能在截图上看到 |

## 4. 硬限制与设计含义

1. **UIA 按会话隔离**：父会话里的 MCP host 看不到子会话的 UIA 树，所以必须「会话内 actuator」——host 负责持有 RDP ActiveX 保活与 `debug_*`，actuator 在子会话内跑 `ui_*`/`screenshot`，两者用命名管道通信。
2. **截图能力取决于会话是否 Active**：客户端断开后无显示面，BitBlt 必败、WGC 也不出帧，只剩 PrintWindow（依赖目标窗口自绘，Chromium/游戏/部分 UWP 不稳）；客户端连着（Active）时 BitBlt 与 WGC 都正常。**保持 ActiveX 连接 = 保持 WGC/BitBlt 可用**，这是方案的前提而非可选项。
3. **开启子会话要管理员，投送不要**：前者一次性（系统级开关），后者每个子会话都要跑，用计划任务即可，不要求宿主提权。
4. **自适应用动态分辨率**：`UpdateSessionDisplaySettings` 必须在连接后约 1–2 s 调用（过早 `E_UNEXPECTED`），参数物理尺寸按 96 DPI 换算成毫米；`SmartSizing` 官方只写「缩放填满客户区」、未承诺保持纵横比，故不依赖它。
5. **密码**：Hello-only 配置下无法免密，需人工输入一次；关闭「仅允许 Windows Hello 登录」后官方自动登录才生效。

## 4.5 密码：为什么本机每次连接都要输

现象：官方语义是 `Connect()` 自动登录子会话、**不弹凭据**（例外只有「父会话用智能卡登录」与「启用 child sessions 之前就已登录」），
但本机实测：每次新建/重连子会话都停在 `Connected=2`，必须人工输入一次密码。

已排除：
- **不是 NLA 配置问题**：把 `EnableCredSspSupport` 设为 false → `Connect()` 直接 `E_INVALIDARG (0x80070057)`，即子会话必须走 CredSSP。
- **不是旧子会话脏**：`logoff 2` 清掉旧子会话、重建全新子会话后照样要凭据。
- **不是客户端代码的问题**：属性全部按子会话要求设好（`Server=localhost`、`EnableCredSspSupport=true`、`ConnectToChildSession=true`、`EnableZoom=true`，其余显示/音频/键盘参数齐全）后，行为不变——仍停在 `Connected=2` 等凭据。

根因（机制层面）：子会话走 CredSSP，需要**默认凭据委派**（SSO）。Windows 默认策略是「不允许把默认凭据委派给任何服务器」——
本机 `HKLM\SOFTWARE\Policies\Microsoft\Windows\CredentialsDelegation` 整个键**不存在**（= 未配置 = 禁止委派），
于是 CredSSP 拿不到当前用户的凭据，只能退化成在服务端弹登录界面。该策略的官方语义：未配置 → 委派不允许到任何计算机；
启用 → 只能委派给列表里的 SPN（`TERMSRV/<主机>`），且**下次登录**才生效。

两条解法（demo 里都已备好入口）：

1. **改策略（推荐，一劳永逸）**：管理员运行 `ChildSessionSpike.exe policy --apply`，写入
   `AllowDefaultCredentials` / `ConcatenateDefaults_AllowDefault` / `AllowDefCredentialsWhenNTLMOnly` / `ConcatenateDefaults_AllowDefNTLMOnly`
   = 1，并在两个子键里只列 `TERMSRV/localhost`、`TERMSRV/127.0.0.1`。**策略在下次登录时生效**，改完要注销/重启一次。
2. **不动策略**：`ChildSessionSpike.exe savecred` 输一次密码 → 存进 Windows 凭据管理器 `TERMSRV/localhost`
   → `connect` 时 `CredRead` 读出来，经 `IMsTscAx.UserName/Domain` + `IMsRdpClientAdvancedSettings.ClearTextPassword` 注入
   （ActiveX 自己不读凭据管理器，必须调用方喂）。属性注入通道已用假密码验证不报错。

另注：本机 `DevicePasswordLessBuildVersion=2`（仅允许 Windows Hello 登录），该配置下 Microsoft 帐户在本机没有可用的密码凭据。
本机实测可以输对密码，所以这条不是当前根因；但若注销后仍弹凭据，需要一并处理。

## 5. 待验证

- 跨会话 ICorDebug attach（`.NET` 目标进程在子会话内，调试器在父会话）。
- 子会话内 `SendInput` 物理输入（`clickPoint`）是否落到子会话输入队列。
- 免密：三条候选路径（委派策略 / 凭据注入 / 关闭 Hello-only）都尚未在本机实际生效验证过——见 §4.5。
