using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Text;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 调试求值工具（P6）：表达式读值安全子集——纯读、无副作用（不执行目标进程代码）。
/// 解析/求值在 Session ExpressionEvaluator；路径直读在 Engine（绕开变量树 32 子项截断）。
/// </summary>
[McpServerToolType]
public static class DebugEvaluateTool
{
    /// <summary>
    /// 求值表达式读当前值（纯读、无副作用，进程需停在断点/异常；threadId 缺省 0 = 最近停点线程）。
    /// 支持字面量、成员访问、数组/字符串任意下标、一元 !、单次比较；属性按字段约定降级。
    /// </summary>
    /// <param name="expression">表达式（必填）。</param>
    /// <param name="threadId">线程 id；缺省 0 = 用最近停点线程。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>表达式值文本或中文提示。</returns>
    [McpServerTool]
    [Description("求值表达式读当前值（纯读、无副作用，进程需停在断点/异常）。支持：字面量（int/string/true/false/null）、成员访问 a.b.c、数组/字符串任意下标 a[i]（字符串索引得单字符）、一元 !、单次比较（== != < <= > >=）。属性 X 不可直接读，自动按字段约定降级（X→_x→_X→<X>k__BackingField），未命中时报错列出可用字段（如 List 的 _items/_size）。不支持算术、方法调用、赋值、链式比较。")]
    public static async Task<string> DebugEvaluate(
        [Description("表达式（必填），如 user.Id、bag._items[3].Name、i == retryCount、!done。")] string expression,
        [Description("线程 id；缺省 0 = 用最近停点线程。")] int threadId = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(expression))
            return "缺少 expression（必填）。支持：成员访问 a.b、数组/字符串索引 a[i]、一元 !、单次比较（== != < <= > >=）与字面量。";
        if (!DebugInspectTool.TryRequireStopped(out var active, out var error)) return error;

        var tid = threadId > 0 ? threadId : active.Buffer.StoppedThreadId;
        if (tid <= 0) return "无停点线程可读（先 debug_continue 运行至断点停下）。";

        try
        {
            var result = await ExpressionEvaluator.EvaluateAsync(active.Session, tid, expression, cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_evaluate", expression, "ok");

            var sb = new StringBuilder($"表达式: {expression} = {result.Display}");
            if (result.TypeName is not null) sb.Append($"（{result.TypeName}）");
            if (result.Children is { Count: > 0 })
            {
                foreach (var child in result.Children)
                    sb.AppendLine().Append(DebugInspectTool.RenderVariable(child, depth: 1));
            }
            return sb.ToString();
        }
        catch (ExpressionEvaluationException ex)
        {
            return ex.Message; // 解析/求值语义错误：本身即面向 agent 的中文提示
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message; // 引擎路径求值错误（段号/类型/可用字段）：同上
        }
        catch (Exception ex)
        {
            return $"求值失败：{ex.Message}";
        }
    }
}
