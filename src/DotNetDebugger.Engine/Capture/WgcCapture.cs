using System.Drawing;
using System.Runtime.InteropServices;
using Windows.Graphics.Capture;
using Windows.Graphics.DirectX;
using Windows.Graphics.DirectX.Direct3D11;
using Windows.Graphics.Imaging;
using Windows.Storage.Streams;

namespace DotNetDebugger.Engine.Capture;

/// <summary>
/// Windows Graphics Capture 单帧抓窗（spec §4.3 第 1 道；2026-09-22 spike 实测跑通版本）：
/// IGraphicsCaptureItemInterop::CreateForWindow（Win32 unpackaged 专用路径）→ CreateFreeThreaded
/// framepool（免 DispatcherQueue 消息泵死锁）→ 限时等首帧 → SoftwareBitmap → PNG → Bitmap。
/// 正确性优先：DWM 取帧不黑图、被遮挡可截、无需置顶。任何失败/超时返回 null 由调用方回退 GDI，不抛。
/// 同步封装：线程池 MTA 线程（无 SynchronizationContext）上 WinRT async 同步等无死锁风险（spec §4.4）。
/// </summary>
internal static class WgcCapture
{
    private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromMilliseconds(500); // spec §4.3「约 500ms 上限」（实测首帧 38ms）

    public static Bitmap? TryCaptureWindow(IntPtr hwnd)
    {
        if (!GraphicsCaptureSession.IsSupported()) return null;   // 老系统/无头 → 直接回退
        try { return CaptureCore(hwnd); }
        catch (Exception) { return null; }                        // 回退链消化一切 WGC 异常
    }

    private static Bitmap? CaptureCore(IntPtr hwnd)
    {
        // 1) item（失败且为 owned/无主窗口 → 临时加 WS_EX_APPWINDOW 提升重试，spec §4.3；完成后还原）
        var item = WgcInterop.CreateItemForWindow(hwnd);
        var restoreStyle = IntPtr.Zero;
        if (item is null)
        {
            restoreStyle = WgcInterop.AddAppWindowStyle(hwnd);
            if (restoreStyle == IntPtr.Zero) return null;
            item = WgcInterop.CreateItemForWindow(hwnd);
            if (item is null) { WgcInterop.RestoreStyle(hwnd, restoreStyle); return null; }
        }

        Direct3D11CaptureFramePool? framePool = null;
        GraphicsCaptureSession? session = null;
        try
        {
            // 2) D3D11(BGRA) → IDXGIDevice → WinRT IDirect3DDevice
            var graphicsDevice = WgcInterop.CreateWinrtDevice();
            if (graphicsDevice is null) return null;

            // 3) framepool（CreateFreeThreaded）+ session：关黄框/光标（ApiInformation 探测兼容老系统）
            framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                graphicsDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
            session = framePool.CreateCaptureSession(item);
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                session.IsBorderRequired = false;       // Win11 22H2+ 可关；Win10 黄框关不掉（spec §9 接受）
            if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                    "Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                session.IsCursorCaptureEnabled = false; // Win10 1903+

            // 4) 等首帧（FrameArrived 单次消费）→ nudge（RedrawWindow/DwmFlush）催 DWM 出帧
            var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(
                TaskCreationOptions.RunContinuationsAsynchronously);
            Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> handler = null!;
            handler = (sender, _) =>
            {
                var f = sender.TryGetNextFrame();
                if (f is null) return;
                if (!tcs.TrySetResult(f)) f.Dispose();
                sender.FrameArrived -= handler;
            };
            framePool.FrameArrived += handler;
            session.StartCapture();
            WgcInterop.Nudge(hwnd);

            if (!tcs.Task.Wait(FirstFrameTimeout)) return null;

            // 5) 等帧 → 编码 → Bitmap（async 私有方法同步等：线程池 MTA 无 SC，无死锁）
            using var frame = tcs.Task.Result;
            return Encode(frame).GetAwaiter().GetResult();
        }
        catch (Exception)
        {
            return null;
        }
        finally
        {
            session?.Dispose();
            framePool?.Dispose();
            if (restoreStyle != IntPtr.Zero) WgcInterop.RestoreStyle(hwnd, restoreStyle);
        }
    }

    /// <summary>帧 → SoftwareBitmap → PNG → System.Drawing.Bitmap。
    /// 坑③：new Bitmap(stream) 生命周期绑定流——先入内存流解码再深拷贝到独立位图，
    /// 否则流释放后 GDI+ 对象失效（spike 原版直接写文件未暴露，落 Engine 必须拷贝）。</summary>
    private static async Task<Bitmap?> Encode(Direct3D11CaptureFrame frame)
    {
        using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)
            .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
        using var ras = new InMemoryRandomAccessStream();
        var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
        encoder.SetSoftwareBitmap(software);
        await encoder.FlushAsync();
        var bytes = new byte[ras.Size];
        using (var dr = new DataReader(ras))
        {
            await dr.LoadAsync((uint)ras.Size);
            dr.ReadBytes(bytes);
        }
        using var ms = new MemoryStream(bytes);
        using var tmp = new Bitmap(ms);
        return new Bitmap(tmp);   // 深拷贝，脱离 stream 生命周期
    }
}

/// <summary>WGC 所需的原生 interop（全部公开 API 的 P/Invoke/vtable 手写；三处坑固化在注释）。</summary>
internal static class WgcInterop
{
    private static readonly Guid ItemIid = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");    // GraphicsCaptureItem
    private static readonly Guid FactoryIid = new("00000035-0000-0000-c000-000000000046");  // IActivationFactory
    private static readonly Guid InteropIid = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356"); // IGraphicsCaptureItemInterop
    private static readonly Guid IdxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice

