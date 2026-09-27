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
/// T5：FindWindowByHwnd/FindForegroundWindow/GetWindowBounds + CaptureWindow(hwnd,clientArea)。
/// </summary>
public static class ScreenCapture
{
    private static int _dpiSet;   // 0=未设 1=已设（幂等）
    private const int ProcessPerMonitorDpiAwareV2 = 4;
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

    /// <summary>幂等设置进程 DPI 感知（首次调用生效；失败=已由系统/调用方设置，容忍）。</summary>
    internal static void EnsureDpi()
    {
        if (Interlocked.CompareExchange(ref _dpiSet, 1, 0) != 0) return;
        SetProcessDpiAwarenessContext(new IntPtr(ProcessPerMonitorDpiAwareV2));
        // ERROR_ACCESS_DENIED(5)=已设置、ERROR_INVALID_PARAMETER(87)=系统过旧——均容忍继续
    }

    /// <summary>
    /// 枚举顶层可见窗口定位目标主窗（spec §3.1 定位规则，语义钉死「pid 优先、标题兜底」的择一）：
    /// <c>processId&gt;0</c> → 仅按 pid 匹配（titleSubstring 被忽略）；
    /// <c>processId=0</c> 且标题非空 → 仅按标题子串（忽略大小写，跨进程搜索）；
    /// 双空 → null（宿主保证先做会话兜底、不传双空）。
    /// EnumWindows 顺序即 Z 序（顶→底），首个命中即 Z 序最前主窗；MatchCount=全部命中数。
    /// </summary>
    public static WindowHandleInfo? FindMainWindow(int processId, string titleSubstring)
    {
        EnsureDpi();
        if (processId <= 0 && string.IsNullOrWhiteSpace(titleSubstring)) return null;

        WindowHandleInfo? first = null;
        var count = 0;
        EnumWindows((h, _) =>
        {
            if (!IsWindowVisible(h) || GetAncestor(h, GaRoot) != h) return true;
            // 注意：返回值是线程 id，进程 id 只能取 out 参数（spike 实录 bug：拿返回值比 pid 会漏窗口）
            GetWindowThreadProcessId(h, out var pid);
            if (processId > 0)
            {
                if (pid != (uint)processId) return true;
            }
            else
            {
                var sb = new StringBuilder(256);
                GetWindowText(h, sb, sb.Capacity);
                if (sb.ToString().IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) < 0) return true;
            }
            count++;
            if (first is null)
            {
                GetWindowRect(h, out var r);
                var tsb = new StringBuilder(256);
                GetWindowText(h, tsb, tsb.Capacity);
                first = new WindowHandleInfo(h, tsb.ToString(),
                    new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                    IsIconic(h), (int)pid, count);
            }
            return true;   // 继续枚举以统计 MatchCount
        }, IntPtr.Zero);

