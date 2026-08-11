using System.Text;

namespace ILSpyMcp.Formatting;

/// <summary>
/// 格式化上下文：头部信息块所需的外界元数据（程序集路径、目标描述），由工具层传入；IsListing 区分「列类型」与「反编译」的措辞。
/// </summary>
public sealed record FormatContext(string AssemblyPath, string Target, bool IsListing = false);

/// <summary>
/// 标准输出结果格式化：默认返回前 200 行，超限截断并提示用 lines 参数拉取；lines 参数按行号范围切片（单次最多 500 行）。 传入 <see
/// cref="FormatContext"/> 时结果前置头部信息块（程序集/目标/总量/当前输出），给 agent 明确代码归属与当前切片位置。
/// </summary>
public static class OutputFormatter
{
    /// <summary>
    /// 标准输出默认返回的最大行数，超过则截断并提示用 lines 参数拉取。
    /// </summary>
    public const int DefaultMaxLines = 200;

    /// <summary>
    /// lines 参数单次可返回的最大行数，防止一次拉取过大。
    /// </summary>
    public const int LinesRangeMax = 500;

    /// <summary>
    /// 解析 lines 参数（格式 "start-end"，1-based 含两端），非法格式/边界返回错误提示。
    /// </summary>
    /// <param name="input">lines 参数原文，如 "200-400"。</param>
    /// <returns>解析结果元组：起始行号、结束行号；非法时 error 非空。</returns>
    public static (int Start, int End, string? Error) ParseLines(string input)
    {
        var match = System.Text.RegularExpressions.Regex.Match(input.Trim(), @"^(\d+)-(\d+)$");
        if (!match.Success)
        {
            return (0, 0, $"lines 参数格式应为 \"start-end\"，例如 \"200-400\"，实际为 \"{input}\"");
        }
        if (!int.TryParse(match.Groups[1].Value, out var start) || !int.TryParse(match.Groups[2].Value, out var end))
        {
            return (0, 0, "lines 行号超出可表示范围，请使用合理的行号。");
        }
        if (start < 1) return (0, 0, "lines 起始行号需 >= 1");
        if (start > end) return (0, 0, $"lines 起始行号 {start} 不能大于结束行号 {end}");
        return (start, end, null);
    }

    /// <summary>
    /// 统一入口：指定了 lines 参数则按行号范围切片，否则返回前 DefaultMaxLines 行；提供 context 时前置头部信息块。
    /// </summary>
    /// <param name="lines">反编译结果行列表。</param>
    /// <param name="linesParam">lines 参数原文；空字符串时返回前 200 行。</param>
    /// <param name="context">头部信息块上下文；为 null 时不加头部（保持既有行为）。</param>
    /// <returns>带行号的格式化文本（含可选头部信息块）。</returns>
    public static string Format(List<string> lines, string linesParam, FormatContext? context = null)
    {
        var body = string.IsNullOrEmpty(linesParam) ? FormatHead(lines) : FormatSlice(lines, linesParam);
        if (context is null) return body;
        var header = BuildHeader(context, lines, linesParam);
        return string.IsNullOrEmpty(body) ? header : $"{header}\n{body}";
    }

    /// <summary>
    /// 默认返回前 DefaultMaxLines 行（每行标注行号）；结果更大时截断并附操作提示（总行数由头部字段提供，此处不再重复）。
    /// </summary>
    /// <param name="lines">反编译结果行列表。</param>
    /// <returns>前 200 行的行号文本；超限时附截断提示。</returns>
    public static string FormatHead(List<string> lines)
    {
        var numbered = NumberLines(lines.Take(DefaultMaxLines).ToList(), 1);
        if (lines.Count <= DefaultMaxLines) return numbered;
        return $"{numbered}\n\n--- 已截断：以上为前 {DefaultMaxLines} 行。可用 lines=\"start-end\" 参数按行读取后续范围（1-based 含两端，单次最多 {LinesRangeMax} 行）---";
    }

    /// <summary>
    /// 从缓存结果按行号切片返回（每行标注行号）；单次最多 LinesRangeMax 行，超出时截断并提示剩余范围（总行数由头部字段提供，此处不再重复）。
    /// </summary>
    /// <param name="lines">反编译结果行列表。</param>
    /// <param name="start">起始行号（1-based，含）。</param>
    /// <param name="end">结束行号（1-based，含）。</param>
    /// <returns>按行号切片的行号文本；越界或超上限时附提示。</returns>
    public static string SliceLines(List<string> lines, int start, int end)
    {
        if (start > lines.Count)
        {
            return $"起始行 {start} 超出总行数 {lines.Count}";
        }
        var rangeEnd = Math.Min(end, start + LinesRangeMax - 1);
        var actualEnd = Math.Min(rangeEnd, lines.Count);
        // 本次请求实际可达的最大行号（受数据末尾约束）；与 actualEnd 相等即无上限截断
        var maxRequestedEnd = Math.Min(end, lines.Count);
        var count = actualEnd - start + 1;
        var range = NumberLines(lines.Skip(start - 1).Take(count).ToList(), start);
        if (maxRequestedEnd > actualEnd)
        {
            return $"{range}\n\n--- 已截断：请求范围 {start}-{end} 超过单次上限 {LinesRangeMax} 行，已返回 {start}-{actualEnd}（{count} 行）。剩余 {actualEnd + 1}-{maxRequestedEnd} 可再次使用 lines 参数拉取 ---";
        }
        return range;
    }

