<!-- 职责：解释 Windows GUI 自动化干扰机制、汇总不干扰用户的交互方案与证据，并给出项目适用性评估；不写：产品参数、正式实施任务表或 Child Session 实测全文（见 research/08）。 -->

# Windows「独立窗口/独立桌面」技术全景与选型

**当前任务已更正（2026-11-14）**：解决 UIA 不足时的交互与用户操作冲突，不为隔离而隔离、不要求所有操作走 SendInput。当前研究与推荐归 §6；独立 PoC 设计见 [试验提案](../specs/2026-11-14-native-gui-input-isolation-poc-design.md)，实际代码见 [NativeInputSpike](../../../tests/Fixtures/NativeInputSpike/README.md)。

> 背景：U1A spec §19 把「独立 Windows 桌面/VM 隔离」留作后续 spec。本文件回答
> **Windows 到底提供哪些形态、每一种的硬约束是什么**，并给出选型建议与待验证点。
> 初次调研：2026-10-23（网络调研 + BetterGI 源码核对）。2026-11-14 按「宿主原生环境 + 用户/Agent 并发真实输入」重新核查，当前结论见 §6；旧选型保留其历史语境，不是新目标的验收证明。

## 0. 一句话结论

隔离的真实分水岭不是「窗口藏不藏」，而是**目标是否拥有不与用户争用的输入环境**。
窗口消息有同 Desktop 限制；窗口枚举、UIA 的根与 provider 可达性也有桌面/会话边界，但不能把这些 API 等同于输入路由。
`SendInput` 没有 HWND/HDESK/Session 参数，不能因调用线程绑定隐藏桌面就认定可以向该桌面真实输入。
跨会话候选通常需要会话内 actuator；仅把 agent 搬进隐藏 Desktop 并不能使它获得第二个活动输入桌面。官方依据见 §6。

### 0.1 需求澄清（2026-10-23，用户裁定）

真正的验收口径是 **「不抢用户的鼠标/键盘/焦点」**，**不是**「用户看不见窗口」。
据此，本文件的隔离方案（C/D/E/F/G）降级为**硬保证兜底**；主线应是「后台不激活驱动」，
见 §5。

## 1. 技术全景（9 类）

| # | 技术 | 隔离单位 | 用户可见 | 截图 | UIA | 输入 | 前置条件 | 成熟度 |
|---|---|---|---|---|---|---|---|---|
| A | 窗口不激活 + 挪副屏 | 同桌面 | 可见 | WGC/BitBlt/PrintWindow 全可用 | 直接可用 | 正常 | 无 | 官方 |
| B | 窗口挪到屏幕外（offscreen） | 同桌面 | 不可见 | PrintWindow（窗口仍在合成） | **直接可用**（普通窗口） | 光标到不了，只能消息 | 无 | 民间成熟 |
| C | `CreateDesktop` 隐藏桌面 | 同 station 的新桌面 | 不可见 | PrintWindow 候选，依赖应用；非活动态 WGC/DWM 待测 | 需桌面内客户端验证 | UIA/消息候选，不提供第二活动输入桌面 | 无 | 官方对象 API；后台自动化为应用相关实现 |
| D | RDP Child Session（桌面分身） | 新会话（登录会话） | 不可见（可嵌 RDP 窗口显示） | **完整可用**（会话内有真实桌面） | 需在会话内跑 agent | 会话内真实输入 | 管理员 + RDP 主机 + 账户密码 | 官方（Power Automate PiP 同源） |
| E | 虚拟显示器（IddCx 驱动） | 同桌面，多一块显示器 | 用户看不到（除非切过去） | **完整可用**（按显示器抓） | 直接可用 | 正常（但光标可进入该屏） | 装签名驱动（管理员一次） | 官方模型 + 成熟第三方驱动 |
| F | Windows Sandbox | Hyper-V 轻量 VM | 独立窗口 | 可用 | 会话内（官方 `winapp ui --on sandbox`） | 需交互客户端 | Win11 24H2+、虚拟化、单实例 | 官方 |
| G | Hyper-V / 云 VM | 整机 | 独立窗口 | 可用（控制台/guest 内） | 会话内 | 会话内 | 重 | 官方 |
| H | Win+Tab 虚拟桌面 | 同会话，窗口 cloaked | 不可见 | WGC 无表面；PrintWindow 存疑 | **拿不到**（RootElement 只覆盖当前虚拟桌面） | — | 无 | **未公开 COM，GUID 逐版本变** |
| I | 新 Window Station / 会话 0 / `CreateProcessAsUser` | 安全隔间 | 无显示输出 | 无 | 无 | 无 | 权限/凭据 | 官方，但不承载 GUI |

### 各技术的实现要点与已知坑

**A. 不激活 + 副屏**：`CreateProcess` 的 `STARTUPINFO.wShowWindow`（app 常忽略）→ 启动后
`SetWindowPos(hwnd, HWND_BOTTOM, x, y, SWP_NOACTIVATE)` + `ShowWindow(SW_SHOWNOACTIVATE)`。
只解决「启动抢焦点/挡主屏」；应用自己后续 `Activate()` 仍会抢前台，任务栏仍有项。

**B. offscreen**：把窗口移到虚拟屏边界之外，并用 `ITaskbarList`/`WS_EX_TOOLWINDOW` 去掉任务栏与
Alt-Tab 痕迹。窗口仍是**交互桌面上的普通窗口**，所以 UIA、`PrintWindow`、`GetWindowRect` 全部照常，
零依赖零权限。代价：窗口仍属于输入桌面（应用仍可能自抢前台）、光标到不了故不能真实点击、
部分应用会把自己的窗口位置夹回可视区。参考实现：`pip install VirtualDesktop`
（`hide_mode=offscreen`，退出还原位置/样式）。

