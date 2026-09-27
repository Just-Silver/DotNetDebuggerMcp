# 14 · 截图工具与 UFO 源码深挖（ScreenToGif 27.7k★ / flameshot 30.9k★ / UFO 9.8k★）

> 来源等级：A 源码核实。生成日期 2026-09-27。

本调研以三份本地 clone 的真实源码为准，逐条给出 `文件:行号`。原则：**查不到即写「未找到」，不臆测**。三处源码与本项目（纯 Windows .NET、无 ML、动作为 UIA 语义调用、不做录制/模板匹配）的相关性差异较大，正文明确标注哪些是可直接移植的、哪些是本项目不需要的。

三个 commit：

- ScreenToGif：`a4d0a67c2131cd048ceec86cd40afc2f1a06f2fd`（2026-07-28，"Merge branch 'dev'"）
- flameshot：`2d478061ffeeba5919d3a3d9168f93542ea9b357`（2026-09-18，"Translated using Weblate (Ukrainian) (#4958)"）
- UFO：`e2a03126241c696fdaf9a669a271ca3fca6d9916`（2026-09-22，"Merge commit from fork"）

---

## ① 结论速览（表格）

| 议题 | 结论 | 关键 `文件:行号` |
|---|---|---|
| ScreenToGif 显示器枚举 | `EnumDisplayMonitors` 回调枚举；每屏 `GetMonitorInfo`；`MonitorFromWindow/Point` 定位 | `ScreenToGif.Util/Native/Monitor.cs:95-106, 158-198`；`ScreenToGif.Native/External/User32.cs:104,111,115` |
| ScreenToGif DPI | `ShCore.GetDpiForMonitor(Effective)`，失败回退 96，再回退 `GetDeviceCaps(LogPixelsX)`；`Scale = Dpi/96` | `ScreenToGif.Util/Native/Monitor.cs:71-88`；`ScreenToGif.Model/Models/Native/Monitor.cs:21-23` |
| ScreenToGif 窗口捕获 | **BitBlt 从桌面 DC 取窗口屏幕矩形**（先取 WindowRect，再按 scale 换算），`SourceCopy\|CaptureBlt` 以含分层窗口；**未找到 PrintWindow** | `ScreenToGif.Util/Native/Capture.cs:89-122`（尤其 `:103`）；`ScreenToGif/Capture/ImageCapture.cs:38-41,60` |
| ScreenToGif 是否用 GPU/WGC | 用 SharpDX **DXGI Desktop Duplication**（不是 WinRT WGC）；`DriverType.Hardware`；整屏/整区抓取走 Duplication，窗口捕获仍走 GDI BitBlt | `ScreenToGif/Capture/DirectImageCapture.cs:19,24,46,142` |
| ScreenToGif 变化检测 | `OnlyCaptureChanges` 启用时用 **DXGI dirty rects / move rects** 判断有无变化，无变化则丢帧（非逐像素 diff） | `ScreenToGif/Capture/DirectChangedImageCapture.cs:40-62,69-94`；`ScreenToGif.Util/Settings/UserSettings.cs:984-988` |
| ScreenToGif 内存/编码 | 帧默认落盘 `{n}.png`；`UseMemoryCache` 走内存缓存；导出格式含 PNG/JPEG/GIF/WebP(FFmpeg)/视频等；WebP 经 `libwebp_anim`，quality 75/100 | `ScreenToGif/Capture/ImageCapture.cs:67`；`ScreenToGif.ViewModel/ExportPresets/AnimatedImage/Webp/FfmpegWebpPreset.cs:15,109` |
| ScreenToGif 被遮挡窗口 | 无专门处理：BitBlt 抓的是「屏幕上显示的内容」，被遮挡处抓到的是遮挡窗口像素；仅分层窗口由 `CaptureBlt` 补全 | `ScreenToGif.Util/Native/Capture.cs:32,103`；`ImageCapture.cs:48-51` |
| flameshot 抓取后端 | 三平台分支：Unix 走 **xdg-desktop-portal / X11 legacy**；Windows/macOS 走 Qt `QScreen::grabWindow`；Windows 侧**不使用**任何 Windows 专用抓屏 API | `src/utils/screengrabber.cpp:189-229, 781-897, 303-384` |
| flameshot 多显示器 | 先逐屏 `grabWindow` 再按**物理像素偏移**拼成一张大画布（Windows）；或抓整桌后按 monitorIndex 裁剪（Linux portal）；macOS 也拼接 | `src/utils/screengrabber.cpp:781-856, 642-779, 349-384` |
| flameshot DPI | `desktopGeometry()` 对非 Windows 除以 DPR；Windows `cropToMonitor` 按各屏 DPR 累加物理宽高；窗口尺寸按 `pixmap().devicePixelRatio()` 折算 | `src/utils/screengrabber.cpp:413-427, 700-728`；`src/widgets/capture/capturewidget.cpp:157-163, 2053-2057` |
| flameshot 标注工具 | 铅笔/直线/箭头/矩形/圆/高亮笔/计数器泡泡/文字/像素化/反色等；高亮笔 = Multiply 混合 + opacity 0.35 | `src/tools/toolfactory.cpp:41-68`；`src/tools/marker/markertool.cpp:52-65`；`src/tools/circlecount/circlecounttool.cpp:100-182` |
| flameshot 导出 | 格式取自 Qt `QImageWriter::supportedImageFormats()`，默认 png；JPEG 质量 0-100 默认 75；**webp 未硬编码**，取决于 Qt 构建插件 | `src/utils/screenshotsaver.cpp:99-111,119-121`；`src/config/generalconf.cpp:560-564`；`src/utils/confighandler.cpp:138` |
| UFO change_detector | **不是图像 diff**：是「任务星座（TaskConstellation）」结构差异（增删任务/依赖、属性变化），逐属性按字段名比对，无像素/阈值/分块。图像逐像素 diff 在 UFO **未找到** | `galaxy/visualization/change_detector.py:27-184` |
| UFO UI 结构 diff | `ui_tree_diff` 按 path 递归比较 name/control_type/rectangle 等字段，输出 added/removed/modified | `ufo/automator/ui_control/ui_tree.py:151-227` |
| UFO control_filter | NLP/嵌入向量过滤（关键词子串 / sentence-transformers 余弦 top-k / CLIP 图标），**不是 UIA 属性过滤**，且**全仓无调用方**（死代码） | `ufo/automator/ui_control/control_filter.py:164-272` |
| UFO「可交互/可见/有效」筛选 | 真正生效的是 UIA 查询条件：`IsEnabled=1`、`IsOffscreen=0`、`IsControlElement=1`、ControlType∈CONTROL_LIST，再限 500 个、`name != ""` | `ufo/automator/ui_control/inspector.py:334-370, 440-463`；`config/ufo/system.yaml:28` |
| UFO screenshot + UIA 对齐 | 标注时把 UIA 控件的**窗口绝对矩形**减去窗口矩形得相对坐标再画；`TargetInfo` 版同理 | `ufo/automator/ui_control/screenshot.py:361-398, 798-824, 692-750` |
| UFO SoM 标注实现 | 半透明色块（按 ControlType 配色）+ 数字/字母徽标（字体 22），色表见配置 | `ufo/automator/ui_control/screenshot.py:508-757`；`config/ufo/system.yaml:34-47` |

