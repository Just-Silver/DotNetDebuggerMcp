using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;

namespace DotNetDebugger.Session;

/// <summary>
/// 表达式读值安全子集求值器（P6）：解析（<see cref="ExpressionParser"/>）+ AST 求值调度。
/// 路径节点经 Engine <c>EvaluatePathAsync</c> 逐段解引用（纯读、无 FuncEval，与 debug_variables 同一来源）；
/// 一元 ! 与单次比较在本层标量化判定（数值统一 decimal、字符串 Ordinal、布尔仅 ==/!=）。
/// 失败抛 <see cref="ExpressionEvaluationException"/>（中文可诊断，宿主直接展示；P7 条件断点据此判不命中）。
/// </summary>
public static class ExpressionEvaluator
{
    /// <summary>求值表达式（进程需停在断点/异常；threadId 与 debug_variables 一致，0 = 最近停点线程）。</summary>
    public static async Task<DebugEvalResult> EvaluateAsync(DebugSession session, int threadId, string expression, CancellationToken ct = default)
    {
        var ast = ExpressionParser.Parse(expression);
        return await EvaluateNodeAsync(session, threadId, ast, ct).ConfigureAwait(false);
    }

    private static async Task<DebugEvalResult> EvaluateNodeAsync(DebugSession session, int threadId, ExpressionNode node, CancellationToken ct)
    {
        switch (node)
        {
            case LiteralNode literal:
                return LiteralResult(literal);
            case PathNode path:
                return await session.EvaluatePathAsync(threadId, path.Root, path.Segments, ct).ConfigureAwait(false);
            case NotNode not:
            {
                var operand = await EvaluateNodeAsync(session, threadId, not.Operand, ct).ConfigureAwait(false);
                if (operand.Kind != DebugEvalKind.Scalar || operand.ScalarValue is not bool flag)
                    throw new ExpressionEvaluationException($"「!」的操作数须为布尔标量（当前：{DescribeOperand(operand)}）。");
                return BoolResult(!flag);
            }
            case CompareNode compare:
            {
                var left = await EvaluateNodeAsync(session, threadId, compare.Left, ct).ConfigureAwait(false);
                var right = await EvaluateNodeAsync(session, threadId, compare.Right, ct).ConfigureAwait(false);
                return Compare(compare.Op, left, right);
            }
            default:
                throw new ExpressionEvaluationException("不支持的表达式节点。");
        }
    }

    private static DebugEvalResult LiteralResult(LiteralNode literal) => new(
        literal.Display,
        literal.Value switch
        {
            string => "System.String",
            bool => "System.Boolean",
            long l => l <= int.MaxValue ? "System.Int32" : "System.Int64",
            _ => null, // null 字面量
        },
        literal.Value is null ? DebugEvalKind.Null : DebugEvalKind.Scalar,
        null,
        literal.Value);

    private static DebugEvalResult Compare(string op, DebugEvalResult left, DebugEvalResult right)
    {
        var ordered = CompareTo(op, left, right);
        var result = op switch
        {
            "==" => ordered == 0,
            "!=" => ordered != 0,
            "<" => ordered < 0,
            "<=" => ordered <= 0,
            ">" => ordered > 0,
            ">=" => ordered >= 0,
            _ => throw new ExpressionEvaluationException($"不支持的比较运算符 {op}。"),
        };
        return BoolResult(result);
    }

    /// <summary>可比较性校验 + 序比较（<0/0/>0）。null 只支持 ==/!=；对象/数组不可比；布尔仅 ==/!=。</summary>
    private static int CompareTo(string op, DebugEvalResult left, DebugEvalResult right)
    {
        if (left.Kind == DebugEvalKind.Null || right.Kind == DebugEvalKind.Null)
        {
            if (op is not ("==" or "!="))
                throw new ExpressionEvaluationException($"null 只支持 == / != 比较（当前 {op}）。");
            // 与 null 相等的只有 null（对象/数组非 null 即不等，标量同理）
            return left.Kind == DebugEvalKind.Null && right.Kind == DebugEvalKind.Null ? 0 : 1;
        }
        if (left.Kind != DebugEvalKind.Scalar || right.Kind != DebugEvalKind.Scalar)
            throw new ExpressionEvaluationException(
                $"非标量比较：{DescribeOperand(left)} 与 {DescribeOperand(right)} 不可比（对象/数组先取字段/元素再比较）。");
        var l = left.ScalarValue;
        var r = right.ScalarValue;
        if (l is string ls && r is string rs)
            return string.CompareOrdinal(ls, rs);
        if (l is bool lb && r is bool rb)
        {
            if (op is not ("==" or "!="))
                throw new ExpressionEvaluationException($"布尔值只支持 == / !=（当前 {op}）。");
            return lb == rb ? 0 : 1;
        }
        if (l is bool || r is bool)
            throw new ExpressionEvaluationException(
                $"布尔与 {DescribeOperand(l is bool ? right : left)} 不可比较。");
        if (TryToDecimal(l, out var ld) && TryToDecimal(r, out var rd))
            return decimal.Compare(ld, rd);
        throw new ExpressionEvaluationException(
            $"不支持的比较：{left.TypeName ?? "未知类型"} 与 {right.TypeName ?? "未知类型"}（须同为我标量类型：数值/字符串/布尔）。");
    }

    /// <summary>数值标量统一 decimal 比较（整型/浮点/char；浮点转 decimal 精度足够调试判定）。</summary>
    private static bool TryToDecimal(object? value, out decimal number)
    {
        try
        {
            switch (value)
            {
                case char c: number = c; return true;
                case sbyte or byte or short or ushort or int or uint or long or ulong:
                    number = Convert.ToDecimal(value); return true;
                case float or double:
                    number = Convert.ToDecimal(value); return true;
                default: number = 0; return false;
            }
        }
        catch (OverflowException) { number = 0; return false; }
    }

    private static DebugEvalResult BoolResult(bool value)
        => new(value ? "True" : "False", "System.Boolean", DebugEvalKind.Scalar, null, value);

    private static string DescribeOperand(DebugEvalResult value) => value.Kind switch
    {
        DebugEvalKind.Null => "null",
        DebugEvalKind.Scalar => $"{value.TypeName ?? "标量"} {value.Display}",
        _ => $"{value.TypeName ?? "对象"}（{value.Display}）",
    };
}