    private const uint RdwInvalidate = 0x0001, RdwAllChildren = 0x0080, RdwUpdateNow = 0x0100;
    private const int GwlExStyle = -20;
    private const long WsExAppWindow = 0x00040000;

    // —— P/Invoke ——
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, uint length, out IntPtr hstring);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int WindowsDeleteString(IntPtr hstring);
    [DllImport("combase.dll", ExactSpelling = true)]
    private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
        IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
        out IntPtr device, out IntPtr immediateContext, IntPtr chosenFeatureLevel);
    [DllImport("d3d11.dll", ExactSpelling = true)]
    private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
    [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
    [DllImport("dwmapi.dll")] private static extern int DwmFlush();
    [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
    [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
    private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int QiDelegate(IntPtr self, ref Guid iid, out IntPtr ppv);
    [UnmanagedFunctionPointer(CallingConvention.StdCall)]
    private delegate int CreateForWindowDelegate(IntPtr thisPtr, IntPtr hwnd, ref Guid iid, out IntPtr item);

    /// <summary>手写 COM QI（vtable slot 0）。坑①：Marshal.QueryInterface 只有 object 重载，
    /// 传 IntPtr 会被装箱成 boxed 对象 → 稳定 E_NOINTERFACE，必须走原生 vtable。</summary>
    private static int QI(IntPtr comPtr, ref Guid iid, out IntPtr result)
    {
        var vtable = Marshal.ReadIntPtr(comPtr);
        var fn = Marshal.ReadIntPtr(vtable, 0);
        return Marshal.GetDelegateForFunctionPointer<QiDelegate>(fn)(comPtr, ref iid, out result);
    }

    /// <summary>RoGetActivationFactory → QI IGraphicsCaptureItemInterop → CreateForWindow（vtable slot 3）。
    /// 坑②：HSTRING 长度必须动态取（手写常量数错一位即 CLASS_E_CLASSNOTAVAILABLE）。</summary>
    public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
    {
        var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
        var hr = WindowsCreateString(className, (uint)className.Length, out var hs);
        if (hr < 0) return null;
        IntPtr factory = IntPtr.Zero, interop = IntPtr.Zero, itemPtr = IntPtr.Zero;
        try
        {
            var fiid = FactoryIid;
            hr = RoGetActivationFactory(hs, ref fiid, out factory);
            if (hr < 0) return null;
            var iiid = InteropIid;
            hr = QI(factory, ref iiid, out interop);
            if (hr < 0) return null;

            var vtable = Marshal.ReadIntPtr(interop);
            var fn = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);   // IUnknown(0-2) 之后第一方法
            var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(fn);
            var iid = ItemIid;
            hr = create(interop, hwnd, ref iid, out itemPtr);
            if (hr < 0) return null;   // E_INVALIDARG=owned/无主窗口，由调用方按 APPWINDOW 策略提升重试
            return WinRT.MarshalInspectable<object>.FromAbi(itemPtr) as GraphicsCaptureItem;
        }
        finally
        {
            if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
            if (interop != IntPtr.Zero) Marshal.Release(interop);
            if (factory != IntPtr.Zero) Marshal.Release(factory);
            WindowsDeleteString(hs);
        }
    }

    /// <summary>D3D11(BGRA) → IDXGIDevice(QI) → CreateDirect3D11DeviceFromDXGIDevice → WinRT IDirect3DDevice。
    /// 坑④（复核记录）：IDXGIDevice 的 IID 是 54ec77fa-1377-44e6-…（dxgi.idl 双源核对）——
    /// 后 96 位凭记忆写错会稳定 E_NOINTERFACE。
    /// 坑⑤（spike 对照实证）：各出参引用一律不 Release——释放任一环节会使后续 framePool 创建
    /// 触发 NRE（WinRT 投影对象与 DXGI/D3D 链的引用归属与经典 COM 直觉不同）；泄漏的几个引用由
    /// 进程生命周期兜底（本工具进程截图场景可接受）。</summary>
    public static IDirect3DDevice? CreateWinrtDevice()
    {
        var hr = D3D11CreateDevice(IntPtr.Zero, 1 /*Hardware*/, IntPtr.Zero, 0x20 /*BgraSupport*/,
            IntPtr.Zero, 0, 7 /*D3D11_SDK_VERSION*/, out var dev, out var ctx, IntPtr.Zero);
        if (hr < 0) return null;
        var iid = IdxgiDeviceIid;
        hr = QI(dev, ref iid, out var dxgi);
        if (hr < 0) return null;
        hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var unk);
        if (hr < 0) return null;
        return WinRT.MarshalInspectable<object>.FromAbi(unk) as IDirect3DDevice;
    }

    /// <summary>催 DWM 出帧：RedrawWindow（invalidate+updatenow）+ DwmFlush（无 DWM 会话容忍异常）。</summary>
    public static void Nudge(IntPtr hwnd)
    {
        RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwAllChildren | RdwUpdateNow);
        try { DwmFlush(); } catch (Exception) { /* 忽略 */ }
    }

    /// <summary>owned/无主窗口提升：加 WS_EX_APPWINDOW，返回原 style（失败返回 Zero）。</summary>
    public static IntPtr AddAppWindowStyle(IntPtr hwnd)
    {
        var old = GetWindowLongPtr(hwnd, GwlExStyle);
        if (old == IntPtr.Zero) return IntPtr.Zero;
        SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(unchecked((long)((ulong)old | WsExAppWindow))));
        Nudge(hwnd);
        return old;
    }

    /// <summary>还原 <see cref="AddAppWindowStyle"/> 保存的原 style。</summary>
    public static void RestoreStyle(IntPtr hwnd, IntPtr oldStyle)
        => SetWindowLongPtr(hwnd, GwlExStyle, oldStyle);
}