---

## ② ScreenToGif：多显示器/DPI/窗口捕获/编码

### 2.1 显示器枚举 API

- 枚举入口是 Win32 `EnumDisplayMonitors` + 回调：`ScreenToGif.Util/Native/Monitor.cs:95-106`（`AllMonitors` 属性）与回调类 `:184-198`；P/Invoke 声明在 `ScreenToGif.Native/External/User32.cs:115`。
- 每个显示器句柄经 `GetMonitorInfo` 填充 `MONITORINFOEX`：`ScreenToGif.Util/Native/Monitor.cs:14-36`（`GetMonitorInfo` 声明 `User32.cs:111`；结构 `ScreenToGif.Native/Structs/MonitorInfoEx.cs`）。从 `info.Monitor` 得 `NativeBounds`、从 `info.Work` 得 `WorkingArea`、`info.Flags & MonitorinfoPrimary` 得 `IsPrimary`。
- 额外友好名走 `EnumDisplayDevices`：`ScreenToGif.Util/Native/Monitor.cs:40-58`（`User32.cs:392`）。
- 按窗口/坐标反查显示器：`MonitorFromWindow`（`User32.cs:104`）用于 `WindowHelper.cs:92,456`；`MonitorFromPoint` 用于 `Monitor.cs:158-163`。
- 另有显示模式枚举 `EnumDisplayDevices`（`User32.cs:392`）与 `AllMonitorsScaled/AllMonitorsGranular`（`Monitor.cs:108-156`）用于按缩放生成给 WPF 坐标的显示器列表。

### 2.2 DPI / 缩放感知

- 每屏 DPI 用 `ShCore.GetDpiForMonitor(handle, DpiTypes.Effective, ...)`：`ScreenToGif.Util/Native/Monitor.cs:71-72`；声明 `ScreenToGif.Native/External/ShCore.cs:16-17`；`DpiTypes` 枚举见 `ScreenToGif.Model/Enums/Native/DpiTypes.cs`。
- 失败回退：先置 96，再 `Gdi32.CreateCompatibleDC` + `GetDeviceCaps(LogPixelsX)`：`Monitor.cs:74-88`。
- 缩放比定义为 `Scale => Dpi / 96d`：`ScreenToGif.Model/Models/Native/Monitor.cs:21-23`。
- 进程 DPI 感知枚举在 `ScreenToGif.Model/Enums/Native/ProcessDpiAwareness.cs`（`ProcessDpiUnaware/SystemDpiAware/PerMonitorDpiAware`）；`ShCore.SetProcessDpiAwareness` 处被注释掉（`ShCore.cs:19-23`），实际 DPI 感知由清单文件声明（未找到运行期封装）。
- 坐标换算大量以 `scale` 参与，例如窗口捕获矩形 `(rect + offset) * scale`（`ScreenToGif.Util/Native/Capture.cs:92-95`）、虚拟桌面边界 `VirtualScreenLeft/Top`（`ScreenToGif/Windows/Recorder.xaml.cs:612-619`）。

### 2.3 窗口捕获实现

- **窗口捕获并非抓窗口自身内容**，而是：取窗口矩形 → 从**桌面窗口 DC** BitBlt 该屏幕矩形。见 `ScreenToGif.Util/Native/Capture.cs:89-122`（`CaptureWindow(handle, scale)`；`:97` `GetWindowDC(GetDesktopWindow())`；`:103` `BitBlt(..., SourceCopy | CaptureBlt)`）。
- 录制循环中的窗口/区域抓取同理走桌面 DC：`ScreenToGif/Capture/ImageCapture.cs:38-41` 建兼容 DC/Bitmap，`:60` 用 `StretchBlt` 从 `WindowDeviceContext` 取指定区域。`_desktopWindow = IntPtr.Zero`（`:18`）。
- 分层窗口（layered windows）通过 `CaptureBlt` 标志补全；远程桌面且开了 `RemoteImprovement` 时**去掉** `CaptureBlt`：`ImageCapture.cs:45-51`。
- **PrintWindow 未找到**：对 `*.cs`/`*.csproj` 全仓检索 `PrintWindow` 无结果。即 ScreenToGif 不用 PrintWindow，无法抓到被遮挡/最小化窗口内容。
- 光标的合成是手工把光标图标 `DrawIconEx` 画到兼容 DC：`ImageCapture.cs:99-144`；独立的光标抓取工具 `ScreenToGif.Util/Native/Capture.cs:124-199`（含单色光标特判 `:152-183`）。
- 窗口吸附 / 悬挂框用 `WindowHelper`（`ScreenToGif.Util/Native/WindowHelper.cs:92` `MonitorFromWindow`；`:456-473` 取窗口所在屏），并对 `ScreenToGif` 自身进程跳过（`Recorder.xaml.cs:593-594`）。

### 2.4 是否用 GPU / WGC

- 使用 SharpDX（Direct3D 11 + DXGI）而非 WinRT `Windows.Graphics.Capture`。整屏/整区为 **Desktop Duplication API**：`ScreenToGif/Capture/DirectImageCapture.cs:24`（注释 "Frame capture using the DesktopDuplication API."）、`:44-46`（`OutputDuplication`）、`:142`（`new Device(DriverType.Hardware, DeviceCreationFlags.VideoSupport)`）。
- 抓帧 `DuplicatedOutput.TryAcquireNextFrame(...)`：`DirectImageCapture.cs:274,442,630` 等。
- Debug 构建用 `DriverType.Hardware + DeviceCreationFlags.Debug` 并开 DXGI/GI 调试层（`DirectImageCapture.cs:130-140`）。
- **WGC（Windows.Graphics.Capture）未使用**：全仓检索 `WindowsGraphicsCapture|GraphicsCapture` 无结果。桌面复制不支持时抛 `GraphicsConfigurationException`（`:190-202`），据此回退到 `UseDesktopDuplication=false` 时的 GDI 路径。

