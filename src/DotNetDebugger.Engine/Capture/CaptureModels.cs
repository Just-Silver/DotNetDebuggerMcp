using System.Drawing;

namespace DotNetDebugger.Engine.Capture;

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
public sealed record CaptureResult(
    byte[] Image, int Width, int Height, int NativeWidth, int NativeHeight,
    string? WindowTitle, string Source, bool WasAllBlack, bool ClippedToScreen = false);

/// <summary>截图失败（约定中文文案由 Engine 生成，宿主 catch 后原样返回——spec §5.2
/// 「屏外判定由 Engine 执行并回传约定错误，宿主不触碰坐标换算」的落地通道）。</summary>
public sealed class CaptureException : Exception
{
    public CaptureException(string message) : base(message) { }
}
