using System.Text.RegularExpressions;
using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Session;

/// <summary>
/// debug_set 新值文本 → DebugWriteValue（W1）。文法判定不依赖目标类型：
/// null/bool/数字字面量 → Scalar（原文透传，不归一化；数值接受度按引擎目标类型决定）；
/// 其余（标识符/成员链/下标）按 ExpressionParser 解析为对象路径 → CopyPath（引用重定向）；
/// 字符串字面量（"…"）产 Scalar 由引擎按目标类型报错（值类型字符可写、字符串内容构造拒绝）。
/// 失败抛 <see cref="ExpressionEvaluationException"/>（中文，宿主直接展示）。
/// </summary>
public static class WriteValueParser
{
    private static readonly Regex NumberRe = new(
        @"^[+-]?(?:0[xX][0-9a-fA-F]+|\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?[mMfFdD]?$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DebugWriteValue Parse(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
            throw new ExpressionEvaluationException(
                "value 为空。新值支持：null / true|false / 数字（整数/小数/科学计数/可带 m/f/d 后缀或 0x 前缀）/ 同帧对象路径（引用重定向）。");
        if (t == "null") return new DebugWriteValue.Null();
        if (t == "true" || t == "false") return new DebugWriteValue.Scalar(t);
        if (IsNumberLike(t)) return new DebugWriteValue.Scalar(t); // 0x/小数/后缀原样传引擎按目标类型解析

        // 路径重定向：复用 P6 文法，要求解析为路径（字面量已在上方消费；true/false/null 大小写敏感同读文法）
        if (ExpressionParser.Parse(t) is PathNode p)
            return new DebugWriteValue.CopyPath(p.Root, p.Segments);
        throw new ExpressionEvaluationException(
            $"value「{text}」既非字面量也非对象路径。支持：null / true|false / 数字 / 或同帧另一条对象路径（如 cfg.Backup、alt.Tag、arr[0]）。");
    }

    /// <summary>
    /// 数字形态判定（文法面）：^[+-]?(?:0x.. 或 十进制整数(可带 .小数) 或 .小数)(指数)?(m/f/d 后缀)?$——
    /// 排除裸 "."/"-"/"+"、字母后缀外的杂串；具体目标类型接受度由引擎转换层判定。
    /// </summary>
    private static bool IsNumberLike(string t) => NumberRe.IsMatch(t);
}
