using DotNetDebugger.Web.Services;
using Xunit;

namespace DotNetDebugger.Web.Tests;

/// <summary>DocumentStore 服务端测试：反编译加载 + 缓存 + 停点行映射（纯服务端可测部分）。</summary>
public sealed class DocumentStoreTests
{
    [Fact]
    public void GetOrLoad_BigClass_成功且含映射()
    {
        var store = new DocumentStore();
        var doc = store.GetOrLoad(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass");

        Assert.True(doc.IsSuccess, doc.Error);
        Assert.Contains("class BigClass", doc.Text);
        Assert.NotEmpty(doc.Mapping);
    }

    [Fact]
    public void GetOrLoad_同类型两次_命中缓存()
    {
        var store = new DocumentStore();
        var doc1 = store.GetOrLoad(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass");
        var doc2 = store.GetOrLoad(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass");

        Assert.True(doc1.IsSuccess);
        Assert.Same(doc1, doc2);   // 同一实例 = 缓存命中
        Assert.Equal(1, store.Count);
    }

    [Fact]
    public void GetOrLoad_未找到类型_返回Error且不入缓存()
    {
        var store = new DocumentStore();
        var doc = store.GetOrLoad(TestDataPaths.TestSamplesDll, "No.Such.Type");

        Assert.False(doc.IsSuccess);
        Assert.Contains("未找到类型", doc.Error);
        Assert.Equal(0, store.Count);   // 失败不入缓存
    }

    [Fact]
    public void GetStopLine_首映射offset_返回行号()
    {
        var store = new DocumentStore();
        var doc = store.GetOrLoad(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass");
        var first = doc.Mapping.First();

        var line = DocumentStore.GetStopLine(doc, first.MethodToken, first.IlOffset);
        Assert.Equal(first.Line, line);
    }

    [Fact]
    public void GetIlStartAtLine_反向映射_返回token()
    {
        var store = new DocumentStore();
        var doc = store.GetOrLoad(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass");
        var entry = doc.Mapping.First();

        var result = DocumentStore.GetIlStartAtLine(doc, entry.Line);
        Assert.NotNull(result);
        Assert.Equal(entry.MethodToken, result.Value.MethodToken);
    }

    [Fact]
    public void Clear_清空缓存()
    {
        var store = new DocumentStore();
        store.GetOrLoad(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass");
        Assert.Equal(1, store.Count);

        store.Clear();
        Assert.Equal(0, store.Count);
    }
}
