using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session;
using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>
/// P6 AST 求值语义纯单测（无进程）：字面量/一元 !/比较的类型规则（数值统一 decimal、字符串 Ordinal、
/// 布尔仅 ==/!=、null 只支持 ==/!=、非标量拒绝）。不含 PathNode——路径求值依赖进程，由 Engine/宿主集成测试覆盖
/// （session 参数在无 PathNode 的用例中不会被触碰，传 null 安全）。
/// </summary>
public sealed class ExpressionEvaluatorTests
{
    private static Task<DebugEvalResult> EvalAsync(string expression)
        => ExpressionEvaluator.EvaluateAsync(session: null!, threadId: 0, expression);

    private static void AssertBool(DebugEvalResult result, bool expected)
    {
        Assert.Equal(DebugEvalKind.Scalar, result.Kind);
        Assert.Equal(expected, Assert.IsType<bool>(result.ScalarValue));
        Assert.Equal(expected ? "True" : "False", result.Display);
        Assert.Equal("System.Boolean", result.TypeName);
    }

    [Fact]
    public async Task Evaluate_NotLiterals()
    {
        AssertBool(await EvalAsync("!true"), false);
        AssertBool(await EvalAsync("!false"), true);
        AssertBool(await EvalAsync("!!true"), true);
    }

    [Theory]
    [InlineData("1 == 1", true)]
    [InlineData("1 == 2", false)]
    [InlineData("1 != 2", true)]
    [InlineData("2 > 1", true)]
    [InlineData("2 >= 2", true)]
    [InlineData("1 < 2", true)]
    [InlineData("1 <= 0", false)]
    [InlineData("9999999999 == 9999999999", true)] // long 字面量与 int 字面量同过 decimal 归一比较
    public async Task Evaluate_NumericComparison(string expression, bool expected)
        => AssertBool(await EvalAsync(expression), expected);

    [Fact]
    public async Task Evaluate_StringComparison_Ordinal()
    {
        AssertBool(await EvalAsync("\"abc\" == \"abc\""), true);
        AssertBool(await EvalAsync("\"abc\" == \"abd\""), false);
        AssertBool(await EvalAsync("\"abc\" < \"abd\""), true);
        AssertBool(await EvalAsync("\"b\" >= \"a\""), true);
        AssertBool(await EvalAsync("\"a\" != \"b\""), true);
    }

    [Fact]
    public async Task Evaluate_NullComparison_OnlyEquality()
    {
        AssertBool(await EvalAsync("null == null"), true);
        AssertBool(await EvalAsync("null != null"), false);
        var err = await Assert.ThrowsAsync<ExpressionEvaluationException>(() => EvalAsync("null < null"));
        Assert.Contains("null 只支持", err.Message);
    }

    [Fact]
    public async Task Evaluate_BoolComparison_OnlyEquality()
    {
        AssertBool(await EvalAsync("true == true"), true);
        AssertBool(await EvalAsync("true != false"), true);
        var ordering = await Assert.ThrowsAsync<ExpressionEvaluationException>(() => EvalAsync("true < false"));
        Assert.Contains("布尔值只支持", ordering.Message);
        var mixed = await Assert.ThrowsAsync<ExpressionEvaluationException>(() => EvalAsync("true == 1"));
        Assert.Contains("布尔与", mixed.Message);
    }

    [Fact]
    public async Task Evaluate_MixedScalarTypes_Rejected()
    {
        var err = await Assert.ThrowsAsync<ExpressionEvaluationException>(() => EvalAsync("1 == \"a\""));
        Assert.Contains("不支持的比较", err.Message);
    }

    [Fact]
    public async Task Evaluate_NotRequiresBool()
    {
        var err = await Assert.ThrowsAsync<ExpressionEvaluationException>(() => EvalAsync("!1"));
        Assert.Contains("须为布尔标量", err.Message);
    }

    [Fact]
    public async Task Evaluate_LiteralResult_Shape()
    {
        var s = await EvalAsync("\"hi\"");
        Assert.Equal(DebugEvalKind.Scalar, s.Kind);
        Assert.Equal("\"hi\"", s.Display);
        Assert.Equal("System.String", s.TypeName);
        Assert.Equal("hi", Assert.IsType<string>(s.ScalarValue));

        var n = await EvalAsync("null");
        Assert.Equal(DebugEvalKind.Null, n.Kind);
        Assert.Equal("null", n.Display);
        Assert.Null(n.ScalarValue);
    }
}
