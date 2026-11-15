using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;

namespace NativeInputSpike;

internal sealed class ConnectionForm : Form
{
    private readonly RdpHost _host = new() { Dock = DockStyle.Fill };
    private readonly string _root;
    private readonly System.Windows.Forms.Timer _timer = new() { Interval = 1000 };

    internal ConnectionForm(string root)
    {
        _root = root;
        Text = "NativeInputSpike — Child Session（需认证时仅在此窗输入）";
        ClientSize = new Size(1024, 768);
        Controls.Add(_host);
        Shown += (_, _) =>
        {
            try { _host.ConnectChild(); _timer.Start(); }
            catch (Exception ex) { Program.Save(Path.Combine(_root, "connection-error.json"), new { error = ex.ToString() }); Close(); }
        };
        _timer.Tick += (_, _) => SaveState();
        FormClosed += (_, _) => _timer.Dispose();
    }

    protected override bool ShowWithoutActivation => true;

    private void SaveState()
    {
        var hasChild = Native.WTSGetChildSessionId(out var child);
        if (!hasChild || _host.Connected != 1 || File.Exists(Path.Combine(_root, "connection.json"))) return;
        using var process = Process.GetCurrentProcess();
        Program.Save(Path.Combine(_root, "connection.json"), new { pid = process.Id,
            startedTicks = process.StartTime.ToUniversalTime().Ticks, parentSession = process.SessionId,
            hasChild, childId = child, connected = _host.Connected, at = DateTimeOffset.UtcNow });
    }
}

internal sealed class RdpHost : AxHost
{
    internal RdpHost() : base("A0C63C30-F08D-4AB4-907C-34905D770C7D") { }
    internal int Connected => Convert.ToInt32(Get(GetOcx() ?? throw new InvalidOperationException("RDP 控件未创建。"), "Connected"));

    internal void ConnectChild()
    {
        var client = GetOcx() ?? throw new InvalidOperationException("RDP 控件未创建。");
        Set(client, "Server", "localhost");
        Set(client, "DesktopWidth", 1024);
        Set(client, "DesktopHeight", 768);
        Set(client, "ColorDepth", 32);
        var advanced = Get(client, "AdvancedSettings7");
        Set(advanced, "EnableCredSspSupport", true);
        Set(advanced, "SmartSizing", false);
        object child = true;
        ((IRdpExtendedSettings)client).SetProperty("ConnectToChildSession", ref child);
        client.GetType().InvokeMember("Connect", BindingFlags.InvokeMethod, null, client, null);
    }

    private static object Get(object value, string name) => value.GetType().InvokeMember(name, BindingFlags.GetProperty, null, value, null)!;
    private static void Set(object target, string name, object value) => target.GetType().InvokeMember(name, BindingFlags.SetProperty, null, target, [value]);

    [ComImport, Guid("302D8188-0052-4807-806A-362B628F9AC5"), InterfaceType(ComInterfaceType.InterfaceIsIUnknown)]
    private interface IRdpExtendedSettings
    {
        void SetProperty([MarshalAs(UnmanagedType.BStr)] string name, [MarshalAs(UnmanagedType.Struct)] ref object value);
        [return: MarshalAs(UnmanagedType.Struct)] object GetProperty([MarshalAs(UnmanagedType.BStr)] string name);
    }
}
