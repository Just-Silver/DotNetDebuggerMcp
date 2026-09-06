using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;

namespace DotNetDebugger.Session;

/// <summary>
/// P7 断点条件求值器：Session 实现 Engine 定义的 <see cref="IBreakpointConditionEvaluator"/>（依赖倒置，
/// Engine 不得反向引用 Session）。解析（P6 parser，纯语法）+ 同步核求值，PathNode 用引擎传入的
/// pathResolver 直读（命令泵线程内、进程停住态）。
/// 红线：Evaluate 在泵线程调用，只做纯计算 + pathResolver，绝不触碰 session/命令泵（再入=死锁）。
/// 抛异常 = 求值失败（引擎放行 + BreakpointConditionFailed 事件计数反馈）。无状态单例。
/// </summary>
public sealed class ExpressionConditionEvaluator : IBreakpointConditionEvaluator
{
    public static ExpressionConditionEvaluator Instance { get; } = new();

    private ExpressionConditionEvaluator() { }

    public bool Evaluate(int threadId, string expression, PathValueResolver pathResolver)
    {
        var result = ExpressionEvaluator.EvaluateCore(
            ExpressionParser.Parse(expression),
            path => pathResolver(threadId, path.Root, path.Segments));
        // 条件须为布尔标量（与 C# 语义一致；`i` 这类非布尔值视作求值失败由引擎计数反馈）
        if (result.Kind != DebugEvalKind.Scalar || result.ScalarValue is not bool flag)
            throw new ExpressionEvaluationException($"条件须为布尔标量（当前：{ExpressionEvaluator.DescribeOperand(result)}）。");
        return flag;
    }
}