### 2.5 内存与编码（格式、质量、是否 webp）

- 抓帧后默认写 PNG 到项目目录：`ScreenToGif/Capture/ImageCapture.cs:67,171`（`frame.Path = "{FullPath}{n}.png"` → `frame.Image.Save(frame.Path)`）。`UseMemoryCache` 开关决定是否用内存缓存实现：`ScreenToGif.Util/Settings/UserSettings.cs:996`；选择逻辑 `ScreenToGif/Controls/BaseScreenRecorder.cs:111-117`（`CachedCapture` vs `ImageCapture` / `DirectCached*` vs `DirectImage*`）。
- 导出格式枚举含 GIF/APNG/WebP/AVIF/视频/PSD/PNG/JPEG 等：`ScreenToGif.Model/Enums/ExportFormats.cs:18`（`Webp`）、`ScreenToGif.Model/Enums/EncoderTypes.cs:8`（`FFmpeg //Gif, Webp, Apng, Avif, Video`）。
- **WebP** 经 FFmpeg 编码：`ScreenToGif.ViewModel/ExportPresets/AnimatedImage/Webp/FfmpegWebpPreset.cs:8-15`（参数 `-c:v libwebp_anim -lossless 0 -quality 75 -loop 0 -f webp`），High 预设 quality 100（`:108-109`），`Quality` 默认 75（`:17`）。`WebpPreset` 定义扩展名 `.webp`（`WebpPreset.cs:7-11`）。
- **JPEG** 预设定义于 `ScreenToGif.ViewModel/ExportPresets/Image/JpegPreset.cs`（默认导出态）；质量经 FFmpeg 参数或 `ImagePreset`（未找到系统级 GDI+ 质量参数集中定义）。
- **GIF** 可由内置量化编码器或 **Gifski** 库完成：`ScreenToGif.Util/GifskiInterop.cs:14-155`（`gifski_new/add_frame_rgb/finish`，含 `quality`、`fast`、`loop` 参数，`:68-72`）。

### 2.6 对「抓取被遮挡窗口」的处理

- **无专门机制**。`CaptureWindow` 从桌面 DC BitBlt 窗口所在屏幕矩形（`ScreenToGif.Util/Native/Capture.cs:97-103`），因此抓到的是**屏幕上可见的合成结果**——窗口被遮挡处得到的是遮挡方像素，最小化/移出屏幕则抓不到或抓到背景。唯一补偿是 `CaptureBlt` 使分层窗口内容并入（`Capture.cs:32,103`；`ImageCapture.cs:48-51`）。
- 想抓窗口自身内容需 `PrintWindow`/DXGI 窗口复制，ScreenToGif 未实现（**未找到**）。

---

## ③ flameshot：区域选择/跨屏/标注/导出

### 3.1 抓取后端（X11/Wayland/Windows?）

- 平台分派在 `grabEntireDesktop`：`src/utils/screengrabber.cpp:303-347` —— macOS → `currentScreen->grabWindow(...)`（`:309-326`）；Unix → `unixScreenshot`（`:328-332`）；Windows → `windowsScreenshot(wid)`（`:333-334`）。`grabFullDesktop` 同构（`:349-384`）。
- Unix 后端：Wayland/X11 优先 **xdg-desktop-portal（`org.freedesktop.portal.Screenshot`）**，失败等超时；纯 X11 且启用 legacy 时走 `x11LegacyScreenshot()`；X11 且 portal 不可用时也回退 legacy。见 `screengrabber.cpp:189-229`、portal 实现 `:54-187`、`x11LegacyScreenshot` `:858-897`。
- Windows/macOS 后端直接用 Qt `QScreen::grabWindow`：`screengrabber.cpp:323-324,405-408,805`。
- Wayland 检测：`src/utils/desktopinfo.cpp:20-23`（`XDG_SESSION_TYPE == "wayland"` 或 `WAYLAND_DISPLAY` 含 "wayland"）。
- 结论：flameshot 是 Linux 优先项目，Windows 侧只做 Qt 通用抓屏，**未使用 Windows 专有抓屏 API**（无 DesktopDuplication/PrintWindow，**未找到**）。

### 3.2 区域选择 UI 与跨多显示器行为

- 整体流程：先抓「整桌」或「指定屏」，再做显示器选择，最后在捕获窗口内做矩形区域选择。
  - `selectMonitorAndCrop`：单屏直接裁剪（`:242-245`）；开了「抓活动显示器」则取光标所在屏（`:247-265`）；否则弹**显示器缩略图选择**（`createMonitorPreviews`，`:275-299`，实现对 `:440-...`）。
  - 指定屏号时绕过选择器：`src/core/flameshot.cpp:212-223`。
- 跨屏拼接（核心）：
  - Windows：逐屏 `grabWindow`，按各屏 DPR 累加「完全在其左/上」的物理宽高得到每屏画布物理偏移，再 `QPainter` 合成到一张大画布：`src/utils/screengrabber.cpp:781-856`（偏移计算 `:817-830`，合成 `:842-853`）。
  - macOS：同理，按 `devicePixelRatio` 与 `geometry` 拼接（`grabFullDesktop` 分支 `:354-376`）。
  - X11 legacy：按逻辑几何 + 统一 DPR 拼接（`:858-897`，注释说明 i3 下 DPR 均匀）。
- 抓整桌后按显示器裁剪：`cropToMonitor`（`:642-779`）。Linux 用「截图尺寸/逻辑尺寸」的比例裁剪（`:682-699`）；Windows 用物理像素累加计算裁剪位置（`:700-728`），并按需平滑缩放到目标物理尺寸（`:759-774`）。
- 区域选择控件本体在 `src/widgets/capture/selectionwidget.cpp`：`setGeometry`（`:133-135`，含 `MARGIN` 外扩）、`setGeometryByKeyboard` 键盘微调（`:432-487,556-566`）、拖拽 `:216,378`。
- 捕获窗口的定位/尺寸：Windows 下窗口 `move` 到所选屏 `geometry().topLeft()`，并按 `pixmap().devicePixelRatio()` 折算尺寸（`src/widgets/capture/capturewidget.cpp:134-167`），`windowHandle()->setScreen(selectedScreen)`（`:165-167`）。选择区域→物理像素换算见 `:2053-2057`。

