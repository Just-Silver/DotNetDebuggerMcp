<!-- 职责：汇总 Windows 上「把目标进程/窗口隔离到用户看不见的地方」的全部可用技术，并给出对本项目的选型评估；不写：最终 spec 的参数设计、任务拆解、验收标准（属 spec/plan）。 -->

# Windows「独立窗口/独立桌面」技术全景与选型

> 背景：U1A spec §19 把「独立 Windows 桌面/VM 隔离」留作后续 spec。本文件回答
> **Windows 到底提供哪些形态、每一种的硬约束是什么**，并给出选型建议与待验证点。
> 调研日期：2026-10-23（网络调研 + BetterGI 源码核对）。

## 0. 一句话结论

隔离的真实分水岭不是「窗口藏不藏」，而是**自动化与被调试进程能不能留在同一个 Windows 桌面/会话里**：
UIA、窗口枚举、窗口消息、`SendInput` 都只作用于调用线程所在的桌面/会话。
因此任何真隔离方案都呈现同一种形态——**在隔离域内再跑一份 agent，外层只做转发**。

### 0.1 需求澄清（2026-10-23，用户裁定）

真正的验收口径是 **「不抢用户的鼠标/键盘/焦点」**，**不是**「用户看不见窗口」。
据此，本文件的隔离方案（C/D/E/F/G）降级为**硬保证兜底**；主线应是「后台不激活驱动」，
见 §5。

## 1. 技术全景（9 类）

| # | 技术 | 隔离单位 | 用户可见 | 截图 | UIA | 输入 | 前置条件 | 成熟度 |
|---|---|---|---|---|---|---|---|---|
| A | 窗口不激活 + 挪副屏 | 同桌面 | 可见 | WGC/BitBlt/PrintWindow 全可用 | 直接可用 | 正常 | 无 | 官方 |
| B | 窗口挪到屏幕外（offscreen） | 同桌面 | 不可见 | PrintWindow（窗口仍在合成） | **直接可用**（普通窗口） | 光标到不了，只能消息 | 无 | 民间成熟 |
| C | `CreateDesktop` 隐藏桌面 | 同 station 的新桌面 | 不可见 | **仅 PrintWindow** | 需在桌面内（或绑定该桌面的线程内） | 只能 PostMessage/SendMessage | 无 | 官方 API，民间成熟 |
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
- **`SendInput` 在非交互桌面上直接失败**（只投递到交互桌面）→ 只能 `PostMessage`/`SendMessage`；
  对 raw input / DirectInput / 检查物理按键状态的应用无效。
- 截图：`BitBlt` 屏幕 DC 只得到活动桌面（黑）；WGC 需要 DWM 正在合成的窗口，隐藏/最小化/cloaked
  窗口返回陈旧或空白帧（社区实现统一用 `IsWindowVisible && !IsIconic && !DwmCloaked` 作闸门）；
  **唯一可靠的是 `PrintWindow(hwnd, hdc, PW_RENDERFULLCONTENT)`**（Win8.1+ 必须带该 flag，
  否则边框错乱、浏览器类窗口更易失败）。
- 实测口径：PrintWindow 对 Win32 / WinForms / WPF / Electron / Qt / GTK 可用；**DirectX/OpenGL
  全屏出黑图**（窗口化通常可用）。
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

约束：目标常是 WPF/WinForms/Win32 桌面程序；工具面已 100% UIA 语义（不注入物理输入）；
截图依赖 WGC→PrintWindow→BitBlt 三级链；调试走 ICorDebug（会话/桌面无关）；产品要能一次安装长期用。

| 方案 | 零打扰 | 截图 | UIA | 免驱动/免管理员 | 工程成本 | 结论 |
|---|---|---|---|---|---|---|
| A 副屏不激活 | 部分 | ✅ | ✅ | ✅ | 1 天 | **先做（止血）** |
| B offscreen | 部分（应用自抢前台仍会） | ✅ PrintWindow | ✅ | ✅ | 1–2 天 | **先做（与 A 同批）** |
| C CreateDesktop | ✅ | ✅ PrintWindow（DX 全屏黑） | 需桌面内 agent 或桌面线程 | ✅ | 3–5 天 | **主推候选（需 spike）** |
| D Child Session | ✅ | ✅ 真实 | 需整份 server 进会话 | ❌ 管理员+RDP+密码 | 1.5–2 周 | 保底（要"人也能看"时） |
| E 虚拟显示器 | ✅ | ✅ 真实 | ✅ | ❌ 一次装驱动 | 2–4 天 | 备选（要 WGC 画质时） |
| F Sandbox | ✅ | ✅ | 需 guest 内 agent | ❌ | 重 | 否决 |
| G VM | ✅ | ✅ | 需 guest 内 agent | ❌ | 最重 | 否决 |
| H 虚拟桌面 | ✅ | ❌ | ❌ | ✅ | — | 否决 |
| I 会话 0 | ✅ | ❌ | ❌ | ❌ | — | 否决 |

选型逻辑：
1. 我们要的是「**调试/验证本地 .NET 程序时不打扰用户，同时 agent 还能看界面**」，
   不是安全沙箱 → 排除 F/G/I。
2. H 既不保 UIA 也不保截图，且 API 不可持续 → 排除。
3. D 能力最完整但要管理员 + RDP + 密码，且必须把整个 server 搬进子会话 → 作保底。
4. **C 是唯一「零依赖 + 真隔离」的组合**，且代价落在我们本来就有的 PrintWindow 链上；
   全 UIA 语义的既有设计天然规避了隐藏桌面不能物理输入的缺点。
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

`src/` 内没有任何 `SetForegroundWindow` / `SetCursorPos` / `SendInput` 直调，抢操作只来自三处：

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
| L3 | `CreateDesktop` / RDP Child Session（本文件 C/D） | 硬保证（输入队列物理隔离），只在目标反复抢前台/独占全屏时启用 |

### 5.3 验收断言

一次 e2e：动作（含 launch、invoke、input、windowstate、verify 的 uiAction）前后
`GetCursorPos`、`GetForegroundWindow`、`GetGUIThreadInfo(前台线程).hwndFocus` 三者均不变；
期间宿主不调用任何前台 API（源码扫描守卫，已有 `NoPhysicalInputGuardTests` 可扩展）。

