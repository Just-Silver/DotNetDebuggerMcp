namespace DotNetDebuggerMcp.Configuration;

/// <summary>
/// screenshot 工具面的用户可见文案常量（spec §4.6 错误表）：参数校验/寻址/兼容性检查的中文提示集中在此，
/// 避免字面量散落 <c>DebugScreenshotTool</c> 多处导致改一处漏一处。库侧抛出的约定错误（region 全屏外 /
/// 抓取全失败）由 <c>SharpSight.Capture.CaptureException</c> 生成，宿主 <c>catch</c> 后原样返回，不在此重复。
/// </summary>
internal static class CaptureText
{
    /// <summary>mode 非法（未知模式名）。</summary>
    public static string InvalidMode(string mode)
        => $"mode 无效：\"{mode}\"（可选 auto/screen/display/window/foreground/region/element）。";

    /// <summary>region 字符串格式错误（需 <c>"x,y,w,h"</c> 且 w/h 为正）。</summary>
    public const string RegionMalformed =
        "region 格式应为 \"x,y,w,h\"（坐标为 mode=screen 返回图像素，原点左上）。";

    /// <summary>display 选择器无效（越界或未知名称）。</summary>
    public static string DisplayInvalid(string selector, int count)
        => $"display 无效：\"{selector}\"（共 {count} 台显示器；可用 1..{count} 或 primary/left/right）。";

    /// <summary>display=left/right 但主屏该侧没有相邻显示器。</summary>
    public static string DisplayNoAdjacent(string side)
        => $"display={side}：主屏{(side == "left" ? "左" : "右")}侧没有相邻显示器（仅一台或布局不邻接），可改用 1..N 或 primary。";

    /// <summary>hwnd 非法/不可见/非顶层主窗。</summary>
    public static string HwndInvalid(string hwnd)
        => $"hwnd 无效或窗口不可见（hwnd={hwnd}）：需为可见顶层主窗的十进制句柄。";

    /// <summary>mode=foreground 时取不到前台窗口（锁屏/无桌面会话）。</summary>
    public const string ForegroundUnavailable =
        "当前没有前台窗口（可能处于锁屏/无桌面会话）：mode=foreground 需有可见前台窗口。";

    /// <summary>mode=element 但缺少可定位元素的进程。</summary>
    public const string ElementNeedsProcess =
        "mode=element 需要 processId 定位目标进程（或先 debug_launch 建立会话自动取目标 pid）。";

    /// <summary>mode=element 未提供 element 引用（R24：不静默截取首个元素）。</summary>
    public const string ElementRequired =
        "mode=element 需提供 element（可为元素序号——可先 ui_find 取编号，或控件名/AutomationId 子串）。";

    /// <summary>mode=element 时未命中任何元素。</summary>
    public static string ElementNotFound(string element)
        => $"未找到 element=\"{element}\" 对应的 UIA 元素。可先 ui_find 看可用控件与 index（同源）再截图。";

    /// <summary>不兼容组合（显式报错而非静默丢参，spec §4.2）。</summary>
    public static string Incompatible(string param, string allowed, string mode)
        => $"参数不兼容：{param} 仅适用于 {allowed}（当前 mode={mode}）。";

    /// <summary>window/foreground 等窗口出现的超时提示。</summary>
    public static string TimeoutNotFound(string selector, int timeoutSeconds)
        => $"{timeoutSeconds} 秒内未找到匹配的可见窗口（{selector}）。";

    /// <summary>提交给取消令牌的取消提示（放弃等待、可重试，不走缓存）。</summary>
    public const string Canceled = "screenshot 已取消（可重试）。";
}
