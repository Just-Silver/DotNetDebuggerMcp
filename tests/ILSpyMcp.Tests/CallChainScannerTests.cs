using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// CallChainScanner 方法级正向调用序列用例：ChainTop.Run 的调用序列（有序含重复）、内部调用 token 非空、
/// 外部调用归属与参数个数。素材：生成测试程序集（tests/TestData）中的 ChainTop/ChainMid。
/// </summary>
public class CallChainScannerTests
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

    private static MethodDefinitionHandle GetMethod(MetadataReader reader, string typeFullName, string methodName)
    {
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        var type = reader.GetTypeDefinition(handle!.Value);
        foreach (var methodHandle in type.GetMethods())
        {
            if (reader.GetString(reader.GetMethodDefinition(methodHandle).Name) == methodName) return methodHandle;
        }
        throw new InvalidOperationException($"测试程序集中未找到 {typeFullName}.{methodName}");
    }

    [Fact]
    public void ChainTop_Run_调用序列按IL序含重复()
    {
        // Run { new ChainMid().Mid(); ChainMid.StaticMid(); ChainMid.StaticMid(); }
        // IL 序：newobj ChainMid..ctor、callvirt ChainMid.Mid、call ChainMid.StaticMid ×2
        using var scope = new MetadataScope();
        var scanner = new CallChainScanner(scope.Pe);
        var calls = scanner.ScanMethod(GetMethod(scope.Reader, "ILSpyMcp.Samples.ChainTop", "Run"));

        Assert.Equal(new[] { ".ctor", "Mid", "StaticMid", "StaticMid" }, calls.Select(c => c.MemberName).ToArray());
    }

    [Fact]
    public void ChainTop_Run_内部调用MemberToken非空ParamCount负一()
    {
        using var scope = new MetadataScope();
        var scanner = new CallChainScanner(scope.Pe);
        var calls = scanner.ScanMethod(GetMethod(scope.Reader, "ILSpyMcp.Samples.ChainTop", "Run"));

        var internalCalls = calls.Where(c => !c.IsExternal).ToList();
        Assert.Equal(4, internalCalls.Count);
        Assert.All(internalCalls, c =>
        {
            Assert.False(string.IsNullOrEmpty(c.MemberToken));
            Assert.StartsWith("0x", c.MemberToken);
            Assert.Equal(-1, c.ParamCount);
            Assert.False(string.IsNullOrEmpty(c.Signature));
        });
    }

    [Fact]
    public void ChainMid_Mid_外部调用ConsoleWriteLine带归属与参数个数()
    {
        // Mid { System.Console.WriteLine("mid"); }：唯一调用为跨程序集 MemberRef System.Console::WriteLine(string)
        using var scope = new MetadataScope();
        var scanner = new CallChainScanner(scope.Pe);
        var calls = scanner.ScanMethod(GetMethod(scope.Reader, "ILSpyMcp.Samples.ChainMid", "Mid"));

        var external = Assert.Single(calls);
        Assert.True(external.IsExternal);
        Assert.Equal("System.Console", external.TypeFullName);
        Assert.Equal("WriteLine", external.MemberName);
        Assert.Null(external.MemberToken);
        Assert.Equal("", external.Signature);
        Assert.StartsWith("System.Console", external.AssemblyFullName);
        Assert.Equal(1, external.ParamCount);
    }
}
