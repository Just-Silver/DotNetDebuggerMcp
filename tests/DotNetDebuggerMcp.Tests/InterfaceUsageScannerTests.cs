using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// InterfaceUsageScanner 接口调用点扫描用例。 素材：生成测试程序集（tests/TestData）中的 IAnimal（Dog/SealedDog
/// 直接实现）、IWorker（WorkerBase 直接实现、WorkerDerived 间接实现） 与 AnimalCaller.Run(IAnimal a) { a.Speak();
/// }（IAnimal.Speak 调用点，MethodDef 直比路径）。
/// </summary>
public class InterfaceUsageScannerTests
{
    [Fact]
    public void IAnimal_调用点_含AnimalCaller_Run到Speak()
    {
        using var scope = new MetadataScope();
        var iface = TypeHandle(scope.Reader, $"{TestDataPaths.SamplesNamespace}.IAnimal");

        var callSites = new InterfaceUsageScanner(scope.Pe).FindCallSites(iface, $"{TestDataPaths.SamplesNamespace}.IAnimal");

        Assert.Contains($"{TestDataPaths.SamplesNamespace}.AnimalCaller::Run → Speak", callSites);
    }

    [Fact]
    public void IWorker_无调用点_结果为空()
    {
        // WorkerBase/WorkerDerived 只实现 Work 不调用；程序集内无调用 IWorker 成员的方法体
        using var scope = new MetadataScope();
        var iface = TypeHandle(scope.Reader, $"{TestDataPaths.SamplesNamespace}.IWorker");

        var callSites = new InterfaceUsageScanner(scope.Pe).FindCallSites(iface, $"{TestDataPaths.SamplesNamespace}.IWorker");

        Assert.Empty(callSites);
    }

    [Fact]
    public void 正常方法体_Aborted计数为零()
    {
        using var scope = new MetadataScope();
        var iface = TypeHandle(scope.Reader, $"{TestDataPaths.SamplesNamespace}.IAnimal");

        var scanner = new InterfaceUsageScanner(scope.Pe);
        scanner.FindCallSites(iface, $"{TestDataPaths.SamplesNamespace}.IAnimal");

        Assert.Equal(0, scanner.AbortedBodies);
    }

    private static TypeDefinitionHandle TypeHandle(MetadataReader reader, string typeFullName)
    {
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        return handle.Value;
    }

    /// <summary>
    /// 持有打开的 PEReader 与元数据读取器，保证 reader 在断言期间有效（PE 释放后 reader 访问会崩）。
    /// </summary>
    private sealed class MetadataScope : IDisposable
    {
        private readonly FileStream _fs;
        private readonly PEReader _pe;

        public MetadataScope()
        {
            _fs = File.OpenRead(TestDataPaths.TestSamplesDll);
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