        if (first is null) return null;
        return first with { MatchCount = count };   // 补全计数，免二次 Win32 调用
    }

    /// <summary>取窗口基础信息（定位/头部/抓取共用）；hwnd 无效返回 null。</summary>
    internal static WindowHandleInfo? GetWindowInfo(IntPtr hwnd)
    {
        if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
        var tsb = new StringBuilder(256);
        GetWindowText(hwnd, tsb, tsb.Capacity);
        GetWindowThreadProcessId(hwnd, out var pid);
        return new WindowHandleInfo(hwnd, tsb.ToString(),
            new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
            IsIconic(hwnd), (int)pid, MatchCount: 1);
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
    /// （<see cref="DisplayInfo.Index"/>）；1 基对外编号与 primary/left/right 解析属宿主（Task 10），库侧不参与。
    /// 复用 screen 的 GDI BitBlt（不开 WGC，spec §4.3-3），把该显示器 Bounds 作为裁剪区（无缩放，k=1）；
    /// origin=显示器左上（虚拟屏物理像素），回填 <see cref="CaptureResult.DisplayIndex"/>。索引越界抛约定错误。
    /// </summary>
    public static CaptureResult CaptureDisplay(int index)
    {
        EnsureDpi();
        var displays = EnumerateDisplays();
        if ((uint)index >= (uint)displays.Length)
            throw new CaptureException($"显示器序号 {index} 不存在（共 {displays.Length} 台，库侧 0 基索引 0..{displays.Length - 1}）。");
        var d = displays[index];
        // CaptureOptions.Clip 是 spec §5 的「图像空间」坐标（相对虚拟屏左上）；k=1 时与原生空间重合，故 Clip = Bounds 偏移（无缩放）。
        var native = GdiCapture.VirtualScreenRect();
        var clipInImageSpace = new Rectangle(d.Bounds.X - native.X, d.Bounds.Y - native.Y, d.Bounds.Width, d.Bounds.Height);
        return CaptureScreen(new CaptureOptions(Clip: clipInImageSpace)) with { DisplayIndex = index };
    }

    /// <summary>
    /// screen/region：GDI BitBlt 虚拟屏（spec §4.3-3：screen/region 不开 WGC）。
    /// <see cref="CaptureOptions.Clip"/>=「mode=screen 返回图像素空间」坐标（宿主仅解析字符串格式，换算唯一在此）：
    /// 图像空间求交——空交集抛 spec §5.2 屏外约定错误（W/H 用图像空间尺寸，agent 可自查口径，
    /// 由 Engine 回传、宿主不触碰坐标换算）；非空交 → round 换算原生空间裁剪（BitBlt 只抓交集）；
    /// 交 ≠ 原始输入即 ClippedToScreen（部分越界=已裁交集，头部注明）。
    /// 坐标模型（spec §5）：origin=抓取矩形左上（虚拟屏物理像素，即 nativeClip 左上）、
    /// scale=Width/NativeWidth（由 ImagePipeline 回填）。
    /// </summary>
    public static CaptureResult CaptureScreen(CaptureOptions options)
    {
        EnsureDpi();
        var native = GdiCapture.VirtualScreenRect();
        var k = ImagePipeline.ScaleFactor(native.Size, options.MaxWidth, options.MaxHeight);
        var imgW = Math.Max(1, (int)Math.Round(native.Width * k));
        var imgH = Math.Max(1, (int)Math.Round(native.Height * k));

        Rectangle nativeClip;
        var clipped = false;
        if (options.Clip is { } c)
        {
            (nativeClip, clipped) = ResolveRegionClip(c, native, k, imgW, imgH);
        }
        else
        {
            nativeClip = native;
        }

        using var bmp = GdiCapture.CaptureScreenBits(nativeClip);
        return ImagePipeline.Process(bmp, native.Size, options.MaxWidth, options.MaxHeight, options.Format, options.Quality,
            windowTitle: null, sourceName: "BitBlt", clippedToScreen: clipped,
            originX: nativeClip.X, originY: nativeClip.Y);
    }

    /// <summary>
    /// 旧签名（宿主当前调用点，Task 10 切换）：委托到 <see cref="CaptureScreen(CaptureOptions)"/>，
    /// maxDimension 同时作 maxWidth/maxHeight。宿主恒传正数（<c>AppConfig.ScreenshotMaxDimension</c>），
    /// 故缩放/裁剪与切换前逐位一致；若传 0/负则两轴皆不限、不缩放（k=1，spec §4.1「0=不缩放」）。
    /// </summary>
    public static CaptureResult CaptureScreen(Rectangle? clipInImageSpace, int maxDimension, string format, int quality)
        => CaptureScreen(new CaptureOptions(clipInImageSpace, maxDimension, maxDimension, format, quality));

    /// <summary>
    /// region 换算纯函数（自 CaptureScreen 提取——锁屏/无桌面环境下屏幕 BitBlt 不可用时，
    /// 换算逻辑仍可经此单测覆盖；spec §3.1 k 换算、§5.2 屏外/交集语义）：
    /// 图像空间求交（空=屏外抛约定错误，W/H 用图像空间尺寸）→ round 换算原生空间 → 夹紧虚拟屏
    /// → 交 ≠ 原输入即部分越界（ClippedToScreen）。
    /// </summary>
    internal static (Rectangle NativeClip, bool Clipped) ResolveRegionClip(
        Rectangle clip, Rectangle native, double k, int imgW, int imgH)
    {
        var inter = Rectangle.Intersect(clip, new Rectangle(0, 0, imgW, imgH));
        if (inter.IsEmpty)
            throw new CaptureException($"region ({clip.X},{clip.Y},{clip.Width},{clip.Height}) 完全在屏幕范围 ({imgW}x{imgH}) 之外。");
        var clipped = inter != clip;
        var nativeClip = new Rectangle(
            native.X + (int)Math.Round(inter.X / k),
            native.Y + (int)Math.Round(inter.Y / k),
            Math.Max(1, (int)Math.Round(inter.Width / k)),
            Math.Max(1, (int)Math.Round(inter.Height / k)));
        nativeClip.Intersect(native);   // round 兜底夹紧（最多溢出 1px）
        if (nativeClip.Width <= 0 || nativeClip.Height <= 0)
            throw new CaptureException($"region ({clip.X},{clip.Y},{clip.Width},{clip.Height}) 完全在屏幕范围 ({imgW}x{imgH}) 之外。");
        return (nativeClip, clipped);
    }

    /// <summary>
    /// window 抓取编排（Task 5，spec §4.2/§4.3）：
    /// <para><paramref name="options"/>.ClientArea=true → GDI BitBlt 客户区屏幕矩形（<see cref="GetWindowBounds"/> 的
    /// <see cref="WindowBounds.ClientArea"/>；被遮挡处截到遮挡物 = best-effort，Source=BitBlt）。</para>
    /// <para>否则整窗回退链：WGC（DWM 取帧不黑图、被遮挡可截、无需置顶；出图即用——黑=真黑由头部备注）
    /// → PrintWindow → 采样纯黑则继续回退 → BitBlt（最小化窗口屏幕无内容，不回退）→ 全失败抛约定错误。</para>
    /// <para><b>整窗 origin 口径（Spike A 实测，docs/planning/research/screenshot-generalization/spike-2026-09-27.md）</b>：
    /// WGC 首帧尺寸与 <c>DWMWA_EXTENDED_FRAME_BOUNDS</c> <b>逐像素相等</b>（UiSampleApp 实测 762x552，
    /// 而 GetWindowRect 为 776x559，含约 7px 的 DWM 阴影/不可见 resize 边框），且 PrintWindow 相关性证实
    /// WGC 帧原点 = 扩展边框左上。故 <b>WGC 源 origin=扩展边框左上</b>；PrintWindow/BitBlt 按 <c>GetWindowRect</c>
    /// 作画/采样，origin=窗口矩形左上。因此本方法不做「裁掉扩展边框偏移」的裁剪（那是 WGC 帧==GetWindowRect 时才需要）。</para>
    /// </summary>
    public static CaptureResult CaptureWindow(IntPtr hwnd, CaptureOptions options)
    {
        EnsureDpi();
        // options.Clip 在 window 模式被忽略（非静默丢参，见 CaptureOptions.Clip 文档）：窗口几何由目标窗口自身
        // 决定——WGC 源取 DWMWA_EXTENDED_FRAME_BOUNDS、GDI 回退源取 GetWindowRect（Spike A 实测口径），
        // 不支持再叠加外部裁剪；元素级裁剪是独立入口 CaptureElement(hwnd, elementRectPx)（不走本方法）。
        var info = GetWindowInfo(hwnd) ?? throw new CaptureException(WindowCaptureFailMsg);
        var bounds = GetWindowBounds(hwnd) ?? throw new CaptureException(WindowCaptureFailMsg);

        // 客户区：GDI BitBlt 客户区屏幕矩形（spec §4.2；无 WGC——客户区本就是屏幕可见区）。
        if (options.ClientArea)
        {
            if (bounds.ClientArea.Width <= 0 || bounds.ClientArea.Height <= 0) throw new CaptureException(WindowCaptureFailMsg);
            using var cbmp = GdiCapture.CaptureScreenBits(bounds.ClientArea);
            return ImagePipeline.Process(cbmp, cbmp.Size, options.MaxWidth, options.MaxHeight, options.Format, options.Quality,
                info.Title, "BitBlt",
                originX: bounds.ClientArea.X, originY: bounds.ClientArea.Y) with { IsClientArea = true };
        }

        var (bmp, source, originX, originY) = CaptureWindowBits(hwnd, info, bounds);
        using (bmp)
            return ImagePipeline.Process(bmp, bmp.Size, options.MaxWidth, options.MaxHeight, options.Format, options.Quality,
                info.Title, source, originX: originX, originY: originY);
    }

    private const string WindowCaptureFailMsg = "窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。";

    /// <summary>
    /// 整窗原生位图回退链 + 帧原点（Spike A 口径）：WGC（首帧几何 == <c>DWMWA_EXTENDED_FRAME_BOUNDS</c>，
    /// origin=扩展边框左上）→ PrintWindow（以 GetWindowRect 原点自画；采样纯黑则继续回退）→
    /// BitBlt（最小化窗口屏幕无内容，不回退；采 GetWindowRect 区域）。全失败抛约定错误。
    /// <b>不处置位图</b>——调用方 using。<see cref="CaptureWindow"/> 与 <see cref="CaptureElement"/> 共用
    /// （元素裁剪与整窗同源，避免对屏幕直接 BitBlt 裁元素而截到遮挡物）。
    /// </summary>
    private static (Bitmap Bitmap, string Source, int OriginX, int OriginY) CaptureWindowBits(
        IntPtr hwnd, WindowHandleInfo info, WindowBounds bounds)
    {
        // 第 1 道：WGC（正确性主力）——首帧几何 == 扩展边框（Spike A 实测）
        Bitmap? bmp = WgcCapture.TryCaptureWindow(hwnd);
        var source = "WGC";
        int originX = bounds.ExtendedFrame.X, originY = bounds.ExtendedFrame.Y;

        // 第 2 道：PrintWindow → 采样纯黑则继续回退（spec §4.3-2）；以 GetWindowRect 原点自画
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

        if (bmp is null) throw new CaptureException(WindowCaptureFailMsg);
        return (bmp, source, originX, originY);
    }

    /// <summary>整窗/客户区抓取便捷重载：<paramref name="clientArea"/>=true 抓客户区；否则整窗（默认不缩放、png）。</summary>
    public static CaptureResult CaptureWindow(IntPtr hwnd, bool clientArea = false)
        => CaptureWindow(hwnd, new CaptureOptions(ClientArea: clientArea));

    /// <summary>
    /// 旧签名（宿主当前调用点，Task 10 切换）：委托到 <see cref="CaptureWindow(IntPtr, CaptureOptions)"/>，
    /// maxDimension 同时作 maxWidth/maxHeight、整窗（clientArea=false）。宿主恒传正数
    /// （<c>AppConfig.ScreenshotMaxDimension</c>），故缩放/回退链与切换前一致；若传 0/负则两轴皆不限、不缩放（k=1）。
    /// </summary>
    public static CaptureResult CaptureWindow(IntPtr hwnd, int maxDimension, string format, int quality)
        => CaptureWindow(hwnd, new CaptureOptions(MaxWidth: maxDimension, MaxHeight: maxDimension,
            Format: format, Quality: quality));

    /// <summary>
    /// element 模式抓取（Task 6，spec §7.1）：先取元素所属<b>顶层窗口</b>自身的窗口帧（与
    /// <see cref="CaptureWindow"/> 同一条 WGC→PrintWindow→BitBlt 回退链），再把 <paramref name="elementRectPx"/>
    /// （虚拟屏物理像素）换算为帧内坐标后裁剪——<b>不对屏幕直接 BitBlt 裁元素</b>（否则截到的是遮挡物）。
    /// 元素属另一顶层窗口（ComboBox 弹层/popup/tooltip）时，调用方须传元素自身
    /// <c>GetAncestor(GA_ROOT)</c> 的 <paramref name="topLevelHwnd"/>（见 <c>UiElementInfo.TopLevelHwnd</c>）。
    /// <para>裁剪 = <paramref name="elementRectPx"/> 与窗口帧求交：空交集抛约定错误；部分越界则裁至交集且
    /// <see cref="CaptureResult.ClippedToScreen"/>=true。origin=实际抓取交集左上（spec §5）；scale=图像/原生。</para>
    /// </summary>
    public static CaptureResult CaptureElement(IntPtr topLevelHwnd, Rectangle elementRectPx)
        => CaptureElement(topLevelHwnd, elementRectPx, new CaptureOptions());

    /// <summary>带抓取选项的 element 实现（缩放/编码选项语义同 <see cref="CaptureWindow(IntPtr, CaptureOptions)"/>）。</summary>
    public static CaptureResult CaptureElement(IntPtr topLevelHwnd, Rectangle elementRectPx, CaptureOptions options)
    {
        EnsureDpi();
        if (elementRectPx.IsEmpty)
            throw new CaptureException("元素矩形为空：无法裁剪（目标元素不可见/无几何）。");
        var info = FindWindowByHwnd(topLevelHwnd)
            ?? throw new CaptureException($"顶层窗口句柄无效或不可见（hwnd={topLevelHwnd}）：元素截图需传元素自身 GetAncestor(GA_ROOT) 的顶层窗口。");
        var bounds = GetWindowBounds(topLevelHwnd) ?? throw new CaptureException(WindowCaptureFailMsg);

        var (bmp, source, originX, originY) = CaptureWindowBits(topLevelHwnd, info, bounds);
        using (bmp)
        {
            var frame = new Rectangle(originX, originY, bmp.Width, bmp.Height);
            var inter = Rectangle.Intersect(elementRectPx, frame);
            if (inter.IsEmpty)
                throw new CaptureException($"元素矩形 ({elementRectPx.X},{elementRectPx.Y},{elementRectPx.Width},{elementRectPx.Height}) 不在顶层窗口帧 ({frame.X},{frame.Y},{frame.Width},{frame.Height}) 内。");
            var local = new Rectangle(inter.X - originX, inter.Y - originY, inter.Width, inter.Height);
            using var cropped = bmp.Clone(local, bmp.PixelFormat);
            return ImagePipeline.Process(cropped, cropped.Size, options.MaxWidth, options.MaxHeight, options.Format, options.Quality,
                info.Title, source, clippedToScreen: inter != elementRectPx, originX: inter.X, originY: inter.Y);
        }
    }
}