**C. CreateDesktop**：`CreateDesktopW` + `STARTUPINFO.lpDesktop="名字"`（同 station 内即可，不必新
window station）。要点与坑：
- 窗口/钩子/消息按桌面隔离；**剪贴板按 window station 共享**（不隔离剪贴板）。
- `SetThreadDesktop` 只能在没有窗口/钩子的线程上调用 → 必须用专用干净线程。
- 非活动 Desktop 不是第二个 input desktop；`SendInput` 无目标桌面参数，不能承诺后台真实输入。
  「一定直接返回失败」不是本项目已取得的实测结论；失败、无目标效果及是否误投都应分别记录。
  UIA/消息与 Raw Input/DirectInput/异步按键状态不是等价路径。
- 截图：`BitBlt` 屏幕 DC 只得到活动桌面（黑）；WGC 需要 DWM 正在合成的窗口，隐藏/最小化/cloaked
  窗口返回陈旧或空白帧（社区实现统一用 `IsWindowVisible && !IsIconic && !DwmCloaked` 作闸门）；
  `PrintWindow` 是由目标程序处理绘制请求的候选，不保证帧完整/新鲜，不能称为「唯一可靠」通用解。
  `PW_RENDERFULLCONTENT` 也不是跨框架正确性的保证。
- 旧稿未附跨框架逐项原始报告，故 Win32/WinForms/WPF/Electron/Qt/GTK/DX/OpenGL 的覆盖均不得记为本项目实测通过；按目标窗口逐项验证。
- **跨桌面 UIA 不可靠**：从交互桌面的进程去枚举/访问隐藏桌面的窗口，UIA 返回
  `ElementNotAvailable`；`AutomationElement.RootElement` 恒等于调用方当前桌面。窗口消息也不能跨桌面。
  → 自动化必须跑在隐藏桌面内（独立 agent 进程），或在**绑定到该桌面的专用线程**内执行（未验证，
  见 §4 待验证点）。

**D. RDP Child Session**：`WTSEnableChildSessions(true)` 开启；用 `MsRdpClient` ActiveX 设
`IMsRdpExtendedSettings.Property("ConnectToChildSession")=true` + `Server="localhost"` 建立**本机
回环子会话**；往子会话里起进程用计划任务 `RunEx(null, TASK_RUN_USE_SESSION_ID, sessionId, null)`
（管理员）。微软文档明确：同一时刻只能有一个已连接子会话；子会话随父会话注销而终止、不能锁屏、
无屏保。Power Automate 的 picture-in-picture「Child session」模式就是这套技术。
坑：家庭版不支持；剪贴板与主会话共享；开机自启的应用会在两个会话各起一份（可能冲突）；
Office 不能两边同时开；PIN 仅首次可用；某些策略/凭据委派设置每次都要输密码；
装了 RDP Wrapper 不兼容（替换 `TermService` 的 `ServiceDll`）。

**E. 虚拟显示器**：IddCx（UMDF 间接显示驱动）在系统里加一块"真"显示器，窗口摆上去即在合成管线内，
`CaptureDisplay` 按显示器抓图完全正常，UIA 也是本桌面普通窗口。代表实现：
VirtualDrivers/Virtual-Display-Driver、parsec-vdd（IOCTL + 定期 ping 保活）、SudoVDA 等；
有封装好的 `VirtualDesktop`（`hide_mode=virtual_screen`，首次 UAC 装驱动）。
代价：驱动签名/分发/管理员一次安装；显示器拓扑变化会触发一次桌面重排；光标可走到该屏上。

**F/G. Sandbox / VM**：唯一能同时隔离文件系统与注册表的方案，也是唯一"多开互不影响"的方案。
微软 `winapp ui --on sandbox` 已把「宿主 CLI → guest 内 agent」这一形态产品化，并明确：
注入类动作需要已连接且非最小化的 Sandbox 客户端。代价是目标应用必须装在 guest 内、
调试附加无法跨 Hyper-V 边界（server 也得搬进 guest）、单实例、Win11 24H2+。

**H. Win+Tab 虚拟桌面**：只有 `IVirtualDesktopManager`（移动/查询）是公开的；**创建桌面**要
`IVirtualDesktopManagerInternal`，GUID 在 Win11 23H2/24H2 多次变更，社区结论是"别在产品里
commit"。而且窗口被 cloaked：UIA 从当前桌面拿不到，WGC 无表面。**否决**。

**I. 新 window station / 会话 0**：非交互站没有显示输出，`SwitchDesktop` 会失败，只适合纯安全隔离，
不承载 GUI 自动化。**否决**。

## 2. 直接同类产品（佐证架构形态）

| 产品 | 形态 | 关键点 |
|---|---|---|
| `aShanki/desktop-control-system` | 宿主 CLI + 隐藏桌面内 `dcs-agent`（命名管道 JSON） | 截图用 PrintWindow（明确"hidden desktops where BitBlt returns black"）；点击三级回退 UIA > PostMessage > SendInput；键盘用 `WM_CHAR`/`WM_KEYDOWN` |
| `codingmachineedge/lowlevel-computer-use-mcp` | MCP server + `create_headless_desktop` / `launch_on_headless_desktop` | 截图走 PrintWindow；后台点击/输入走窗口消息；需要人工登录时 `show_headless_desktop` 临时切桌面并挂"不可关闭横幅 + 紧急返回" |
| `pip VirtualDesktop` | 三档：`virtual_screen`（驱动）/ `offscreen`（无驱动）/ 私有桌面 | 私有桌面明确「外部进程拿不到窗口与像素，自动化必须在该桌面内跑」 |
| Power Automate「picture-in-picture」 | Child session / 虚拟桌面（预览） | 官方产品化；"View only" 阻断用户输入 |
| BetterGI「桌面分身」 | Child session + RDP ActiveX 嵌窗 + 计划任务塞进程 | 子会话内再跑一份自身，根进程用命名管道调度 |

