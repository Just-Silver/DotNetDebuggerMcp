using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

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

        var handle = MetadataNaming.FindType(reader, "ILSpyMcp.Formatting.OutputFormatter");
        Assert.True(handle.HasValue);
        Assert.False(CompilerGeneratedFilter.IsCompilerGenerated(reader, reader.GetTypeDefinition(handle!.Value)));
    }

    [Fact]
    public void 枚举全部类型_普通类型不被误杀()
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();

        // 名称不含 '<' 的类型不应被误判为编译器生成（CompilerGeneratedAttribute 兜底仅命中真带特性的类型）
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            var name = reader.GetString(type.Name);
            if (name.Contains('<')) continue;
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, type))
            {
                Assert.Fail($"普通类型被误判为编译器生成：{MetadataNaming.FullName(reader, type)}");
            }
        }
    }
}
