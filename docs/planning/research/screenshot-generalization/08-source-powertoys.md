# 08 · PowerToys 源码深挖（PowerOCR OCR 全链路 + 窗口枚举 + 多显示器）

> **来源等级：A 源码核实**（本地 clone `Externals/PowerToys` @ `62e60ca858`，2026-09-27，微软官方；引用 `文件:行`）。

## ① 结论与可借鉴点（先给答案）

1. **非打包 .NET 10 能直接用 `Windows.Media.Ocr`（证据确凿）**：`PowerOCR.csproj:20` `WindowsPackageType=None`（非 MSIX）却用完整 OCR；`PowerOCR.Core.csproj:19-21` 只引 `System.Drawing.Common`，TFM `net10.0-windows10.0.26100.0`；`PowerOCR.Core.UnitTests` 纯控制台 exe 直接用 `OcrEngine.MaxImageDimension`。**本项目 TFM `net10.0-windows10.0.22621.0` → `Windows.Media.Ocr`/`Windows.Graphics.Imaging` 开箱可用、零新增包。**
2. **OCR「黄金配方」**（`PowerOCR.Core/`）：
   - 三级缩放：优先 **1.5×**（`EnhancedScale`），超 `MaxImageDimension` 退回 **1.0×**，再不行降采样；**Word(点词)模式永不放大**（`TextExtractorService.cs:74-100`）。
   - **最小画布 80×80**（内容 64 + 两侧各 8 padding；短边 <64 触发）（`BitmapPreprocessor.cs:13-14,44-66`）。
   - 坐标映射 **`ScaleX/ScaleY + OffsetX/OffsetY`**（`PreparedBitmap.cs:11-30`）。
   - **行框 = 词框 Union**（`WindowsOcrRecognizer.cs:76-78`；`OcrRect.Union/Contains/IntersectionArea`，`OcrRect.cs:13-31`）。
   - **CJK/中英混排智能空格拼接**（`OcrTextFormatter.cs:61-146`）——对中文质量关键。
3. **窗口枚举两套成熟实现，建议直接借过滤器清单**：WindowWalker（`Window.cs`/`OpenWindows.cs`）过滤最全（含 UWP ApplicationFrameHost 换真 PID）；`UITestAutomation.Next`（`WindowControl.cs`/`WindowHelper.cs`/`MonitorInfo.cs`）最新最干净（含 `EXTENDED_FRAME_BOUNDS`、`AttachThreadInput` 置前）。
4. 本项目现状差距：`ScreenCapture.cs:56-82` 有 `EnumWindows+IsWindowVisible+GetAncestor(GA_ROOT)`，但**缺** cloak/工具窗口/属主/UWP 宿主过滤；多显示器只有 `SM_*VIRTUALSCREEN`（`GdiCapture.cs:98-105`）。

## ② 源码级事实

### A. OCR 工程配置
| 事实 | 位置 |
|---|---|
| 非打包 `WindowsPackageType=None` | `PowerOCR.csproj:20` |
| WinUI3 自包含 `WindowsAppSDKSelfContained=true`、AOT `PublishAot=true`、自持 Main | `PowerOCR.csproj:21,24-29` |
| Core 只引 `System.Drawing.Common` | `PowerOCR.Core.csproj:19-21` |
| TFM `net10.0-windows10.0.26100.0`（min 19041） | `Common.Dotnet.props:6-10` |
| DPI manifest PMv2 | `PowerOCR/app.manifest:12-13` |
| CsWinRT 兼容层需 `WinRT.ComWrappersSupport.InitializeComWrappers()`（仅 WinUI/AOT 自持 Main） | `PowerOCR/Program.cs:20` |