## 3. 对本项目的评估

当时约束：目标常是 WPF/WinForms/Win32 桌面程序；主路径为 UIA 语义，但真实点击、focus、失焦提交等有副作用；
截图依赖 WGC→PrintWindow→BitBlt 三级链；调试走 ICorDebug（会话/桌面无关）；产品要能一次安装长期用。

| 方案 | 零打扰 | 截图 | UIA | 免驱动/免管理员 | 工程成本 | 结论 |
|---|---|---|---|---|---|---|
| A 副屏不激活 | 部分 | ✅ | ✅ | ✅ | 1 天 | **先做（止血）** |
| B offscreen | 部分（应用自抢前台仍会） | ✅ PrintWindow | ✅ | ✅ | 1–2 天 | **先做（与 A 同批）** |
| C CreateDesktop | 仅后台语义/消息候选，不满足并发真实输入 | 应用相关，未通验 | 需桌面内 agent 或桌面线程验证 | ✅ | 原估 3–5 天，非当前承诺 | **只适用于旧的语义自动化目标** |
| D Child Session | ✅ | ✅ 真实 | 需整份 server 进会话 | ❌ 管理员+RDP+密码 | 1.5–2 周 | 保底（要"人也能看"时） |
| E 虚拟显示器 | ❌ 同桌面仍争用光标/焦点 | 有显示面后逐项验证 | 同桌面可用 | ❌ 一次装驱动 | 原估 2–4 天，非当前承诺 | 仅补显示面，不是输入隔离方案 |
| F Sandbox | ✅ | ✅ | 需 guest 内 agent | ❌ | 重 | 否决 |
| G VM | ✅ | ✅ | 需 guest 内 agent | ❌ | 最重 | 否决 |
| H 虚拟桌面 | ✅ | ❌ | ❌ | ✅ | — | 否决 |
| I 会话 0 | ✅ | ❌ | ❌ | ❌ | — | 否决 |

选型逻辑：
1. 我们要的是「**调试/验证本地 .NET 程序时不打扰用户，同时 agent 还能看界面**」，
   不是安全沙箱 → 排除 F/G/I。
2. H 既不保 UIA 也不保截图，且 API 不可持续 → 排除。
3. D 能力最完整但要管理员 + RDP + 密码，且必须把整个 server 搬进子会话 → 作保底。
4. 当时优先考虑 C 是因为目标允许 UIA 语义路径；不应称其为并发真实输入的「零依赖 + 真隔离」方案。
   新目标明确要求真实输入后，该推荐不再成立，截图能力也仍需逐应用验证。
5. A/B 不解决真隔离，但一天成本消除 80% 的日常打扰，且是 C 失效时的回退。

> **收尾裁定（2026-10-23，用户）**：本轮不实施任何隔离或"不抢操作"改造。结论定为：
> ① 抢用户操作的根因是**物理输入属于会话的输入队列、无法按窗口限定**，因此根治只能靠
> 「目标自己拥有输入桌面的会话」= RDP Child Session（或 VM），`CreateDesktop` 不算根治
> （非输入桌面上 `SendInput` 不可用，仍受 UIA 有无的限制）；
> ② 现状（全 UIA 语义 + 默认零物理输入）继续作为安全默认值，`clickPoint=true` 仍是显式 opt-in；
> ③ 真要做时按 D29 的形态启动（外层 host + 会话内 actuator）。

## 4. 待验证点（决定 C 的最终形态，半天 spike）

1. **桌面线程内 UIA 是否可用**：专用线程 `SetThreadDesktop(OpenDesktop(隐藏桌面))` 后，
   `AutomationElement.RootElement` / `EnumWindows` / `FindWindow` 是否只看到该桌面，UIA 查询与
   Invoke/Toggle 是否成功。（若可用 → 单进程即可，无需第二份 server；若不可用 → 必须走
   "隐藏桌面内 agent + 命名管道" 形态。）
2. **PrintWindow 对本项目测试目标**（WPF `UiSampleApp`、WinForms）在隐藏桌面是否稳定出非黑帧，
   含 `PW_RENDERFULLCONTENT`、含子控件/弹窗/ComboBox 下拉（下拉常是独立顶层窗）。
3. **WGC 在隐藏桌面是否必然失败**（预期失败，用于确定"隔离模式下自动降级到 PrintWindow"的文案
   与错误口径）。
4. 附带确认：隐藏桌面上 `debug_terminate` / 会话释放（`WTSLogoffSession` 无关，C 不涉及）后
   桌面对象是否随最后一个进程退出而销毁；重复 launch 同名桌面 `CreateDesktopW` 会复用（需去重）。

## 5. 「不抢用户操作」的最小实现（与隔离解耦，当前主线）

> **收尾（2026-10-23）：本轮不实施，本节仅存档为备选清单**（裁定见 D29）。

口径：**用户的光标、键盘焦点、前台窗口在 agent 操作期间必须保持不变**；窗口是否可见不计。

### 5.1 现状盘点（2026-10-23，源码 + FlaUI 5.0.0 本地反编译核实）

以下是 2026-10-23 的盘点，不代表当前源码没有前台 API；2026-11-14 已确认 `UiPatternDispatcher.BringToForeground` 直调 `SetForegroundWindow`/`AttachThreadInput`。历史上识别的三类干扰入口如下：

