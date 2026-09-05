using DotNetDebugger.Decompiler.Document;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using Xunit;

namespace DotNetDebugger.Decompiler.Tests;

/// <summary>DocumentService 语句级 IL→行映射测试（无 PDB TestSamples 实测校准）。</summary>
public sealed class DocumentServiceTests
{
    private static string Dll => TestDataPaths.TestSamplesDll;

    [Fact]
    public void GetTypeDocument_BigClass_文本含类型且行数正确()
    {
        var doc = DocumentService.GetTypeDocument(Dll, $"{TestDataPaths.SamplesNamespace}.BigClass");

        Assert.True(doc.IsSuccess, doc.Error);
        Assert.Contains("class BigClass", doc.Text);
        Assert.Equal(doc.Text.Replace("\r\n", "\n").Split('\n').Length, doc.Lines.Length);
        // 行数组按 1-based：Lines[0] 是第一行
        Assert.Equal(doc.Text.Replace("\r\n", "\n").Split('\n')[0], doc.Lines[0]);
    }

    [Fact]
    public void GetTypeDocument_BigClass_映射含BigMethod且offset0有行()
    {
        var doc = DocumentService.GetTypeDocument(Dll, $"{TestDataPaths.SamplesNamespace}.BigClass");
        Assert.True(doc.IsSuccess, doc.Error);

        var bigMethodToken = FindMethodToken(Dll, $"{TestDataPaths.SamplesNamespace}.BigClass", "BigMethod");
        Assert.True(bigMethodToken > 0, "未找到 BigMethod token");

        var tokenEntries = doc.Mapping.Where(e => e.MethodToken == bigMethodToken).ToList();
        Assert.NotEmpty(tokenEntries);
        // 入口 offset 0 应有映射
        var atZero = tokenEntries.FirstOrDefault(e => e.IlOffset == 0);
        Assert.NotNull(atZero);
        Assert.InRange(atZero.Line, 1, doc.Lines.Length);
        Assert.False(string.IsNullOrWhiteSpace(doc.Lines[atZero.Line - 1]));
    }

    [Fact]
    public void GetLineForIlOffset_BigMethod首条sp_返回对应行()
    {
        var doc = DocumentService.GetTypeDocument(Dll, $"{TestDataPaths.SamplesNamespace}.BigClass");
        var token = FindMethodToken(Dll, $"{TestDataPaths.SamplesNamespace}.BigClass", "BigMethod");
        var first = doc.Mapping.First(e => e.MethodToken == token);

        var line = DocumentService.GetLineForIlOffset(doc, token, first.IlOffset);
        Assert.Equal(first.Line, line);
    }

    [Fact]
    public void GetTypeDocument_BigMethod_映射为语句级密度高()
    {
        var doc = DocumentService.GetTypeDocument(Dll, $"{TestDataPaths.SamplesNamespace}.BigClass");
        var token = FindMethodToken(Dll, $"{TestDataPaths.SamplesNamespace}.BigClass", "BigMethod");

        // 探针实测 BigMethod 603 个可见 sequence point——语句级而非方法级（方法级只有 1 条）
        var count = doc.Mapping.Count(e => e.MethodToken == token);
        Assert.True(count > 50, $"BigMethod 映射条数过少（{count}），疑似未走位置回写管线");
    }

    /// <summary>运行时经元数据解析方法 token（不硬编码，避免测试与 TestData 漂移）。</summary>
    internal static int FindMethodToken(string dll, string typeFullName, string methodName)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            var full = MetadataNaming.FullName(mr, td);
            if (full != typeFullName) continue;
            foreach (var mh in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mh);
                if (mr.GetString(md.Name) == methodName)
                    return MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }
}
