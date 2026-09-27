# 01 · 寻址通用（多显示器 / 前台 / 客户区 / hwnd / 元素级 / 坐标空间 / 旧帧护栏）

> **来源等级**：主体 A（源码核实：FlaUI/Windows-MCP/ShareX/PowerToys/本项目代码）+ B（官方文档：Microsoft Learn/WinRT）；个别条目标注 C（低星来源待核）。生成日期 2026-09-27。

> 结论优先；API 给签名/用法/坑；落地建议给改动点/代价/风险。

## ① 结论与推荐（先给答案）

| 目标 | 推荐方案 | 理由 |
|---|---|---|
| 多显示器 | 新增 `mode=display`（`N`/`primary`/`left`/`right`）；默认走 **GDI BitBlt 该显示器矩形**（复用 `CaptureScreen(clip)`）；WGC `CreateForMonitor` 仅 best-effort 增强 | 显示器级 BitBlt 就是虚拟屏矩形裁剪，零新抓取路径；`CreateForMonitor` 在 RDP/部分 Win11 26100 稳定 `E_INVALIDARG` |
| 前台窗口 | `windowTitle` 支持 `@active`（或 `foreground=true`）；`GetForegroundWindow`+`GetWindowThreadProcessId` → 当 hwnd 用 | 复用 window 路径；失败给「锁屏/提权/无交互桌面」中文原因 |
| 客户区 vs 边框 | window 加 `clientArea=true`：`GetClientRect`+`ClientToScreen`；**整窗默认改用 `DWMWA_EXTENDED_FRAME_BOUNDS`**（去阴影） | `GetWindowRect` 含 7px 隐形 resize 边+阴影，截出来有黑边/偏移 |
| hwnd 直寻址 | 加 `hwnd`（**string**，十进制约 `IntPtr`）；`IsWindow`+`IsWindowVisible`+`GetAncestor(GA_ROOT)==h` 校验 | 与 `FindMainWindow` 同源，省掉查找；string 避开 JSON >2^53 精度 |
| 元素级 | 加 `mode=element`，**复用 ui_* 的定位身份**（`process`+`index`/`name`/`type`）→ FlaUI `BoundingRectangle` → **Engine 从窗口帧裁剪**；可选 `highlight` | 遮挡安全必须「先拿窗口帧再裁」；身份复用现成 `UiElementLocator` |
| 坐标空间/护栏 | **`origin + scale`**（`screen = origin + image/scale`）统一表达；region 用轻量 `frameId` 护栏 | `origin+scale` 是现有 `k` 的超集，能统一 window/display/element 原点 |

**总判断**：寻址扩展**不需要 WinAppSDK、不需要重写抓取链**；元素级是唯一跨层改动（宿主 FlaUI → Engine 裁剪），成本中；`TryCreateFromDisplayId` 路线不值得跟（需 packaged capability + WinAppSDK + 不透明 token）。

## ② API 清单（名称 + 用法 + 坑）

### 多显示器
- `EnumDisplayMonitors(hdc=NULL, lprcClip=NULL, MonitorEnumProc, 0)`：枚举全部；此时回调 `hdc/lprcRect` 必为 NULL，**矩形要自己 `GetMonitorInfo` 取**。**枚举顺序未文档化**，不可当稳定编号。
- `GetMonitorInfoW(hMonitor, MONITORINFOEX)`：调用前**必须设 `cbSize`**；`rcMonitor`=物理矩形、`rcWork`=去任务栏、`dwFlags` 含 `MONITORINFOF_PRIMARY(0x1)`、`szDevice`（如 `\\.\DISPLAY1`）。
- `MonitorFromWindow/MonitorFromPoint/MonitorFromRect`：flags `MONITOR_DEFAULTTONEAREST=2`（默认取交集面积最大）；最小化窗口用最小化前矩形判归属。
- `GetSystemMetrics`：`SM_CMONITORS=80`、`SM_X/Y/CX/CYVIRTUALSCREEN=76/77/78/79`（原点可负）。项目 `GdiCapture.VirtualScreenRect()` 已用。
- `GetDpiForMonitor(MDT_EFFECTIVE_DPI=0)`：官方警告「不 DPI-aware，PM aware 线程应避免」；有 hwnd 时更稳用 `GetDpiForWindow`。本项目进程已 PMv2。
- WGC 抓显示器：`IGraphicsCaptureItemInterop::CreateForMonitor(HMONITOR, riid, void**)`；riid 仅 `GraphicsCaptureItem`（IID `79c3f95b-31f7-4ec2-a464-632ef5d30760`）；interop IID `3628e81b-3cac-4c60-b7f4-23ce0e0c3356`；**vtable：IUnknown 0-2 → CreateForWindow=3 → CreateForMonitor=4**（复用现有手写 vtable 模式）。
  - 坑：RDP/非交互/AppContainer/LowIL/跨用户提升 → `E_ACCESSDENIED`；部分 Win11 26100 机器对所有显示器 `E_INVALIDARG`；旧版无活动显示器会崩（Win11 修复）；WebRTC 判 Win10 20H1 前有虚拟屏 bug。
