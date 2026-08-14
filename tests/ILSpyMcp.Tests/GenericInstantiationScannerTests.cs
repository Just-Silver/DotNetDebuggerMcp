using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// GenericInstantiationScanner 泛型实例化使用点扫描用例。
/// 素材：生成测试程序集（tests/TestData）中的 GenericBox`1（泛型类型，GenericUser 用 int/string 两个具体参数实例化）、
/// GenericHelper（泛型方法 Echo&lt;T&gt;，GenericCaller.Run 以 int 调用）。
/// </summary>
public class GenericInstantiationScannerTests
{
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

    [Fact]
    public void GenericBox_成员签名命中_含GenericUser与int及string实例化()
    {
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find("ILSpyMcp.Samples.GenericBox");

        Assert.Contains(result.SignatureHits, l => l.Contains("ILSpyMcp.Samples.GenericUser::"));
        Assert.Contains(result.SignatureHits, l => l.Contains("GenericBox<int>"));
        Assert.Contains(result.SignatureHits, l => l.Contains("GenericBox<string>"));
    }

    [Fact]
    public void GenericHelper_方法体调用命中_含GenericCaller的Echo_int()
    {
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find("ILSpyMcp.Samples.GenericHelper");

        Assert.NotEmpty(result.CallHits);
        Assert.Contains(result.CallHits, l => l.Contains("ILSpyMcp.Samples.GenericCaller::"));
        Assert.Contains(result.CallHits, l => l.Contains("Echo<int>"));
    }

    [Fact]
    public void GenericBox_无arity输入_同样命中()
    {
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find("GenericBox");

        Assert.Contains(result.SignatureHits, l => l.Contains("ILSpyMcp.Samples.GenericUser::"));
    }

    [Fact]
    public void GenericBox_自引用实例化_不捕获()
    {
        // GenericBox 自身成员（First/Add）方法体经 get_Items 访问器引用 GenericBox`1<!0>（自身类型参数），
        // 以及 Items 属性 List<T> 等含类型参数的实例化均非「具体化」实例化，不应出现在命中行
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find("ILSpyMcp.Samples.GenericBox`1");

        Assert.DoesNotContain(result.SignatureHits, l => l.Contains("GenericBox`1::"));
        Assert.DoesNotContain(result.SignatureHits, l => l.Contains("GenericBox<T0>"));
        Assert.DoesNotContain(result.CallHits, l => l.Contains("GenericBox`1::"));
        Assert.DoesNotContain(result.CallHits, l => l.Contains("GenericBox<T0>"));
    }

    [Fact]
    public void 正常方法体_Aborted计数为零()
    {
        using var scope = new MetadataScope();
        var scanner = new GenericInstantiationScanner(scope.Pe);

        scanner.Find("ILSpyMcp.Samples.GenericBox");

        Assert.Equal(0, scanner.AbortedBodies);
    }
}