### B. OCR 封装
- `WindowsOcrRecognizer.cs`：**只用 `OcrEngine.TryCreateFromLanguage(language)`**（:25），null 抛 `InvalidOperationException`（:26-30）；**未用 `TryCreateFromUserProfileLanguages()`**。
- 送图通道（:32-45）：`bitmap.Save(stream, ImageFormat.Bmp)` → `stream.AsRandomAccessStream()` → `BitmapDecoder.CreateAsync` → `GetSoftwareBitmapAsync().AsTask(ct)` → `RecognizeAsync`；`softwareBitmap`/`stream` 于 `finally` 释放。
- `MapLine`（:64-80）：词框 → `OcrRect`；行框 = `Aggregate(Union)`。
- `OcrDocument.Words => Lines.SelectMany(line => line.Words)`（`OcrDocument.cs:9`）。
- 坐标：`BoundingRect` 相对**送入引擎的那张图**（=padding+缩放后）。

### C. 预处理（`BitmapPreprocessor.cs`，最值得抄）
- 常量 `MinimumDimension=64`、`Padding=8`（:13-14）。
- 输出 `Format32bppArgb`，**底色 = `source.GetPixel(0,0)` 的 `graphics.Clear(...)`**（:25-27）→ 铺底/去透明落地方式。
- 缩放 `HighQualityBicubic`+`PixelOffsetMode.HighQuality`（:28-29）。
- 尺寸：`scaled=max(1,round(src*scale))`；短边 <64 → `output=max(scaled+16,80)` 且 `offset=8`（逐轴独立）。**最小可 OCR 画布 80×80**（单测 `BitmapPreprocessorTests.cs:16-30`：10×10→80×80）。
- `PreparedBitmap` 回传 `Bitmap/ScaleX/ScaleY/OffsetX/OffsetY`；`ScaleX=scaled.Width/src.Width`（真实缩放，:36-41）。

### D. 缩放决策（`TextExtractorService.cs`）
- `DefaultScale=1.0`、`EnhancedScale=1.5`（:16-17）；`SelectScale`（:74-100）三级；`FitsOcrLimit` 用**运行时 `OcrEngine.MaxImageDimension`**（:102-104，从不硬编码）；padding 后仍超限抛（:40-43）。
- 点词映射：`ocrPoint=(clickX*ScaleX+OffsetX, clickY*ScaleY+OffsetY)`（:62-72），在 `document.Words` 上 `Bounds.Contains`（:106-117）。
- 选区 DIP→物理像素 ×`rasterizationScale`，产出 `Local`(显示器内)/`Absolute`(虚拟屏)两套矩形（`SelectionGeometry.cs:19-31`）。

### E. 线程/取消/GC/AOT
- 串行化 `IsProcessing` 拒重入（`OverlayManager.cs:215-229`）；会话级 `CancellationTokenSource`（:541）；token 透传 `AsTask(ct)`；取消 `OperationCanceledException` 单独 catch（:274-277）。
- **AOT 坑**：WinRT `IVectorView` 的 `IEnumerable` 投影在 AOT 下可能 `InvalidCastException` → **按索引遍历**（`OverlayManager.cs:92-95`）。
- OCR 失败/无词/空结果不抛上层（`OcrFailed`/`NoTextFound`，:278-300）。

### F. 语言回退链（`PopulateLanguages`，`OverlayManager.cs:431-480`）
`AvailableRecognizerLanguages`（空→`NoOcrLanguages`）→ 设置 `PreferredLanguage`（按 NativeName）→ 当前输入法 `Language.CurrentInputMethodLanguageTag`（精确→AbbreviatedName）→ `Languages[0]`。

### G. OCR 文本格式化（`OcrTextFormatter.cs`）
`UsesSpaces`：zh/ja 开头 → false（:61-63）；拉丁用 `line.Text`，CJK 逐词拼，RTL 反转词序（:26-29）；`JoinCjkAwareWords` 按 Rune UnicodeCategory 决定空格（:65-89）；`FormatSingleLine` 折换行（:37-59）。