- `GraphicsCaptureItem.TryCreateFromDisplayId` + `Microsoft.UI.Win32Interop.GetDisplayIdFromMonitor`：**不推荐**（需 packaged `graphicsCaptureProgrammatic` capability；`Microsoft.UI.DisplayId` 与 `Windows.Graphics.DisplayId` 命名空间不兼容；`DisplayId` 不透明，社区说法冲突）。
- 备选 Desktop Duplication（`IDXGIOutputDuplication`）：可 SYSTEM 运行但绑 adapter/output、代码量大，本项目不值。

### 前台窗口
- `GetForegroundWindow()` + `GetWindowThreadProcessId(HWND, out uint)`。坑：锁屏/UAC 安全桌面/非交互会话返回 NULL 或受限窗口；有竞态（拿到即用别缓存）。

### 客户区 vs 真实边框
- `GetClientRect`：客户区坐标**左上恒 (0,0)**，不含标题栏/边框/菜单；`ClientToScreen(hwnd, ref POINT)` 取 (0,0) 得客户区屏幕原点。客户区屏幕矩形 = 原点 + 尺寸。
- `DwmGetWindowAttribute(hwnd, DWMWA_EXTENDED_FRAME_BOUNDS=9, out RECT, sizeof) `：**去阴影可见整窗，屏幕空间，物理像素（不受 DPI 虚拟化）**。
  - `GetWindowRect` 含 7px 隐形 resize 边（左/右/下）+ 阴影，**且是 DPI 虚拟化的**；三者关系：客户区 ⊆ 扩展边框 ⊆ 窗口矩形。
  - 窗口显示前调用不准；自绘非客户区窗口可能两者相等；个别 2px 黑边假象。
- `AdjustWindowRectEx(ForDpi)` 用于反推（客户区→整窗），一般寻址用不到。
- **正确做法**：整窗默认 `EXTENDED_FRAME_BOUNDS`，失败回退 `GetWindowRect`；只截内容用 `GetClientRect`+`ClientToScreen`。

### hwnd 直寻址
- `IsWindow` + `IsWindowVisible` + `GetAncestor(hwnd, GA_ROOT=2)==hwnd` 校验；项目已有 `GetWindowInfo(hwnd)`，只需入口改直传。

### 元素级（FlaUI/UIA）
- `AutomationElement.BoundingRectangle`（UIA 属性，`Rectangle`，**物理像素、相对虚拟屏原点，副屏可负**）；未渲染/离屏为 `Rectangle.Empty`；`Properties.IsOffscreen`。
- FlaUI 现成 `Capture.Element(el)` = `Capture.Rectangle(el.BoundingRectangle)` → **内部就是 BitBlt 屏幕矩形**（`SourceCopy|CaptureBlt`）。`Capture.Screen(screenIndex)` 用 `EnumDisplayMonitors`+`SM_CMONITORS` 按 index 取屏。
  - **关键坑**：`Capture.Element` 直接屏上 BitBlt → **被遮挡截到遮挡物**；FlaUI issue #429：高 DPI（150%/250%）下截错区域（`Rectangle` 逻辑坐标 vs BitBlt 物理）。→ 元素截图必须**先拿元素所属顶层窗口的 WGC/PrintWindow 帧，再换算裁剪**。
- 布局：元素可能属**另一个顶层窗口**（ComboBox 展开 `ComboLBox`、popup、tooltip）；元素可跨显示器，裁剪矩形与虚拟屏求交；离屏/虚拟化先 `ScrollIntoView`/`Realize`。
- 高亮：解码后 `Graphics.DrawRectangle`；把元素矩形从屏幕空间换算到图像空间（下节公式）。

