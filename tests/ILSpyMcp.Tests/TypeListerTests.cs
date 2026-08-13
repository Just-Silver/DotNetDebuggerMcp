using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// TypeLister 纯元数据类型列表的类别判定与编译器生成过滤用例。素材：生成的测试程序集（601 class + &lt;Module&gt;）与主程序集（interface/struct/嵌套/编译器生成类型）。
/// </summary>
public class TypeListerTests
{
    // 生成测试程序集：601 个 class（Class0001-0600 + BigClass）+ interface（IAnimal 等）+ 编译器生成的 <Module>
    private static readonly string TestSamplesPath = TestDataPaths.TestSamplesDll;
    // 主项目程序集：含 struct（ToolPipelineResult 等）、enum、嵌套类型与编译器生成类型
    private static readonly string MainAssemblyPath = typeof(OutputFormatter).Assembly.Location;

    /// <summary>
    /// 打开程序集并保持 fs/pe 存活的情况下执行元数据断言。
    /// </summary>
    private static void WithReader(string path, Action<MetadataReader> action)
    {
        using var fs = File.OpenRead(path);
        using var pe = new PEReader(fs);
        action(pe.GetMetadataReader());
    }

    [Fact]
    public void TestSamples_列出class_含样本类型且过滤编译器生成类型()
    {
        WithReader(TestSamplesPath, reader =>
        {
            var names = TypeLister.ListTypes(reader, "c").Select(e => e.FullName).ToList();

            Assert.Contains("ILSpyMcp.Samples.Class0001", names);
            Assert.Contains("ILSpyMcp.Samples.BigClass", names);
            Assert.DoesNotContain("<Module>", names); // 编译器生成类型被过滤
            Assert.True(names.Count > 500, $"应为 601 个 class，实际 {names.Count}");
        });
    }

    [Fact]
    public void TestSamples_列出class_不包含名字含尖括号的类型()
    {
        WithReader(TestSamplesPath, reader =>
        {
            var list = TypeLister.ListTypes(reader, "c");
            Assert.DoesNotContain(list, e => e.FullName.Contains('<'));
        });
    }

    [Fact]
    public void 列出class与interface_类别字母正确()
    {
        WithReader(TestSamplesPath, reader =>
        {
            var entries = TypeLister.ListTypes(reader, "ci").ToDictionary(e => e.FullName, e => e.Category);

            Assert.Equal('i', entries["ILSpyMcp.Samples.IAnimal"]);
            Assert.Equal('c', entries["ILSpyMcp.Samples.BigClass"]);
        });
    }

    [Fact]
    public void 主程序集_列出struct_含ToolPipelineResult()
    {
        WithReader(MainAssemblyPath, reader =>
        {
            var entries = TypeLister.ListTypes(reader, "s").ToDictionary(e => e.FullName, e => e.Category);

            Assert.Equal('s', entries["ILSpyMcp.Pipeline.ToolPipelineResult"]);
        });
    }

    [Fact]
    public void 类别与全名配对正确_所有class条目类别为c()
    {
        WithReader(MainAssemblyPath, reader =>
        {
            var list = TypeLister.ListTypes(reader, "c");
            Assert.NotEmpty(list);
            Assert.All(list, e => Assert.Equal('c', e.Category));
        });
    }

    [Fact]
    public void 主程序集_列出enum_含DecompileKind()
    {
        // 主程序集原无 enum/delegate，进程内改造后新增 ILSpyMcp.Pipeline.DecompileKind 枚举，验证类别字母 e
        WithReader(MainAssemblyPath, reader =>
        {
            var entries = TypeLister.ListTypes(reader, "e").ToDictionary(e => e.FullName, e => e.Category);

            Assert.Equal('e', entries["ILSpyMcp.Pipeline.DecompileKind"]);
        });
    }

