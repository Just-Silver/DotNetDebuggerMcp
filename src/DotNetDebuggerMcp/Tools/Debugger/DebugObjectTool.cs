using DotNetDebugger.Engine.Engine;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 对象树下钻工具（D1）：按路径定位对象/数组后做受控递归展开——depth 层预算 + 每层字段/元素 limit +
/// 沿路径 &lt;cyclic&gt; 环占位，让 agent 逐级下钻对象结构而不盲猜路径。定位复用 P6 文法（$exception 伪根前缀特判），
/// 渲染复用 DebugInspectTool.RenderVariable（children 递归）。
/// </summary>
[McpServerToolType]
public static class DebugObjectTool
{
    /// <summary>
    /// 查看对象/数组的下一级结构（受控递归下钻，进程需停）：path 定位对象（根=栈顶帧局部/参数），
    /// depth 递归层数（默认 2，上限 6），limit 每层字段/元素上限（默认 32，范围 1-128），同路径环输出 &lt;cyclic&gt;。
    /// </summary>
    /// <param name="path">对象路径（必填）。</param>
    /// <param name="depth">递归深度（默认 2，范围 1-6）。</param>
    /// <param name="limit">每层字段/元素上限（默认 32，范围 1-128）。</param>
    /// <param name="threadId">线程 id；缺省 0 = 用最近停点线程。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>children 递归清单文本或中文提示。</returns>
    [McpServerTool]
    [Description("查看对象/数组的下一级结构（受控递归下钻，进程需停）：path 用 P6 文法定位对象（根=栈顶帧局部/参数，支持 $exception 伪根），depth 递归层数（默认 2，上限 6），limit 每层字段/元素上限（默认 32，范围 1-128），同路径环输出 <cyclic>。返回 children 递归清单。与 debug_evaluate 分工：debug_evaluate 取标量值，debug_object 探索对象结构。")]
    public static async Task<string> DebugObject(
        [Description("对象路径（必填），如 order.Customer、$exception.InnerException（根为栈顶帧局部/参数名）。")] string path,
        [Description("递归深度（默认 2，范围 1-6）。")] int depth = 2,
        [Description("每层字段/元素上限（默认 32，范围 1-128）。")] int limit = 32,
        [Description("线程 id；缺省 0 = 用最近停点线程。")] int threadId = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path)) return "缺少 path（必填）。对象路径如 order.Customer、$exception。";
        if (!DebugInspectTool.TryRequireStopped(out var active, out var error)) return error;

        var tid = threadId > 0 ? threadId : active.Buffer.StoppedThreadId;
        if (tid <= 0) return "无停点线程可读（先 debug_continue 运行至断点停下）。";

        try
        {
            var target = ParseObjectPath(path);
            if (target is null) return $"path「{path}」不是有效路径（应为变量名/字段/下标链）。";
            var d = Math.Clamp(depth, 1, DebugEngineCore.MaxDrillDepth);
            var lim = Math.Clamp(limit, 1, 128);
            var value = await active.Session.ReadObjectAtPathAsync(tid, target.Value.Root, target.Value.Segments, d, lim, cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_object", $"{path} depth={d} limit={lim}", "ok");
            if (value.Children is not { Count: > 0 } children)
                return $"对象 {path}（depth={d}）：{value.Display}（空对象/空数组，无 children）。"; // 标量/字符串/null 已由引擎抛错
            var sb = new StringBuilder($"对象 {path}（depth={d}，{children.Count} 项）:");
            foreach (var c in children)
                sb.AppendLine().Append(DebugInspectTool.RenderVariable(c, depth: 1));
            return sb.ToString();
        }
        catch (ExpressionEvaluationException ex)
        {
            return ex.Message; // 路径解析错误：本身即面向 agent 的中文提示
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message; // 引擎路径/类型错误（不是对象/数组等）
        }
        catch (Exception ex)
        {
            return $"展开失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 解析 path → (root, segments)。$exception 伪根特判：ExpressionParser 分词器不接受 $ 开头（$exception 根
    /// 只能由引擎直传 rootName 到达）——工具层对 $exception / $exception.… 前缀特判：根固定 $exception，
    /// 剩余段串经 ExpressionParser.Parse 复用其 Segments。
    /// </summary>
    private static (string Root, IReadOnlyList<PathSegment> Segments)? ParseObjectPath(string path)
    {
        if (path == "$exception") return ("$exception", []);
        if (path.StartsWith("$exception.", StringComparison.Ordinal))
        {
            var rest = path["$exception.".Length..];
            return ExpressionParser.Parse(rest) is PathNode excNode ? ("$exception", excNode.Segments) : null;
        }
        return ExpressionParser.Parse(path) is PathNode node ? (node.Root, node.Segments) : null;
    }
}