### 坐标空间与旧帧护栏
- **物理虚拟屏空间**：主屏左上 (0,0)，左/上显示器坐标为负。
- **`origin+scale` 坐标模型（A 源码核实）**：Windows-MCP 截图被降采样时以元数据回传 `Coordinate Scale = round(1/scale, 6)`，要求 agent 按「image pixel × scale → 屏幕坐标」乘回（`11-source-windows-mcp.md` ④.3/⑥.1；本地 `Windows-MCP/src/windows_mcp/tools/_snapshot_helpers.py:186-197`；降采样双轴取 min 见 `src/windows_mcp/desktop/service.py:241-259`，记录 `screenshot_scale` 见 `:235,254,280-281`，比例来源含环境变量 `WINDOWS_MCP_SCREENSHOT_SCALE`（`_snapshot_helpers.py:24-34`））。chrome-devtools-mcp 同款思路但降采样做在**源头**（CDP `clip.scale`，非后处理），双轴独立比例取 min（`13-source-browser-mcp.md` ③；本地 `chrome-devtools-mcp/src/tools/screenshot.ts:93-124,223-233`）——即「上限约束的是送去模型的位图字节尺寸」。**本项目现有 `k=output/native` 就是 scale**，据此把 `origin+scale` 定为统一表达（`screen = origin + image/scale`）。
- **旧帧护栏（A：Windows-MCP 缺失反例 + C：agent-browser 先例）**：Windows-MCP **无** capture/snapshot id、无代际号、无时间戳，`label` 只是「当前列表下标」（`11` ⑥.2；本地 `Windows-MCP/src/windows_mcp/desktop/service.py:651-663,273-290`）；`Screenshot` 以 `use_ui_tree=False` 运行会产出空元素列表，故一次 `Snapshot` 后调 `Screenshot`，先前记下的 `label` 会因 `IndexError` 崩——**这正是本项目要补 `frameId`/代际护栏的反例依据**。另可辅以 agent-browser 的 stale-ref 先例（ref 退役不复用、用旧 ref 动作大声失败）——**【等级 C｜低星来源，未核实，需实测】**，经 `03-semantic-bridge.md` 二手转引。
- **评估**：`origin+scale` 是显式无状态换算，能统一三种原点；本项目**不做坐标输入注入**（UIA 动作为语义调用），护栏只需护 region 一处 → 主模型 `origin+scale` + 轻量 `frameId`。

## ③ 对本项目的落地建议与代价

**分层原则**：Engine 无 UIA；元素定位在宿主 `UiElementLocator`，把「窗口 hwnd + 元素矩形 + clientArea/去阴影标志」传给 Engine 裁剪；Engine 只做 Win32 矩形运算与抓取。

| 改动 | 量级 | 备注 |
|---|---|---|
| 坐标模型：`CaptureResult` 增 `OriginX/OriginY`、`Scale`、`FrameId`；各模式填 origin | 小 | 先做，收益最大 |
| 显示器/hwnd/前台：`EnumerateMonitors`+`CaptureDisplay(rect)`+`FindWindowByHwnd` | 小 | P/Invoke 补全 + 参数/文案 |
| 客户区/DWM 边框：`DwmGetWindowAttribute`+`GetClientRect`；window 加 `clientArea` | 小（**需 spike**） | 先确认 WGC 帧几何口径 |
| 元素级：`UiElementLocator` 暴露 (BoundingRectangle, 顶层 hwnd)；`mode=element` | 中 | 风险在遮挡/虚拟化/跨顶层窗口 |
| WGC 显示器抓取（可选） | 小 | 建议二期，BitBlt 已够 |

**文档必改（同 commit）**：根 `README.md` screenshot 行、握手 `AppText.HandshakeFeatureIntro`（现写死「三模式」）、回归断言 `HandshakeFeatureIntro_覆盖全部能力族触发条件`；spec §8「明确不做」里点名的多显示器/元素级/`origin` 坐标系需解冻。

## ④ 风险 / 开放问题

1. **WGC 窗口帧几何**：`CreateForWindow` 帧是 `GetWindowRect` 还是 `EXTENDED_FRAME_BOUNDS`？决定裁剪偏移——**必须先 spike**。
2. **region 坐标系是否切换**：直接 origin+scale（通用但行为变更）还是兼容旧口径 + 新参数。
3. **元素身份跨工具一致性**：`mode=element` 的 index 与 `ui_find` 是否共用同一缓存（同一 `UiElementLocator`）；共用需定义消费/失效语义。
4. **显示器编号语义**：枚举序（不稳）vs `szDevice`（稳但难猜）；建议 `primary/left/right` + 头部回显矩形。
5. **DPI 取值**：`GetDpiForMonitor` vs `GetDpiForWindow`，混合 DPI 下以谁为准，spike 定。
6. **旧帧护栏范围**：当前只护 region；若将来加物理输入才升级为「旧帧坐标即失败」。

## 附：来源