### H. 窗口枚举（两套）
**WindowWalker（过滤最全）** `OpenWindows.cs:105-131`：`IsWindow && Visible && IsOwner && (!IsToolWindow||IsAppWindow) && !TaskListDeleted && ClassName!="Windows.UI.Core.CoreWindow" && Process!=自身exe && (!IsCloaked||cloakState==OtherDesktop)`。
- 判据实现（`Window.cs`）：标题(:49-70)、可见(:110-115)、**Cloaked=`DwmGetWindowAttribute(...,14)`**(:305-322)、工具窗 `WS_EX_TOOLWINDOW`(:144-152)、`WS_EX_APPWINDOW`(:157-165)、`ITaskList_Deleted`(:170-176)、属主 `GetWindow(GW_OWNER)==0`(:181-186)、类名(:342-353)。
- **UWP 宿主换真 PID**：进程名 `ApplicationFrameHost.exe` → `EnumChildWindows` 找类名 `Windows.UI.Core.*` 子窗口取真 PID（`Window.cs:390-421`；**仅窗口未最小化时有效**）。

**UITestAutomation.Next（最新最干净）**
- `WindowControl.EnumerateAllWindows/EnumerateProcessWindows`（`WindowControl.cs:139-250`）：`EnumWindows`+`GetWindowThreadProcessId`+`GetWindowRect`+`GetClassNameW`+`GetWindowTextW`+`IsWindowVisible`；每窗口 try/catch 容错。
- `WindowsFinder.ToWindowInfos` 过滤 `IsVisible && W>0 && H>0`（`Windows.cs:51-73`）。
- `WindowControl.TryBringToForeground`（:394-441）：`AttachThreadInput`+`BringWindowToTop`+`SetForegroundWindow`——**绕过前台锁的标准手法**。
- `WindowHelper`：`IsWindowCloaked`（:195-207）、**`GetVisibleBounds` 用 `DWMWA_EXTENDED_FRAME_BOUNDS=9`**（:224-243，物理像素、排不可见 resize 边框、不受 DPI 虚拟化）、`CaptureVisibleWindow`（SetWindowPos TOPMOST+DwmFlush+CopyFromScreen，:256-289）。
- ⚠️ `WindowHelper:215-218`：与显示器几何比较**必须在 per-monitor-DPI-aware 宿主内**（本项目 `EnsureDpi` 满足）。

### I. 多显示器
- PowerOCR 主路径用 WinRT `DisplayArea.FindAll()`（`OverlayManager.cs:79,94-100`）。
- 纯 Win32：`MonitorInfo.cs:63-80`（`EnumDisplayMonitors`+`MONITORINFOEX`：rcMonitor/rcWork/deviceName/primary）、`:101-132`（`MonitorFromWindow(MONITOR_DEFAULTTONEAREST)`）。
- 虚拟屏 `SM_X/Y/CX/CYVIRTUALSCREEN`（`ScreenHelper.cs:24-31`）；逐显示器 DPI `GetDpiForMonitor(MDT_EFFECTIVE_DPI)` / `GetDpiForWindow`。

## ③ 落地建议与代价
1. **OCR 门面放 Engine**（新建 `Capture/WindowsOcr.cs` 或 `Vision/`）：照搬 `WindowsOcrRecognizer`+`BitmapPreprocessor`+`SelectScale`+`OcrRect/OcrWordData/OcrLineData/OcrDocument`+`OcrTextFormatter`；依赖用现有 `System.Drawing.Common`，**无需改 csproj**；约 250–350 行 + 单测（FakeRecognizer）。**不要放 Session/宿主**。
2. **坐标链要消化两段缩放**：`原生物理像素 →(k)→ 输出图 →(OCR scale+pad)→ OCR 图`。**强烈建议对未缩放的 native 图跑 OCR**，再乘 k 画 marks；抽 `OcrCoordinateTransform(k,scaleX,scaleY,offsetX,offsetY,originX,originY)` 纯函数并单测。
3. **UIA 优先、OCR 兜底、按 IoU 融合**：UIA 元素矩形更精确；仅对"画布/位图型"区域 OCR；用 `OcrRect.IntersectionArea` 合并去重。
4. **窗口枚举补齐过滤器**（照抄 WindowWalker）：属主、工具窗、`ITaskList_Deleted`、cloaked、UWP 换 PID；抓取矩形改 `EXTENDED_FRAME_BOUNDS`。约 60–100 行 P/Invoke。
5. **多显示器**：引入 `MonitorInfo` 风格枚举（`EnumDisplayMonitors`+`MONITORINFOEX`）+ `MonitorFromWindow` + per-monitor DPI。约 80 行，是当前明显缺口。
6. **OCR 串行化**：Engine 门面 `SemaphoreSlim(1,1)` + 按 languageTag 缓存 `OcrEngine`。

