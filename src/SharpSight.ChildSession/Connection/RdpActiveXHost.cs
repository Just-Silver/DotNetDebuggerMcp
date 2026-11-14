using System.Reflection;
using System.Runtime.InteropServices;

namespace SharpSight.ChildSession.Connection;

internal sealed class RdpActiveXHost() : AxHost("A0C63C30-F08D-4AB4-907C-34905D770C7D")
{
    internal bool Connected => Convert.ToInt32(Get(Ocx, "Connected")) == 1;
    private object Ocx => GetOcx() ?? throw new InvalidOperationException("RDP控件尚未创建。");

    internal void ConnectChild()
    {
        Set(Ocx, "Server", "localhost");
        Set(Ocx, "DesktopWidth", 1024);
        Set(Ocx, "DesktopHeight", 768);
        Set(Ocx, "ColorDepth", 32);
        var settings = Get(Ocx, "AdvancedSettings7");
        Set(settings, "EnableCredSspSupport", true);
        Set(settings, "SmartSizing", false);
        object enabled = true;
        ((IRdpExtendedSettings)Ocx).SetProperty("ConnectToChildSession", ref enabled);
        Ocx.GetType().InvokeMember("Connect", BindingFlags.InvokeMethod, null, Ocx, null);
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
