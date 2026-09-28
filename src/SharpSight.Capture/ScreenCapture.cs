using System.Drawing;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpSight.Capture;

/// <summary>
/// 窗口/屏幕截图门面（spec：docs/planning/specs/2026-09-22-screenshot-tool-design.md §4.2）。
/// 截图与 ICorDebug/命令泵零交互（§4.4），工具线程直接同步调用。
/// 首次调用统一设进程 DPI 为 Per-Monitor V2（全链物理像素，与 region 坐标空间定义一致；
/// 运行时调用、不用 manifest，已设置时容忍 ERROR_ACCESS_DENIED）。
/// T2 范围：DPI + FindMainWindow + GetWindowInfo；CaptureScreen（T4）/CaptureWindow（T5）随后追加。
/// T5：FindWindowByHwnd/FindForegroundWindow/GetWindowBounds + CaptureWindow(hwnd, CaptureOptions)。
/// </summary>
public static class ScreenCapture
{
    private static int _dpiSet;   // 0=未设 1=已设（幂等）
    // DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 是**负**伪句柄 -4（见 win32 `DPI_AWARENESS_CONTEXT` 定义：
    // UNAWARE=-1/SYSTEM_AWARE=-2/PER_MONITOR_AWARE=-3/PER_MONITOR_AWARE_V2=-4）。曾误写 +4 →
    // SetProcessDpiAwarenessContext 恒失败（ERROR_INVALID_PARAMETER=87），进程停留 DPI-UNAWARE；
    // 100% 缩放时无差别故未被发现，非 100% 时 GetMonitorInfo/截图全变「逻辑像素」（batch3 F3）。
    internal const int ProcessPerMonitorDpiAwareV2 = -4;
    private const int DwmwaExtendedFrameBounds = 9;   // DwmGetWindowAttribute 属性号（DWM 可见帧，去阴影）
    private const uint GaRoot = 2;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr extra);

    [StructLayout(LayoutKind.Sequential)]
    private struct Rect { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct NativePoint { public int X, Y; }

    [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
    [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr extra);
    [DllImport("user32.dll")] private static extern bool IsWindow(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags);   // 2=GA_ROOT
    [DllImport("user32.dll")] private static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool GetClientRect(IntPtr hwnd, out Rect rect);
    [DllImport("user32.dll")] private static extern bool ClientToScreen(IntPtr hwnd, ref NativePoint point);
    [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
    [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("dwmapi.dll")] private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out Rect value, int size);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetWindowText(IntPtr hwnd, StringBuilder text, int maxCount);

    // 扩展样式（GWL_EXSTYLE=-20；WS_EX_TOOLWINDOW=0x80）：用于「可截主窗」评分时排除缩略图/任务栏类助手窗。
    private const int GwlExStyle = -20;
    private const long WsExToolWindow = 0x00000080L;
    [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")]
    private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);

    /// <summary>
    /// 幂等设置进程 DPI 感知为 Per-Monitor V2（首次调用生效）。**所有查显示器/截图入口都会先调**——
    /// 否则矩形与图像会被系统按 DPI 虚拟化成逻辑像素（非 100% 缩放下尺寸/坐标全错）。
    /// 失败容忍：ERROR_ACCESS_DENIED(5)=已被清单/先前调用设置（此时以既有设置为准）；
    /// ERROR_INVALID_PARAMETER(87)=上下文值非法（说明常量写错，见 <see cref="ProcessPerMonitorDpiAwareV2"/>）。
    /// </summary>
    internal static void EnsureDpi()
    {
        if (Interlocked.CompareExchange(ref _dpiSet, 1, 0) != 0) return;
        SetProcessDpiAwarenessContext(new IntPtr(ProcessPerMonitorDpiAwareV2));
    }

    /// <summary>
    /// 枚举顶层可见窗口定位目标主窗（spec §3.1 定位规则，语义钉死「pid 优先、标题兜底」的择一）：
    /// <c>processId&gt;0</c> → 仅按 pid 匹配（titleSubstring 被忽略）；
    /// <c>processId=0</c> 且标题非空 → 仅按标题子串（忽略大小写，跨进程搜索）；
    /// 双空 → null（宿主保证先做会话兜底、不传双空）。MatchCount=全部命中数。
    /// <para><b>pid 检索的择优选</b>（2026-09-28，修「1×1 助手窗假成功」）：命中多个可见根窗时**不再取 Z 序最前**，
    /// 而按「<b>非 WS_EX_TOOLWINDOW</b> → <b>有标题</b> → <b>面积最大</b>」分层评分，同分回退 Z 序最前——
    /// 否则大量应用（资源管理器等）的 Z 序最前是 1×1 缩略图/任务栏类助手窗，会截出 1×1 黑图却貌似成功。</para>
    /// <para><b>标题检索保持 Z 序最前</b>（命中者标题已匹配，语义不变）。</para>
    /// </summary>
    public static WindowHandleInfo? FindMainWindow(int processId, string titleSubstring)
    {
        EnsureDpi();
        if (processId <= 0 && string.IsNullOrWhiteSpace(titleSubstring)) return null;

        WindowHandleInfo? first = null;     // Z 序最前（标题检索口径 & pid 检索兜底）
        WindowHandleInfo? best = null;      // pid 检索：分层评分最优
        var bestScore = long.MinValue;
        var count = 0;
        var byPid = processId > 0;

        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetAncestor(h, GaRoot) != h) return true;
            // 注意：返回值是线程 id，进程 id 只能取 out 参数（spike 实录 bug：拿返回值比 pid 会漏窗口）
            GetWindowThreadProcessId(h, out var pid);
            if (byPid)
            {
                if (pid != (uint)processId) return true;
            }
            else
            {
                if (ReadWindowTitle(h).IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) < 0) return true;
            }
            count++;
            var info = InfoOf(h, (int)pid, count);
            first ??= info;
            if (byPid)
            {
                // 分层评分：tier（非工具窗 2 / 有标题 1）权重量级远大于面积 → 先分层、层内再比面积。
                var tier = (IsToolWindow(h) ? 0L : 2L) + (info.Title.Length > 0 ? 1L : 0L);
                var area = (long)Math.Max(0, info.Rect.Width) * Math.Max(0, info.Rect.Height);
                var score = tier * 1_000_000_000L + area;
                if (score > bestScore) { bestScore = score; best = info; }
            }
            return true;   // 继续枚举以统计 MatchCount
        }, IntPtr.Zero);

        var chosen = byPid ? best : first;
        if (chosen is null) return null;
        return chosen with { MatchCount = count };   // 补全计数，免二次 Win32 调用
    }

    /// <summary>
    /// 枚举全部可见顶层窗口（Z 序，顶→底）：仅 <c>IsWindowVisible</c> 且为根窗口。供宿主「可截窗口清单」工具与
    /// 调用方选择 hwnd/标题寻址——枚举**不限进程**（任意进程），与 <see cref="FindMainWindow"/> 的「按 pid/标题择一取首个」互补。
    /// <b>本方法不做可用性过滤</b>（<c>EnumWindows</c> 本身只枚举顶层窗口；实测对顶层窗 <c>GetAncestor(GA_ROOT)==self</c>
    /// 恒成立、不排除 owned/工具窗）——「有标题 + 尺寸&gt;0」这类可截判定由调用方（宿主工具）负责。
    /// 空标题/零尺寸窗口也返回；<see cref="WindowHandleInfo.MatchCount"/> 恒 1（无选择器语义）。
    /// </summary>
    public static WindowHandleInfo[] EnumerateVisibleWindows()
    {
        EnsureDpi();
        var list = new List<WindowHandleInfo>();
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetAncestor(h, GaRoot) != h) return true;
            GetWindowThreadProcessId(h, out var pid);
            GetWindowRect(h, out var r);
            list.Add(new WindowHandleInfo(h, ReadWindowTitle(h),
                new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                IsIconic(h), (int)pid, 1));
            return true;
        }, IntPtr.Zero);
        return list.ToArray();
    }

    /// <summary>取窗口基础信息（定位/头部/抓取共用）；hwnd 无效返回 null。</summary>
    internal static WindowHandleInfo? GetWindowInfo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out _)) return null;
        GetWindowThreadProcessId(hwnd, out var pid);
        return InfoOf(hwnd, (int)pid, matchCount: 1);
    }

    /// <summary>是否工具窗（<c>WS_EX_TOOLWINDOW</c>）：缩略图/任务栏/输入法等助手窗多属此类，不应作为截图主窗。</summary>
    internal static bool IsToolWindow(IntPtr hwnd)
        => (GetWindowLongPtr(hwnd, GwlExStyle).ToInt64() & WsExToolWindow) != 0;

    /// <summary>按 hwnd 填 <see cref="WindowHandleInfo"/>（定位共用；调用方负责先校验 hwnd 有效）。</summary>
    private static WindowHandleInfo InfoOf(IntPtr hwnd, int pid, int matchCount)
    {
        GetWindowRect(hwnd, out var r);
        return new WindowHandleInfo(hwnd, ReadWindowTitle(hwnd),
            new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
            IsIconic(hwnd), pid, matchCount);
    }

    // 标题读取缓冲（GetWindowText 最多写 Capacity-1 字符）；超长者只取到上限并以 … 明示，不返回完整超长标题。
    private const int TitleCapacity = 256;

    /// <summary>
    /// 读窗口标题（上限 <see cref="TitleCapacity"/>-1 字符）。读满缓冲（可能被截断）时末尾补 <c>…</c> 明示——
    /// <para>2026-09-28 修（batch3 D6）：此前固定 <c>StringBuilder(256)</c> 且**不加标记**，超长标题被静默截到 255、
    /// 看似"标题就这么长"，误导排查。此处仅加 <c>…</c>；**刻意不返回完整标题**：完整标题对 agent 定位（按标题子串/pid）
    /// 无价值，却会让 <c>screenshot_windows</c>（最多 200 行）等输出灌爆上下文。截断只影响展示，标题匹配仍按月/pid 正常。</para>
    /// </summary>
    private static string ReadWindowTitle(IntPtr hwnd)
    {
        var sb = new StringBuilder(TitleCapacity);
        var read = GetWindowText(hwnd, sb, sb.Capacity);
        var title = sb.ToString();
        return read >= sb.Capacity - 1 ? title + "…" : title;   // 饱和=可能被截断 → 明示
    }

    /// <summary>
    /// 按 hwnd 定位窗口（Task 5，spec §4.1 寻址校验）：<c>IsWindow</c> + <c>IsWindowVisible</c> +
    /// <c>GetAncestor(hwnd, GA_ROOT)==hwnd</c>（必须是可见的顶层主窗）全部通过才返回，否则 null。
    /// 语义与 <see cref="FindMainWindow"/> 一致（同一 <see cref="WindowHandleInfo"/>、MatchCount=1）。
    /// </summary>
    public static WindowHandleInfo? FindWindowByHwnd(IntPtr hwnd)
    {
        EnsureDpi();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd) || !IsWindowVisible(hwnd) || GetAncestor(hwnd, GaRoot) != hwnd)
            return null;
        return GetWindowInfo(hwnd);
    }

    /// <summary>
    /// 取当前前台窗口（Task 5，spec §4.1 <c>windowTitle=@active</c> 的库侧支撑）。
    /// <c>GetForegroundWindow</c> 返回 NULL（无头/服务会话）→ null（宿主转中文原因）；
    /// 否则交 <see cref="GetWindowInfo"/> 填 hwnd/标题/矩形/IsIconic/pid。
    /// </summary>
    public static WindowHandleInfo? FindForegroundWindow()
    {
        EnsureDpi();
        var hwnd = GetForegroundWindow();
        if (hwnd == IntPtr.Zero) return null;
        return GetWindowInfo(hwnd);
    }

    /// <summary>
    /// 取窗口几何三元组（Task 5，spec §5 坐标模型）：<c>GetWindowRect</c>（含阴影）+
    /// <c>DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)</c>（可见帧，失败回退窗口矩形）+
    /// 客户区屏幕矩形（<c>GetClientRect</c> 左上恒 (0,0) + <c>ClientToScreen</c>）；
    /// 均为虚拟屏物理像素。hwnd 无效/取矩失败返回 null。
    /// </summary>
    public static WindowBounds? GetWindowBounds(IntPtr hwnd)
    {
        EnsureDpi();
        if (hwnd == IntPtr.Zero || !IsWindow(hwnd)) return null;
        if (!GetWindowRect(hwnd, out var wr)) return null;
        var windowRect = new Rectangle(wr.Left, wr.Top, wr.Right - wr.Left, wr.Bottom - wr.Top);

        // DWM 可见帧（去阴影/不可见 resize 边框）；失败或退化（空矩形）即回退窗口矩形（与 §Step 3 一致）。
        var extended = windowRect;
        if (DwmGetWindowAttribute(hwnd, DwmwaExtendedFrameBounds, out var ef, Marshal.SizeOf<Rect>()) == 0
            && ef.Right > ef.Left && ef.Bottom > ef.Top)
            extended = new Rectangle(ef.Left, ef.Top, ef.Right - ef.Left, ef.Bottom - ef.Top);

        // 客户区：GetClientRect 左上恒 (0,0)，其宽高即客户区；ClientToScreen((0,0)) 得客户区屏幕原点。
        var client = Rectangle.Empty;
        if (GetClientRect(hwnd, out var cr) && cr.Right > cr.Left && cr.Bottom > cr.Top)
        {
            var pt = new NativePoint { X = 0, Y = 0 };
            if (ClientToScreen(hwnd, ref pt))
                client = new Rectangle(pt.X, pt.Y, cr.Right - cr.Left, cr.Bottom - cr.Top);
        }
        return new WindowBounds(extended, windowRect, client);
    }

    /// <summary>
    /// 枚举全部显示器（spec §4.2 mode=display / §3.3）：<see cref="DisplayInfo.Index"/> 为库侧 0 基、
    /// 按 <c>EnumDisplayMonitors</c> 枚举序；含设备名、主屏标志、Bounds/WorkArea（虚拟屏物理像素）与有效 DPI/缩放。
    /// 仅 Win32 查询、不读屏幕 DC，锁屏/无头环境亦可用。1 基对外编号与 primary/left/right 解析属宿主（Task 10）。
    /// </summary>
    public static DisplayInfo[] EnumerateDisplays()
    {
        EnsureDpi();   // 与其它公开入口一致：先设 PMv2，否则 GetMonitorInfoW 的矩形会被 DPI 虚拟化，破坏物理像素口径
        return DisplayEnumerator.Enumerate();
    }

    /// <summary>
    /// 指定显示器逐屏捕获（spec §4.2 mode=display）：<paramref name="index"/> 为库侧 0 基枚举序
    /// （<see cref="DisplayInfo.Index"/>）；1 基对外编号与 primary/left/right 解析属宿主，库侧不参与。
    /// 透传 <paramref name="options"/> 的 <see cref="CaptureOptions.IncludeCursor"/>。
    /// <para><b>display 与 region 是不同入口（不可混用坐标口径）</b>：本方法<b>直接按该显示器矩形 <c>d.Bounds</c>
    /// 抓取</b>（原生 1:1），结果 origin=显示器左上、DisplayIndex=库侧 0 基。
    /// 而 <see cref="CaptureScreen(CaptureOptions)"/> 的 <see cref="CaptureOptions.Clip"/> 是「<b>虚拟屏</b>图像空间」
    /// 坐标——若把显示器原生矩形当 Clip 传入，多显示器下会被按虚拟屏坐标夹回整屏或判为屏外（R21 前的缺陷），
    /// 故本方法<b>不经</b> <see cref="CaptureScreen(CaptureOptions)"/>。</para>
    /// <b>忽略 <see cref="CaptureOptions.Clip"/>/<see cref="CaptureOptions.ClientArea"/></b>（显示器几何由自身决定）。
    /// 索引越界抛约定错误。
    /// </summary>
    public static CaptureResult CaptureDisplay(int index, CaptureOptions options)
    {
        EnsureDpi();
        var displays = EnumerateDisplays();
        if ((uint)index >= (uint)displays.Length)
            throw new CaptureException($"显示器序号 {index} 不存在（共 {displays.Length} 台，库侧 0 基索引 0..{displays.Length - 1}）。");
        var d = displays[index];
        // 直接抓该显示器屏幕矩形（原生 1:1）：origin 即显示器左上，无虚拟屏偏移换算。
        using var bmp = GdiCapture.CaptureScreenBits(d.Bounds);
        var cursorNote = OverlayCursorNote(bmp, options.IncludeCursor, d.Bounds.X, d.Bounds.Y);
        return ImagePipeline.Process(bmp, windowTitle: null, sourceName: "BitBlt" + cursorNote,
            originX: d.Bounds.X, originY: d.Bounds.Y) with { DisplayIndex = d.Index };
    }

    /// <summary>
    /// screen/region：GDI BitBlt 虚拟屏（spec §4.3-3：screen/region 不开 WGC）。
    /// <see cref="CaptureOptions.Clip"/>=「mode=screen 返回图像素空间」坐标（宿主仅解析字符串格式，换算唯一在此）：
    /// 不缩放后图像空间与虚拟屏物理像素只差一个 origin 平移（图像 (0,0) 对应 native 左上）。
    /// 图像空间求交——空交集抛 spec §5.2 屏外约定错误（W/H 用虚拟屏尺寸，agent 可自查口径，
    /// 由库回传、宿主不触碰坐标换算）；非空交 → 平移为原生空间裁剪（BitBlt 只抓交集）；
    /// 交 ≠ 原始输入即 ClippedToScreen（部分越界=已裁交集，头部注明）。
    /// 坐标模型（spec §5）：origin=抓取矩形左上（虚拟屏物理像素，即 nativeClip 左上）；输出恒为原生 1:1。
    /// </summary>
    public static CaptureResult CaptureScreen(CaptureOptions options)
    {
        EnsureDpi();
        var native = GdiCapture.VirtualScreenRect();

        Rectangle nativeClip;
        var clipped = false;
        if (options.Clip is { } c)
        {
            (nativeClip, clipped) = ResolveRegionClip(c, native);
        }
        else
        {
            nativeClip = native;
        }

        using var bmp = GdiCapture.CaptureScreenBits(nativeClip);
        var cursorNote = OverlayCursorNote(bmp, options.IncludeCursor, nativeClip.X, nativeClip.Y);
        return ImagePipeline.Process(bmp, windowTitle: null, sourceName: "BitBlt" + cursorNote,
            clippedToScreen: clipped, originX: nativeClip.X, originY: nativeClip.Y);
    }

    /// <summary>
    /// GDI 源的光标叠加（Task 9）：仅 <c>IncludeCursor</c> 时尝试（在编码之前叠加；本库不缩放，无「随图缩放」一说）；
    /// 成功/无需叠加返回空串，失败返回「（光标未叠加）」备注（失败不计为错误，保持「来源行如实标注」口径）。
    /// WGC 源不走此处（<c>IsCursorCaptureEnabled</c> 已内建）。
    /// </summary>
    private static string OverlayCursorNote(Bitmap bmp, bool includeCursor, int originX, int originY)
        => !includeCursor || GdiCapture.TryOverlayCursor(bmp, originX, originY) ? "" : "（光标未叠加）";

    /// <summary>
    /// region 换算纯函数（自 CaptureScreen 提取——锁屏/无桌面环境下屏幕 BitBlt 不可用时，
    /// 换算逻辑仍可经此单测覆盖；spec §5.2 屏外/交集语义）：
    /// 图像空间与虚拟屏求交（空=屏外抛约定错误，W/H 用虚拟屏尺寸）→ 平移回原生空间 → 夹紧虚拟屏
    /// → 交 ≠ 原输入即部分越界（ClippedToScreen）。本库不缩放，故无 k 换算（图像空间=原生空间平移 origin）。
    /// </summary>
    internal static (Rectangle NativeClip, bool Clipped) ResolveRegionClip(Rectangle clip, Rectangle native)
    {
        var inter = Rectangle.Intersect(clip, new Rectangle(0, 0, native.Width, native.Height));
        if (inter.IsEmpty)
            throw new CaptureException($"region ({clip.X},{clip.Y},{clip.Width},{clip.Height}) 完全在屏幕范围 ({native.Width}x{native.Height}) 之外。");
        var clipped = inter != clip;
        var nativeClip = new Rectangle(
            native.X + inter.X,
            native.Y + inter.Y,
            Math.Max(1, inter.Width),
            Math.Max(1, inter.Height));
        nativeClip.Intersect(native);   // 兜底夹紧
        if (nativeClip.Width <= 0 || nativeClip.Height <= 0)
            throw new CaptureException($"region ({clip.X},{clip.Y},{clip.Width},{clip.Height}) 完全在屏幕范围 ({native.Width}x{native.Height}) 之外。");
        return (nativeClip, clipped);
    }

    /// <summary>
    /// window 抓取编排（Task 5，spec §4.2/§4.3）：
    /// <para><paramref name="options"/>.ClientArea=true → GDI BitBlt 客户区屏幕矩形（<see cref="GetWindowBounds"/> 的
    /// <see cref="WindowBounds.ClientArea"/>；被遮挡处截到遮挡物 = best-effort，Source=BitBlt）。</para>
    /// <para>否则整窗回退链：WGC（DWM 取帧不黑图、被遮挡可截、无需置顶；出图即用——黑=真黑由头部备注）
    /// → PrintWindow → 采样纯黑则继续回退 → BitBlt（最小化窗口屏幕无内容，不回退）→ 全失败抛约定错误。</para>
    /// <para><b>整窗 origin 口径（Spike A 实测）</b>：
    /// WGC 首帧尺寸与 <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> <b>逐像素相等</b>（UiSampleApp 实测 762x552，
    /// 而 GetWindowRect 为 776x559，含约 7px 的 DWM 阴影/不可见 resize 边框），且 PrintWindow 相关性证实
    /// WGC 帧原点 = 扩展边框左上。故 <b>WGC 源 origin=扩展边框左上</b>；PrintWindow/BitBlt 按 <c>GetWindowRect</c>
    /// 作画/采样，origin=窗口矩形左上。因此本方法不做「裁掉扩展边框偏移」的裁剪（那是 WGC 帧==GetWindowRect 时才需要）。
    /// <b>该边框差值不是恒定 7px</b>（随 DWM 阴影有无、系统 DPI、窗口样式/粗边框、Windows 版本变化）——
    /// 故实现<b>绝不施加固定偏移</b>，而是每次按源分别取 <c>ExtendedFrame</c>/<c>WindowRect</c> 两个矩形（见 CaptureWindowBits）。</para>
    /// </summary>
    public static CaptureResult CaptureWindow(IntPtr hwnd, CaptureOptions options)
    {
        EnsureDpi();
        // options.Clip 在 window 模式被忽略（非静默丢参，见 CaptureOptions.Clip 文档）：窗口几何由目标窗口自身
        // 决定——WGC 源取 DWMWA_EXTENDED_FRAME_BOUNDS、GDI 回退源取 GetWindowRect（Spike A 实测口径），
        // 不支持再叠加外部裁剪；元素级裁剪是独立入口 CaptureElement(hwnd, elementRectPx)（不走本方法）。
        var info = GetWindowInfo(hwnd) ?? throw WindowCaptureFailure(hwnd);
        var bounds = GetWindowBounds(hwnd) ?? throw WindowCaptureFailure(hwnd);

        // 客户区：GDI BitBlt 客户区屏幕矩形（spec §4.2；无 WGC）。**边界**：仅在窗口可见且未被遮挡时等价于
        // 「客户区内容」——被遮挡/移出屏幕时会截到遮挡物或失败；即 clientArea 不具整窗链的遮挡捕获能力
        // （调用方若需遮挡安全，用默认整窗走 WGC/PrintWindow）。
        if (options.ClientArea)
        {
            if (bounds.ClientArea.Width <= 0 || bounds.ClientArea.Height <= 0) throw WindowCaptureFailure(hwnd);
            using var cbmp = GdiCapture.CaptureScreenBits(bounds.ClientArea);
            var cursorNote = OverlayCursorNote(cbmp, options.IncludeCursor, bounds.ClientArea.X, bounds.ClientArea.Y);
            return ImagePipeline.Process(cbmp, info.Title, "BitBlt" + cursorNote,
                originX: bounds.ClientArea.X, originY: bounds.ClientArea.Y) with { IsClientArea = true };
        }

        var (bmp, source, originX, originY) = CaptureWindowBits(hwnd, info, bounds, options.IncludeCursor);
        using (bmp)
            return ImagePipeline.Process(bmp, info.Title, source, originX: originX, originY: originY);
    }

    private const string WindowCaptureFailMsg = "窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。";

    /// <summary>窗口抓取失败异常：目标最小化时给可执行指引（先还原窗口），否则给通用文案。
    /// 2026-09-28 修：此前一律归因「无桌面会话」，会让 agent 误判环境不可用而放弃能力。</summary>
    private static CaptureException WindowCaptureFailure(IntPtr hwnd)
        => new(IsIconic(hwnd)
            ? "目标窗口已最小化，无法抓取：请先还原窗口（ui_action verb=windowstate windowstate=normal）或改用 mode=screen/region。"
            : WindowCaptureFailMsg);

    /// <summary>
    /// 整窗原生位图回退链 + 帧原点（Spike A 口径）：WGC（首帧几何 == <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>，
    /// origin=扩展边框左上）→ PrintWindow（以 GetWindowRect 原点自画；采样纯黑则继续回退）→
    /// BitBlt（最小化窗口屏幕无内容，不回退；采 GetWindowRect 区域）。全失败抛约定错误。
    /// <b>不处置位图</b>——调用方 using。<see cref="CaptureWindow"/> 与 <see cref="CaptureElement"/> 共用
    /// （元素裁剪与整窗同源，避免对屏幕直接 BitBlt 裁元素而截到遮挡物）。
    /// </summary>
    private static (Bitmap Bitmap, string Source, int OriginX, int OriginY) CaptureWindowBits(
        IntPtr hwnd, WindowHandleInfo info, WindowBounds bounds, bool includeCursor)
    {
        // 第 1 道：WGC（正确性主力）——首帧几何 == 扩展边框（Spike A 实测）；光标按 includeCursor 内建开关
        Bitmap? bmp = WgcCapture.TryCaptureWindow(hwnd, includeCursor);
        var source = "WGC";
        int originX = bounds.ExtendedFrame.X, originY = bounds.ExtendedFrame.Y;

        // 第 2 道：PrintWindow → 采样纯黑则继续回退（spec §4.3-2）；以 GetWindowRect 原点自画。
        // **边界**：PrintWindow 依赖目标窗口处理 WM_PRINT/WM_PRINTCLIENT——Chromium 系/游戏/部分 UWP 可能不
        // 支持或只画一部分；「纯黑判据」只能筛「全黑」，**筛不出「非纯黑但内容陈旧/不完整」**（该道能兜住多少
        // 取决于目标应用生态，非本链可控）。
        if (bmp is null)
        {
            bmp = GdiCapture.TryPrintWindow(hwnd);
            source = "PrintWindow";
            originX = bounds.WindowRect.X; originY = bounds.WindowRect.Y;
            if (bmp is not null && ImagePipeline.IsAllBlack(bmp)) { bmp.Dispose(); bmp = null; }
        }

        // 第 3 道：BitBlt（最小化窗口屏幕无内容，不回退——spec §4.2 IsIconic 规则）；采 GetWindowRect 区域
        if (bmp is null && !info.IsIconic)
        {
            bmp = GdiCapture.TryBitBltWindow(hwnd);
            source = "BitBlt";
            originX = bounds.WindowRect.X; originY = bounds.WindowRect.Y;
        }

        if (bmp is null) throw WindowCaptureFailure(hwnd);

        // GDI 源无内建光标开关：编码前手动叠加（失败仅在 Source 备注，不计错误）；WGC 已内建、不重复叠加
        if (source != "WGC") source += OverlayCursorNote(bmp, includeCursor, originX, originY);
        return (bmp, source, originX, originY);
    }

    /// <summary>
    /// element 模式抓取（Task 6，spec §7.1）：先取元素所属<b>顶层窗口</b>自身的窗口帧（与
    /// <see cref="CaptureWindow"/> 同一条 WGC→PrintWindow→BitBlt 回退链），再把 <paramref name="elementRectPx"/>
    /// （虚拟屏物理像素）换算为帧内坐标后裁剪——<b>不对屏幕直接 BitBlt 裁元素</b>（否则截到的是遮挡物）。
    /// 元素属另一顶层窗口（ComboBox 弹层/popup/tooltip）时，调用方须传元素自身
    /// <c>GetAncestor(GA_ROOT)</c> 的 <paramref name="topLevelHwnd"/>（见 <c>UiElementInfo.TopLevelHwnd</c>）。
    /// <para>裁剪 = <paramref name="elementRectPx"/> 与窗口帧求交：空交集抛约定错误；部分越界则裁至交集且
    /// <see cref="CaptureResult.ClippedToScreen"/>=true。origin=实际抓取交集左上（spec §5）；输出原生 1:1。</para>
    /// <para>编码选项语义同 <see cref="CaptureWindow(IntPtr, CaptureOptions)"/>。</para>
    /// </summary>
    public static CaptureResult CaptureElement(IntPtr topLevelHwnd, Rectangle elementRectPx, CaptureOptions options)
    {
        EnsureDpi();
        if (elementRectPx.IsEmpty)
            throw new CaptureException("元素矩形为空：无法裁剪（目标元素不可见/无几何）。");
        var info = FindWindowByHwnd(topLevelHwnd)
            ?? throw new CaptureException($"顶层窗口句柄无效或不可见（hwnd={topLevelHwnd}）：元素截图需要可用的顶层窗口句柄（元素自身或其所属窗口）。");
        var bounds = GetWindowBounds(topLevelHwnd) ?? throw new CaptureException(WindowCaptureFailMsg);

        var (bmp, source, originX, originY) = CaptureWindowBits(topLevelHwnd, info, bounds, options.IncludeCursor);
        using (bmp)
        {
            var frame = new Rectangle(originX, originY, bmp.Width, bmp.Height);
            var inter = Rectangle.Intersect(elementRectPx, frame);
            if (inter.IsEmpty)
                throw new CaptureException($"元素矩形 ({elementRectPx.X},{elementRectPx.Y},{elementRectPx.Width},{elementRectPx.Height}) 不在顶层窗口帧 ({frame.X},{frame.Y},{frame.Width},{frame.Height}) 内。");
            var local = new Rectangle(inter.X - originX, inter.Y - originY, inter.Width, inter.Height);
            using var cropped = bmp.Clone(local, bmp.PixelFormat);
            return ImagePipeline.Process(cropped, info.Title, source,
                clippedToScreen: inter != elementRectPx, originX: inter.X, originY: inter.Y);
        }
    }
}