    /// <summary>
    /// 按 \n 或 \r\n 拆分行，去掉末尾空行；行内 \r 残留一并去除。
    /// </summary>
    /// <param name="text">子进程输出原文。</param>
    /// <returns>去除末尾空行与 \r 残留的行列表。</returns>
    public static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        while (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// 检测反编译文本是否含未解析的 //IL_ 注释（动态类型/异常路径等无法可靠反编译的片段），供头部信息块提示仅结构参考。
    /// </summary>
    /// <param name="lines">纯净行列表（未加行号前）。</param>
    /// <returns>任一纯净行包含子串 "//IL_" 即返回 true。</returns>
    public static bool ContainsIlUnresolved(List<string> lines)
        => lines.Any(l => l.Contains("//IL_", StringComparison.Ordinal));

    /// <summary>
    /// 计算结果总字节数（每行长度 + 换行符），用于截断提示；DecompileCache 复用其统计缓存占用。
    /// </summary>
    /// <param name="lines">反编译结果行列表。</param>
    /// <returns>总字节数。</returns>
    public static long CountBytes(List<string> lines)
    {
        long n = 0;
        foreach (var line in lines) n += line.Length + 1;
        return n;
    }

    /// <summary>
    /// 按行号范围切片；非法格式/边界返回错误提示。
    /// </summary>
    private static string FormatSlice(List<string> lines, string linesParam)
    {
        var (start, end, error) = ParseLines(linesParam);
        if (error is not null) return error;
        return SliceLines(lines, start, end);
    }

    /// <summary>
    /// 给每行添加行号前缀（`行号\t内容`），行号从 start 起；统一用 \n 换行避免 \r 残留。
    /// </summary>
    /// <param name="lines">待标注行号的行列表。</param>
    /// <param name="start">首行行号（1-based）。</param>
    /// <returns>带行号前缀的文本。</returns>
    private static string NumberLines(List<string> lines, int start)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < lines.Count; i++)
        {
            sb.Append(start + i).Append('\t').Append(lines[i]).Append('\n');
        }
        return sb.ToString().TrimEnd('\n');
    }

    /// <summary>
    /// 生成头部信息块：程序集 / 目标 两行 + 总量与当前输出字段行 + 分隔线；输出含 //IL_ 未解析注释时在分隔线前附提示行。
    /// 总量：反编译为「总行数」，列类型同时给出「匹配实体」与「总行数」（每行一个实体，行数=实体数）；当前输出统一按「行」定位。 头部为纯文本、不带行号前缀，避免与源码行号混淆。
    /// </summary>
    private static string BuildHeader(FormatContext ctx, List<string> lines, string linesParam)
    {
        var sb = new StringBuilder();
        sb.Append("程序集: ").Append(ctx.AssemblyPath).Append('\n');
        sb.Append("目标:   ").Append(ctx.Target).Append('\n');
        sb.Append(DescribeStats(ctx, lines.Count)).Append('\n');
        sb.Append(DescribeCurrent(linesParam, lines.Count));
        if (ContainsIlUnresolved(lines)) sb.Append('\n').Append("提示: 输出含 //IL_ 未解析注释（动态类型/异常路径），仅供结构参考");
        sb.Append("\n---");
        return sb.ToString();
    }

    /// <summary>
    /// 总量字段：反编译为「总行数: N 行」；列类型先给「匹配实体: N 个」再给「总行数: N 行」，兼顾语义计数与行定位。
    /// </summary>
    private static string DescribeStats(FormatContext ctx, int total)
    {
        var line = $"总行数:   {total} 行";
        return ctx.IsListing ? $"匹配实体: {total} 个\n{line}" : line;
    }

    /// <summary>
    /// 当前输出字段：本次返回的行号范围与数量（统一按行定位）；空结果、越界、超上限时附说明。
    /// </summary>
    private static string DescribeCurrent(string linesParam, int total)
    {
        if (total == 0) return "当前输出: 无";
        if (string.IsNullOrEmpty(linesParam))
        {
            return total <= DefaultMaxLines
                ? $"当前输出: 1-{total}（{total} 行）"
                : $"当前输出: 1-{DefaultMaxLines}（{DefaultMaxLines} 行，已截断）";
        }
        var (start, end, error) = ParseLines(linesParam);
        if (error is not null) return $"当前输出: 无效（{error}）";
        if (start > total) return $"当前输出: 无效（起始行 {start} 超出总行数 {total}）";
        var rangeEnd = Math.Min(end, start + LinesRangeMax - 1);
        var actualEnd = Math.Min(rangeEnd, total);
        var count = actualEnd - start + 1;
        return rangeEnd < end
            ? $"当前输出: {start}-{actualEnd}（{count} 行，已截断）"
            : $"当前输出: {start}-{actualEnd}（{count} 行）";
    }
}