    [Fact]
    public void 测试程序集_名称子串过滤_忽略大小写命中泛型类型()
    {
        WithReader(TestSamplesPath, reader =>
        {
            // "genericbox" 小写应忽略大小写命中 ILSpyMcp.Samples.GenericBox`1
            var names = TypeLister.ListTypes(reader, "c", "genericbox").Select(e => e.FullName).ToList();

            Assert.Contains("ILSpyMcp.Samples.GenericBox`1", names);
        });
    }

    [Fact]
    public void 名称子串过滤_子串命中即可()
    {
        WithReader(TestSamplesPath, reader =>
        {
            // 子串 "Generic" 应命中多个含该片段的类型（部分匹配即可）
            var names = TypeLister.ListTypes(reader, "c", "Generic").Select(e => e.FullName).ToList();

            Assert.Contains("ILSpyMcp.Samples.GenericBox`1", names);
            Assert.Contains("ILSpyMcp.Samples.GenericCaller", names);
            Assert.Contains("ILSpyMcp.Samples.GenericHelper", names);
        });
    }

    [Fact]
    public void 名称子串过滤_无匹配返回空()
    {
        WithReader(TestSamplesPath, reader =>
        {
            Assert.Empty(TypeLister.ListTypes(reader, "c", "不存在的类型名XYZ"));
        });
    }

    [Fact]
    public void 空或null名称子串_不过滤()
    {
        WithReader(TestSamplesPath, reader =>
        {
            var unfiltered = TypeLister.ListTypes(reader, "c");
            var withEmpty = TypeLister.ListTypes(reader, "c", "");
            var withNull = TypeLister.ListTypes(reader, "c", null);

            Assert.Equal(unfiltered, withEmpty);
            Assert.Equal(unfiltered, withNull);
        });
    }

    [Fact]
    public void CountCategories_返回5类别计数_ByCategory总和等于实体数且Total为两者之和()
    {
        WithReader(TestSamplesPath, reader =>
        {
            var (byCategory, gen, total) = TypeLister.CountCategories(reader);

            // 5 类别键齐全
            foreach (var category in new[] { 'c', 'i', 's', 'd', 'e' }) Assert.Contains(category, byCategory.Keys);
            // ByCategory 总和 = 实体数 = total - gen
            var entity = byCategory.Values.Sum();
            Assert.Equal(entity, total - gen);
            Assert.Equal(entity + gen, total);
        });
    }

    [Fact]
    public void CountCategories_类别计数与ListTypes结果一致()
    {
        WithReader(TestSamplesPath, reader =>
        {
            var (byCategory, _, _) = TypeLister.CountCategories(reader);

            foreach (var category in new[] { 'c', 'i', 's', 'd', 'e' })
            {
                var listed = TypeLister.ListTypes(reader, category.ToString()).Count;
                Assert.Equal(listed, byCategory[category]);
            }
        });
    }

    [Fact]
    public void CountCategories_测试程序集_class超过600且含interface()
    {
        WithReader(TestSamplesPath, reader =>
        {
            var (byCategory, gen, total) = TypeLister.CountCategories(reader);

            Assert.True(byCategory['c'] >= 600, $"class 应 >= 600，实际 {byCategory['c']}");
            Assert.True(byCategory['i'] >= 1, $"interface 应 >= 1，实际 {byCategory['i']}");
            Assert.True(gen >= 0, $"编译器生成数应 >= 0，实际 {gen}");
            Assert.True(total > byCategory['c'], "类型总数应包含编译器生成类型，大于 class 数");
        });
    }

    [Fact]
    public void CountCategories_主程序集_含struct与enum计数()
    {
        WithReader(MainAssemblyPath, reader =>
        {
            var (byCategory, _, _) = TypeLister.CountCategories(reader);

            Assert.True(byCategory['s'] >= 1, $"struct 应 >= 1，实际 {byCategory['s']}");
            Assert.True(byCategory['e'] >= 1, $"enum 应 >= 1，实际 {byCategory['e']}");
            Assert.True(byCategory['c'] >= 1, $"class 应 >= 1，实际 {byCategory['c']}");
        });
    }
}
