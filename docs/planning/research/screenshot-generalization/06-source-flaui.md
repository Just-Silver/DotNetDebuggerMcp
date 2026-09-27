# 06 · FlaUI 源码深挖（元素截图 / 几何 / 控件树 / 文本 / DPI / Patterns）

> **来源等级：A 源码核实**（本地 clone `Externals/FlaUI` @ `fd7cc64`，`v5.0.0-15-gfd7cc64`，main；行号按此 clone）。

## ① 结论与可借鉴点（先给答案）
1. **元素截图 = 屏幕 BitBlt 裁矩形**，不是逐元素渲染。`Capture.Element` 取 `BoundingRectangle` → `Capture.Rectangle`；仅当输出尺寸≠原尺寸才 `SetStretchBltMode(HALFTONE)`+`StretchBlt`。
2. **FlaUI 自身不设 DPI 感知**（全仓零命中）。#429 高 DPI 错位根因＝「UIA 矩形（受客户端进程 DPI 感知影响，可能虚拟化）」vs「BitBlt 物理像素」。**修法在调用方**：本项目 `ScreenCapture.EnsureDpi()` 已设 PMv2，但**必须在任何 UIA 调用之前**完成。
3. **几何取值**：`BoundingRectangle` 用 `ValueOrDefault`（不支持返回 `Rectangle.Empty`，不抛）；`IsOffscreen`/`IsEnabled` 用 `.Value`（不支持抛）。坐标系＝**物理像素、相对虚拟屏原点（副屏可负）**。
4. **`FindAllDescendants()` = raw view（全树）**，非 control view；无缓存、无重试、无兜底。#614 偶发不完整是 provider 侧问题，FlaUI 无 workaround。
5. **`IsSupported` 每次都是一次真取 pattern 的跨进程调用**（`GetCurrentPattern`），不是读 availability 属性；**不缓存**。项目 `Probe` 每元素最多 10 次 COM 往返，`Safe` 包住是对的（其它异常会外抛）。
6. **文本抽取链 FlaUI 已内置**：`TextBox.Text` = ValuePattern.Value → TextPattern.DocumentRange.GetText → Win32 WM_GETTEXT。

## ② 源码级事实

### 元素截图（`src/FlaUI.Core/Capturing/`）
- `AutomationElement.Capture()`/`CaptureToFile()`（`AutomationElements/AutomationElement.cs:289-314`）→ `Capture.Element(this).Bitmap` / `.ToFile`；**无 WGC/PrintWindow 回退、无缩放/编码管线**。
- `Capture.cs`：`MainScreen=(0,0,SM_CXSCREEN,SM_CYSCREEN)`（:22-24）；`Screen(index)` 用 `GetBoundsByScreenIndex`（:35-39）或整桌面 `SM_*VIRTUALSCREEN`（:43-45，原点可负）；`Element(el)=Rectangle(el.BoundingRectangle)`（:82-85，**不判空/离屏**）；`Rectangle(bounds)`（:106-130）直取分支 `BitBlt(...,SRCCOPY|CAPTUREBlt)`（:115-118），缩放分支 `StretchBlt`（:123-127）；目标 DC 由 `GetDesktopWindow()`→`GetWindowDC`→`CreateCompatibleDC`（:149-164）。
- `GetBoundsByScreenIndex`：`EnumDisplayMonitors`+`GetMonitorInfo`，**screen 索引=枚举顺序**（:132-147），**不保证与 `Screen.AllScreens`/编号一致**。
- `CaptureUtilities.GetScale`（:24-45）、`ScaleAccordingToSettings` 目标恒 (0,0)（:79-109）；`CaptureCursor`（:114-186）。
- `CaptureImage`：`OriginalBounds`（:33-36，做坐标映射用）；`ToFile` 按扩展名默认 PNG（:47-70）；**`ApplyOverlays(ICaptureOverlay[])`（:75-95）是自定义 SoM 标注的官方扩展点**（单 overlay 异常被吞）。现成 overlay 只有 Mouse/Info，**无矩形/编号**。

