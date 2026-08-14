using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// ExternalCallExpander 跨程序集调用链展开用例：ExtCaller.Run 对 TestSamples.Callee 的跨程序集调用可经
/// UniversalAssemblyResolver 定位 TestSamples.dll 并展开其方法体子序列；框架类调用（System.Console）解析失败
/// 或不抛异常（找不到返回空）。素材：tests/TestData 的 ILSpyMcp.TestSamplesExt.dll（引用 TestSamples.dll）。
/// </summary>
public class ExternalCallExpanderTests
{
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

    private static IReadOnlyList<CallSite> ScanExtCallerRun(MetadataReader reader, PEReader pe)
    {
        var handle = MetadataNaming.FindType(reader, "ILSpyMcp.SamplesExt.ExtCaller");
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

    [Fact]
    public void ExtCaller_Run_跨程序集调用展开非空含Object构造函数()
    {
        // Run { var c = new Callee(); c.Help(); }：外部调用点含 newobj Callee..ctor（AssemblyFullName 归属 TestSamples），
        // 展开其方法体子序列应含 System.Object::.ctor（默认构造函数调用基类构造）。
        using var scope = new MainScope();
        var external = Assert.Single(ScanExtCallerRun(scope.Reader, scope.Pe), c =>
            c.IsExternal && c.MemberName == ".ctor");
        Assert.StartsWith("ILSpyMcp.TestSamples", external.AssemblyFullName);

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
}
