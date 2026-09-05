using DotNetDebugger.Decompiler.Document;
using Xunit;

namespace DotNetDebugger.Decompiler.Tests;

/// <summary>DocumentService 反向查询（行 → 方法 token+ilStart，设断点用）测试。</summary>
public sealed class DocumentServiceReverseTests
{
    private static string Dll => TestDataPaths.TestSamplesDll;

    [Fact]
    public void GetIlStartForLine_BigMethod某行_返回对应token与ilStart()
    {
        var doc = DocumentService.GetTypeDocument(Dll, "ILSpyMcp.Samples.BigClass");
        Assert.True(doc.IsSuccess, doc.Error);

        var bigMethodToken = DocumentServiceTests.FindMethodToken(Dll, "ILSpyMcp.Samples.BigClass", "BigMethod");
        Assert.True(bigMethodToken > 0);

        // 取 BigMethod 一个映射条目的行做反向查询
        var entry = doc.Mapping.First(e => e.MethodToken == bigMethodToken);
        var result = DocumentService.GetIlStartForLine(doc, entry.Line);

        Assert.NotNull(result);
        Assert.Equal(bigMethodToken, result.Value.MethodToken);
        // 该行映射的 ilStart 应 ≤ 该条目的 IlOffset（行可能被多个区间覆盖，取最小 ilStart）
        Assert.True(result.Value.IlStart <= entry.IlOffset);
    }

    [Fact]
    public void GetIlStartForLine_无映射行_返回null()
    {
        var doc = DocumentService.GetTypeDocument(Dll, "ILSpyMcp.Samples.BigClass");
        Assert.True(doc.IsSuccess, doc.Error);

        // 找一个不在任何映射里的行（映射行集合之外的空行/注释行）
        var mappedLines = doc.Mapping.Select(e => e.Line).ToHashSet();
        int freeLine = Enumerable.Range(1, doc.Lines.Length).FirstOrDefault(l => !mappedLines.Contains(l));
        if (freeLine == 0) return; // 全行都被映射（不可能，但保守跳过）

        Assert.Null(DocumentService.GetIlStartForLine(doc, freeLine));
    }

    [Fact]
    public void GetLineForIlOffset_未命中_返回null()
    {
        var doc = DocumentService.GetTypeDocument(Dll, "ILSpyMcp.Samples.BigClass");
        Assert.True(doc.IsSuccess, doc.Error);

        // 超大 offset 不应命中
        Assert.Null(DocumentService.GetLineForIlOffset(doc, int.MaxValue, int.MaxValue));
    }

    [Fact]
    public void GetLineForIlOffset_区间外偏移_返回null()
    {
        var doc = DocumentService.GetTypeDocument(Dll, "ILSpyMcp.Samples.BigClass");
        Assert.True(doc.IsSuccess, doc.Error);

        var bigMethodToken = DocumentServiceTests.FindMethodToken(Dll, "ILSpyMcp.Samples.BigClass", "BigMethod");
        var entries = doc.Mapping.Where(e => e.MethodToken == bigMethodToken).OrderBy(e => e.IlOffset).ToList();
        Assert.NotEmpty(entries);

        // 最后一个区间之后 + 一个巨大偏移不应命中
        var lastEnd = entries[^1].EndOffset;
        Assert.Null(DocumentService.GetLineForIlOffset(doc, bigMethodToken, lastEnd + 1000));
    }
}
