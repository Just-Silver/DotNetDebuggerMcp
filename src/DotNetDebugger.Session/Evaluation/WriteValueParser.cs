using System.Text.RegularExpressions;
using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Session;

/// <summary>
/// debug_set 新值文本 → DebugWriteValue（W1）。文法判定不依赖目标类型：
/// null / true|false / 数字字面量 → Scalar（原文透传、不归一化；数值接受度按引擎目标类型决定——
/// 引擎按目标元素类型解析：整型拒小数/后缀、浮点接受小数/科学计数/后缀、decimal 等非标量值类型目标整体 v1 降级）；
/// 数字形态：**无符号 `0x` 十六进制**（`0x1F`）或**带符号十进制**（`-5`/`+7`/`12.5`/`1.5e3`，可带 `m/f/d` 后缀）；
/// 其余（标识符/字段链/下标）按 ExpressionParser 解析为对象路径 → CopyPath（引用重定向）。
/// 不在文法内：双引号字符串文本（v1 不支持字符串内容写——引擎需 func-eval 构造，已关；置空请用 null、指向已有字符串请用对象路径）
/// 与单引号字符文本（char 目标请给整数码点 0-65535，引擎按目标元素类型写）——两者都抛中文提示，不虚构文法能力。
/// 失败抛 <see cref="ExpressionEvaluationException"/>（中文，宿主直接展示）。
/// </summary>
public static class WriteValueParser
{
    // 十进制数字可带符号/小数/指数/后缀；0x 十六进制**无符号且不带后缀**（0x 尾字母是数字位，不属 m/f/d 后缀）
    private static readonly Regex NumberRe = new(
        @"^(?:0[xX][0-9a-fA-F]+|[+-]?(?:\d+(?:\.\d*)?|\.\d+)(?:[eE][+-]?\d+)?[mMfFdD]?)$",
        RegexOptions.Compiled | RegexOptions.CultureInvariant);

    public static DebugWriteValue Parse(string text)
    {
        var t = text.Trim();
        if (t.Length == 0)
            throw new ExpressionEvaluationException(
                "value 为空。新值支持：null / true|false / 数字（0x 十六进制，或带符号十进制整数/小数/科学计数，可带 m/f/d 后缀）/ 同帧对象路径（引用重定向）。");
        if (t == "null") return new DebugWriteValue.Null();
        if (t == "true" || t == "false") return new DebugWriteValue.Scalar(t);
        if (IsNumberLike(t)) return new DebugWriteValue.Scalar(t); // 0x/小数/后缀原样传引擎按目标类型解析
        if (t.StartsWith('"'))
            throw new ExpressionEvaluationException(
                $"字符串内容不可改（v1 需构造新字符串对象，func-eval 已关）：value「{text}」请改用 null（引用置空）或同帧对象路径（重定向到已有字符串，如 cfg.Backup）。");
        if (t.StartsWith('\''))
            throw new ExpressionEvaluationException(
                $"单引号字符文本不在文法内：value「{text}」不能表达字符字面量——char 目标请给整数码点（0-65535，如 65 = 'A'），引擎按目标元素类型转换。");

        // 路径重定向：复用 P6 文法，要求解析为路径（字面量已在上方消费；true/false/null 大小写敏感同读文法）
        if (ExpressionParser.Parse(t) is PathNode p)
            return new DebugWriteValue.CopyPath(p.Root, p.Segments);
        throw new ExpressionEvaluationException(
            $"value「{text}」既非字面量也非对象路径。支持：null / true|false / 数字 / 或同帧另一条对象路径（如 cfg.Backup、alt.Tag、arr[0]）。");
    }

    /// <summary>
    /// 数字形态判定（文法面）：0x 十六进制（无符号）或带符号十进制（整数/小数/科学计数，可带 m/f/d 后缀）——
    /// 排除裸 "."/"-"/"+"、字符串/字符引号、字母杂串；0x 数字位（a-f/A-F）不会被误当后缀。具体目标类型接受度由引擎转换层判定。
    /// </summary>
    private static bool IsNumberLike(string t) => NumberRe.IsMatch(t);
}
