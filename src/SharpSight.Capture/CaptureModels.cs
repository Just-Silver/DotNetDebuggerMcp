using System.Drawing;

namespace SharpSight.Capture;

// screenshot 计划 T2 —— 数据形状对 spec §4.2 的说明：spec 列举「hwnd/标题/rect/IsIconic」为必备字段；
// 为落实 §3.3 头部「目标 (pid=N)」「命中 N 个可见窗口」与「已裁至屏幕交集」文案，补 3 个派生字段
// （Pid / MatchCount / ClippedToScreen），语义零变化、不引入新行为。

/// <summary>定位到的窗口信息（FindMainWindow 返回；Z 序最前的可见顶层主窗）。</summary>
/// <param name="Hwnd">窗口句柄。</param>
/// <param name="Title">窗口标题（GetWindowText）。</param>
/// <param name="Rect">窗口矩形（物理像素——入口统一设 DPI Per-Monitor V2）。</param>
/// <param name="IsIconic">是否最小化（CaptureWindow 据此跳过 BitBlt 回退，spec §4.2）。</param>
/// <param name="Pid">窗口所属进程 id（头部「目标」行展示）。</param>
/// <param name="MatchCount">按当前选择器命中的可见窗口总数（多命中取 Z 序最前，头部注明用）。</param>
public sealed record WindowHandleInfo(
    IntPtr Hwnd, string Title, Rectangle Rect, bool IsIconic, int Pid, int MatchCount);

/// <summary>
/// 窗口几何三元组（<see cref="ScreenCapture.GetWindowBounds"/> 返回；Task 5，spec §5 坐标模型）：
/// <see cref="ExtendedFrame"/> 为 DWM 可见帧（去阴影/不可见 resize 边框，
/// <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>，失败回退窗口矩形）；<see cref="WindowRect"/> 为
/// <c>GetWindowRect</c>（含不可见边框/阴影余量）；<see cref="ClientArea"/> 为客户区的
/// <b>屏幕坐标矩形</b>（<c>GetClientRect</c> 左上恒 (0,0) + <c>ClientToScreen</c> 换算）。
/// 三者均为虚拟屏物理像素。
/// </summary>
/// <param name="ExtendedFrame">DWM 扩展边框（可见帧，不含阴影）。</param>
/// <param name="WindowRect">GetWindowRect 窗口矩形（含阴影/不可见边框）。</param>
/// <param name="ClientArea">客户区屏幕坐标矩形（宽高=客户区，左上=客户区屏幕原点）。</param>
public sealed record WindowBounds(Rectangle ExtendedFrame, Rectangle WindowRect, Rectangle ClientArea);

/// <summary>截图结果（Engine 唯一产出形状；宿主只消费字段拼头部/双轨，不做任何坐标与图像处理）。</summary>
/// <param name="Image">编码后字节（png/jpeg，按入参 format）。</param>
/// <param name="Width">输出（缩放后）宽。</param>
/// <param name="Height">输出（缩放后）高。</param>
/// <param name="NativeWidth">原生（缩放前）宽：window=窗口、screen=全屏、region=裁剪区。</param>
/// <param name="NativeHeight">原生（缩放前）高，口径同 <paramref name="NativeWidth"/>。</param>
/// <param name="WindowTitle">window 模式标题；screen/region 为 null。</param>
/// <param name="Source">抓取来源 "WGC" | "PrintWindow" | "BitBlt"（回退链结果即来源；screen/region 恒 BitBlt）。</param>
/// <param name="WasAllBlack">输出图纯黑采样（头部「备注」行数据源；抓到纯黑不报错）。</param>
/// <param name="ClippedToScreen">region 部分越界已裁至屏幕交集（头部「尺寸」行注记，spec §3.3）。</param>
/// <param name="OriginX">抓取矩形左上角 X（虚拟屏物理像素；spec §5 origin）。</param>
/// <param name="OriginY">抓取矩形左上角 Y（虚拟屏物理像素；spec §5 origin）。</param>
/// <param name="Scale">图像像素 / 原生物理像素（spec §5 scale = Width/NativeWidth）。</param>
/// <param name="FrameId">采集代际号（由 SharpSight.UiAutomation 维护、宿主注入；未接入时恒 0）。</param>
/// <param name="DisplayIndex">归属显示器序号；screen 全屏/未定位到具体显示器时为 -1。</param>
/// <param name="IsClientArea">抓取的是否为窗口客户区（仅 window 模式 ClientArea=true 时置 true，其余模式恒 false）。</param>
public sealed record CaptureResult(
    byte[] Image, int Width, int Height, int NativeWidth, int NativeHeight,
    string? WindowTitle, string Source, bool WasAllBlack, bool ClippedToScreen = false,
    int OriginX = 0, int OriginY = 0, double Scale = 1.0, int FrameId = 0,
    int DisplayIndex = -1, bool IsClientArea = false);

/// <summary>
/// CaptureScreen 入参（T3 引入；spec §5 坐标模型 / §6.2 降采样）。旧
/// <c>CaptureScreen(clip, maxDimension, format, quality)</c> 重载委托到此，行为不变（宿主切换见 Task 10）。
/// </summary>
/// <param name="Clip">裁剪矩形（mode=screen 返回图像素空间）；null=全屏。
/// <b>适用模式：screen / display /（将来 element）</b>；<b>window 模式忽略本参数</b>——窗口几何由目标窗口自身
/// 决定（WGC 源取 <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>、GDI 回退源取 <c>GetWindowRect</c>），不套用裁剪。</param>
/// <param name="MaxWidth">输出长边上限（原生物理像素）；0/负=不限。两轴皆 0/负 ⇒ 不缩放（k=1，R10）。R8 临时映射，见 ScreenCapture.ResolveMaxDimension。</param>
/// <param name="MaxHeight">输出长边上限；0/负=不限，语义同 <paramref name="MaxWidth"/>。</param>
/// <param name="Format">输出编码 "png" | "jpeg"（默认 png）。</param>
/// <param name="Quality">jpeg 质量 0-100（默认 80）。</param>
/// <param name="ClientArea">window 模式是否抓客户区（<see cref="ScreenCapture.CaptureWindow(IntPtr, CaptureOptions)"/> 消费；
/// screen/display/region 忽略）。默认 false=整窗。Task 5 引入。</param>
public sealed record CaptureOptions(
    Rectangle? Clip = null,
    int MaxWidth = 0,
    int MaxHeight = 0,
    string Format = "png",
    int Quality = 80,
    bool ClientArea = false);

/// <summary>截图失败（约定中文文案由 Engine 生成，宿主 catch 后原样返回——spec §5.2
/// 「屏外判定由 Engine 执行并回传约定错误，宿主不触碰坐标换算」的落地通道）。</summary>
public sealed class CaptureException : Exception
{
    public CaptureException(string message) : base(message) { }
}
