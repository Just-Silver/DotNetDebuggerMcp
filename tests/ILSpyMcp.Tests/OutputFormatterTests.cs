using ILSpyMcp.Infrastructure;
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
    public void FormatHead_超限_只返回前200行并附截断提示()
    {
        var lines = Enumerable.Range(1, 250).Select(i => $"line{i}").ToList();
        var result = OutputFormatter.FormatHead(lines);
        Assert.Contains("已截断", result);
        Assert.Contains("250 行", result);
        Assert.StartsWith("1\tline1", result);
        Assert.Contains("200\tline200", result);
        Assert.DoesNotContain("201\tline201", result);
    }

    [Fact]
    public void FormatHead_恰好200行_不截断无截断提示()
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
    public void SliceLines_请求超过单次上限500行_截断并提示剩余范围()
    {
        var lines = Enumerable.Range(1, 600).Select(i => $"line{i}").ToList();
        var result = OutputFormatter.SliceLines(lines, 1, 600);
        Assert.Contains("已截断", result);
        Assert.Contains("500 行", result);
        Assert.Contains("1\tline1", result);
        Assert.DoesNotContain("501\tline501", result);
    }
}