| 入口 | 实际行为 | 依据 |
|---|---|---|
| `ui_action clickPoint=true` → `PerformClickPoint` → `element.Click(moveMouse:false)` | FlaUI 的 `moveMouse:false` 只表示"瞬移而非缓慢移动"：**仍会 `Mouse.Position = 可点坐标`（SetCursorPos）再 `Mouse.LeftClick()`（SendInput）** → 光标被瞬移、真实点击落在该点 | `FlaUI.Core.AutomationElements.AutomationElement.PerformMouseAction` |
| `ui_action verb=focus` → `element.Focus()` | 非窗口控件走 `FocusNative()`：`AttachThreadInput` + `SetFocus` → **键盘焦点进入目标窗口**（用户后续按键落入目标应用）；窗口控件走 `SetForegroundWindow`（已在 dispatcher 拦截） | `AutomationElement.Focus/FocusNative` |
| `EnsureWindowRestored` / `verb=windowstate` → `WindowPattern.SetWindowVisualState(Normal)` | provider 多实现为 `ShowWindow(SW_RESTORE)`，**会激活窗口** | UIA WindowPattern 约定 |

目标应用自身也会抢（启动首个 `Show`、模态框、`Activate()`、托盘气泡），外部无法只靠"我们别调"解决。

### 5.2 分级方案

| 级别 | 手段 | 强度 |
|---|---|---|
| L0 | 默认全走 UIA pattern（`Invoke/Toggle/Select/Value/ExpandCollapse`）——不需要前台、不动光标 | 已具备 |
| L1 | ① `clickPoint` 改**消息点击**：`PostMessage(WM_MOUSEMOVE → WM_LBUTTONDOWN → WM_LBUTTONUP)`（客户区坐标，不激活）；真实点击降级为独立显式开关或删除。② 删除 `focus` 动词（或加 `allowFocusSteal` 显式开关）。③ 还原/最大化窗口改用 `ShowWindow(hwnd, SW_SHOWNOACTIVATE)`。④ launch 后对目标主窗与新出现的顶层窗施加 `WS_EX_NOACTIVATE` + `SetWindowPos(HWND_BOTTOM, SWP_NOACTIVATE)`（用 `SetWinEventHook` 观察 `EVENT_OBJECT_SHOW`）。 | 保证"我们的代码不抢" |
| L2 | 会话期间 `SystemParametersInfo(SPI_SETFOREGROUNDLOCKTIMEOUT, 0)`，释放时恢复原值 | 抑制"目标应用抢"，**系统级副作用**，需 opt-in |
| L3 | `CreateDesktop` / RDP Child Session（本文件 C/D） | 两者不等价：C 隔离窗口但不增加活动输入桌面；D 才是并发真实输入的待验证候选 |

### 5.3 验收断言

一次 e2e：动作（含 launch、invoke、input、windowstate、verify 的 uiAction）前后
`GetCursorPos`、`GetForegroundWindow`、`GetGUIThreadInfo(前台线程).hwndFocus` 三者均不变；
期间宿主不调用任何前台 API（源码扫描守卫，已有 `NoPhysicalInputGuardTests` 可扩展）。

## 6. 2026-11-14： 不干扰用户的 GUI 交互复核

### 6.1 当前目标与证据口径

维护者更正：首要目标是补齐 UIA 不支持、需要鼠标/键盘/前台的交互，同时让用户继续工作；宿主软件/硬件复用、低成本和原调试流程是重要偏好，而非“所有操作必须一个隔离后端/一个 API”的先决条件。Sandbox/VM 不优先，但纳入成本对照。允许独立 PoC，不允许未经验证重构核心。

证据分四类：**官方契约**、**已读源码/产品文档**、**本机实测**、**未验证推断**。产品支持一种场景不证明本项目已通过；物理输入、SendInput 系统合成、窗口消息、UIA 语义分别记录。

### 6.2 为什么会抢用户操作

1. **同一 input desktop 共享系统光标与前台路由。** `SendInput` 把事件插进输入流，不带 HWND/HDESK/SessionId；键盘送往有焦点的前台线程，鼠标通常依据光标位置/捕获等路由。将目标挪副屏或添虚拟鼠标不创建第二套系统光标/焦点。[S1–S4]
2. **一个 Session 有自己的 WinSta0，WinSta0 同一时刻只有一个活动 input desktop。** Desktop 是窗口、菜单、钩子等对象域；Window Station 含 clipboard、atom table 和多个 Desktop。`CreateWindowStation` 不会创建第二个交互 WinSta0；非交互站可以有对象/窗口，不等于能承载可见交互。[S1–S3]
3. `SetThreadDesktop` 是线程对象归属，不激活 Desktop；已有窗口/钩子的线程不能随意切换。`SwitchDesktop` 会切可见/输入桌面，会打断用户。`OpenInputDesktop` 只打开当前输入桌面；Disconnected 时甚至可以返回恢复连接后才会活动的 Desktop，句柄存在不证明现在可交互。[S3–S7]
4. `SetCursorPos` 官方明确 cursor 为 shared resource，且调用时当前 Desktop 必须是 input desktop。`AttachThreadInput` 会合并线程输入状态，不会拆出独立焦点，也不能跨 Desktop 连接线程。[S8–S9]
5. 当前源码真实干扰不只 `clickPoint=true`：`UiPatternDispatcher.PerformClickPoint` 先 `BringToForeground` 再 FlaUI Click；后者即使 `moveMouse:false` 仍 SetCursorPos + SendInput。`BindingCommit.TryCommit` 会激活/聚焦并按 Tab，必要时真实点击；`focus` 和窗口还原亦有副作用。因此“用了 UIA 工具”不等于“没有物理输入/焦点副作用”。源码入口见 §6.7。

**结论边界**：没有受支持的“普通同桌面 SendInput 指定另一个 HWND 焦点、同时不影响用户前台”的公开用法。不能进一步夸成任何专用 provider、注入式虚拟化或第三方多席位都绝不可能；它们需各自证据。

