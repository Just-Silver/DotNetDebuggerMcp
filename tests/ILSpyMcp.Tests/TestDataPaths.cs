namespace ILSpyMcp.Tests;

/// <summary>
/// 单测访问 tests/TestData 下测试程序集的路径解析：从测试进程 CWD（bin/Debug/net10.0）上溯仓库根再拼 TestData。
/// </summary>
internal static class TestDataPaths
{
    /// <summary>生成的测试程序集 ILSpyMcp.TestSamples.dll（601 class + BigClass）。</summary>
    public static readonly string TestSamplesDll = Locate("tests", "TestData", "ILSpyMcp.TestSamples.dll");

    private static string Locate(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // bin/Debug/net10.0 → 上溯 5 层到仓库根（含 ILSpyMcp.slnx）
        for (var i = 0; i < 5 && dir is not null; i++) dir = dir.Parent;
        if (dir is null || !File.Exists(Path.Combine(dir.FullName, "ILSpyMcp.slnx")))
        {
            throw new DirectoryNotFoundException("未找到仓库根目录（缺少 ILSpyMcp.slnx）");
        }
        return Path.Combine([dir.FullName, .. segments]);
    }
}