### 3.3 DPI 处理

- `desktopGeometry()`：非 Windows 平台对每屏 `geometry` 除以 `devicePixelRatio` 后取并集；Windows 直接用逻辑几何：`src/utils/screengrabber.cpp:413-427`。
- Windows 裁剪按「各屏物理宽高」累加（`cropToMonitor` `:700-721`），裁剪结果再 `setDevicePixelRatio(targetDpr)`（`:776`）。
- 绘制时的缩放比统一取 `m_context.screenshot.devicePixelRatio()`：`capturewidget.cpp:319,688,752,1377,1912,2053-2057`。
- 抓取成功后显式设 DPR：macOS `screenshot.setDevicePixelRatio(currentScreen->devicePixelRatio())`（`screengrabber.cpp:325`）、X11 legacy `:869,885`、portal 日志含 `res.devicePixelRatio()`（`:176-179`）。

### 3.4 标注工具清单（形状/文字/序号/高亮）

工厂枚举全部工具：`src/tools/toolfactory.cpp:41-68`。分类如下：

- 形状/绘制：`PencilTool`、`LineTool`、`ArrowTool`、`RectangleTool`、`CircleTool`、`MarkerTool`（高亮笔）、`CircleCountTool`（计数器）、`PixelateTool`（马赛克）、`InvertTool`（反色）。
- 文字：`TextTool`（`text/texttool.cpp`）。
- 序号/计数：`CircleCountTool`（详见下）。
- 选择/移动：`SelectionTool`、`MoveTool`。
- 操作：`UndoTool`/`RedoTool`/`CopyTool`/`SaveTool`/`PinTool`/`AcceptTool`/`ExitTool`/`SizeIncreaseTool`/`SizeDecreaseTool`/`AppLauncher`。
- `SelectionTool` 本体的 `process` 仅画一个矩形边框（`src/tools/selection/selectiontool.cpp:46-52`），它是「再选框」工具，与初始区域选择不同。

**高亮笔（MarkerTool）手法**：用 `CompositionMode_Multiply` + `opacity 0.35` 画半透明高亮线，能透出下层内容——`src/tools/marker/markertool.cpp:52-65`（`setCompositionMode(Multiply)`、`setOpacity(0.35)`、`QPen(color(), size())`），预览同款 `:67-80`。

**编号徽标（CircleCountTool）手法**（对本项目 `annotate` 编号徽标最直接）：
- 自动递增计数、范围 1..999：`src/tools/circlecount/circlecounttool.cpp:14-15,215-224,226-237`。
- 画圆形泡泡 + 描边：`process` 中 `bubble_size = size() + THICKNESS_OFFSET`（`THICKNESS_OFFSET=15`，`:13,113`），可选白色/黑底描边 `:138-148`。
- 前景/背景对比色自动判定：`ColorUtils::colorIsDark(color())` 决定文字白色或黑色（`:108-111`）。
- 数字自适应字号（直到塞进圆内）：`while (bRect.width() > textRect.width()) fontSize--;` `:153-177`。
- 鼠标在外时画指向尾部三角：`:115-136`。
- 预览为半透明（opacity 0.35）+ 白字 `:184-213`。

### 3.5 导出格式与质量参数

- 保存对话框基于 Qt 支持的图片格式：`QImageWriter::supportedImageFormats()` 填充下拉（`src/config/generalconf.cpp:560-564`）。
- 保存扩展名默认 `png`（`src/utils/screenshotsaver.cpp:99-102`），由配置 `saveAsFileExtension` 决定（`confighandler.cpp:104`）。
- JPEG 质量：`jpegQuality` 配置，范围 0-100，**默认 75**（`src/utils/confighandler.cpp:138`）；写入时 `imageWriter.setQuality(...)`（`screenshotsaver.cpp:120`），另 `capture.save(&file, nullptr, ConfigHandler().jpegQuality())`（`:52,307`）。
- **webp 是否支持**：未硬编码，取决于 Qt 构建是否带 webp imageformat 插件（`supportedImageFormats()` 动态返回）。**未找到** flameshot 自身的 webp 编码实现或 webp 质量参数。

### 3.6 对本项目 `annotate`（高亮+边框+编号徽标）的可借鉴手法

- 高亮：用「底色 × Multiply + 低透明度」而不是纯不透明色块，能保留底层文字可读性（`markertool.cpp:58-61`）。若 .NET 侧用 GDI+/WPF，等价于 `BlendMode.Multiply` 或预乘 alpha。
- 边框：`SelectionTool` 用 `MiterJoin + SquareCap` 的实线矩形（`selectiontool.cpp:49-51`）。
- 编号徽标：直接用 `CircleCountTool` 的做法——圆形泡泡、对比色自动判定、字号自适应收缩、指针三角；阈值/常量给得很具体（`THICKNESS_OFFSET=15`, `PADDING_VALUE=2`, 1..999）。
- 结论：这三者是**纯绘制**逻辑，与本项目「纯 Windows .NET」完全兼容，可直接用 `System.Drawing`/WPF 复刻。

---

## ④ UFO `change_detector`：变化检测算法（可移植）

> **重要更正**：`galaxy/visualization/change_detector.py` **不是图像像素 diff**，而是「任务星座（TaskConstellation）结构差异」检测器。文件中**没有**像素指标/阈值/分块/SSIM 之类内容。UFO 全仓检索 `ImageChops|ImageStat|pixel|ssim|hash` 等，**未找到**图像逐像素变化检测实现。若本项目要「变化检测（diff）」，UFO 只能借鉴其**结构 diff 思路**，不能直接搬图像算法。

### 4.1 该文件实际算法（结构 diff）

