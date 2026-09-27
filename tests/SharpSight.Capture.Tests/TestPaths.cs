namespace SharpSight.Capture.Tests;

/// <summary>Capture 测试访问 tests/TestData 下测试目标的路径解析（迁自 Engine.Tests，仅保留截图用例所需项）。</summary>
internal static class TestPaths
{
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
