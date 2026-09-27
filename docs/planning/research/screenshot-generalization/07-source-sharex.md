# 07 · ShareX 源码深挖（多显示器 / 窗口捕获 / 编码 / 标注 / 历史 / 健壮性）

> **来源等级：A 源码核实**（本地 clone `Externals/ShareX` @ `cf5a6fe`，2026-09-24，已迁 Avalonia+SkiaSharp 新架构；引用 `文件:行`）。

## ① 结论与可借鉴点（按对本项目价值排序）

### A. 图像经济（低耦合可移植）
1. **`memcmp` 逐字节比图做「无变化检测」**：`ImageHelpers.CompareImages` 用 `ntdll!memcmp` 比对锁定 32bppArgb 位图（`ImageHelpers.cs:1050-1069`）。**比哈希省一步**。
2. **按内容自动降位深**：PNG 保存检测透明 → 32bpp/24bpp（`ImageHelpers.cs:2904-2942`）。
3. **有损编码质量回退到满足体积**：`SaveJPEGAutoQuality` 从高到低循环重编码直到 `<= sizeLimit`（`ImageHelpers.cs:3028-3059`）；上层"超 N KB 转 JPEG"（`TaskHelpers.cs:412-439`）。
4. **缩放**：`ResizeImage` 默认 HighQualityBicubic + `SetWrapMode(TileFlipXY)` 消边缘暗边（`ImageHelpers.cs:43-72`）；灰度/黑白见 `ImageEffectsLib/Adjustments/Grayscale.cs`、`BlackWhite.cs`。
5. **注意**：ShareX 截图管线**原生不支持 WebP**（`Enums.cs:55-67`）；WebP 只在 FFmpeg 录制与 ImageEditor（Skia）。

### B. 寻址
1. **虚拟屏**＝`SystemInformation.VirtualScreen`（物理像素、含负坐标，`CaptureHelpers.cs:35-38`）；**"活动显示器"＝光标所在屏**（`Screen.FromPoint(GetCursorPosition())`，:88-91）——**不是**前台窗口所在屏（关键语义分叉）。
2. 虚拟坐标↔0 基：`SM_X/YVIRTUALSCREEN` 偏移（`CaptureHelpers.cs:103-125`）。
3. **窗口矩形优先 `DWMWA_EXTENDED_FRAME_BOUNDS`**，失败回退 `GetWindowRect`（`CaptureHelpers.cs:326-346`）。
4. **窗口清单语义过滤**：可见 + 未被 DWM cloaked + 有标题 + 类名不在忽略表 + 矩形有效（`WindowsList.cs:65-69`）；忽略 `Progman/Button`（:37）；`WindowsRectangleList` 排除 `WS_EX_TOOLWINDOW|NOACTIVATE`（:140-150）、忽略 `CEF-OSC-WIDGET`（NVIDIA 覆盖层，:38-41）、被包含小矩形去重（:79-99）。**可直接照抄为"可截窗口白名单"**。
5. **元素级 = Win32 子窗口，不是 UIA**：`EnumChildWindows`（:171-181）+ 每个窗口附客户区矩形（:183-191）。**ShareX 完全没有 UIA**——元素级/UIA 语义桥是它的空白。
6. 按标题/进程寻址：`SearchWindow` 精确→遍历进程 `MainWindowTitle.Contains`（`NativeMethods_Helpers.cs:609-625`）。

### C. 语义桥
1. **OCR 用系统内置 `Windows.Media.Ocr`**：`OCRHelper` 用 `OcrEngine.TryCreateFromLanguage`，要求 OS ≥ 10.0.18362（`ShareX.Tools/Tools/OCR/OCRHelper.cs:36-106`）；识别前 `ScaleImageFast` 放大（:62-73）；中日**词拼接不加空格**、RTL 反转词序（:91-99）。零第三方依赖范式。
2. **标注 = 数据驱动**：`Annotation` 基类只有几何+样式+`HitTest/GetBounds/Clone`（`ShareX.ImageEditor/Core/Annotations/Base/Annotation.cs:52-240`），子类 `[JsonDerivedType]` 多态序列化；矩形=`RectangleAnnotation`；**编号球=`NumberAnnotation`**（自动递增、半径自适应、引线尾，`Text/NumberAnnotation.cs:34-240`）。**适合 agent 生成结构化标注指令**。
3. **无 UIA 语义桥**：ShareX 的"语义"只有 `TaskMetadata` 记的窗口标题+进程名（落历史 Tags）。

## ② 源码级事实（要点）

- **窗口捕获路径**：`Screenshot.CaptureWindow(handle)` 先选矩形（`CaptureClientArea`→`GetClientRect`；否则 `GetWindowRectangle`=DWM 扩展边框），再 `CaptureRectangle`→`CaptureRectangleGDI`，**从桌面 DC BitBlt 该矩形**（`Screenshot.cs:61-97,113-124,158-186`）。
  - **`PrintWindow` P/Invoke 已声明但全仓无调用**（`NativeMethods.cs:219`，死代码）。窗口截图＝"把窗口摆前台后截它所在屏幕矩形"，**不是遮挡无关渲染**。
  - 托管回退 `CaptureRectangleManaged`（`Graphics.CopyFromScreen`，:188-204）**无调用者**。
  - 遮挡/最小化靠调用方 `Restore()+Activate()`（`CaptureWindow.cs:48-76`）。
