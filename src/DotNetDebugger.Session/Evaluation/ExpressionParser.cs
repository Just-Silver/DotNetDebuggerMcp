using System.Text;
using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Session;

/// <summary>表达式子集解析/求值失败。Message 为面向 agent 的中文可诊断提示（宿主工具直接展示）。</summary>
public sealed class ExpressionEvaluationException(string message) : Exception(message);

/// <summary>表达式 AST 节点（P6 子集文法，见 ExpressionParser 注释）。</summary>
public abstract record ExpressionNode;

/// <summary>字面量：int/string/true/false/null。Value 为 long/string/bool/null。</summary>
public sealed record LiteralNode(object? Value, string Display) : ExpressionNode;

/// <summary>路径：根变量名 + 段序列（段数 ≤ PathSegment.MaxSegments）。</summary>
public sealed record PathNode(string Root, IReadOnlyList<PathSegment> Segments) : ExpressionNode;

/// <summary>一元取反：操作数须为布尔标量。</summary>
public sealed record NotNode(ExpressionNode Operand) : ExpressionNode;

/// <summary>单次比较：Op ∈ { ==, !=, &lt;, &lt;=, &gt;, &gt;= }（不链）。</summary>
public sealed record CompareNode(string Op, ExpressionNode Left, ExpressionNode Right) : ExpressionNode;

/// <summary>
/// 表达式读值子集解析器（P6，自研递归下降、不引 Roslyn——取舍见 spec §1/取舍点②）。
/// 文法（能解析即已支持，报错指向第一个不支持的位置）：
/// <code>
/// Expr       := Comparison
/// Comparison := Unary (('=='|'!='|'&lt;'|'&lt;='|'&gt;'|'&gt;=') Unary)?      // 单次比较，不链
/// Unary      := '!' Unary | Path
/// Path       := Primary ('.' Field | '[' Int ']')*                   // 段数 ≤ 8
/// Primary    := Identifier | Literal
/// Literal    := int | string | true | false | null
/// </code>
/// 不支持：算术、方法调用、赋值、负数字面量、括号、泛型/类型操作——tokenize/parse 报错附子集范围提示。
/// </summary>
public static class ExpressionParser
{
    private const string SubsetHint =
        "表达式子集：字面量（int/string/true/false/null）、成员访问 a.b、数组/字符串索引 a[i]（非负整数）、一元 !、单次比较（== != < <= > >=）；不支持算术、方法调用、赋值。";

    private enum TokKind { Ident, Int, Str, Op }

    private readonly record struct Token(TokKind Kind, string Text, int Pos);

    /// <summary>解析表达式（纯语法，不碰调试进程）。失败抛 <see cref="ExpressionEvaluationException"/>。</summary>
    public static ExpressionNode Parse(string text)
    {
        if (string.IsNullOrWhiteSpace(text))
            throw new ExpressionEvaluationException($"表达式为空。{SubsetHint}");
        var tokens = Tokenize(text);
        var pos = 0;
        var node = ParseComparison(tokens, ref pos);
        if (pos < tokens.Count)
            throw new ExpressionEvaluationException(
                $"位置 {tokens[pos].Pos} 附近「{tokens[pos].Text}」：表达式结尾多余内容（单次比较不链式；算术/方法调用不在子集内）。{SubsetHint}");
        return node;
    }

    private static ExpressionNode ParseComparison(List<Token> tokens, ref int pos)
    {
        var left = ParseUnary(tokens, ref pos);
        if (pos < tokens.Count && tokens[pos] is { Kind: TokKind.Op, Text: "==" or "!=" or "<" or "<=" or ">" or ">=" })
        {
            var op = tokens[pos].Text;
            pos++;
            var right = ParseUnary(tokens, ref pos);
            return new CompareNode(op, left, right);
        }
        return left;
    }

    private static ExpressionNode ParseUnary(List<Token> tokens, ref int pos)
    {
        if (pos < tokens.Count && tokens[pos] is { Kind: TokKind.Op, Text: "!" })
        {
            pos++;
            return new NotNode(ParseUnary(tokens, ref pos));
        }
        return ParsePath(tokens, ref pos);
    }

