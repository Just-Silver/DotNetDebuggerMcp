using ILSpyMcp.Formatting;
using Xunit;

namespace ILSpyMcp.Tests;

public class OutputFormatterTests
{
    [Theory]
    [InlineData("200-400", 200, 400)]
    [InlineData("1-1", 1, 1)]
    [InlineData(" 10-20 ", 10, 20)]
    public void ParseLines_合法范围_返回起止行号(string input, int start, int end)
    {
        var (s, e, error) = OutputFormatter.ParseLines(input);
        Assert.Null(error);
        Assert.Equal(start, s);
        Assert.Equal(end, e);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("200")]
    [InlineData("200-")]
    [InlineData("-400")]
    [InlineData("20-10-5")]
    public void ParseLines_非法格式_返回错误提示(string input)
    {
        var (_, _, error) = OutputFormatter.ParseLines(input);
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseLines_起始为0_返回错误提示()
    {
        var (_, _, error) = OutputFormatter.ParseLines("0-5");
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseLines_起始大于结束_返回错误提示()
    {
        var (_, _, error) = OutputFormatter.ParseLines("5-2");
        Assert.NotNull(error);
    }

    [Fact]
    public void ParseLines_行号超出int范围_返回错误提示而非抛异常()
    {
        var (_, _, error) = OutputFormatter.ParseLines("99999999999999-99999999999999");
        Assert.NotNull(error);
    }

    [Fact]
    public void SplitLines_CRLF_换行_去掉回车符残留()
    {
        var lines = OutputFormatter.SplitLines("a\r\nb\r\n");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void SplitLines_LF_换行_正常拆分()
    {
        var lines = OutputFormatter.SplitLines("a\nb\n");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void SplitLines_空字符串_返回空列表()
    {
        Assert.Empty(OutputFormatter.SplitLines(""));
    }

    [Fact]
    public void SplitLines_中间空行保留_末尾空行去除()
    {
        var lines = OutputFormatter.SplitLines("a\n\nb\n");
        Assert.Equal(new[] { "a", "", "b" }, lines);
    }

    [Fact]
    public void SplitLines_双换行结尾_去除全部末尾空行()
    {
        var lines = OutputFormatter.SplitLines("a\n\n");
        Assert.Equal(new[] { "a" }, lines);
    }

    [Fact]
    public void SplitLines_多个换行结尾_去除全部末尾空行()
    {
        var lines = OutputFormatter.SplitLines("a\nb\n\n\n");
        Assert.Equal(new[] { "a", "b" }, lines);
    }

    [Fact]
    public void FormatHead_未超限_返回全部行且无截断提示()
    {
        var lines = Enumerable.Range(1, 3).Select(i => $"line{i}").ToList();
        var result = OutputFormatter.FormatHead(lines);
        Assert.Equal("1\tline1\n2\tline2\n3\tline3", result);
        Assert.DoesNotContain("已截断", result);
    }

    [Fact]
    public void FormatHead_长行按字符预算截断_返回行数远小于总量并附截断提示()
    {
        var lines = Enumerable.Range(1, 250).Select(i => new string('x', 100)).ToList();
        var result = OutputFormatter.FormatHead(lines);
        Assert.Contains("已截断", result);
        Assert.Contains("KB", result);
        Assert.Contains("单次最多约 32 KB", result);
        Assert.StartsWith("1\t", result);
        Assert.Contains("78\t", result);
        Assert.DoesNotContain("\n79\t", result);
    }

    [Fact]
    public void FormatHead_恰好预算内_不截断无截断提示()
    {
        var lines = Enumerable.Range(1, 200).Select(i => $"line{i}").ToList();
        var result = OutputFormatter.FormatHead(lines);
        Assert.DoesNotContain("已截断", result);
        Assert.Contains("200\tline200", result);
    }

    [Fact]
    public void SliceLines_正常切片_行号基于原始位置()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line{i}").ToList();
        var result = OutputFormatter.SliceLines(lines, 3, 5);
        Assert.Equal("3\tline3\n4\tline4\n5\tline5", result);
    }

    [Fact]
    public void SliceLines_起始超出总行数_返回提示()
    {
        var lines = Enumerable.Range(1, 3).Select(i => $"line{i}").ToList();
        var result = OutputFormatter.SliceLines(lines, 5, 6);
        Assert.Contains("超出总行数", result);
    }

    [Fact]
    public void SliceLines_结束超出总行数_截断到末尾()
    {
        var lines = Enumerable.Range(1, 3).Select(i => $"line{i}").ToList();
        var result = OutputFormatter.SliceLines(lines, 1, 10);
        Assert.Equal("1\tline1\n2\tline2\n3\tline3", result);
    }

    [Fact]
    public void SliceLines_请求超过单次字符预算_截断并提示剩余范围()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => new string('x', 100)).ToList();
        var result = OutputFormatter.SliceLines(lines, 1, 1000);
        Assert.Contains("已截断", result);
        Assert.Contains("已返回 1-313（313 行）", result);
        Assert.Contains("剩余 314-1000", result);
        Assert.StartsWith("1\t", result);
        Assert.DoesNotContain("\n314\t", result);
    }

    [Fact]
    public void SliceLines_请求范围超出总行数且超预算_剩余提示用真实行数()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => new string('x', 100)).ToList();
        var result = OutputFormatter.SliceLines(lines, 1, 5000);
        Assert.Contains("已返回 1-313（313 行）", result);
        Assert.Contains("剩余 314-1000", result);
        Assert.DoesNotContain("314-5000", result);
    }

    [Fact]
    public void SliceLines_请求范围超出总行数但不足预算_返回全部不报截断()
    {
        var lines = Enumerable.Range(1, 100).Select(i => $"line{i}").ToList();
        var result = OutputFormatter.SliceLines(lines, 1, 1000);
        Assert.StartsWith("1\tline1", result);
        Assert.EndsWith("100\tline100", result);
        Assert.DoesNotContain("已截断", result);
    }

    [Fact]
    public void SliceLines_短行密集_行数软上限先触发()
    {
        var lines = Enumerable.Range(1, 2000).Select(i => new string('a', 5)).ToList();
        var result = OutputFormatter.SliceLines(lines, 1, 2000);
        Assert.Contains("已截断", result);
        Assert.Contains("已返回 1-1900（1900 行）", result);
        Assert.Contains("剩余 1901-2000", result);
        Assert.DoesNotContain("\n1901\t", result);
    }

    [Fact]
    public void SliceLines_单行超预算_至少返回1行()
    {
        var lines = new List<string> { new string('a', 100_000) };
        var result = OutputFormatter.SliceLines(lines, 1, 100);
        Assert.StartsWith("1\t", result);
        Assert.Contains(new string('a', 100_000), result);
        Assert.DoesNotContain("已截断", result);
    }

    [Fact]
    public void Format_带context_前置头部信息块()
    {
        var lines = Enumerable.Range(1, 3).Select(i => $"line{i}").ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.String");

        var result = OutputFormatter.Format(lines, "", ctx);

        Assert.StartsWith("程序集: D:\\a\\b.dll\n目标:   类型 System.String\n总行数:   3 行\n当前输出: 1-3（3 行，0.0 KB）\n---\n1\tline1", result);
        Assert.EndsWith("1\tline1\n2\tline2\n3\tline3", result);
    }

    [Fact]
    public void Format_带context且超限_头部标注总量与截断范围_截断提示不含重复行数()
    {
        var lines = Enumerable.Range(1, 250).Select(i => new string('x', 100)).ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.String");

        var result = OutputFormatter.Format(lines, "", ctx);

        Assert.Contains("总行数:   250 行", result);
        Assert.Contains("当前输出: 1-78（78 行，7.9 KB，已截断：超过默认预算约 8 KB）", result);
        Assert.Contains("已截断", result);
        Assert.DoesNotContain("已截断：共 250 行", result);
    }

    [Fact]
    public void Format_带context且lines切片_头部标注总量与当前范围()
    {
        var lines = Enumerable.Range(1, 600).Select(i => $"line{i}").ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.String");

        var result = OutputFormatter.Format(lines, "200-400", ctx);

        Assert.Contains("总行数:   600 行", result);
        Assert.Contains("当前输出: 200-400（201 行，2.4 KB）", result);
        Assert.DoesNotContain("剩余:", result);
        Assert.Contains("200\tline200", result);
    }

    [Fact]
    public void Format_带context且lines越界_头部标注当前输出无效()
    {
        var lines = Enumerable.Range(1, 3).Select(i => $"line{i}").ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.String");

        var result = OutputFormatter.Format(lines, "5-6", ctx);

        Assert.Contains("总行数:   3 行", result);
        Assert.Contains("当前输出: 无效（起始行 5 超出总行数 3）", result);
        Assert.Contains("超出总行数", result);
    }

    [Fact]
    public void Format_带context且截断_剩余可一次获取()
    {
        var lines = Enumerable.Range(1, 200).Select(i => new string('x', 100)).ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.String");

        var result = OutputFormatter.Format(lines, "", ctx);

        Assert.Contains("剩余:     122 行 / 约 12.5 KB，可一次获取：lines=\"79-200\"", result);
    }

    [Fact]
    public void Format_带context且截断_建议的剩余范围照抄不二次截断()
    {
        var lines = Enumerable.Range(1, 200).Select(i => new string('x', 100)).ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.String");

        var result = OutputFormatter.Format(lines, "", ctx);

        Assert.Contains("lines=\"79-200\"", result); // 头部建议的剩余范围
        var sliced = OutputFormatter.SliceLines(lines, 79, 200); // 照抄建议的 lines 参数
        Assert.DoesNotContain("已截断", sliced);
        Assert.Contains("79\t", sliced);
        Assert.Contains("200\t", sliced); // 尾部行到达 200
    }

    [Fact]
    public void Format_带context且截断_剩余需分次获取()
    {
        var lines = Enumerable.Range(1, 1000).Select(i => new string('x', 100)).ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.String");

        var result = OutputFormatter.Format(lines, "", ctx);

        Assert.Contains("剩余:     922 行 / 约 94.5 KB，超过单次预算（约 32 KB），需分次获取：先用 lines=\"79-390\"", result);
    }

    [Fact]
    public void Format_带context_未截断_无剩余行()
    {
        var lines = Enumerable.Range(1, 3).Select(i => $"line{i}").ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.String");

        var result = OutputFormatter.Format(lines, "", ctx);

        Assert.DoesNotContain("剩余:", result);
        Assert.DoesNotContain("已截断", result);
    }

    [Fact]
    public void Format_默认短类型全量返回_无截断无剩余()
    {
        var lines = Enumerable.Range(1, 10).Select(i => $"line{i}").ToList();

        var result = OutputFormatter.Format(lines, "");

        Assert.Equal("1\tline1\n2\tline2\n3\tline3\n4\tline4\n5\tline5\n6\tline6\n7\tline7\n8\tline8\n9\tline9\n10\tline10", result);
        Assert.DoesNotContain("已截断", result);
    }

    [Fact]
    public void Format_带listing_context_空结果_头部标注匹配实体与总行数()
    {
        var ctx = new FormatContext(@"D:\a\b.dll", "实体类别 c(class)", IsListing: true);

        var result = OutputFormatter.Format(new List<string>(), "", ctx);

        Assert.Contains("匹配实体: 0 个", result);
        Assert.Contains("总行数:   0 行", result);
        Assert.Contains("当前输出: 无", result);
        Assert.EndsWith("---", result);
        Assert.DoesNotContain("1\t", result);
    }

    [Fact]
    public void Format_带listing_context_非空结果_匹配实体与总行数并存()
    {
        var lines = Enumerable.Range(1, 2).Select(i => $"C{i}").ToList();
        var ctx = new FormatContext(@"D:\a\b.dll", "实体类别 c(class)", IsListing: true);

        var result = OutputFormatter.Format(lines, "", ctx);

        Assert.Contains("匹配实体: 2 个", result);
        Assert.Contains("总行数:   2 行", result);
        Assert.Contains("当前输出: 1-2（2 行，0.0 KB）", result);
    }

    [Fact]
    public void Format_无context_保持原有行为不加头部()
    {
        var lines = Enumerable.Range(1, 3).Select(i => $"line{i}").ToList();

        var result = OutputFormatter.Format(lines, "");

        Assert.Equal("1\tline1\n2\tline2\n3\tline3", result);
        Assert.DoesNotContain("程序集:", result);
    }

    [Fact]
    public void ContainsIlUnresolved_含IL注释_返回true()
    {
        var lines = new List<string> { "public void M()", "    //IL_0001: nop", "}" };
        Assert.True(OutputFormatter.ContainsIlUnresolved(lines));
    }

    [Fact]
    public void ContainsIlUnresolved_无IL注释_返回false()
    {
        var lines = new List<string> { "public void M()", "}" };
        Assert.False(OutputFormatter.ContainsIlUnresolved(lines));
    }

    [Fact]
    public void Format_含IL未解析注释_头部分隔线前追加提示()
    {
        var lines = new List<string> { "public void M()", "    //IL_0001: call [dynamic]" };
        var ctx = new FormatContext(@"D:\a\b.dll", "类型 System.X");

        var result = OutputFormatter.Format(lines, "", ctx);

        Assert.Contains("提示: 输出含 //IL_ 未解析注释（动态类型/异常路径），仅供结构参考", result);
        Assert.Contains("提示: 输出含 //IL_ 未解析注释（动态类型/异常路径），仅供结构参考\n---", result); // 提示在分隔线之前
    }

    [Fact]
    public void Format_无context_含IL注释也不加提示()
    {
        var lines = new List<string> { "    //IL_0001: nop" };

        var result = OutputFormatter.Format(lines, "");

        Assert.Equal("1\t    //IL_0001: nop", result); // 无头部时提示不生效
        Assert.DoesNotContain("仅供结构参考", result);
    }
}