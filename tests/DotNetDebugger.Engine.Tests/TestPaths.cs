namespace DotNetDebugger.Engine.Tests;

/// <summary>Engine 测试访问 tests/TestData/DebugTarget.exe 的路径解析。</summary>
internal static class TestPaths
{
    /// <summary>从测试 CWD 上溯仓库根（含 DotNetDebuggerMcp.slnx）拼 tests/TestData/DebugTarget.exe。</summary>
    public static string DebugTargetExe { get; } = Locate("tests", "TestData", "DebugTarget.exe");

    /// <summary>UiSampleApp.exe（U1A WinForms 目标；screenshot Capture 测试同用）。</summary>
    public static string UiSampleAppExe { get; } = Locate("tests", "TestData", "UiSampleApp", "UiSampleApp.exe");

    private static string Locate(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetDebuggerMcp.slnx"))) break;
            dir = dir.Parent;
        }
        if (dir is null) throw new DirectoryNotFoundException("未找到仓库根目录（缺少 DotNetDebuggerMcp.slnx）");
        return Path.Combine([dir.FullName, .. segments]);
    }
}
