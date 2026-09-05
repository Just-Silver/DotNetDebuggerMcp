namespace DotNetDebugger.Web.Tests;

/// <summary>测试数据路径定位：逐级上溯仓库根（含 DotNetDebuggerMcp.slnx）后定位 tests/TestData。</summary>
public static class TestDataPaths
{
    /// <summary>
    /// 测试程序集标识常量（与 generate-testdata.ps1 顶部变量保持一致；改名只动这里）。
    /// </summary>
    public const string SamplesNamespace = "DotNetDebuggerMcp.Samples";

    /// <summary>Ext 程序集命名空间。</summary>
    public const string SamplesExtNamespace = "DotNetDebuggerMcp.SamplesExt";

    /// <summary>主样本程序集名（dll 文件名 = 该值 + ".dll"）。</summary>
    public const string TestSamplesAssemblyName = "DotNetDebuggerMcp.TestSamples";

    /// <summary>Ext 程序集名。</summary>
    public const string TestSamplesExtAssemblyName = "DotNetDebuggerMcp.TestSamplesExt";

    /// <summary>反编译/映射测试用样例程序集。</summary>
    public static readonly string TestSamplesDll = Locate("tests", "TestData", TestSamplesAssemblyName + ".dll");

    private static string Locate(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetDebuggerMcp.slnx"))) break;
            dir = dir.Parent;
        }
        if (dir is null)
            throw new DirectoryNotFoundException("未找到仓库根目录（缺少 DotNetDebuggerMcp.slnx）");
        return Path.Combine([dir.FullName, .. segments]);
    }
}
