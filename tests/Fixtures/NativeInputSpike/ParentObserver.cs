using System.Runtime.InteropServices;

namespace NativeInputSpike;

internal static class ParentObserver
{
    private delegate nint HookProc(int code, nint wParam, nint lParam);
    [DllImport("user32.dll", SetLastError = true)] private static extern nint SetWindowsHookEx(int type, HookProc callback, nint module, uint thread);
    [DllImport("user32.dll")] private static extern bool UnhookWindowsHookEx(nint hook);
    [DllImport("user32.dll")] private static extern nint CallNextHookEx(nint hook, int code, nint wParam, nint lParam);

    internal static void Run(string root, int seconds)
    {
        var samples = new List<object>();
        var markedKeyboard = 0; var markedMouse = 0;
        HookProc keyboard = (code, wp, lp) =>
        {
            if (code >= 0 && Marshal.ReadIntPtr(lp, 16) == (nint)0x4e495350) markedKeyboard++;
            return CallNextHookEx(0, code, wp, lp);
        };
        HookProc mouse = (code, wp, lp) =>
        {
            if (code >= 0 && Marshal.ReadIntPtr(lp, IntPtr.Size == 8 ? 24 : 20) == (nint)0x4e495350) markedMouse++;
            return CallNextHookEx(0, code, wp, lp);
        };
        var kh = SetWindowsHookEx(13, keyboard, 0, 0);
        var mh = SetWindowsHookEx(14, mouse, 0, 0);
        if (kh == 0 || mh == 0)
        {
            if (kh != 0) UnhookWindowsHookEx(kh);
            if (mh != 0) UnhookWindowsHookEx(mh);
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        }
        using var context = new ApplicationContext();
        using var timer = new System.Windows.Forms.Timer { Interval = 50 };
        var until = DateTime.UtcNow.AddSeconds(seconds);
        timer.Tick += (_, _) => { samples.Add(Native.Observe()); if (DateTime.UtcNow >= until) context.ExitThread(); };
        timer.Start();
        try { Application.Run(context); }
        finally
        {
            UnhookWindowsHookEx(kh); UnhookWindowsHookEx(mh);
            GC.KeepAlive(keyboard); GC.KeepAlive(mouse);
        }
        var result = new { seconds, markedKeyboard, markedMouse, samples };
        Program.Save(Path.Combine(root, "parent-observations.json"), result);
        Program.Print(new { seconds, markedKeyboard, markedMouse, sampleCount = samples.Count });
    }
}
