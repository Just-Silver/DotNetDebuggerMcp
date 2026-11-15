using System.Reflection;
using System.Runtime.InteropServices;
using SharpSight.ChildSession.Security;

namespace SharpSight.ChildSession.Connection;

internal sealed class RdpActiveXHost() : AxHost("A0C63C30-F08D-4AB4-907C-34905D770C7D")
{
    private static readonly Guid EventsIid = new("336D5562-EFA8-482E-8CB3-C5C0FC7A7DB6");
    private Action? _loginComplete;
    private Action<int>? _disconnected;
    internal bool Connected => Convert.ToInt32(Get(Ocx, "Connected")) == 1;
    internal bool LoginCompleted { get; private set; }
    internal string? LoginEventError { get; private set; }
    internal bool CredentialApplied { get; private set; }
    internal string? CredentialError { get; private set; }
    internal RdpReadiness Readiness => new(true, Connected, LoginCompleted, LoginEventError);
    private object Ocx => GetOcx() ?? throw new InvalidOperationException("RDP控件尚未创建。");

    protected override void CreateSink()
    {
        base.CreateSink();
        // 重建sink属于新一代连接，不能继承上一代的登录结论。
        LoginCompleted = false;
        LoginEventError = null;
        _loginComplete = () => LoginCompleted = true;
        _disconnected = _ => LoginCompleted = false;
        try
        {
            ComEventsHelper.Combine(Ocx, EventsIid, 3, _loginComplete);
            ComEventsHelper.Combine(Ocx, EventsIid, 4, _disconnected);
        }
        catch (Exception ex)
        {
            // AxHost.StartEvents会吞掉CreateSink的异常并认定sink已附加；不留下失败标记就只会永久停在等待认证。
            LoginEventError = $"{ex.GetType().Name}：{ex.Message}";
            DetachEvents();
        }
    }

    protected override void DetachSink()
    {
        try { DetachEvents(); }
        finally { base.DetachSink(); }
    }

    // 解绑发生在控件销毁路径上，单个dispid失败不能阻断其余解绑与关闭流程。
    private void DetachEvents()
    {
        LoginCompleted = false;
        try { if (_disconnected is not null) ComEventsHelper.Remove(Ocx, EventsIid, 4, _disconnected); }
        catch (Exception) { }
        try { if (_loginComplete is not null) ComEventsHelper.Remove(Ocx, EventsIid, 3, _loginComplete); }
        finally { _loginComplete = null; _disconnected = null; }
    }

    /// <summary>
    /// 拖动连接窗后按 RDP 8.1 动态分辨率把子会话桌面改成客户区大小，保持 1:1 不变形。
    /// 只用 UpdateSessionDisplaySettings，不 Disconnect/Connect（后者会白屏），也不依赖 SmartSizing（不保证纵横比）。
    /// </summary>
    internal bool ApplyDisplaySettings(int width, int height)
    {
        try
        {
            if (!Connected) return false;
            object[] arguments = [(uint)width, (uint)height, ToMillimeters(width), ToMillimeters(height),
                0u, 100u, 100u];
            Ocx.GetType().InvokeMember("UpdateSessionDisplaySettings", BindingFlags.InvokeMethod, null, Ocx, arguments);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"动态分辨率同步失败（{width}×{height}）：{ex.GetBaseException().Message}");
            return false;
        }
    }

    /// <summary>尺寸合法、与上次成功设置不同、且连接已稳定时才下发。</summary>
    internal static bool ShouldUpdateDisplay(int width, int height, int appliedWidth, int appliedHeight,
        TimeSpan sinceConnected)
        => width is >= MinimumDesktopPixels and <= MaximumDesktopPixels &&
           height is >= MinimumDesktopPixels and <= MaximumDesktopPixels &&
           (width != appliedWidth || height != appliedHeight) &&
           sinceConnected >= DisplaySettleTime;

    /// <summary>物理尺寸按 96 DPI 换算成毫米；MS-RDPEDISP 规定毫米值 &lt;10 或 &gt;10000 时会被忽略。</summary>
    internal static uint ToMillimeters(int pixels)
        => (uint)Math.Clamp(Math.Round(pixels / 96.0 * 25.4), 10, 10000);

    internal const int MinimumDesktopPixels = 200;
    internal const int MaximumDesktopPixels = 8192;

    /// <summary>连接后要留出动态分辨率生效窗口，过早调用会拿到 E_UNEXPECTED（RDP 8.1 实测）。</summary>
    internal static readonly TimeSpan DisplaySettleTime = TimeSpan.FromSeconds(1.5);

    internal void ConnectChild(ChildCredential? credential)    {
        LoginCompleted = false;
        CredentialApplied = false;
        CredentialError = null;
        Set(Ocx, "Server", "localhost");
        Set(Ocx, "DesktopWidth", 1024);
        Set(Ocx, "DesktopHeight", 768);
        Set(Ocx, "ColorDepth", 32);
        var settings = Get(Ocx, "AdvancedSettings7");
        Set(settings, "EnableCredSspSupport", true);
        Set(settings, "SmartSizing", false);
        if (credential is not null) ApplyCredential(credential, settings);
        object enabled = true;
        ((IRdpExtendedSettings)Ocx).SetProperty("ConnectToChildSession", ref enabled);
        Ocx.GetType().InvokeMember("Connect", BindingFlags.InvokeMethod, null, Ocx, null);
    }

    /// <summary>
    /// 认证停住且要补配凭据时先断开当前连接，避免用户同时在 RDP 登录界面与新凭据窗口里输入。
    /// 取消配置时调用方会用 <see cref="ConnectChild"/>（null 凭据）恢复系统登录界面。
    /// </summary>
    internal bool DisconnectChild()
    {
        try
        {
            Ocx.GetType().InvokeMember("Disconnect", BindingFlags.InvokeMethod, null, Ocx, null);
            return true;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"断开旧连接以更新凭据失败：{ex.Message}");
            return false;
        }
    }

    /// <summary>Connect 前短暂等待旧连接释放；Disconnect 完成由事件通知，不能假设同步归零。</summary>
    internal void WaitForDisconnected(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            try { if (Convert.ToInt32(Get(Ocx, "Connected")) == 0) return; }
            catch (Exception) { return; }
            Application.DoEvents();
            Thread.Sleep(50);
        }
    }

    /// <summary>
    /// 用户名与明文密码由调用方从当前用户凭据库取出后直接注入，ActiveX 自己不读凭据管理器。
    /// 注入失败不能阻断连接：退回系统连接窗口认证原路径，并留下诊断原因。
    /// </summary>
    private void ApplyCredential(ChildCredential credential, object settings)
    {
        try
        {
            Set(Ocx, "UserName", credential.UserName);
            if (credential.Domain.Length > 0) Set(Ocx, "Domain", credential.Domain);
            Set(settings, "ClearTextPassword", credential.Password);
            CredentialApplied = true;
        }
        catch (Exception ex)
        {
            var cause = ex.GetBaseException();
            CredentialError = $"{cause.GetType().Name}：0x{cause.HResult:X8}";
            Console.Error.WriteLine($"自动登录凭据注入失败（{CredentialError}），改由系统连接窗口认证。");
        }
    }

    private static object Get(object target, string name)
        => target.GetType().InvokeMember(name, BindingFlags.GetProperty, null, target, null)!;
    private static void Set(object target, string name, object value)
        => target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpExtendedSettings
    {
        void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
        [return: MarshalAs(UnmanagedType.Struct)] object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
    }
}
