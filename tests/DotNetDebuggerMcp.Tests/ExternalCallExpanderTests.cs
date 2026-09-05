using DotNetDebugger.Decompiler.Configuration;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// ExternalCallExpander 跨程序集调用链展开用例：ExtCaller.Run 对 TestSamples.Callee 的跨程序集调用可经
/// UniversalAssemblyResolver 定位 TestSamples.dll 并展开其方法体子序列；框架类调用（System.Console）解析失败
/// 或不抛异常（找不到返回空）。素材：tests/TestData 的 DotNetDebuggerMcp.TestSamplesExt.dll（引用 TestSamples.dll）。
/// </summary>
public class ExternalCallExpanderTests
{
    [Fact]
    public void ExtCaller_Run_跨程序集调用展开非空含Object构造函数()
    {
        // Run { var c = new Callee(); c.Help(); }：外部调用点含 newobj Callee..ctor（AssemblyFullName 归属
        // TestSamples）， 展开其方法体子序列应含 System.Object::.ctor（默认构造函数调用基类构造）。
        using var scope = new MainScope();
        var external = Assert.Single(ScanExtCallerRun(scope.Reader, scope.Pe), c =>
            c.IsExternal && c.MemberName == ".ctor");
        Assert.StartsWith(TestDataPaths.TestSamplesAssemblyName, external.AssemblyFullName);

        using var expander = new ExternalCallExpander(TestDataPaths.TestSamplesExtDll);
        var expanded = expander.Expand(external, new[] { Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, Environment.CurrentDirectory });

        Assert.NotEmpty(expanded);
        Assert.Contains("调用:", expanded[0]);
        Assert.Contains(expanded, line => line.Contains("System.Object::.ctor"));
    }

    [Fact]
    public void SystemConsole类调用_展开不抛异常()
    {
        // 框架调用 System.Console::WriteLine(string)：无论 resolver 能否定位到共享框架，展开均不得抛异常（找不到返回空）。
        var callSite = new CallSite(
            IsExternal: true,
            TypeFullName: "System.Console",
            MemberName: "WriteLine",
            Signature: "",
            MemberToken: null,
            AssemblyFullName: "System.Console, Version=4.1.0.0, Culture=neutral, PublicKeyToken=b03f5f7f11d50a3a",
            ParamCount: 1);

        using var expander = new ExternalCallExpander(TestDataPaths.TestSamplesExtDll);
        var expanded = expander.Expand(callSite, new[] { Environment.CurrentDirectory });

        Assert.NotNull(expanded);
    }

    [Fact]
    public void 展开方法体解码中止_累计降级计数()
    {
        // 把 TestSamplesExt 复制到临时目录并放置合成同名 TestSamples.dll（Callee 方法体 IL 截断 → 解码中止）： resolver 经主 dll
        // 同目录定位到合成程序集，展开中止的方法体须累计进 AbortedBodies（供 call_chain 降级提示并入）。
        var tempDir = Path.Combine(Path.GetTempPath(), "DotNetDebuggerMcp-expander-abort-test");
        Directory.CreateDirectory(tempDir);
        try
        {
            TestAssemblyWriter.WriteCorruptTestSamples(tempDir);
            var tempMain = Path.Combine(tempDir, TestDataPaths.TestSamplesExtAssemblyName + ".dll");
            File.Copy(TestDataPaths.TestSamplesExtDll, tempMain, overwrite: true);

            var callSite = new CallSite(
                IsExternal: true,
                TypeFullName: $"{TestDataPaths.SamplesNamespace}.Callee",
                MemberName: ".ctor",
                Signature: "",
                MemberToken: null,
                AssemblyFullName: $"{TestDataPaths.TestSamplesAssemblyName}, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                ParamCount: 0);

            using var expander = new ExternalCallExpander(tempMain);
            var expanded = expander.Expand(callSite, new[] { tempDir });

            Assert.NotEmpty(expanded);
            Assert.Equal(1, expander.AbortedBodies);
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 深度超限_最深层不再展开()
    {
        // 自引用深链 DeepChain.dll（M0→M1→...→M6）：预修复无深度限制时 7 层全部展开（含 ::M6 调用: 头行）； 修复后深度达到
        // ExternalExpandMaxDepth 的子树不再展开，最深层 M6 的头行不再出现。
        var tempDir = Path.Combine(Path.GetTempPath(), "DotNetDebuggerMcp-expander-depth-test");
        Directory.CreateDirectory(tempDir);
        try
        {
            var mainPath = TestAssemblyWriter.WriteDeepChain(tempDir);
            var callSite = new CallSite(
                IsExternal: true,
                TypeFullName: "DotNetDebuggerMcp.Deep.Chain",
                MemberName: "M0",
                Signature: "",
                MemberToken: null,
                AssemblyFullName: "DeepChain, Version=1.0.0.0, Culture=neutral, PublicKeyToken=null",
                ParamCount: 0);

            using var expander = new ExternalCallExpander(mainPath);
            var expanded = expander.Expand(callSite, Array.Empty<string>());

            Assert.Contains(expanded, l => l.Contains("::M0 调用:"));
            Assert.DoesNotContain(expanded, l => l.Contains("::M6 调用:"));
        }
        finally
        {
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public void 深度与节点上限常量_为合理正数()
    {
        // 深度/节点上限须为正整数且可正常生效（防误写 0/负数导致全部外部展开被截断）
        Assert.InRange(DecompilerConfig.ExternalExpandMaxDepth, 1, 64);
        Assert.InRange(DecompilerConfig.ExternalExpandMaxNodes, 1, 100_000);
    }

    private static IReadOnlyList<CallSite> ScanExtCallerRun(MetadataReader reader, PEReader pe)
    {
        var handle = MetadataNaming.FindType(reader, $"{TestDataPaths.SamplesExtNamespace}.ExtCaller");
        Assert.True(handle.HasValue, "测试程序集中未找到 ExtCaller");
        var type = reader.GetTypeDefinition(handle!.Value);
        foreach (var methodHandle in type.GetMethods())
        {
            var method = reader.GetMethodDefinition(methodHandle);
            if (reader.GetString(method.Name) != "Run") continue;
            return new CallChainScanner(pe).ScanMethod(methodHandle);
        }
        throw new InvalidOperationException("测试程序集中未找到 ExtCaller.Run");
    }

    /// <summary>
    /// 持有主 dll（TestSamplesExt）的 PEReader，供 ScanMethod 取 ExtCaller.Run 的调用点（PE 释放后 reader 访问会崩）。
    /// </summary>
    private sealed class MainScope : IDisposable
    {
        private readonly FileStream _fs;
        private readonly PEReader _pe;

        public MainScope()
        {
            _fs = File.OpenRead(TestDataPaths.TestSamplesExtDll);
            _pe = new PEReader(_fs);
            Reader = _pe.GetMetadataReader();
        }

        public PEReader Pe => _pe;

        public MetadataReader Reader { get; }

        public void Dispose()
        {
            _pe.Dispose();
            _fs.Dispose();
        }
    }
}