### 元素几何
- `BoundingRectangle => Properties.BoundingRectangle.ValueOrDefault`（`AutomationElement.cs:154`）；`IsOffscreen/IsEnabled/Name/AutomationId/ControlType` 用 `.Value`。
- `AutomationProperty.Value`（不支持抛 `PropertyNotSupportedException`，:68）；`ValueOrDefault`（返回 default，:71-78）；`IsSupported=>TryGetValue`（:87）。
- `BoundingRectangle` 不支持→`Rectangle.Empty`；`DrawHighlight` 用 `!rectangle.IsEmpty` 兜底（`AutomationElementExtensions.cs:41-42`）。
- `GetClickablePoint` 三段回退：原生 `GetClickablePoint()`→`Properties.ClickablePoint`→`BoundingRectangle.Center()`（`UIA3FrameworkAutomationElement.cs:147-176`）；失败抛 `NoClickablePointException`。**SoM 标注点可直接复用。**

### 控件树遍历
- `FindAllDescendants()=FindAll(TreeScope.Descendants, TrueCondition)`（`AutomationElement.Find.cs:219-222`）；底层 `NativeElement.FindAll`（`UIA3FrameworkAutomationElement.cs:99-114`），**无重试/无跨 view 兜底**。
- raw view：MS 官方「不指定属性搜索就是 raw view」→ 会含布局面板/装饰/0 尺寸元素。要 control/content view 用 `TreeWalkerFactory.GetControlViewWalker()/GetContentViewWalker()`（`UIA3TreeWalkerFactory.cs:24-49`）或 `FindAllWithOptions`（`IUIAutomationElement7`，`UIA3FrameworkAutomationElement.cs:117-134`）。
- `AutomationElement.Parent` 用 **raw view walker**。
- **顶层窗口获取**：`Application.GetMainWindow`（`MainWindowHandle`→`FromHandle().AsWindow()`，`Application.cs:303-317`）；`GetAllTopLevelWindows`＝`desktop.FindAllChildren(Window AND pid)`（:322-327，**慢**，本项目已改 `FindFirstChild`）。
- **弹层归属**：Win32 右键菜单在 **desktop 下**（`Window.cs:84-95`）、WinForms 菜单在主窗下、WPF 菜单在 Popup 子窗（:97-111）；WinForms/Win32 ComboBox 列表是 ComboBox 子/顶层 `List`（`ComboBox.cs:142`；本项目已知 `ComboLBox` 是 owner=0 顶层窗口）；tooltip 挂 desktop（FlaUI 无定位助手）。
- 虚拟化：`ItemContainer.FindItemByProperty`→`VirtualizedItem.Realize`→`ScrollItem.ScrollIntoView`（项目 `TryRealizeVirtualized` 正是此链）。
- 缓存：`CacheRequest` 是 `[ThreadStatic]` 栈；不激活时全实时查。

### 文本抽取
- `TextBox.Text`（`TextBox.cs:24-46`）：`ValuePattern.Value` → `TextPattern.DocumentRange.GetText(int.MaxValue)` → `Win32Fallback.GetTextWin32`；都没有抛。**password 直接抛异常**（:28-31）。
- `ValuePattern` 读值每属性一次 COM 往返、**无缓存**（`UIA3Patterns/ValuePattern.cs:10-25`）；`TextPattern.DocumentRange` 每次新建（:25-32）。
- `UIA3TextRange.GetText(maxLength)`（`UIA3TextRange.cs:102-105`）；**`GetBoundingRectangles()`（:72-88）可作"语义桥"的文本块坐标来源**；`RangeFromPoint`/`GetVisibleRanges`（`TextPattern.cs:43-66`）。
- 建议链：Edit/ComboBox→Value；Document/富文本→TextPattern；兜底 WM_GETTEXT（`Win32Fallback.cs:66-82`）。

### Pattern 能力
- `AutomationPattern.Pattern`（不支持抛 `PatternNotSupportedException`，:64-71）；`PatternOrDefault`（:74-81）；`IsSupported=>TryGetPattern`（:96）。
- `GetNativePattern`→`UIA3FrameworkAutomationElement.cs:90-96` `NativeElement.GetCurrentPattern(patternId)`；`TryGetNativePattern` 只捕 `PatternNotSupportedException`（其它异常外抛）。
- **`IsSupported` 用 `GetCurrentPattern`，不是 `UIA_IsXxxPatternAvailablePropertyId`**（后者仅供元数据）。每字段一次跨进程调用、10 字段最多 10 次、无缓存。
- `Com.Call` 异常映射（`Tools/Com.cs:114-146`）：`UIA_E_ELEMENTNOTAVAILABLE→ElementNotAvailableException`、`NOTENABLED`、`NOCLICKABLEPOINT`、`TIMEOUT`、`NOTSUPPORTED`。