- 入口 `calculate_constellation_changes(old_constellation, new_constellation)`（`galaxy/visualization/change_detector.py:27-122`）。输入为两个 `TaskConstellation`（旧可为 `None`）。
- 旧为空 → 全部视为新增，`modification_type = "constellation_created"`（`:49-59`）。
- 任务集合 diff：`added = new_ids - old_ids`、`removed = old_ids - new_ids`（`:62-67`）。
- 「修改」判定：对共同 ID 逐字段比对（`_task_properties_changed` `:152-184`），字段清单固定为 `name, description, status, priority, target_device_id, timeout, retry_count, tips`（`:161-170`），另比 `task_data`（`:180-182`）。
- 依赖 diff：把依赖规约为 `(from_task_id, to_task_id)` 元组集合做差（`:85-103`）；共同依赖再逐字段比对 `trigger_action, trigger_actor, condition, keyword, description, priority`（`_dependency_properties_changed` `:186-214`）。
- 总体类型判定 `_determine_modification_type`（`:124-149`）：按「增/删任务 → 增/删依赖 → 改属性」的优先级返回一个字符串枚举。
- 人类可读摘要 `format_change_summary`（`:216-266`）：只展示前 3 个名字，其余计 N more。

### 4.2 真正与「结构变化」相关的另一处：UI 树 diff

- `ui_tree_diff(ui_tree_1, ui_tree_2)`（`ufo/automator/ui_control/ui_tree.py:151-227`）：递归按 path 比对，输出 `{"added":[], "removed":[], "modified":[]}`；比较字段 `name, control_type, rectangle, adjusted_rectangle, relative_rectangle, level`（`:179-186`）；子节点按索引对齐、假设顺序稳定（`:196-214`）。
- 配套 `apply_ui_tree_diff` 可把 diff 应用到旧树（`:229-328`）。
- **可移植价值**：本项目若要「前后两次抓取后判断 UI 是否变化」，用 UIA 控件（name/type/rect/token）做结构 diff 是可靠且无需 ML 的方案；可与图像 diff 互为补充。但**没有阈值/分块/相似度常量**可抄。

---

## ⑤ UFO `control_filter`：可交互控件筛选

> **重要更正**：`ufo/automator/ui_control/control_filter.py` **不是 UIA 属性过滤器**，而是**文本/语义/图标相似度过滤器**（NLP）。且**全仓无任何调用方**（除自身与配置 schema），属**死代码**——`git grep -rn "ControlFilter"` 仅命中该文件本身与 `config_schemas.py:365,539` 的配置项，`app_agent_processing_strategy.py` 走的是 UIA/OmniParser 收集路线，不经过它。

### 5.1 control_filter.py 的真实过滤条件

- 工厂 `ControlFilterFactory.create_control_filter` 支持 `text/semantic/icon` 三类（`:16-29`）。
- `TextControlFilter.control_filter`：把 plan 拆词（只留字母/中文，`plans_to_keywords` `:112-129`），用**双向子串匹配** `keyword in control_text or control_text in keyword`（`:180-187`）。
- `SemanticControlFilter.control_filter`：sentence-transformers 编码 + 余弦相似度，`heapq.nlargest(top_k, ...)` 取 top-k（`:195-230`）；模型名 `all-MiniLM-L6-v2`（`config/ufo/system.yaml:102`）。
- `IconControlFilter.control_filter`：CLIP 编码裁剪图标 + 余弦 top-k（`:238-272`）；模型 `clip-ViT-B-32`（`system.yaml:103`）。
- 匹配对象始终是 `control_item.name.lower()`（`:181,219`），即控件**名称**，不涉及 enabled/visible/control_type。
- **对本项目**：本项目「无 ML」约束下 semantic/icon 不可用；text 子串匹配思路可作**轻量语义线索**（例如按 name 过滤 SoM 元素），但不是「可交互性」判定。

### 5.2 真正判定「可交互/可见/有效」的机制（在 inspector.py）

这才是本项目 SoM 元素筛选真正该借鉴的：

- UIA 查询条件 `_get_control_filter_condition`（`ufo/automator/ui_control/inspector.py:334-370`）用 `CreateAndConditionFromArray` 组合：
  - `UIA_IsEnabledPropertyId == is_enabled`（默认 `True`）：`:342-344`
  - `UIA_IsOffscreenPropertyId == not is_visible`（默认 `True`→要求 offscreen=False）：`:345-349`
  - `UIA_IsControlElementPropertyId == True`：`:350-352`
  - `ControlType ∈ control_type_list` 的 OR 条件：`:353-367`
- 该条件在 `UIABackendStrategy.find_control_elements_in_descendants` 中生效（`:243-255`），结果**上限 500 个**（`min(com_elem_array.Length, 500)` `:266`），并预读 `ControlType/Name/BoundingRectangle` 缓存（`_get_cache_request` `:323-331`）。
- 后置过滤（Win32 后端）：`is_visible()`（`:440-443`）、`is_enabled()`（`:444-447`）、标题匹配（`:448-453`）、control_type 匹配（`:454-459`），最后**只保留 name 非空**（`:461-463`）。
- 「可交互控件类型白名单」来自配置 `CONTROL_LIST`：`config/ufo/system.yaml:28` = `Button, Edit, TabItem, Document, ListItem, MenuItem, ScrollBar, TreeItem, Hyperlink, ComboBox, RadioButton, Spinner, CheckBox, Group, Text`；schema 默认值同见 `config/config_schemas.py:286-304`。
- 元素 ID 是 **1 起始的字符串**（`{"1": control, ...}`）：`ufo/client/mcp/local_servers/ui_mcp_server.py:762,788`；转 `TargetInfo(kind=CONTROL, id=..., name, type, rect, source="uia")`（`:792-808`）。这正是 SoM 编号的来源。
- 每类控件配色 + 高亮开关：`config/ufo/system.yaml:34-47`（`ANNOTATION_COLORS` 按 ControlType 映射、`HIGHLIGHT_BBOX: True`、`ANNOTATION_FONT_SIZE: 22`）。

### 5.3 对本项目 SoM 元素筛选的可借鉴点

1. 用 UIA 条件（IsEnabled / IsOffscreen / IsControlElement / ControlType∈白名单）在**查询阶段**过滤，而不是拉全树再筛——性能更好（`inspector.py:334-370`）。
2. 白名单 `CONTROL_LIST` 就是一份「可交互控件类型」清单，可直接采纳或裁剪（`system.yaml:28`）。
3. 结果再做 `name != ""` 后置过滤，避免匿名容器占位（`inspector.py:461-463`）。
4. 元素 ID 用稳定序号字符串，便于 agent 引用与再定位（`ui_mcp_server.py:762,788`）。
5. 每类控件一个颜色 + 半透明高亮，提升 SoM 可读性（`system.yaml:34-47` + screenshot.py 见 ⑥）。

