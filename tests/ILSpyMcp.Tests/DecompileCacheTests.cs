using ILSpyMcp.Caching;
using System.Reflection;
using Xunit;

namespace ILSpyMcp.Tests;

public class DecompileCacheTests
{
    [Fact]
    public void Put后Get_命中并返回相同行()
    {
        var cache = new DecompileCache();
        var key = new CacheKey(@"C:\a.dll", "fp1", "sig");
        var lines = Lines("a", "b");
        cache.Put(key, lines);
        Assert.Same(lines, cache.Get(key));
    }

    [Fact]
    public void 未Put的key_Get返回null()
    {
        var cache = new DecompileCache();
        Assert.Null(cache.Get(new CacheKey(@"C:\a.dll", "fp1", "sig")));
    }

    [Fact]
    public void 内存超限_驱逐最久未访问条目()
    {
        var cache = new DecompileCache(maxBytes: 12);
        var keyA = new CacheKey(@"C:\a.dll", "fp1", "sigA");
        var keyB = new CacheKey(@"C:\a.dll", "fp1", "sigB");
        var keyC = new CacheKey(@"C:\a.dll", "fp1", "sigC");

        cache.Put(keyA, Lines("aaaaa"));
        cache.Put(keyB, Lines("bbbbb"));
        cache.Get(keyA);
        cache.Put(keyC, Lines("ccccc"));

        Assert.NotNull(cache.Get(keyA));
        Assert.Null(cache.Get(keyB));
        Assert.NotNull(cache.Get(keyC));
    }

    [Fact]
    public void 程序集更新指纹变化_同路径同签名旧条目被清理()
    {
        var cache = new DecompileCache();
        var oldKey = new CacheKey(@"C:\a.dll", "old-fp", "sig");
        var newKey = new CacheKey(@"C:\a.dll", "new-fp", "sig");

        cache.Put(oldKey, Lines("old"));
        cache.Put(newKey, Lines("new"));

        Assert.Null(cache.Get(oldKey));
        Assert.NotNull(cache.Get(newKey));
    }

    [Fact]
    public void 不同路径相同签名_互不清理()
    {
        var cache = new DecompileCache();
        var key1 = new CacheKey(@"C:\a.dll", "fp", "sig");
        var key2 = new CacheKey(@"C:\b.dll", "fp", "sig");

        cache.Put(key1, Lines("a"));
        cache.Put(key2, Lines("b"));

        Assert.NotNull(cache.Get(key1));
        Assert.NotNull(cache.Get(key2));
    }

    [Fact]
    public void BuildKey_路径绝对化并包含指纹与签名()
    {
        var cache = new DecompileCache();
        var key = cache.BuildKey(Assembly.GetExecutingAssembly().Location, "sig");
        var full = Path.GetFullPath(Assembly.GetExecutingAssembly().Location).ToLowerInvariant();
        Assert.Equal(full, key.AssemblyPath);
        Assert.Equal("sig", key.Signature);
        Assert.NotEqual("", key.Fingerprint);
    }

    [Fact]
    public void BuildKey_Windows路径大小写_归一为同一缓存键()
    {
        var cache = new DecompileCache();
        var upper = cache.BuildKey(@"C:\Path\A.dll", "sig");
        var lower = cache.BuildKey(@"c:\path\a.dll", "sig");

        Assert.Equal(upper.AssemblyPath, lower.AssemblyPath);
        Assert.Equal(upper, lower);
    }

    [Fact]
    public void 并发GetPut_不抛异常且结果完整()
    {
        var cache = new DecompileCache();
        var lines = Lines(Enumerable.Range(1, 50).Select(i => $"line{i}").ToArray());
        var key = new CacheKey(@"C:\a.dll", "fp", "sig");

        Parallel.For(0, 200, i => cache.Put(key, lines));
        Parallel.For(0, 200, i =>
        {
            var result = cache.Get(key);
            Assert.NotNull(result);
            Assert.Equal(50, result!.Count);
        });
    }

    [Fact]
    public void 并发Put不同key_不抛异常()
    {
        var cache = new DecompileCache();
        var lines = Lines("x");

        Parallel.For(0, 200, i =>
        {
            var key = new CacheKey(@"C:\a.dll", "fp", $"sig{i}");
            cache.Put(key, lines);
            cache.Get(key);
        });
    }

    private static List<string> Lines(params string[] lines) => lines.ToList();
}