using System.Text;

namespace ILSpyMcp.Infrastructure;

/// <summary>
/// 格式化上下文：头部信息块所需的外界元数据（程序集路径、目标描述、参数串），由工具层传入；IsListing 区分「列类型」与「反编译」的措辞。
/// </summary>
public sealed record FormatContext(string AssemblyPath, string Target, string Parameters, bool IsListing = false);

/// <summary>
/// 标准输出结果格式化：默认返回前 200 行，超限截断并提示用 lines 参数拉取；lines 参数按行号范围切片（单次最多 500 行）。
/// 传入 <see cref="FormatContext"/> 时结果前置头部信息块（程序集/目标/参数/内容），给 agent 明确代码归属与当前切片位置。
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
    /// 按行号范围切片；非法格式/边界返回错误提示。
    /// </summary>
    private static string FormatSlice(List<string> lines, string linesParam)
    {
        var (start, end, error) = ParseLines(linesParam);
        if (error is not null) return error;
        return SliceLines(lines, start, end);
    }

    /// <summary>
    /// 默认返回前 DefaultMaxLines 行（每行标注行号）；结果更大时截断并附总行数/大小提示。
    /// </summary>
    /// <param name="lines">反编译结果行列表。</param>
    /// <returns>前 200 行的行号文本；超限时附截断提示。</returns>
    public static string FormatHead(List<string> lines)
    {
        var numbered = NumberLines(lines.Take(DefaultMaxLines).ToList(), 1);
        if (lines.Count <= DefaultMaxLines) return numbered;
        var kb = (CountBytes(lines) / 1024.0).ToString("0.0");
        return $"{numbered}\n\n--- 已截断：共 {lines.Count} 行 / {kb} KB，以上为前 {DefaultMaxLines} 行。可用 lines=\"start-end\" 参数按行读取后续范围（1-based 含两端，单次最多 {LinesRangeMax} 行）---";
    }

    /// <summary>
    /// 从缓存结果按行号切片返回（每行标注行号）；单次最多 LinesRangeMax 行，超出时截断并提示剩余范围。
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
        var count = actualEnd - start + 1;
        var range = NumberLines(lines.Skip(start - 1).Take(count).ToList(), start);
        if (rangeEnd < end)
        {
            return $"{range}\n\n--- 已截断：请求范围 {start}-{end} 共 {end - start + 1} 行，超过单次上限 {LinesRangeMax} 行，已返回 {start}-{actualEnd}（{count} 行）。剩余 {actualEnd + 1}-{end} 可再次使用 lines 参数拉取 ---";
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
    /// 生成头部信息块：程序集 / 目标 / 参数 / 内容 四行 + 分隔线。头部为纯文本、不带行号前缀，避免与源码行号混淆。
    /// </summary>
    private static string BuildHeader(FormatContext ctx, List<string> lines, string linesParam)
    {
        var sb = new StringBuilder();
        sb.Append("程序集: ").Append(ctx.AssemblyPath).Append('\n');
        sb.Append("目标:   ").Append(ctx.Target).Append('\n');
        if (!string.IsNullOrEmpty(ctx.Parameters)) sb.Append("参数:   ").Append(ctx.Parameters).Append('\n');
        sb.Append("内容:   ").Append(DescribeContent(ctx, lines.Count, linesParam));
        sb.Append("\n---");
        return sb.ToString();
    }

    /// <summary>
    /// 描述内容规模与当前切片范围：反编译按「行」、列类型按「匹配实体」，两者皆以行为单位分页。
    /// </summary>
    private static string DescribeContent(FormatContext ctx, int total, string linesParam)
    {
        var unit = ctx.IsListing ? "个匹配实体" : "行";
        var baseText = $"共 {total} {unit}";
        if (string.IsNullOrEmpty(linesParam))
        {
            return total > DefaultMaxLines ? $"{baseText}，当前显示前 {DefaultMaxLines} 行" : baseText;
        }
        var (start, end, error) = ParseLines(linesParam);
        if (error is not null) return baseText;
        var shownEnd = Math.Min(Math.Min(end, start + LinesRangeMax - 1), total);
        return $"{baseText}，当前显示 {start}-{shownEnd} 行";
    }
}