## ④ 坑与风险
| # | 风险 | 依据 | 应对 |
|---|---|---|---|
| 1 | 语言包缺失（`AvailableRecognizerLanguages` 空→`TryCreateFromLanguage` null） | `WindowsOcrRecognizer.cs:25-30`；`OverlayManager.cs:222-226,433-442` | 先查已装语言，空→中文提示"设置→时间和语言→语言"，不抛/不空返回 |
| 2 | 点词模式不放大，小字识别不到 | `TextExtractorService.cs:76-82` | 点词/小区域显式放大 + 更新映射 |
| 3 | 小图 OCR 直接空 | `BitmapPreprocessor` 64/8 | 严格 64 内容 + 8 padding（最小 80×80） |
| 4 | `MaxImageDimension` 超限 | :89-104 | 运行时读，三级缩放 + padding 后复核 |
| 5 | **alpha 语义**：GDI 抓屏 alpha 未定义；PowerOCR clone 成 `Format32bppRgb` 再用 BMP 往返 | `NativeSelectionWindow.cs:77-81`；`BitmapPreprocessor.cs:25-27` | 直接喂 `Format32bppArgb` 前先 `Clone(Format32bppRgb)` 或铺不透明底，别让 A=0 变透明/黑 |
| 6 | AOT 下 WinRT 集合 foreach 崩 | `OverlayManager.cs:92-95` | 按索引遍历 |
| 7 | 现状**未用** `TryCreateFromUserProfileLanguages` | `WindowsOcrRecognizer.cs:25` | 若要跟随用户配置，自行实现回退链（参考 `PopulateLanguages`） |
| 8 | UWP 宿主换 PID 仅未最小化有效 | `Window.cs:390-391` | 最小化时先 restore 或退化按标题 |
| 9 | 无桌面/服务会话：截图必败；OCR 未验证 | `OverlayManager.cs:112-168` | 明确检测 + 中文提示，别把无桌面误判为 OCR 失败 |
| 10 | 取消不立刻停 CPU | `WindowsOcrRecognizer.cs:39-45` | 按"放弃等待"处理 |
| 11 | 本项目多段缩放坐标易错 | `ImagePipeline.cs:18` + `BitmapPreprocessor` | 抽纯函数 + 单测；OCR 尽量跑 native 图 |
| 12 | DPI 感知前提 | `WindowHelper.cs:215-218`；`ScreenCapture.cs:35-40` | 保持 `EnsureDpi` 首调语义 |
| 13 | 行框 Union 近似 | `WindowsOcrRecognizer.cs:76-78` | SoM 用词框更精确 |

**最值得直接复制的最小组件清单**：`WindowsOcrRecognizer.cs`(全文)、`BitmapPreprocessor.cs`+`PreparedBitmap.cs`(全文)、`TextExtractorService.SelectScale`+`TransformPoint`、`OcrRect.cs`、`OcrTextFormatter.cs`、`Window.cs:144-186,305-322,390-421`、`WindowHelper.cs:224-243`、`WindowControl.cs:394-441`、`MonitorInfo.cs:63-80,101-132`。（MIT，注意保留出处。）
