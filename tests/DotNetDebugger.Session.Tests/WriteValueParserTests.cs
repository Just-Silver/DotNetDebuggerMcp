using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session;
using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>
/// W1 WriteValueParser 纯内存单测（无进程）：value 文本 → DebugWriteValue 文法判定。
/// null/bool/数字（整数/小数/科学计数/后缀/0x）→ Scalar（原文，不归一化）；其余按 ExpressionParser 路径文法 → CopyPath；
/// 空/杂串 → ExpressionEvaluationException（中文，宿主直接展示）。
/// </summary>
public sealed class WriteValueParserTests
{
    // ---- 字面量 ----

    [Fact]
    public void Parse_Null()
    {
        Assert.IsType<DebugWriteValue.Null>(WriteValueParser.Parse("null"));
    }

    [Theory]
    [InlineData("true")]
    [InlineData("false")]
    public void Parse_BoolScalar(string text)
    {
        var v = Assert.IsType<DebugWriteValue.Scalar>(WriteValueParser.Parse(text));
        Assert.Equal(text, v.Text); // 原文透传（不归一化）
    }

    [Theory]
    [InlineData("99")]
    [InlineData("-5")]
    [InlineData("1000m")]
    [InlineData("0.5")]
    [InlineData("1.5e3")]
    [InlineData("0x1F")]
    [InlineData("3.5d")]
    [InlineData("+7")]
    public void Parse_NumberLike_ScalarKeepsOriginalText(string text)
    {
        var v = Assert.IsType<DebugWriteValue.Scalar>(WriteValueParser.Parse(text));
        Assert.Equal(text, v.Text); // 数值不做归一化，原样传引擎按目标类型转换
    }

    // ---- 路径（CopyPath）----

    [Fact]
    public void Parse_RootOnlyPath()
    {
        var v = Assert.IsType<DebugWriteValue.CopyPath>(WriteValueParser.Parse("bag2"));
        Assert.Equal("bag2", v.Root);
        Assert.Empty(v.Segments);
    }

    [Fact]
    public void Parse_FieldChain()
    {
        var v = Assert.IsType<DebugWriteValue.CopyPath>(WriteValueParser.Parse("cfg.Backup"));
        Assert.Equal("cfg", v.Root);
        Assert.Equal([new PathSegment.Field("Backup")], v.Segments);
    }

    [Fact]
    public void Parse_ArrayIndex()
    {
        var v = Assert.IsType<DebugWriteValue.CopyPath>(WriteValueParser.Parse("arr[0]"));
        Assert.Equal("arr", v.Root);
        Assert.Equal([new PathSegment.Index(0)], v.Segments);
    }

    [Fact]
    public void Parse_MixedSegments()
    {
        var v = Assert.IsType<DebugWriteValue.CopyPath>(WriteValueParser.Parse("a.b[2].C"));
        Assert.Equal("a", v.Root);
        Assert.Equal([new PathSegment.Field("b"), new PathSegment.Index(2), new PathSegment.Field("C")], v.Segments);
    }

    // ---- 非法输入 ----

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void Parse_Empty_ThrowsChinese(string text)
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => WriteValueParser.Parse(text));
        Assert.Contains("value 为空", ex.Message);
    }

    [Theory]
    [InlineData("a + b")]
    [InlineData("123abc")]
    [InlineData("!flag")]
    public void Parse_NonValueNonPath_ThrowsChinese(string text)
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => WriteValueParser.Parse(text));
        Assert.True(ex.Message.Length > 0);
    }
}