---

## ⑥ UFO screenshot：截图与 UIA 对齐

### 6.1 截图 API 与抓取回退链

- 抽象基类 `Photographer.capture()`（`ufo/automator/ui_control/screenshot.py:36-43`）。
- 控件截图 `ControlPhotographer.capture`（`:70-133`）三级回退：
  1. `control.capture_as_image()`（pywinauto）：`:91-95`，并校验尺寸 >1（`:97-105`）；
  2. `_win32_print_window(hwnd)`（PrintWindow，RDP 安全）：`:107-120`；
  3. 桌面截图兜底 `DesktopPhotographer(all_screens=False)`：`:122-126`。
- `_win32_print_window`（`:136-204`）：`PW_RENDERFULLCONTENT = 2`（`:167-170`），失败回退 `PW_CLIENTONLY = 1`（`:172-174`）；用 `Image.frombuffer("RGB", ..., "BGRX")` 转 PIL（`:183-193`）。注释明确「可抓被遮挡/离屏窗口」（`:167-169`）。
- 桌面截图 `DesktopPhotographer.capture`（`:290-338`）：`ImageGrab.grab(all_screens=...)` → 主屏重试 → `_win32_grab_screen`（`:311-328`）；全失败返回 1×1 占位（`:330-332`）。
- `_win32_grab_screen`（`:207-287`）：BitBlt → 桌面 `PrintWindow` → 前台窗口 `PrintWindow`，并对「全黑图」做 RDP 断开检测（`:251-255`）。
- 常量化：`DEFAULT_PNG_COMPRESS_LEVEL` 来自配置（`:33`），默认 **1**（`config/ufo/system.yaml:68`）。

### 6.2 是否缩放 / 编码

- **缩放**：`Photographer.rescale_image`（`:45-67`）——按比例 LANCZOS 缩放后贴到目标尺寸黑底画布（letterbox）；由 `scalar` 参数触发（`:128-129,334-335`）。这是可借鉴的「图像经济」做法：把大图统一塞进固定画布。
- **编码**：`image_to_base64`（`:1209-1220`）用 `PNG, optimize=True`；落盘一律 `save(..., compress_level=DEFAULT_PNG_COMPRESS_LEVEL)`（`:132,337,469,504,754,913-915,1124,1196`）。
- 拼接：`concat_screenshots` 水平拼接（`:1160-1198`），等高裁剪后左右并排。
- SoM 徽标图元缓存：`_get_button_img` 用 `lru_cache(2048)`（`:573-605`），字体 `lru_cache(64)`（`:607-610`）——频繁标注时避免重复渲染。

### 6.3 截图与 UIA 控件坐标如何对齐

- **核心换算**：`PhotographerDecorator.coordinate_adjusted(window_rect, control_rect)`（`:361-377`）——用控件**绝对屏幕矩形**减去窗口矩形 `left/top`，得到「相对窗口」的 `(left, top, right, bottom)`，用于在窗口截图上画框。
- 归一化版本 `coordinate_adjusted_to_relative`（`:379-398`）：除以窗口宽高，得到 0..1 浮点相对坐标（供不同分辨率复用）。
- 标注绘制 `AnnotationDecorator.capture_with_annotation_dict`（`:665-757`）：对每个控件 `control.rectangle()` → `coordinate_adjusted` → 画半透明色块（`:693-731`）→ 画数字/字母徽标（`:733-750`）。
- 基于 `TargetInfo` 的版本 `TargetAnnotationDecorator._convert_absolute_to_relative_coords`（`:798-824`）与 `capture_with_target_info`（`:826-919`）：直接用 `TargetInfo.rect`（绝对坐标）减 app 窗口 `rect`，无需 UIAWrapper。
- 编号生成：`get_annotation_dict`（`:632-644`）——`number` → `str(i+1)`，`letter` → `number_to_letter`（`:612-630`）。`TargetAnnotationDecorator` 用 `target.id or str(i+1)`（`:898`）。
- 图标裁剪 `get_cropped_icons_dict`（`:646-663`）：按控件相对矩形从窗口截图裁出小图，供 icon 过滤。
- IoU 去重：`control_iou`（`:1222-1244`）与 `merge_control_list` / `target_info_iou` / `merge_target_info_list`，阈值默认 **0.1**（`:1246-1338`，`:1250,1320`），配置项 `IOU_THRESHOLD_FOR_MERGE: 0.1`（`system.yaml:12`）。

> 注：UFO 的 SoM 配色 / 字体 / 高亮开关见 `system.yaml:34-47`；`AnnotationDecorator` 的默认底色 `#FFF68F`（`:519`），半透明填充 alpha=80（约 31%，`:715,718`），描边 `(255,160,160,180)`（`:724`）。

---

## ⑦ 可借鉴清单（服务①/②/③）

> 标记：① 寻址通用 ② 图像经济 ③ 语义桥。按性价比排序。