    private static ExpressionNode ParsePath(List<Token> tokens, ref int pos)
    {
        if (pos >= tokens.Count)
            throw new ExpressionEvaluationException($"表达式意外结束（此处应为变量名或字面量）。{SubsetHint}");
        var t = tokens[pos];
        ExpressionNode node;
        switch (t.Kind)
        {
            case TokKind.Ident:
                pos++;
                node = t.Text switch
                {
                    "true" => new LiteralNode(true, "True"),
                    "false" => new LiteralNode(false, "False"),
                    "null" => new LiteralNode(null, "null"),
                    _ => new PathNode(t.Text, []),
                };
                break;
            case TokKind.Int:
                pos++;
                node = new LiteralNode(ParseInt(t), t.Text);
                break;
            case TokKind.Str:
                pos++;
                node = new LiteralNode(t.Text, $"\"{t.Text.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"");
                break;
            default:
                throw new ExpressionEvaluationException($"位置 {t.Pos} 附近「{t.Text}」：此处应为变量名或字面量。{SubsetHint}");
        }

        var segments = new List<PathSegment>();
        while (pos < tokens.Count && tokens[pos].Kind == TokKind.Op)
        {
            if (tokens[pos].Text == ".")
            {
                pos++;
                if (pos >= tokens.Count || tokens[pos].Kind != TokKind.Ident)
                    throw new ExpressionEvaluationException($"位置 {(pos < tokens.Count ? tokens[pos].Pos : t.Pos)}：成员访问「.」后应为字段名。{SubsetHint}");
                segments.Add(new PathSegment.Field(tokens[pos].Text));
                pos++;
            }
            else if (tokens[pos].Text == "[")
            {
                var bracketPos = tokens[pos].Pos;
                pos++;
                if (pos >= tokens.Count || tokens[pos].Kind != TokKind.Int)
                    throw new ExpressionEvaluationException($"位置 {bracketPos}：「[」后应为非负整数下标（负数/表达式下标不在子集内）。{SubsetHint}");
                var index = ParseIndex(tokens[pos]);
                pos++;
                if (pos >= tokens.Count || tokens[pos] is not { Kind: TokKind.Op, Text: "]" })
                    throw new ExpressionEvaluationException($"位置 {bracketPos}：「[」未闭合（应有 ]）。");
                pos++;
                segments.Add(new PathSegment.Index(index));
            }
            else break;
        }

        if (segments.Count > PathSegment.MaxSegments)
            throw new ExpressionEvaluationException($"路径段数 {segments.Count} 超上限 {PathSegment.MaxSegments}（防失控长链）。");
        if (node is PathNode path)
            node = path with { Segments = segments };
        else if (segments.Count > 0)
            throw new ExpressionEvaluationException($"位置 {t.Pos} 附近「{t.Text}」：字面量不支持成员访问/索引。{SubsetHint}");
        return node;
    }

    private static long ParseInt(Token token)
    {
        if (int.TryParse(token.Text, out var i)) return i;
        if (long.TryParse(token.Text, out var l)) return l;
        throw new ExpressionEvaluationException($"位置 {token.Pos}：整数「{token.Text}」超出范围。");
    }

    /// <summary>索引下标：须落在 int 正范围（PathSegment.Index 为 int；负数在 tokenize 已不可能出现——'-' 不在子集）。</summary>
    private static int ParseIndex(Token token)
    {
        if (!int.TryParse(token.Text, out var i))
            throw new ExpressionEvaluationException($"位置 {token.Pos}：下标「{token.Text}」超出范围。");
        return i;
    }

    private static List<Token> Tokenize(string s)
    {
        var tokens = new List<Token>();
        var i = 0;
        while (i < s.Length)
        {
            var c = s[i];
            if (char.IsWhiteSpace(c)) { i++; continue; }
            if (char.IsLetter(c) || c == '_')
            {
                var start = i;
                while (i < s.Length && (char.IsLetterOrDigit(s[i]) || s[i] == '_')) i++;
                tokens.Add(new Token(TokKind.Ident, s[start..i], start));
                continue;
            }
            if (char.IsDigit(c))
            {
                var start = i;
                while (i < s.Length && char.IsDigit(s[i])) i++;
                tokens.Add(new Token(TokKind.Int, s[start..i], start));
                continue;
            }
            if (c == '"')
            {
                var start = i;
                i++;
                var sb = new StringBuilder();
                while (i < s.Length && s[i] != '"')
                {
                    if (s[i] == '\\' && i + 1 < s.Length && (s[i + 1] == '"' || s[i + 1] == '\\'))
                    {
                        sb.Append(s[i + 1]);
                        i += 2;
                    }
                    else
                    {
                        sb.Append(s[i]);
                        i++;
                    }
                }
                if (i >= s.Length)
                    throw new ExpressionEvaluationException($"位置 {start}：字符串未闭合。子集支持 \"…\" 字面量（转义仅 \\\\ 与 \\\"）。");
                i++; // 跳过收尾引号
                tokens.Add(new Token(TokKind.Str, sb.ToString(), start));
                continue;
            }
            if (i + 1 < s.Length)
            {
                var two = s[i..(i + 2)];
                if (two is "==" or "!=" or "<=" or ">=")
                {
                    tokens.Add(new Token(TokKind.Op, two, i));
                    i += 2;
                    continue;
                }
            }
            if (c is '<' or '>' or '!' or '.' or '[' or ']')
            {
                tokens.Add(new Token(TokKind.Op, c.ToString(), i));
                i++;
                continue;
            }
            throw new ExpressionEvaluationException($"位置 {i} 附近「{c}」：表达式子集不支持该符号。{SubsetHint}");
        }
        return tokens;
    }
}
