using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// GenericInstantiationScanner 泛型实例化使用点扫描用例。 素材：生成测试程序集（tests/TestData）中的
/// GenericBox`1（泛型类型，GenericUser 用 int/string 两个具体参数实例化）、 GenericHelper（泛型方法
/// Echo&lt;T&gt;，GenericCaller.Run 以 int 调用）。
/// </summary>
public class GenericInstantiationScannerTests
{
    [Fact]
    public void GenericBox_成员签名命中_含GenericUser与int及string实例化()
    {
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find($"{TestDataPaths.SamplesNamespace}.GenericBox");

        Assert.Contains(result.SignatureHits, l => l.Contains($"{TestDataPaths.SamplesNamespace}.GenericUser::"));
        Assert.Contains(result.SignatureHits, l => l.Contains("GenericBox<int>"));
        Assert.Contains(result.SignatureHits, l => l.Contains("GenericBox<string>"));
    }

    [Fact]
    public void GenericHelper_方法体调用命中_含GenericCaller的Echo_int()
    {
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find($"{TestDataPaths.SamplesNamespace}.GenericHelper");

        Assert.NotEmpty(result.CallHits);
        Assert.Contains(result.CallHits, l => l.Contains($"{TestDataPaths.SamplesNamespace}.GenericCaller::"));
        Assert.Contains(result.CallHits, l => l.Contains("Echo<int>"));
    }

    [Fact]
    public void GenericBox_无arity输入_同样命中()
    {
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find("GenericBox");

        Assert.Contains(result.SignatureHits, l => l.Contains($"{TestDataPaths.SamplesNamespace}.GenericUser::"));
    }

    [Fact]
    public void GenericBox_自引用实例化_不捕获()
    {
        // GenericBox 自身成员（First/Add）方法体经 get_Items 访问器引用 GenericBox`1<!0>（自身类型参数）， 以及 Items 属性
        // List<T> 等含类型参数的实例化均非「具体化」实例化，不应出现在命中行
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find($"{TestDataPaths.SamplesNamespace}.GenericBox`1");

        Assert.DoesNotContain(result.SignatureHits, l => l.Contains("GenericBox`1::"));
        Assert.DoesNotContain(result.SignatureHits, l => l.Contains("GenericBox<T0>"));
        Assert.DoesNotContain(result.CallHits, l => l.Contains("GenericBox`1::"));
        Assert.DoesNotContain(result.CallHits, l => l.Contains("GenericBox<T0>"));
    }

    [Fact]
    public void 泛型方法内以类型参数调用Echo_不产出虚假Echo_T0()
    {
        // GenericSelfEcho.Run<T> 内调 GenericHelper.Echo(value)（value: T，泛型实参为方法类型参数）：预修复时
        // CaptureMethodInstantiation 无条件加 Echo<T0>（空上下文解码类型参数为 T0）产出虚假具体化命中； 修复后按「任一实参含类型参数」门控，方法级捕获跳过
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find($"{TestDataPaths.SamplesNamespace}.GenericHelper");

        Assert.DoesNotContain(result.CallHits, l => l.Contains("GenericSelfEcho::"));
        Assert.DoesNotContain(result.CallHits, l => l.Contains("Echo<T0>"));
    }

    [Fact]
    public void 嵌套部分具体化实参_GenericBox_SomeGeneric_T_不记为具体()
    {
        // NestedGenericUser<T> 的字段/参数类型为 GenericBox<SomeGeneric<T>>（嵌套实例化，内层 SomeGeneric<T> 含类型参数）：
        // 预修复时内层实例化的 last-element 标志重置使外层 GenericBox 被误判为具体化命中；修复后按任一实参跟踪不再捕获
        using var scope = new MetadataScope();

        var result = new GenericInstantiationScanner(scope.Pe).Find($"{TestDataPaths.SamplesNamespace}.GenericBox");

        Assert.DoesNotContain(result.SignatureHits, l => l.Contains("NestedGenericUser::"));
    }

    [Fact]
    public void 正常方法体_Aborted计数为零()
    {
        using var scope = new MetadataScope();
        var scanner = new GenericInstantiationScanner(scope.Pe);

        scanner.Find($"{TestDataPaths.SamplesNamespace}.GenericBox");

        Assert.Equal(0, scanner.AbortedBodies);
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