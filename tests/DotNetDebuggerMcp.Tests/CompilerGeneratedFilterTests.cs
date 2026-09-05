using DotNetDebuggerMcp.Formatting;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public class CompilerGeneratedFilterTests
{
    // 主项目程序集：含 async 状态机（<M>d__N）、lambda 显示类（<>c）等编译器生成类型
    private static readonly string AssemblyPath = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public void 名称含尖括号的类型_判定为编译器生成()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var seenGenerated = false;

        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            if (!name.Contains('<')) continue;
            Assert.True(CompilerGeneratedFilter.IsCompilerGenerated(reader, type),
                $"名称含 '<' 的类型 {MetadataNaming.FullName(reader, type)} 应判定为编译器生成");
            seenGenerated = true;
        }

        Assert.True(seenGenerated, "测试程序集应至少含一个名称带尖括号的编译器生成类型（如 async 状态机）");
    }

    [Fact]
    public void 普通类型_判定为非编译器生成()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        var handle = MetadataNaming.FindType(reader, "DotNetDebuggerMcp.Formatting.OutputFormatter");
        Assert.True(handle.HasValue);
        Assert.False(CompilerGeneratedFilter.IsCompilerGenerated(reader, reader.GetTypeDefinition(handle!.Value)));
    }

    [Fact]
    public void 枚举全部类型_普通类型不被误杀()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        // 判定语义为「全名含 '<'」：全名不含 '<' 的类型（含普通嵌套类型）不应被误判为编译器生成
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (MetadataNaming.FullName(reader, type).Contains('<')) continue;
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, type))
            {
                Assert.Fail($"普通类型被误判为编译器生成：{MetadataNaming.FullName(reader, type)}");
            }
        }
    }

    [Fact]
    public void 嵌套编译器生成类型_短名不含尖括号也判定为生成()
    {
        // <PrivateImplementationDetails>+__StaticArrayInitTypeSize=NN 的嵌套类型短名不含 '<'， 但外层链含 '<'（编译时常量数组下沉产物），按全名判定必须命中——验证修复前的漏网场景
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        TypeDefinitionHandle? found = null;
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Name).StartsWith("__StaticArrayInitTypeSize", StringComparison.Ordinal))
            {
                found = handle;
                break;
            }
        }

        Assert.True(found is not null, "测试程序集应含 <PrivateImplementationDetails>+__StaticArrayInitTypeSize 嵌套类型");
        var nested = reader.GetTypeDefinition(found!.Value);
        Assert.DoesNotContain('<', reader.GetString(nested.Name)); // 短名确实不含 <
        Assert.True(CompilerGeneratedFilter.IsCompilerGenerated(reader, nested), "嵌套编译器生成类型应判定为生成");
        Assert.Contains("__StaticArrayInitTypeSize", MetadataNaming.FullName(reader, nested));
    }
}