### 6.3 后台交互：有价值，但不是通用真实输入等价物

| 方式 | 能做什么 | 不能据此保证什么 |
|---|---|---|
| UIA Invoke/Value/Toggle/Selection 等 | provider 实现了 pattern 时可直接操作业务控件，通常不需鼠标/前台 | 焦点、hover、游戏式输入、未暴露 peer 的自绘区域；provider 自身也可能激活窗口 |
| UIA Drag/DropTarget | **确实存在这些 pattern**，可读拖放状态/项目与效果；不能写成“UIA 没有 Drag” | `IUIAutomationDragPattern` 没有通用 `BeginDrag/MovePointer/Drop` 方法，不替代任意坐标鼠标轨迹 [S10–S11] |
| `SendMessage`/`PostMessage` | 向具体 HWND 发消息；部分 Win32、自绘消息驱动区域可实现后台点击/轨迹/字符 | 不等价更新系统输入流/真实键态/Raw Input；修饰键、鼠标捕获、IME、前台检查与框架过滤可不成立；SendMessage 应有超时，防目标挂死 [S12] |
| 控件/应用专用接口 | 在应用主动支持的前提下很可靠，适合有限控件或协作式测试接口 | 调内部命令或处理器不是物理交互，不签收输入路由和前台要求 |
| 应用内 hook/API 虚拟化/多光标软件 | 可以为受控应用模拟光标/伪造前台查询等，属候选特定兼容层 | 没有证据可覆盖普通任意 WPF/WinUI/Avalonia/游戏窗口；注入、位数、反作弊、版本漂移代价高，不作为稳定默认路径 |

WPF 基类 `UIElement.OnCreateAutomationPeer` 可返回 null；Avalonia 默认 `NoneAutomationPeer` 不作为 control/content 元素；WinUI 自绘元素亦须适当 peer/provider。**Canvas 容器无内建操作不等于其所有可访问子控件都必不可见**；自绘几何不会自动变成可操作控件。不能用一个框架的 pattern 集合推定全部框架能力。[S13]

本轮源码核验的两个开源隐桌面项目不是“后台真实输入通用解”：