- **透明捕获**：白/黑底三次截图 → `CompareImages` 校验两次白底一致后 alpha 反解（`Screenshot_Transparent.cs:38-205`）；阴影裁剪 `TrimShadow`（:207-284）；`CaptureShadow` 时矩形 `Inflate(ShadowOffset)`（:44-49，说明 DWM 扩展边框本身不含阴影）。
- **编码分发**：`TaskHelpers.SaveImageAsStream`（:470-514）：PNG→`SavePNG(bitDepth)`；JPEG→`FillBackground(White)` 后 `SaveJPEG(quality)`；GIF→量化器。`SaveJPEG` 用 `EncoderParameters(Quality)` + `Clamp(0,100)`（`ImageHelpers.cs:3002-3026`）。
- **技术栈**：核心截图=System.Drawing；新 ImageEditor/区域捕获=**SkiaSharp**；HDR=`Vortice.Direct3D11/DXGI/WIC`。
- **标注范式**：`ShareX.ImageEditor/Core/Annotations/`（Shapes/Effects/Text 三类，全实现 `HitTest/GetBounds/Clone` + JSON 序列化）。区域选择 UI 见 `RegionCaptureWindow.axaml.cs`（1502 行）、`RegionSelectionOverlay.cs:149-196`（**本项目不实现交互**）。
- **历史**：SQLite schema（`HistoryLib/Managers/HistoryManagerSQLite.cs:64-85`）`History(Id,FileName,FilePath,DateTime,Type,Host,URL,...,Tags)`；`busy_timeout=5000`；**无内容去重**（`HistoryManager.cs:92-96`）；`CleanupManager` 只按"保留最近 N 个"清备份/日志，**截屏文件本身无容量上限**。→ **没有值得照抄的归档去重/容量上限策略**。
- **HDR**：`HDRScreenCapture.ApplyColorCorrection`（`HDRScreenCapture.cs:57-137`）DXGI 复制 + WIC 色调映射；per-output try/catch 回退（:123-126）；仅 `RgbFullG2084NoneP2020` 且相交时生效（:672-685）。
- **稳健性**：无 `WM_DISPLAYCHANGE`/WTS session 处理（每次现读 `Screen.*` 自愈）；`WindowsRectangleList` 支持 `CancelAfter(Timeout)`（:43-75）；区域捕获窗口枚举 5000ms 超时。

## ③ 落地建议 / 明确不照搬

**建议借鉴**：
1. `memcmp` 无变化检测（**先归一化**：统一 32bppArgb + 同尺寸）。
2. 目标体积压缩（质量回退循环）→ 移植到 WebP。
3. PNG 位深自适应（透明→32bpp，否则 24bpp）。
4. 窗口白名单过滤链（cloaked/工具窗/忽略类名/被包含去重）。
5. DWM 扩展边框 + 客户区双矩形口径。
6. 按标题/进程寻址的"精确→模糊"两级查找。
7. OCR `Windows.Media.Ocr`（CJK/RTL 处理 + 放大）。

**需自研（ShareX 未覆盖）**：真正的 UIA 元素级寻址；遮挡/最小化窗口独立捕获（本项目 WGC 链更强，别退化）；截图历史去重/容量上限。

**明确不照搬**：区域选择交互/上传/热键/历史存储/设置 UI/效果预设打包/FFmpeg 录制/GDI+ 效果与 Avalonia 强耦合/透明三次截图（脆弱，改用 WGC alpha）。

## ④ 坑与风险
1. **DPI 不感知会全线错位**（ShareX 靠 PMv2；本项目 `EnsureDpi` 已设，须保证早于调用）。
2. **"活动显示器"=光标屏，不是前台窗屏**；要前台窗屏用 `Screen.FromHandle(GetForegroundWindow())`。
3. **虚拟屏负坐标**：裁剪换算必须走 `SM_X/YVIRTUALSCREEN`。
4. **ShareX 窗口截图不是遮挡无关的**（`PrintWindow` 是死代码）——别当遮挡参考。
5. `CaptureBlt` 与硬件 overlay/受保护内容仍可能黑屏；ShareX 现代路径**无 WGC 兜底**，本项目更强别丢。
6. `CompareImages` 尺寸不同即"已变化"——必须先归一化再 memcmp。
7. HDR 路径环境依赖（RDP/虚拟显示/独占全屏失败），照搬要保留回退。
8. 透明捕获时序脆弱（10ms sleep+DoEvents），别当可靠 API。
9. 历史无去重/无上限（不是可借模型）。
10. ImageEditor WebP 质量硬编码 100（不可配）。
11. **死代码易误读**：`CaptureRectangleManaged`、`GetScreenBounds2/3/4` 无调用者。
12. 多显示器热插拔/锁屏无事件处理；若本项目缓存显示器清单/DPI 需自行监听 `WM_DISPLAYCHANGE` 并失效。