| # | 借鉴点 | 服务 | 出处 | 说明 |
|---|---|---|---|---|
| 1 | UIA 查询阶段过滤可交互控件（IsEnabled/IsOffscreen/IsControlElement/ControlType 白名单） | ③ | `ufo/automator/ui_control/inspector.py:334-370` | 一次查询拿到「有效可交互」元素，避免拉全树 |
| 2 | 可交互控件类型白名单 `CONTROL_LIST` | ③ | `config/ufo/system.yaml:28`；`config_schemas.py:286-304` | 可直接采纳/裁剪为 SoM 元素类型集 |
| 3 | 匿名元素后置过滤 `name != ""` + 结果上限 500 | ③② | `inspector.py:266,461-463` | 避免空名容器污染 SoM，限制规模 |
| 4 | SoM 标注 = 每控件类型一色 + 半透明色块 + 数字徽标 | ①②③ | `screenshot.py:693-750`；`system.yaml:34-47` | 半透明不遮内容、类型配色助识别 |
| 5 | 绝对→窗口相对坐标换算 `coordinate_adjusted` | ①③ | `screenshot.py:361-377,798-824` | UIA 绝对矩形与截图对齐的基础 |
| 6 | 归一化相对坐标（0..1）适配不同分辨率 | ① | `screenshot.py:379-398` | 分辨率无关的元素寻址 |
| 7 | 缩放统一塞入固定画布（LANCZOS + letterbox） | ② | `screenshot.py:45-67` | 直接服务「图像经济」控 token/体积 |
| 8 | PNG 压缩级别可配 + base64 optimize | ② | `screenshot.py:33,1209-1220`；`system.yaml:68` | UFO 取 compress_level=1（速度优先） |
| 9 | 用 UIA 结构（控件树/属性）做前后变化 diff | ③② | `ufo/automator/ui_control/ui_tree.py:151-227` | 无 ML 的「UI 是否变化」判定 |
| 10 | 显示器枚举 + 每屏 DPI（Effective）→ Scale = Dpi/96 | ① | `Monitor.cs:71-88,95-106`；`Monitor.cs(model):21-23` | 多显示器/DPI 抓取基座 |
| 11 | 窗口抓取优先 `PrintWindow(PW_RENDERFULLCONTENT=2)`，回退 GDI，再回退桌面 | ① | `screenshot.py:136-204,107-126` | 可抓被遮挡/离屏窗口（本项最实用对比） |
| 12 | 抓屏回退链 + 全黑检测（RDP 断开识别） | ① | `screenshot.py:207-287` | 提升抓取鲁棒性 |
| 13 | 多显示器按各屏 DPR 物理像素偏移拼成大画布 | ① | `flameshot screengrabber.cpp:781-856,700-728` | Windows 混合 DPI 拼图参考 |
| 14 | 高亮笔 = Multiply + opacity 0.35（透出底图） | ②③ | `flameshot markertool.cpp:58-61` | annotate 高亮手法 |
| 15 | 编号徽标：圆形泡泡 + 对比色自动判定 + 字号自适应收缩 | ③ | `flameshot circlecounttool.cpp:100-182` | annotate 编号徽标手法 |
| 16 | 简单 IoU 去重（阈值 0.1）合并叠加控件 | ③ | `screenshot.py:1246-1338`；`system.yaml:12` | 避免重复标注同类重叠控件 |
| 17 | 徽标图元 `lru_cache` 复用 | ② | `screenshot.py:573-610` | 批量标注降耗 |

---

## ⑧ 不采纳清单

> 给定本项目约束：**纯 Windows .NET、无 ML、动作为 UIA 语义调用、不做录制/模板匹配**。

| 不采纳 | 理由 | 出处 |
|---|---|---|
| UFO `SemanticControlFilter` / `IconControlFilter`（sentence-transformers / CLIP） | 依赖 ML 模型，本项目明确「无 ML」 | `control_filter.py:190-272` |
| UFO 整块 `control_filter.py` 的 NLP 过滤路线 | 与「UIA 语义调用」正交，且该模块在当前 UFO 中无调用方（死代码）；真筛选在 inspector | `control_filter.py:164-272`；全仓 `ControlFilter` 仅命中自身 |
| UFO `ui_tree_diff` 的「子节点按索引对齐」 | 依赖顺序稳定这一脆弱假设，UIA 子序可能变化；本项目可改用 token/Name 为键 | `ui_tree.py:200-214` |
| ScreenToGif 的 GDI 窗口 BitBlt（从桌面 DC 取窗口矩形） | 抓不到被遮挡窗口内容，恰是本项目要避免的缺陷；UFO 的 PrintWindow 更优 | `ScreenToGif.Util/Native/Capture.cs:97-103` |
| ScreenToGif 的录制/帧管线（DXGI dirty rects 丢帧、帧落盘、GIF/APNG/PSD/视频编码、Gifski、FFmpeg） | 本项目是「单次/按需抓取」，不做录制与动图/video 编码 | `DirectChangedImageCapture.cs`；`ExportFormats.cs`；`GifskiInterop.cs`；`FfmpegWebpPreset.cs` |
| flameshot 的 X11/Wayland/portal 后端与其「大画布拼接后裁剪」的 Linux 逻辑 | 纯 Windows .NET，用不上 X11/portal；Windows 拼接逻辑也需按本项目坐标体系重写 | `screengrabber.cpp:189-229,682-699` |
| ScreenToGif/flameshot 的交互式区域选择 UI（SelectControl / SelectionWidget / 显示器缩略图） | 本项目坐标来自 UIA 元素或显式区域，不需要人工拖拽选择覆盖层 | `ScreenToGif/Controls/SelectControl.cs`；`flameshot selectionwidget.cpp` |
| flameshot `jpegQuality` / `QImageWriter` 导出路线 | 本项目面向 agent 图像经济，PNG/Base64 与有损压缩策略按自身 SDK 定，无需抄 Qt 导出 | `screenshotsaver.cpp:99-121`；`confighandler.cpp:138` |
| UFO 的 OmniParser grounding / 模板匹配式控件识别 | 依赖视觉模型，且本项目动作为 UIA 语义调用，不做模板匹配 | `screenshot.py` 之外；`grounding/omniparser.py:177-195`（box/iou 阈值、`BOX_THRESHOLD=0.05`、`IOU_THRESHOLD=0.1`，`system.yaml:90-95`） |

---

## ⑨ 来源（三个 commit + 文件:行号清单）

### commit

- ScreenToGif：`a4d0a67c2131cd048ceec86cd40afc2f1a06f2fd`（2026-07-28）。
- flameshot：`2d478061ffeeba5919d3a3d9168f93542ea9b357`（2026-09-18）。
- UFO：`e2a03126241c696fdaf9a669a271ca3fca6d9916`（2026-09-22）。

### ScreenToGif