> 分级：**A** = 本地源码核实；**B** = 官方文档/官方规范 + 第一方佐证；**C** = 低星或未核实来源（仅旁证）。

**A · 本地源码核实**
- FlaUI `src/FlaUI.Core/Capturing/Capture.cs`、issue #429/#614（`06-source-flaui.md`）。
- Windows-MCP `59e77f6`：`src/windows_mcp/tools/_snapshot_helpers.py`、`src/windows_mcp/desktop/service.py`（Coordinate Scale / 双轴降采样 / 无快照代际反例，`11-source-windows-mcp.md`）。
- chrome-devtools-mcp `ae0aaef` / playwright：`src/tools/screenshot.ts`（`clip.scale` 源头降采样、双轴取 min，`13-source-browser-mcp.md`）。
- ScreenToGif / flameshot / UFO：多显示器枚举 / DPI / 窗口捕获 / 截图与 UIA 坐标对齐（`14-source-capture-tools.md`）。
- ShareX（`07-source-sharex.md`）、PowerToys（`08-source-powertoys.md`）。
- 本项目代码：`Engine/Capture/*`、`Tools/Debugger/DebugScreenshotTool.cs`、`Services/Ui/UiElementLocator.cs`、screenshot spec。

**B · 官方文档/规范 + 第一方佐证**
- Win32（Microsoft Learn）：`EnumDisplayMonitors`/`GetMonitorInfo`/`MonitorFromWindow`/`MonitorFromPoint`/`MonitorFromRect`/`GetSystemMetrics`/`GetDpiForMonitor`/`GetDpiForWindow`/`GetForegroundWindow`/`GetWindowThreadProcessId`/`GetClientRect`/`ClientToScreen`/`DwmGetWindowAttribute`/`AdjustWindowRectExForDpi`。
- WinRT（Microsoft Learn）：`IGraphicsCaptureItemInterop::CreateForMonitor`、`GraphicsCaptureItem`。
- MCP 官方规范 + computer-use 官方参考（`12-source-mcp-resources.md`）。
- 第一方佐证：Microsoft Q&A #5772548（`CreateForMonitor` E_INVALIDARG）、`microsoft/Windows.UI.Composition-Win32-Samples#125`（access_denied 条件）、WebRTC `wgc_capturer_win.cc`。

**C · 低星/未核实来源（已降级，仅旁证，不作定稿依据）**
- `desktop-touch-mcp` `screenshot.ts`（`origin+scale` 原始引例）——**已改引 A 等效来源**（Windows-MCP Coordinate Scale + chrome-devtools-mcp `clip.scale`）。
- `windows-computer-use-mcp` README（`capture_id` 旧帧护栏）——**已改引 A**（Windows-MCP 缺失反例）+ C（agent-browser 先例）。
- agent-browser stale-ref 先例——**【等级 C｜低星来源，未核实，需实测】**，经 `03-semantic-bridge.md` 二手转引。
- `gui-mcp`、`mcp-screenshot-server`、`ubuntu-desktop-control-mcp`——**本文件未引用**（仅在 `README.md` 的业界清单出现，与本结论无关）。

## 来源等级

| 结论区 | 依据等级 | 代表证据 |
|---|---|---|
| ① 结论与推荐 | A + B | Windows-MCP/FlaUI 源码 + Win32/WinRT 文档 |
| ② 多显示器 | A + B | FlaUI `Capture.cs`、ScreenToGif `Monitor.cs`、`EnumDisplayMonitors`/`CreateForMonitor` |
| ② 前台窗口 | B | `GetForegroundWindow`/`GetWindowThreadProcessId` |
| ② 客户区 / DWM 边框 | B | `GetClientRect`/`ClientToScreen`/`DwmGetWindowAttribute` |
| ② hwnd 直寻址 | B | `IsWindow`/`IsWindowVisible`/`GetAncestor` |
| ② 元素级（FlaUI/UIA） | A | FlaUI `Capture.cs`/issue #429 |
| ② 坐标空间 | A | Windows-MCP Coordinate Scale（`_snapshot_helpers.py:186-197`）+ chrome-devtools-mcp `clip.scale`（`screenshot.ts:93-124,223-233`） |
| ② 旧帧护栏 | A（主）+ C（旁） | Windows-MCP 无代际反例（`desktop/service.py:651-663`，A）；agent-browser stale-ref（C） |
| ③ 落地建议与代价 | A | 本项目 `Engine/Capture/*`、`Services/Ui/UiElementLocator.cs`、screenshot spec |
| ④ 风险 / 开放问题 | A（待 spike） | WGC 窗口帧几何、region 坐标系切换均需实测 |
