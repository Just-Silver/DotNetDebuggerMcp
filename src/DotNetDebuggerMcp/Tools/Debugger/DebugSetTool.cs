using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 停点现场改写工具（W1 debug_set）：把停点现场的值改成给定新值后继续观察行为——二分定位实验的闭环工具。
/// path 与 debug_evaluate 同款（根=栈顶帧局部/参数名 + 字段/下标链）；value 由 Session WriteValueParser 判定文法
/// （null/bool/数字=字面量 → 按目标类型转换；同帧对象路径=引用重定向）。写进程内存有崩目标风险——明示并只写可定位目标。
/// </summary>
[McpServerToolType]
public static class DebugSetTool
{
    /// <summary>
    /// 改写停点现场的值后继续（进程需停在断点/异常）：把局部变量/参数/对象字段/数组元素改成给定值，
    /// 返回「原值 → 新值」回显（防误判改写是否触及原因）。
    /// path 为目标路径（根=栈顶帧局部/参数名，支持字段 .a 与数组下标 [n]，同 debug_evaluate）。
    /// value 支持：null（引用置空）/ true|false / 数字（无符号 0x 十六进制，或带符号十进制整数/小数/科学计数，
    /// 可带 m/f/d 后缀——浮点目标接受小数/后缀，整型拒后缀，枚举给底层整数值）/
    /// 或同帧另一条对象路径（引用重定向，如 cfg.Backup）。
    /// 不支持：改 readonly/const/静态字段、构造新对象、改字符串内容（双引号/单引号文本不在文法内，char 用整数码点）、表达式/方法调用；
    /// decimal 字段目标 v1 不支持整值写（中文降级提示）。
    /// 注意风险：写目标进程内存可能使其崩溃，只改你确认的变量；目标引用为 null 时的重定向无类型校验，请保证源与目标同型；改完用 debug_continue 观察行为是否变化。
    /// </summary>
    /// <param name="path">目标路径（必填），如 scores[2]、b.A、i、cfg.Current（根为栈顶帧局部/参数名）。</param>
    /// <param name="value">新值（必填）：null / true|false / 数字（无符号 0x 或带符号十进制，可带小数/m/f/d 后缀）/ 同帧对象路径（引用重定向）。</param>
    /// <param name="threadId">线程 id；缺省 0 = 用最近停点线程。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果（含原值→新值回显）或中文提示。</returns>
    [McpServerTool]
    [Description("改写停点现场的值后继续（进程需停在断点/异常）：把局部变量/参数/对象字段/数组元素改成给定值，返回「原值 → 新值」回显。path 为目标路径（根=栈顶帧局部/参数名，支持字段 .a 与数组下标 [n]，同 debug_evaluate）。value 支持：null（引用置空）/ true|false / 数字（无符号 0x 十六进制，或带符号十进制整数/小数/科学计数，可带 m/f/d 后缀——浮点目标接受小数/后缀，整型拒后缀，枚举给底层整数值）/ 或同帧另一条对象路径（引用重定向，如 cfg.Backup）。不支持：改 readonly/const/静态字段、构造新对象、改字符串内容（双引号/单引号文本不在文法内，char 请用整数码点 0-65535）、表达式/方法调用；decimal 字段目标 v1 不支持整值写（引擎给中文降级提示）。注意风险：写目标进程内存可能使其崩溃，只改你确认的变量；目标引用为 null 时的重定向无类型校验，请保证源与目标同型；改完用 debug_continue 观察行为是否变化。")]
    public static async Task<string> DebugSet(
        [Description("目标路径（必填），如 scores[2]、b.A、i、cfg.Current（根为栈顶帧局部/参数名）。")] string path,
        [Description("新值（必填）：null / true|false / 数字（无符号 0x 十六进制，或带符号十进制整数/小数/科学计数，可带 m/f/d 后缀）/ 同帧对象路径（引用重定向）。")] string value,
        [Description("线程 id；缺省 0 = 用最近停点线程。")] int threadId = 0,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return "缺少 path（必填）。目标路径如 b.A、scores[2]、i（根为栈顶帧局部/参数名，支持字段与数组下标）。";
        if (string.IsNullOrWhiteSpace(value))
            return "缺少 value（必填）。支持 null / true|false / 数字 / 同帧对象路径（引用重定向）。";
        if (!DebugInspectTool.TryRequireStopped(out var active, out var error)) return error;

        var tid = threadId > 0 ? threadId : active.Buffer.StoppedThreadId;
        if (tid <= 0) return "无停点线程可写（先 debug_continue 运行至断点停下）。";

        try
        {
            // path 与 debug_evaluate 同款文法：变量名/字段链/数组下标（字面量/comparison 等非路径形态拒绝）
            if (ExpressionParser.Parse(path) is not PathNode target)
                return $"path「{path}」不是有效路径（应为变量名/字段/下标链，如 b.A、scores[2]、i）。";
            var write = WriteValueParser.Parse(value);
            var result = await active.Session.SetPathValueAsync(tid, target.Root, target.Segments, write, cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_set", $"{path} = {value}", "ok");
            return $"路径 {path} 已改：原值 {result.OldDisplay} → 新值 {result.NewDisplay}" +
                   (result.TypeName is null ? "" : $"（{result.TypeName}）") +
                   "。进程仍在停点——可用 debug_evaluate 复核；debug_continue 运行观察行为变化。";
        }
        catch (ExpressionEvaluationException ex)
        {
            return ex.Message; // value 文法/路径错误：本身即面向 agent 的中文提示
        }
        catch (InvalidOperationException ex)
        {
            return ex.Message; // 引擎路径写错误（段/类型/readonly/降级提示）
        }
        catch (Exception ex)
        {
            return $"改写失败：{ex.Message}";
        }
    }
}