- `ScreenToGif.Util/Native/Monitor.cs:14-36, 40-58, 71-88, 95-106, 108-156, 158-163, 184-198` — 显示器枚举 / GetMonitorInfo / DPI / 反查
- `ScreenToGif.Model/Models/Native/Monitor.cs:7-25` — Monitor 模型（Dpi/Scale/IsPrimary）
- `ScreenToGif.Native/External/User32.cs:104, 111, 115, 392` — MonitorFromWindow / GetMonitorInfo / EnumDisplayMonitors / EnumDisplayDevices
- `ScreenToGif.Native/External/ShCore.cs:16-17` — GetDpiForMonitor
- `ScreenToGif.Native/Structs/MonitorInfoEx.cs` — MONITORINFOEX
- `ScreenToGif.Model/Enums/Native/DpiTypes.cs`、`ScreenToGif.Model/Enums/Native/ProcessDpiAwareness.cs` — DPI 枚举
- `ScreenToGif.Util/Native/Capture.cs:22-50, 60-87, 89-122, 124-199` — 屏幕/窗口 BitBlt、光标
- `ScreenToGif/Capture/ImageCapture.cs:18, 38-41, 45-51, 60, 67, 99-144, 171` — 区域/窗口抓取 + 光标 + PNG 落盘
- `ScreenToGif/Capture/DirectImageCapture.cs:19, 24, 44-46, 130-142, 274, 442, 630` — DXGI Desktop Duplication
- `ScreenToGif/Capture/DirectChangedImageCapture.cs:24, 40-62, 69-94, 180-364` — dirty/move rects 变化检测
- `ScreenToGif/Capture/CachedCapture.cs:59, 90, 263` — 内存缓存 BitBlt
- `ScreenToGif/Controls/BaseScreenRecorder.cs:111-117` — Direct/Cached 选择
- `ScreenToGif.Util/Settings/UserSettings.cs:550-560, 984-997` — SelectedRegion / OnlyCaptureChanges / UseDesktopDuplication / UseMemoryCache
- `ScreenToGif.Util/GifskiInterop.cs:14-155` — Gifski GIF 编码
- `ScreenToGif.ViewModel/ExportPresets/AnimatedImage/Webp/FfmpegWebpPreset.cs:8-17, 88-111`; `WebpPreset.cs:7-11` — WebP/FFmpeg
- `ScreenToGif.ViewModel/ExportPresets/Image/JpegPreset.cs:5-27` — JPEG 预设
- `ScreenToGif.Model/Enums/ExportFormats.cs:18`; `ScreenToGif.Model/Enums/EncoderTypes.cs:8` — 格式枚举
- `ScreenToGif/Windows/Recorder.xaml.cs:593-627` — 窗口吸附/多显示器边界
- `ScreenToGif/Controls/SelectControl.cs:21, 129-158` — 区域选择控件（交互，不采纳）
- `ScreenToGif/Capture/ImageCapture.cs:38`、`Capture.cs:25,63,97` — GetWindowDC(桌面)，佐证「无 PrintWindow」

### flameshot

- `src/utils/screengrabber.cpp:54-187` — freedesktop portal
- `src/utils/screengrabber.cpp:189-229` — unixScreenshot（X11 legacy / portal）
- `src/utils/screengrabber.cpp:231-301` — selectMonitorAndCrop
- `src/utils/screengrabber.cpp:303-347, 349-384` — grabEntireDesktop / grabFullDesktop（平台分派）
- `src/utils/screengrabber.cpp:386-427` — screenGeometry / grabScreen / desktopGeometry（DPI）
- `src/utils/screengrabber.cpp:440-...` — createMonitorPreviews（显示器缩略图选择）
- `src/utils/screengrabber.cpp:642-779` — cropToMonitor（Windows 物理像素 / Linux 比例）
- `src/utils/screengrabber.cpp:781-856` — windowsScreenshot 多屏拼图
- `src/utils/screengrabber.cpp:858-897` — x11LegacyScreenshot 多屏拼图
- `src/utils/desktopinfo.cpp:20-23` — Wayland 检测
- `src/widgets/capture/capturewidget.cpp:120-189, 319, 688, 752, 1377, 1912, 2053-2057` — 捕获窗口定位/尺寸/DPR
- `src/widgets/capture/selectionwidget.cpp:124-135, 216, 378, 432-487, 556-566` — 区域选择控件
- `src/tools/toolfactory.cpp:41-68` — 标注工具清单
- `src/tools/marker/markertool.cpp:8, 52-80` — 高亮笔
- `src/tools/circlecount/circlecounttool.cpp:11-15, 100-182, 184-213, 215-237` — 编号徽标
- `src/tools/selection/selectiontool.cpp:46-52` — 矩形框
- `src/utils/screenshotsaver.cpp:44-52, 90-121, 283-307` — 保存/质量
- `src/config/generalconf.cpp:554-576, 844-857` — 格式下拉 / jpegQuality
- `src/utils/confighandler.cpp:104, 138` — saveAsFileExtension / jpegQuality(默认75)

### UFO

- `galaxy/visualization/change_detector.py:27-184, 186-214, 216-266` — 任务星座结构 diff
- `ufo/automator/ui_control/ui_tree.py:122-149, 151-227, 229-328` — UI 树扁平化 / diff / apply
- `ufo/automator/ui_control/control_filter.py:10-29, 112-129, 164-187, 190-230, 233-272` — NLP/语义/图标过滤
- `ufo/automator/ui_control/inspector.py:66-89, 207-311, 313-377, 404-463, 466-549` — 查询条件/可交互筛选/上限/后置过滤
- `ufo/automator/ui_control/screenshot.py:33, 36-67, 70-204, 207-338, 361-398, 401-505, 508-767, 770-919, 922-935, 938-1158, 1160-1207, 1209-1220, 1222-1338` — 抓取/缩放/坐标换算/SoM/合并
- `ufo/client/mcp/local_servers/ui_mcp_server.py:70-119, 746-808` — 控件收集与 TargetInfo（SoM 编号来源）
- `ufo/agents/processors/strategies/app_agent_processing_strategy.py:475-570, 587-709` — UIA/grounding 收集与合并
- `ufo/agents/processors/schemas/log_schema.py:11-21` — 录制字段
- `config/ufo/system.yaml:12, 28, 34-47, 68, 90-95, 98-103` — 配置常量
- `config/config_schemas.py:269-270, 286-304, 317-319, 339, 365, 539` — 配置 schema
- `ufo/automator/ui_control/grounding/omniparser.py:30, 160-195, 197-219` — OmniParser grounding（不采纳）

### 未找到项汇总

- ScreenToGif 使用 `PrintWindow` / WinRT WGC（`WindowsGraphicsCapture`）：**未找到**。
- flameshot 自身的 webp 编码实现/webp 质量参数：**未找到**（仅 Qt 动态格式列表）。
- UFO 图像逐像素 diff（`ImageChops`/`ImageStat`/SSIM/感知哈希/阈值/分块）：**未找到**。
- UFO `control_filter.py` 的生产调用方：**未找到**（仅自身与配置 schema）。
