using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Engine.Session;

/// <summary>
/// 条件求值的路径解析委托：引擎命令泵线程内同步直读（进程停住态，绕开 MaxChildren 截断）。
/// 仅在本次 Evaluate 调用栈内有效（泵线程、停住态），实现方不得持有跨调用复用。
/// </summary>
public delegate DebugEvalResult PathValueResolver(int threadId, string rootName, IReadOnlyList<PathSegment> segments);

/// <summary>
/// 断点条件求值器契约（P7 依赖倒置）：Engine 定义、Session 实现（注入 P6 表达式求值器）——Engine 不得反向
/// 引用 Session，而条件求值必须发生在命令泵内（进程停住态才读得到值，P5 trace 同款「不停即放行」）。
/// 实现红线：Evaluate 在命令泵 MTA 线程内调用，只做纯计算 + pathResolver 调用，**绝不**触碰 session/命令泵
/// （再入 = 死锁）。返回 true=条件为真（继续命中流程：计数/hitCount/trace/停）；false=放行；
/// 抛异常=求值失败（引擎放行并发布 BreakpointConditionFailed 事件供 Session 计数反馈，防静默空等）。
/// </summary>
public interface IBreakpointConditionEvaluator
{
    bool Evaluate(int threadId, string expression, PathValueResolver pathResolver);
}
