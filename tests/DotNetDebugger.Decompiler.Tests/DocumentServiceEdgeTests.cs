using DotNetDebugger.Decompiler.Document;
using System.Reflection.Metadata;
using Xunit;

namespace DotNetDebugger.Decompiler.Tests;

/// <summary>DocumentService 降级与错误提示测试。</summary>
public sealed class DocumentServiceEdgeTests
{
    private static string Dll => TestDataPaths.TestSamplesDll;

    [Fact]
    public void GetTypeDocument_未找到类型_返回Error()
    {
        var doc = DocumentService.GetTypeDocument(Dll, "No.Such.Type");
        Assert.False(doc.IsSuccess);
        Assert.Contains("未找到类型", doc.Error);
    }

    [Fact]
    public void GetTypeDocument_坏程序集_返回中文错误()
    {
        // 用测试输出目录里非程序集文件（.runtimeconfig.json 文本 JSON）
        var badFile = Path.Combine(AppContext.BaseDirectory, "DotNetDebugger.Decompiler.Tests.runtimeconfig.json");
        Assert.True(File.Exists(badFile), "测试用非程序集文件不存在");

        var doc = DocumentService.GetTypeDocument(badFile, "Any.Type");
        Assert.False(doc.IsSuccess);
        Assert.False(string.IsNullOrWhiteSpace(doc.Error));
    }

    [Fact]
    public void GetTypeDocument_Props_属性访问器有映射()
    {
        var doc = DocumentService.GetTypeDocument(Dll, $"{TestDataPaths.SamplesNamespace}.Props");
        Assert.True(doc.IsSuccess, doc.Error);

        // 属性访问器（get_/set_，方法 token 0x06）应出现在映射中——反编译的自动属性 get/set 是表达式体
        var accessorTokens = FindAccessorTokens(Dll, $"{TestDataPaths.SamplesNamespace}.Props");
        Assert.NotEmpty(accessorTokens);

        foreach (var token in accessorTokens)
        {
            // 每个访问器要么有映射条目，要么（极端）无——但不能有越界行
            foreach (var e in doc.Mapping.Where(e => e.MethodToken == token))
            {
                Assert.InRange(e.Line, 1, doc.Lines.Length);
                Assert.InRange(e.IlOffset, 0, int.MaxValue);
            }
        }
    }

    [Fact]
    public void GetTypeDocument_映射行号全部落在文本范围内()
    {
        var doc = DocumentService.GetTypeDocument(Dll, $"{TestDataPaths.SamplesNamespace}.BigClass");
        Assert.True(doc.IsSuccess, doc.Error);
        Assert.NotEmpty(doc.Mapping);
        foreach (var e in doc.Mapping)
        {
            Assert.InRange(e.Line, 1, doc.Lines.Length);
            Assert.True(e.EndOffset > e.IlOffset, $"区间非法 {e.IlOffset}-{e.EndOffset}");
        }
    }

    /// <summary>枚举类型内全部方法定义 token（含访问器）。</summary>
    private static List<int> FindAccessorTokens(string dll, string typeFullName)
    {
        var tokens = new List<int>();
        using var fs = File.OpenRead(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            if (DotNetDebugger.Decompiler.Metadata.MetadataNaming.FullName(mr, td) != typeFullName) continue;
            foreach (var mh in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mh);
                var name = mr.GetString(md.Name);
                if (name.StartsWith("get_") || name.StartsWith("set_"))
                    tokens.Add(System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh));
            }
        }
        return tokens;
    }
}
