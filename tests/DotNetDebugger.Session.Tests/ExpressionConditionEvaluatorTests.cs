using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session;
using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>
/// P7 条件求值器纯单测（无进程，resolver 用桩）：true/false 判定、PathNode 走 resolver、
/// 非布尔条件拒绝、解析错误透传。Engine 侧命中接线与宿主反馈由 Engine/宿主集成测试覆盖。
/// </summary>
public sealed class ExpressionConditionEvaluatorTests
{
    /// <summary>桩路径解析器：rootName → 预置结果（未预置的根视作测试错误）。</summary>
    private static PathValueResolver StubResolver(params (string Root, DebugEvalResult Result)[] values)
    {
        var map = values.ToDictionary(v => v.Root, v => v.Result);
        return (tid, root, segs) => map.TryGetValue(root, out var r)
            ? r
            : throw new InvalidOperationException($"桩未预置根 {root}");
    }

    private static DebugEvalResult Scalar(object value, string typeName) =>
        new(value.ToString()!, typeName, DebugEvalKind.Scalar, null, value);

    private static bool Eval(string condition, PathValueResolver? resolver = null)
        => ExpressionConditionEvaluator.Instance.Evaluate(0, condition, resolver ?? StubResolver());

    [Theory]
    [InlineData("1 == 1", true)]
    [InlineData("1 == 2", false)]
    [InlineData("!true", false)]
    [InlineData("null == null", true)]
    [InlineData("\"a\" != \"b\"", true)]
    public void Evaluate_LiteralConditions(string condition, bool expected)
        => Assert.Equal(expected, Eval(condition));

    [Fact]
    public void Evaluate_PathCondition_ThroughResolver()
    {
        var resolver = StubResolver(("i", Scalar(2, "System.Int32")));
        Assert.True(Eval("i == 2", resolver));
        Assert.False(Eval("i == 3", resolver));
        Assert.True(Eval("i < 3", resolver));
    }

    [Fact]
    public void Evaluate_PathChainCondition()
    {
        // bag.A == 7：两段路径（字段段经 resolver 桩整体返回）
        var bag = new DebugEvalResult("<对象>", "T", DebugEvalKind.Object,
            [new DebugVariable("A", -1, DebugValue.Scalar("7"), IsArgument: false)], null);
        var resolver = StubResolver(("bag", bag));
        // 对象不可比较——路径段必须落到标量；桩只按根返回，这里验证对象比较被拒
        Assert.Throws<ExpressionEvaluationException>(() => Eval("bag == 1", resolver));
    }

    [Fact]
    public void Evaluate_NonBoolCondition_Rejected()
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => Eval("i", StubResolver(("i", Scalar(0, "System.Int32")))));
        Assert.Contains("须为布尔标量", ex.Message);
        var nullEx = Assert.Throws<ExpressionEvaluationException>(() => Eval("null"));
        Assert.Contains("须为布尔标量", nullEx.Message);
    }

    [Fact]
    public void Evaluate_ParseError_Propagates()
    {
        var ex = Assert.Throws<ExpressionEvaluationException>(() => Eval("a +"));
        Assert.Contains("不支持", ex.Message);
    }

    [Fact]
    public void Evaluate_ResolverError_Propagates()
    {
        // 引擎侧把异常转成 BreakpointConditionFailed（放行+计数），此处验证异常不被吞
        PathValueResolver failing = (_, _, _) => throw new InvalidOperationException("栈顶帧无变量「i」");
        Assert.Throws<InvalidOperationException>(() => Eval("i == 1", failing));
    }
}