### DPI / 多显示器
- FlaUI 不设 DPI；`Capture.Screen` monitor 索引=枚举顺序（见上）；整桌面用 `SM_*VIRTUALSCREEN`。
- 缺陷 #429：DPI-unaware 进程下逻辑/物理不一致→截错，FlaUI 不能修。

## ③ 对本项目「元素级截图 / SoM 元素来源 / 文本抽取」的落地建议
1. **不要直接用 `element.Capture()/CaptureToFile()`**（绕过 Engine 的 WGC/PrintWindow/纯黑回退与 ImagePipeline）。用 FlaUI 取 `BoundingRectangle` 交给 Engine 裁剪，纳入 screenshot 同一套头部/落盘/双轨。
2. **DPI 顺序坑（必修）**：`EnsureDpi` 现为懒执行；若 `ui_find` 先于首次截图，UIA 返回逻辑坐标，之后设 PMv2 → **同会话坐标口径漂移**。建议宿主启动最早处调一次 `EnsureDpi()`（幂等）。
3. **空/离屏/最小化**：矩形 `IsEmpty`→提示「不可见/无几何」；最小化窗口改走 HWND 的 WGC 窗口抓取（已有 IsIconic 处理）。
4. **多屏坐标**：`BoundingRectangle` 相对虚拟屏、副屏可负；裁剪前与虚拟屏求交，别假设 (0,0)；FlaUI monitor 索引≠显示器编号。
5. **SoM 过滤**：raw view 会混入布局面板/0 尺寸 → 过滤 `ControlType` 白名单 ∪ `IsControlElement`、`IsOffscreen==false`、矩形非空且宽高>阈值、按矩形去重。
6. **`GetClickablePoint` 三段回退**天然给出可标注点，失败回退 `BoundingRectangle.Center()`。
7. **#614**：SoM 若几何异常/数量骤降，用 `Retry` + `FindAllChildren` 递归自建并集。
8. **弹层归属**：SoM 只遍历主窗会漏下拉/菜单/tooltip → 按场景把 desktop 下相关顶层窗口纳入（复用 `FindMainWindow` 的 EnumWindows 思路）。
9. **虚拟化**：`Realize/ScrollIntoView` 会改目标 UI 状态，纯观察型 SoM 慎用（只标"当前可见"）。
10. **编号 overlay**：自实现 `OverlayBase.Draw(Graphics)`，用 `CaptureImage.OriginalBounds` 把物理坐标换算到图像坐标（再乘 `GetScale`），经 `ApplyOverlays` 叠加。
11. **文本链**：Value→TextPattern→WM_GETTEXT；去掉 password 抛异常语义（改为脱敏展示）；批量取文本设超时/限长。
12. **文本块坐标**：`ITextRange.GetBoundingRectangles()` + `RangeFromPoint/GetVisibleRanges/GetSelection` 是"可点坐标"语义桥的现成素材（物理像素、相对虚拟屏）。

## ④ 风险 / 开放问题
1. 版本差异：报告基于 main `v5.0.0-15`，项目钉 NuGet 5.0.0；相关代码未见结构性变化，**升 FlaUI 需复核行号**。
2. **DPI 顺序是硬风险**（见 ③2）。
3. #429/#614 无法从 FlaUI 侧根治，需 e2e 覆盖（多 DPI 屏、虚拟化列表）。
4. `FindAllDescendants()=raw view` 是现有 `ui_find` 既成事实；若 SoM/元素清单改 control view，需评估与 `TargetDescriptor`/`SameIdentity` 缓存口径一致性。
5. 未验证：`EnumDisplayMonitors` 顺序与编号对应；`FindAllWithOptions` 在旧系统可用性；tooltip UIA 归属。
6. 同进程多组件设 DPI 只有第一次生效（后续 ACCESS_DENIED，`EnsureDpi` 已容忍）。