- [`aShanki/desktop-control-system`，`b89400542d0596f3e5dbbff39479f6733ab3b48c`](https://github.com/aShanki/desktop-control-system/tree/b89400542d0596f3e5dbbff39479f6733ab3b48c)：`runtime/agent/mouse.py` 为 UIA → PostMessage → SendInput 回退，后者注释明确会动 global cursor；`keyboard.py` 主要用窗口消息。隐藏 Desktop + agent/IPC 可借鉴，但并非已验证真实输入隔离。
- [`codingmachineedge/lowlevel-computer-use-mcp`，`ae648ae6c994e8135c874cc895291896f60b8c19`](https://github.com/codingmachineedge/lowlevel-computer-use-mcp/tree/ae648ae6c994e8135c874cc895291896f60b8c19)：`winio.py` 主要 PostMessage/PrintWindow，`server.py` 热键有 ControlSend/消息及显式干扰护栏；隐藏桌面相关测试主要为 mock/模型测试。可复用机制思路，不将其 README 承诺当本机验收。

两者为社区实验实现，未发现足以支持通用性结论的独立实机验证。另有成熟 AHK ControlClick/ControlSend 类窗口定向工具，其价值也在于应用相关消息/控件路径，不等于任意前台程序的输入等价保证。

### 6.4 Session：值得优先验证的前台补充环境

普通 Windows 11 登录/快速用户切换可保留多个会话的进程，**不等于多个普通用户同时活动交互**。普通 RDP/FUS 的限制不可用补丁绕过。`LogonUser` 创建认证 token/LSA logon session，不创建带显示的 TS Session；`CreateProcessAsUser` 在 token 指定的已存在 Session 运行进程，不会新增可交互 Session，profile/环境/Desktop ACL 需分别处理。[S14–S17]

**官方例外候选：RDP Child Session。** 官方定义为与现有用户 Session 绑定的 special loopback RDP session；用 ActiveX `ConnectToChildSession`，同一时刻最多一个 active and connected child。它与任意两个普通并发 RDP 登录不是一回事，不能据此宣称 Win11 通用多用户并发已开放。[S14]

微软 Power Automate PiP 明确说 Child Session 支持 mouse/keyboard、physical UI automation，且用户可以在主桌面并行工作。这是比“理论上独立 WinSta0”更强的产品证据，但仍不保证自写 worker 在最小化/隐藏/Disconnected 等全部状态都可用。[S15]

- **复用环境**：同一 OS/文件系统和已安装软件，无需维护第二套 Windows/Runtime；同一账户也不需要新增第二个用户。父终端临时环境变量、session-local 命名对象、ROT/COM server、单实例应用和设备权限/独占状态不保证相同。[S16–S18]
- **硬件**：不是 VM 设备直通问题，但 RDP/驱动/API 仍可能按 Session 限制访问；GPU 图形路径与 console 可能不同。相同 Windows 安装 ≠ 所有设备在所有 Session 完全透明。所需硬件必须由目标实际调用验证，不凭枚举或一般架构签收。
- **前台**：目标可在自己的子会话成为前台，父会话用户继续用别的前台；这不意味着两个窗口在同一个 Desktop 都是系统前台。各 Session 的具体焦点/渲染需实测。
- **生命周期**：child 本身不能锁屏，随父会话注销结束；不证明父锁屏时连接持有器仍收帧。Disconnected 与 client 最小化/被遮挡必须区分。历史连接态/断开态截图证据见 [research/08](08-rdp-child-session-spike.md)。
- **自动管理**：一次启用需管理员（本机实测），登录是否免密取决于实际认证/策略；API 不保证每次零人工。计划任务 RunEx 定向投送是本机已用的路径；WTSQueryUserToken 要求 LocalSystem + SeTcb，不是普通管理员可随意调用。[S17]
- **产品支持与授权**：Power Automate PiP 文档列 Win11 Pro/Enterprise，Home 不支持；该产品还有 Entra 云机、共享剪贴板、应用双开等限制。不能把产品 SKU 表当所有底层 API 的完整白名单，也不能由“不需要 Remote Interactive right”自行推导全部 Windows/应用许可结论。

### 6.5 HID、显示与多席位

VHF 创建虚拟 HID 并提交报告，IddCx 创建/驱动显示输出；两者公开配置没有 HWND/Desktop/Session 定向输入参数。Raw Input 的 `hwndTarget`/`RIDEV_INPUTSINK` 是事件订阅，不是把用户系统键鼠专属分配给后台窗口；`RIDEV_NOLEGACY` 只影响本应用的 legacy 消息。DirectInput 是设备读取模型，也不创建第二套系统焦点。[S19–S22]

因此虚拟设备**有价值但不单独解决本题**：IddCx 可补 headless 显示面；专用 Raw Input 应用可区分设备；均不能据此承诺未改造普通桌面应用中的独立鼠标/键盘路由。第三方内核多席位可另有机制，不从“几个公开结构无目标字段”推出内核层绝无实现可能。

| 现有实现 | 证据与支持归属 | 本项目判断 |
|---|---|---|
| ASTER | 成熟商用、厂商支持同一 Windows 多席位；有虚拟显示文章、`mutectl` 与 Pro API 提示 | 需核对当前 build/签名/自动管理接口。厂商限制页称 Memory Integrity 不兼容，Secure Boot 与版本历史表述有冲突；本机 build 26300 相关驱动策略风险不适合默认安装。无已验证的本项目席位内软件输入证据 [S23] |
| Hydra Multi-Seat | 厂商多席位产品；文档描述真实外设分配 | 公开文档中 headless/集中管理等仍有待实现项；“没有虚拟键鼠”不能单独证明无法使用外部虚拟 HID，但该组合未获确认。不默认推荐 [S24] |
| MultiPoint / Enterprise multi-session | 微软 Server/云端产品，不是普通 Win11 本地多席位 API | 不是本机轻量默认路径；不将其授权外推到第三方客户端多席位 [S25] |
| Windows-driver-samples IddSample / VirtualDrivers VDD | 官方样例/社区成熟显示驱动，只提供 monitor/swapchain | 可作显示组件，不提供 seat 或独立键鼠；ViGEmBus 则是已退役的游戏控制器驱动，不是键鼠隔离解 [S22] |

本轮未安装上述驱动/产品，未关闭 HVCI/Secure Boot；厂商技术支持、驱动签名与微软许可是三件不同的事，不能互相替代。

### 6.6 可行性矩阵与推荐

资源/成本为定性判断，**不是本机性能测量**。✓ 表示相应受支持能力或指定实测，△ 表示条件/应用相关，? 为未测，✗ 为不提供该能力。

| 方案 | 后台语义/控件操作 | 前台真实输入且用户并行 | 宿主环境复用 | 渲染/截图 | 成本与成熟度 |
|---|---|---|---|---|---|
| 同桌面 UIA | ✓ provider 实现时 | ✗ 不提供第二前台；无需真实输入的操作可达目标 | ✓ | 普通可见窗成熟；隐/最小化需验 | 最轻；官方 API/成熟库 |
| 窗口消息/专用控件接口 | △ 本轮自绘消息区域有实测 | ✗ 对严格前台/系统键态目标不等价 | ✓ | 同桌面现有截图链可复用 | 轻；官方 API，兼容性按控件维护 |
| CreateDesktop + 同域 worker | △ 需 UIA/provider/消息验证 | ✗ 不产生第二活动 input desktop | ✓ 同 Session 软件最好复用 | 非活动态无通用正确性保证 | 轻；官方对象机制，完整兼容性未测 |
| 新 Window Station / Session 0 | △ 可有对象/消息，不是第二交互站 | ✗ | 部分上下文不同 | 无正常用户交互显示 | 不作前台交互路线 |
| 普通 RDP/FUS | △ 会话内可用 | ✗ 普通 Win11 不据此支持两个普通并发活动用户 | ✓ 软件，设备有条件 | 连接/断开相关 | 官方机制，但不是并行默认解 |
| **RDP Child Session** | ✓ 会话内 UIA/消息可组合 | **有官方场景支持，本项目正在实测** | ✓ 同 OS/运行时；设备/COM/调试逐项验 | 连接态 WGC/BitBlt 有历史证据 | 比同桌面重、比完整 VM 简化；管理员一次启用/认证/连接持有 |
| VHF / IddCx 单独使用 | 不提供控件语义 | ✗ 本身不提供独立系统焦点/输入环境 | ✓ 但驱动侵入高 | IddCx 可补显示面 | 驱动签名/管理员/OS 维护；非默认 |
| ASTER 等 Multiseat | △ 每席位内可做 | 厂商物理多席位成熟；自动化注入未验 | ✓ OS；账户/profile/设备冲突另算 | 多席位显示，headless 需确认 | 安装/重启/安全设置/许可/维护成本高 |
| Sandbox / 完整 VM | ✓ 内部运行时 | 可建立独立交互环境；本机 Sandbox 有部分输入证据 | ✗ 不继承宿主完整软件/设备环境 | 连接/状态相关 | 独立系统准备与硬件适配成本；仅条件备选 |
| Win+Tab 虚拟桌面 | △ 窗口分组/部分自动化 | ✗ 不创建第二输入流 | ✓ | cloaking 等需验 | 不作为输入隔离解 |

**首选组合而非单一替代**：保留已可靠的 UIA；对具体控件补经验证的窗口级交互；只把确需系统前台/真实输入的应用放到已验证交互环境中，当前优先 Child Session。消息路径是有边界的正面能力，不因不能覆盖全部操作而否决；也不得把消息动作标为物理输入成功。

**备选顺序**：无法使用 Child Session 时，先报告具体登录/设备/应用限制；有明确厂商支持及授权才试 Multiseat。只有应用易部署、硬件不敏感且维护者接受环境成本时再考虑 Sandbox/VM。若必要设备必须 console、应用必须系统前台且不能改造，同时用户要并行使用同一 input desktop，目前没有证据支持一个通用无干扰解；最小妥协是 UIA/控件级部分覆盖、明确独占时段或专用测试机，而不是偷偷抢焦点。

### 6.7 当前项目复用边界

| 当前入口 | 实际情况 / 集成影响 |
|---|---|
| `SharpSight.UiAutomation/UiAutomationService.cs` | 现有定位/读取/等待/语义分派可继续用；门面 internal，不为 PoC 提前公开全部 API |
| `UiPatternDispatcher.PerformClickPoint/BringToForeground` | 有真实点击及前台接管；隔离补充路径须在正确交互环境内执行，不能让父宿主先点 |
| `BindingCommit.TryCommit` | `ui_input` 默认提交会带来焦点/键盘/点击副作用；“后台安全”必须按实际路径分级，`commit=false` 又不保证 WPF 业务已提交 |
| `SharpSight.Capture/ScreenCapture.cs` | 全屏 BitBlt，整窗 WGC→PrintWindow→BitBlt；保留来源/DPI/原点。调用应在对应 Session 的 MTA 工作线程，不能阻塞目标 WinForms UI 线程等 WinRT async |
| `DebugSessionManager.LaunchAndAttachAsync/AttachAsync` | 默认在调用进程当前 Session 启动；attach 按 PID，无专门跨 Session 路由。跨 Session ICorDebug 和 Main 前 launch 要分开验证，不能只看接口无 Session 参数就承诺可用 |
| `DotNetDebuggerMcpCmd.DispatchCliAsync` / `DebugCliRunner` | 现有 CLI 主要反编译/元数据与一次性调试；尚无通用交互环境任务 CLI。新增 PoC 有独立脚本/CLI，不强迫全部经 MCP |

正式集成仅在对应能力验证后进行：薄 CLI 与 MCP 适配共享执行实例；保留原 UIA 工具及默认行为兼容，不全局强制新后端；元素/画面引用需关联会话、worker 代际和 frameId。细化实验设计归 [PoC 提案](../specs/2026-11-14-native-gui-input-isolation-poc-design.md)，本轮没有生产 `src/` 改动。

### 6.8 本轮后台消息 PoC 的初步实测

代码：`tests/Fixtures/NativeInputSpike/`。Debug 框架依赖构建直接加载宿主 `Microsoft.NETCore.App/10.0.14`，未安装额外 Runtime。原始报告：`tests/TestData/NativeInputSpike/background-3f73675aac1240e0a78ff90920f710b9/background-report.json`。

- 自绘 Canvas 的 UIA 查询成功，但 Invoke/Value 均不支持；消息驱动点击与拖拽在正常可见、最小化、隐藏（该轮隐藏前已最小化）均改变测试目标事件状态。**这是窗口消息交互，不是真实输入证明。**
- 严格“前台 + 异步鼠标按键状态”点击始终为 0；Ctrl+K 未触发业务快捷键，文本出现额外 `kk`。直接证明消息路径兼容性有限，不能作为透明真实键盘替代。
- 正常可见窗 WGC 644×397；最小化后返回 PrintWindow 237×39，仅有最小化窗口外形，不能签收完整目标画面；隐藏且最小化时抓取失败。非黑并不等于内容可用。
- 观测前后及三次中间采样父前台 HWND、光标位置相同；这是短采样证据，**未做持续人工操作/输入内容完整性验收**。父会话 worker 收到 `real` 命令时按护栏拒绝。
- 初版在 WinForms UI 线程同步调用 WGC 发生等待超时；已按能力库现有 MTA 使用约束移出 UI Invoke，随后上述流程完成。这是 PoC 调用方式修正，不改生产截图库。

Child Session 的本轮真实输入结果只登记 [research/08 §6](08-rdp-child-session-spike.md#6-2026-11-14-原生输入补充-poc)，不复制于此。

### 6.9 可核验来源

访问/核验日期：2026-11-14。GitHub 资料经 `gh` API/CLI 查询；微软 Learn 与厂商网站直接阅读。下列机制契约与本机实测分开使用。

| 编号 | 来源与要点 |
|---|---|
| S1 | [Window Stations](https://learn.microsoft.com/en-us/windows/win32/winstation/window-stations)：每 Session 独立 WinSta0，只有 interactive station 可见并接收用户输入 |
| S2 | [About Window Stations and Desktops](https://learn.microsoft.com/en-us/windows/win32/winstation/about-window-stations-and-desktops)：对象层级与共享资源 |
| S3 | [Desktops](https://learn.microsoft.com/en-us/windows/win32/winstation/desktops)：一个活动 input desktop、窗口消息同 Desktop 限制 |
| S4 | [SendInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendinput)：签名无目标窗口/会话，UIPI 与现有按键状态约束 |
| S5 | [SetThreadDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setthreaddesktop)、[CreateDesktopEx](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createdesktopexw)：归属、窗口/钩子及 heap 约束 |
| S6 | [SwitchDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-switchdesktop)、[CreateWindowStation](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-createwindowstationw)：可见/活动切换，不创建第二交互站 |
| S7 | [OpenInputDesktop](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-openinputdesktop)：断开会话可返回将来恢复时的输入桌面 |
| S8 | [SetCursorPos](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-setcursorpos)、[GetAsyncKeyState](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-getasynckeystate)：共享光标/input desktop 与键态条件 |
| S9 | [AttachThreadInput](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-attachthreadinput)：共享输入状态，不能跨 Desktop |
| S10 | [UIA Patterns Overview](https://learn.microsoft.com/en-us/windows/win32/winauto/uiauto-controlpatternsoverview)：包括 Drag/DropTarget；依赖 provider 实现 |
| S11 | [IUIAutomationDragPattern](https://learn.microsoft.com/en-us/windows/win32/api/uiautomationclient/nn-uiautomationclient-iuiautomationdragpattern)：状态/项目读取，无通用鼠标拖拽执行方法 |
| S12 | [PostMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-postmessagew)、[SendMessage](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-sendmessagew)、[PrintWindow](https://learn.microsoft.com/en-us/windows/win32/api/winuser/nf-winuser-printwindow)：消息/UIPI/同步等待及目标自绘责任 |
| S13 | [WPF 自定义控件 UIA](https://learn.microsoft.com/en-us/dotnet/desktop/wpf/controls/ui-automation-of-a-wpf-custom-control)：自定义 peer；本地 `../../Externals/{wpf,Avalonia,microsoft-ui-xaml}` 的 UIElement/Control/AutomationPeer/Canvas 源码核验 |
| S14 | [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions)、[ActiveX ExtendedSettings](https://learn.microsoft.com/en-us/windows/win32/termserv/imsrdpextendedsettings-property)：special loopback、单 child、父生命周期、认证与开关 |
| S15 | [Power Automate PiP](https://learn.microsoft.com/en-us/power-automate/desktop-flows/run-desktop-flows-pip)：child 支持 physical UI automation/用户并行，认证/SKU/应用限制；不要混同其 virtual desktop 预览模式 |
| S16 | [CreateProcessAsUser](https://learn.microsoft.com/en-us/windows/win32/api/processthreadsapi/nf-processthreadsapi-createprocessasuserw)、[LogonUser](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-logonuserw)：token Session、profile/环境/ACL，不能跨 Session 继承句柄 |
| S17 | [WTSQueryUserToken](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsqueryusertoken)、[TokenSessionId](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ne-winnt-token_information_class)：LocalSystem/SeTcb 权限要求；计划任务投送采用本机实测而非据此猜测 |
| S18 | [Kernel Object Namespaces](https://learn.microsoft.com/en-us/windows/win32/termserv/kernel-object-namespaces)、[Session Moniker](https://learn.microsoft.com/en-us/windows/win32/termserv/session-to-session-activation-with-a-session-moniker)：会话级对象/COM 激活边界 |
| S19 | [VHF](https://learn.microsoft.com/en-us/windows-hardware/drivers/hid/virtual-hid-framework--vhf-)、[VHF_CONFIG](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/vhf/ns-vhf-_vhf_config)：虚拟设备树与报告，不提供 HWND/Session 路由 |
| S20 | [RAWINPUTDEVICE](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-rawinputdevice)、[Raw Input](https://learn.microsoft.com/en-us/windows/win32/inputdev/about-raw-input)：设备来源区分与后台订阅，不是独立系统焦点 |
| S21 | [IddCx overview](https://learn.microsoft.com/en-us/windows-hardware/drivers/display/indirect-display-driver-model-overview)、[IDDCX_MONITOR_INFO](https://learn.microsoft.com/en-us/windows-hardware/drivers/ddi/iddcx/ns-iddcx-iddcx_monitor_info)：Session 0 UMDF 显示驱动与 monitor 信息 |
| S22 | [Microsoft IddSample 源码](https://github.com/microsoft/Windows-driver-samples/blob/d5569c08aa2818c6240744bb47a00f67f20fdb54/video/IndirectDisplay/IddSampleDriver/Driver.cpp)、[VirtualDrivers VDD](https://github.com/VirtualDrivers/Virtual-Display-Driver/tree/d7244969b2aa8bb38e76d79505eda217996cefea)、[ViGEmBus](https://github.com/nefarius/ViGEmBus/tree/d986e1d93708ec9b11049542fa6027272cce716c)：分别是显示设备/已退役游戏设备，不是输入席位 API |
| S23 | ASTER [限制页](https://dokwiki.ibiksoft.com/en/v3/core/faq/faq_limitations)、[虚拟显示](https://dokwiki.ibiksoft.com/en/v3/core/article/virtualmonitors)、[版本历史](https://dokwiki.ibiksoft.com/en/v3/core/history)、[席位 FAQ](https://dokwiki.ibiksoft.com/en/v3/core/faq/faq_terminals)：厂商能力与安全/自动管理不确定项 |
| S24 | Hydra [输入](https://hydramultiseat.com/docs/input/)、[兼容性](https://hydramultiseat.com/compatibility/)、[FAQ](https://hydramultiseat.com/questions/)、[路线图](https://hydramultiseat.com/roadmap/)：厂商多席位实现与尚未保证的 headless/安全条件 |
| S25 | [MultiPoint](https://learn.microsoft.com/en-us/windows-server/remote/multipoint-services/multipoint-services-stations)、[Enterprise multi-session FAQ](https://learn.microsoft.com/en-us/azure/virtual-desktop/windows-multisession-faq)：Server/云端边界，不外推本地客户端许可 |

另：`KEYEVENTF_UNICODE` 是 SendInput 的 VK_PACKET 输入队列路径，不是 ValuePattern 或直接设 Text；键码/扫描码与 IME composition 仍需分别验证，契约见 [KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput)。

