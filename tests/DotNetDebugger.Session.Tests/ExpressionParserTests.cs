using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session;
using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>
/// P6 表达式子集 parser 纯单测（无进程）：合法文法逐类覆盖 + 非法输入全报错（报错含子集范围提示）。
/// 「能解析即已支持」——语法面即规格。
/// </summary>
public sealed class ExpressionParserTests
{
    // ---- 合法：字面量 ----

    [Theory]
    [InlineData("42", 42L, "42")]
    [InlineData("0", 0L, "0")]
    [InlineData("true", true, "True")]
    [InlineData("false", false, "False")]
    public void Parse_Literal(string text, object? expected, string display)
    {
        var node = Assert.IsType<LiteralNode>(ExpressionParser.Parse(text));
        Assert.Equal(expected, node.Value);
        Assert.Equal(display, node.Display);
    }

    [Fact]
    public void Parse_NullLiteral()
    {
        var node = Assert.IsType<LiteralNode>(ExpressionParser.Parse("null"));
        Assert.Null(node.Value);
        Assert.Equal("null", node.Display);
    }

    [Fact]
    public void Parse_StringLiteral_WithEscapes()
    {
        var node = Assert.IsType<LiteralNode>(ExpressionParser.Parse("\"a\\\"b\\\\c\""));
        Assert.Equal("a\"b\\c", node.Value);
        Assert.Equal("\"a\\\"b\\\\c\"", node.Display);
    }

    [Fact]
    public void Parse_LargeIntLiteral_BecomesLong()
    {
        var node = Assert.IsType<LiteralNode>(ExpressionParser.Parse("9999999999"));
        Assert.Equal(9999999999L, node.Value);
    }

    // ---- 合法：路径 ----

    [Fact]
    public void Parse_RootOnly()
    {
        var node = Assert.IsType<PathNode>(ExpressionParser.Parse("i"));
        Assert.Equal("i", node.Root);
        Assert.Empty(node.Segments);
    }

    [Fact]
    public void Parse_FieldChain()
    {
        var node = Assert.IsType<PathNode>(ExpressionParser.Parse("order.Customer.Name"));
        Assert.Equal("order", node.Root);
        Assert.Equal(
            [new PathSegment.Field("Customer"), new PathSegment.Field("Name")],
            node.Segments);
    }

    [Fact]
    public void Parse_MixedFieldAndIndex()
    {
        var node = Assert.IsType<PathNode>(ExpressionParser.Parse("list._items[3].Name"));
        Assert.Equal("list", node.Root);
        Assert.Equal(
            [
                new PathSegment.Field("_items"),
                new PathSegment.Index(3),
                new PathSegment.Field("Name"),
            ],
            node.Segments);
    }

    [Fact]
    public void Parse_ChainedIndexes()
    {
        var node = Assert.IsType<PathNode>(ExpressionParser.Parse("grid[1][2]"));
        Assert.Equal(
            [new PathSegment.Index(1), new PathSegment.Index(2)],
            node.Segments);
    }

    [Fact]
    public void Parse_MaxSegments_Accepted()
    {
        var text = string.Join("", Enumerable.Range(0, PathSegment.MaxSegments).Select(_ => ".a"));
        var node = Assert.IsType<PathNode>(ExpressionParser.Parse("root" + text));
        Assert.Equal(PathSegment.MaxSegments, node.Segments.Count);
    }

    // ---- 合法：一元与比较 ----

    [Fact]
    public void Parse_Not()
    {
        var node = Assert.IsType<NotNode>(ExpressionParser.Parse("!flag"));
        Assert.IsType<PathNode>(node.Operand);
    }

    [Fact]
    public void Parse_DoubleNot()
    {
        var node = Assert.IsType<NotNode>(ExpressionParser.Parse("!!flag"));
        Assert.IsType<NotNode>(node.Operand);
    }

    [Theory]
    [InlineData("==")]
    [InlineData("!=")]
    [InlineData("<")]
    [InlineData("<=")]
    [InlineData(">")]
    [InlineData(">=")]
    public void Parse_Comparison(string op)
    {
        var node = Assert.IsType<CompareNode>(ExpressionParser.Parse($"i {op} 5"));
        Assert.Equal(op, node.Op);
        Assert.IsType<PathNode>(node.Left);
        Assert.IsType<LiteralNode>(node.Right);
    }

    [Fact]
    public void Parse_NotBindsToUnary_NotAcrossComparison()
    {
        // 文法：Comparison := Unary op Unary —— ! 只作用于单个 Unary，不横跨比较
        var node = Assert.IsType<CompareNode>(ExpressionParser.Parse("!a == b"));
        Assert.IsType<NotNode>(node.Left);
        Assert.IsType<PathNode>(node.Right);
    }

    [Fact]
    public void Parse_ComparisonWithPaths()
    {
        var node = Assert.IsType<CompareNode>(ExpressionParser.Parse("a.B <= list[3].Age"));
        Assert.Equal("<=", node.Op);
    }

    // ---- 非法：全部报 ExpressionEvaluationException ----

    [Theory]
    [InlineData("")]                     // 空
    [InlineData("   ")]                  // 纯空白
    [InlineData("a + b")]                // 算术
    [InlineData("a - b")]
    [InlineData("a * b")]
    [InlineData("a / 2")]
    [InlineData("a % 2")]
    [InlineData("a.b()")]                // 方法调用
    [InlineData("f(1)")]                 // 调用
    [InlineData("x = 1")]                // 赋值
    [InlineData("a < b < c")]            // 链式比较
    [InlineData("a[-1]")]                // 负下标
    [InlineData("a[b]")]                 // 非整型下标
    [InlineData("a[")]                   // 未闭合
    [InlineData("a.")]                   // 悬空点
    [InlineData(".a")]                   // 前导点
    [InlineData("\"unterminated")]       // 字符串未闭合
    [InlineData("a @ b")]                // 非法符号
    [InlineData("()")]                   // 括号不在子集
    [InlineData("a b")]                  // 结尾多余内容
    [InlineData("1 2")]                  // 结尾多余内容
    [InlineData("a == ")]                // 比较缺右操作数
    [InlineData("99999999999999999999999")] // 超范围整数
    public void Parse_Invalid_ThrowsWithHint(string text)
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => ExpressionParser.Parse(text));
        Assert.False(string.IsNullOrWhiteSpace(ex.Message));
    }

    [Fact]
    public void Parse_TooManySegments_Throws()
    {
        var text = "root" + string.Join("", Enumerable.Range(0, PathSegment.MaxSegments + 1).Select(_ => ".a"));
        var ex = Assert.Throws<ExpressionEvaluationException>(() => ExpressionParser.Parse(text));
        Assert.Contains("超上限", ex.Message);
        Assert.Contains("8", ex.Message);
    }

    [Fact]
    public void Parse_ArithmeticError_MentionsSubset()
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => ExpressionParser.Parse("a + b"));
        Assert.Contains("不支持", ex.Message);
    }
}
