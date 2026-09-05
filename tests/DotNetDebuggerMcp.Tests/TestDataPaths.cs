using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// 单测访问 tests/TestData 下测试程序集的路径解析：从测试进程 CWD（bin/Debug/net10.0）上溯仓库根再拼 TestData。
/// </summary>
internal static class TestDataPaths
{
    /// <summary>
    /// 测试程序集标识常量（与 generate-testdata.ps1 顶部变量保持一致；改名只动这里，以下路径/断言均由此拼接）。
    /// </summary>
    public const string SamplesNamespace = "DotNetDebuggerMcp.Samples";

    /// <summary>Ext 程序集命名空间（generate-testdata.ps1 的 $SamplesExtNamespace）。</summary>
    public const string SamplesExtNamespace = "DotNetDebuggerMcp.SamplesExt";

    /// <summary>主样本程序集名（dll 文件名 = 该值 + ".dll"，generate-testdata.ps1 的 $TestSamplesAssemblyName）。</summary>
    public const string TestSamplesAssemblyName = "DotNetDebuggerMcp.TestSamples";

    /// <summary>Ext 程序集名（generate-testdata.ps1 的 $TestSamplesExtAssemblyName）。</summary>
    public const string TestSamplesExtAssemblyName = "DotNetDebuggerMcp.TestSamplesExt";

    /// <summary>
    /// 生成的测试程序集 DotNetDebuggerMcp.TestSamples.dll（601 class + BigClass）。
    /// </summary>
    public static readonly string TestSamplesDll = Locate("tests", "TestData", TestSamplesAssemblyName + ".dll");

    /// <summary>
    /// 生成的跨程序集测试程序集 DotNetDebuggerMcp.TestSamplesExt.dll（引用 TestSamples.dll 的 Callee）。
    /// </summary>
    public static readonly string TestSamplesExtDll = Locate("tests", "TestData", TestSamplesExtAssemblyName + ".dll");

    /// <summary>
    /// 取指定程序集中 Callee 类型首个方法（Help，被 Caller.Run 的 c.Help() 调用）的元数据 token， 供 call_graph 的 token
    /// 方法级调用点用例。CallGraphToolTests / DotNetDebuggerMcpCmdTests / CallGraphExtractorTests 共用， 避免三处各存一份逐字符相同的辅助。
    /// </summary>
    /// <param name="dll">程序集路径（通常传 <see cref="TestSamplesDll"/>）。</param>
    /// <returns>形如 0x06000005 的元数据 token。</returns>
    public static string FirstCalleeMethodToken(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "Callee") continue;
            return $"0x{MetadataTokens.GetToken(type.GetMethods().First()):x8}";
        }
        throw new InvalidOperationException("TestSamples 未找到 Callee 类型");
    }

    private static string Locate(params string[] segments)
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        // 从测试进程 CWD（bin/Debug/net10.0）逐级上溯，直到找到含 DotNetDebuggerMcp.slnx 的仓库根
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetDebuggerMcp.slnx"))) break;
            dir = dir.Parent;
        }
        if (dir is null)
        {
            throw new DirectoryNotFoundException("未找到仓库根目录（缺少 DotNetDebuggerMcp.slnx）");
        }
        return Path.Combine([dir.FullName, .. segments]);
    }
}