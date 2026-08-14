using System.Text;

namespace ILSpyMcp.Formatting;

/// <summary>
/// 格式化上下文：头部信息块所需的外界元数据（程序集路径、目标描述），由工具层传入；IsListing 区分「列类型」与「反编译」的措辞，
/// IsCached 标注本次结果来自缓存命中（供 agent 感知重复查询低成本），Degraded 标注本次结果降级解析的方法体计数（仅新鲜扫描显示）。
/// </summary>
public sealed record FormatContext(string AssemblyPath, string Target, bool IsListing = false, bool IsCached = false, int Degraded = 0);

/// <summary>
/// 标准输出结果格式化：默认按字符预算（UTF-8 字节）返回前若干行并附行数软上限，超限截断并提示用 lines 参数拉取；lines 参数按行号范围切片
/// （单次同样受字节预算与行数软上限约束）。 传入 <see cref="FormatContext"/> 时结果前置头部信息块（程序集/目标/总量/当前输出/剩余），给
/// agent 明确代码归属与当前切片位置。
/// </summary>
public static class OutputFormatter
{
    /// <summary>默认返回（不带 lines）的字符预算（UTF-8 字节），超过则截断并提示用 lines 拉取。</summary>
    public const int DefaultMaxOutputChars = 8 * 1024;

    /// <summary>默认返回（不带 lines）的行数软上限，防止短行密集输出在字节未超限时行数爆量。</summary>
    public const int DefaultMaxLines = 1500;

    /// <summary>lines 参数单次返回的字符预算（UTF-8 字节），低于 opencode 宿主 50KB 边界。</summary>
    public const int LinesMaxOutputChars = 32 * 1024;

    /// <summary>lines 参数单次返回的行数软上限。</summary>
    public const int LinesMaxCount = 1900;

    /// <summary>预算截断的终止原因：取到末尾未截断 / 字节预算先到 / 行数软上限先到。</summary>
    private enum BudgetLimit { End, Chars, Lines }

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
    /// 统一入口：指定了 lines 参数则按行号范围切片，否则返回预算内默认行；提供 context 时前置头部信息块。
    /// </summary>
    /// <param name="lines">反编译结果行列表。</param>
    /// <param name="linesParam">lines 参数原文；空字符串时按默认预算返回。</param>
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
    /// 渲染成员 JSON 分隔行内容（不含 #MEMBER 前缀）：{name, token[, signature][, type]}。供 decompile_member 多匹配分隔行与超限
    /// 签名清单共用，agent 免解析文本分隔线直接取 name/token。用 UnsafeRelaxedJsonEscaping 避免成员名含中文时 \uXXXX 转义
    /// 导致 token 膨胀。
    /// </summary>
    /// <param name="name">成员名。</param>
    /// <param name="token">成员 token（如 0x060004b2）。</param>
    /// <param name="signature">成员签名；超限清单提供，普通分隔行为 null。</param>
    /// <param name="type">成员所属类型全名；跨程序集搜索时提供（供 agent 分辨成员归属），普通分隔行为 null。</param>
    /// <returns>JSON 对象文本，如 {"name":"BigHelper","token":"0x060004b2","type":"Ns.BigClass"}。</returns>
    public static string MemberJson(string name, string token, string? signature = null, string? type = null)
    {
        var sb = new StringBuilder("{\"name\":");
        AppendJsonString(sb, name);
        sb.Append(",\"token\":");
        AppendJsonString(sb, token);
        if (signature is not null)
        {
            sb.Append(",\"signature\":");
            AppendJsonString(sb, signature);
        }
        if (type is not null)
        {
            sb.Append(",\"type\":");
            AppendJsonString(sb, type);
        }
        sb.Append('}');
        return sb.ToString();
    }

    /// <summary>
    /// 追加 JSON 字符串字面量：转义引号、反斜杠与控制字符；非 ASCII 字符原样保留（避免 \uXXXX 转义膨胀 token）。
    /// </summary>
    private static void AppendJsonString(StringBuilder sb, string value)
    {
        sb.Append('"');
        foreach (var c in value)
        {
            switch (c)
            {
                case '"': sb.Append("\\\""); break;
                case '\\': sb.Append("\\\\"); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default:
                    if (c < 0x20) sb.Append("\\u").Append(((int)c).ToString("x4", System.Globalization.CultureInfo.InvariantCulture));
                    else sb.Append(c);
                    break;
            }
        }
        sb.Append('"');
    }

    /// <summary>
    /// 默认返回按字符预算（DefaultMaxOutputChars UTF-8 字节）与行数软上限（DefaultMaxLines）取前若干行（每行标注行号）；
    /// 结果更大时截断并附操作提示（总行数由头部字段提供，此处不再重复）。
    /// </summary>
    /// <param name="lines">反编译结果行列表。</param>
    /// <returns>预算内前若干行的行号文本；超限时附截断提示。</returns>
    public static string FormatHead(List<string> lines)
    {
        var (count, limit) = CountLinesWithinBudget(lines, 0, DefaultMaxOutputChars, DefaultMaxLines);
        var numbered = NumberLines(lines.Take(count).ToList(), 1);
        if (count >= lines.Count) return numbered;
        var hint = $"可用 lines=\"start-end\" 参数按行号读取后续范围（1-based 含两端，单次最多约 {LinesMaxOutputChars / 1024} KB）---";
        if (limit == BudgetLimit.Lines)
        {
            return $"{numbered}\n\n--- 已截断：达到默认行数软上限（{DefaultMaxLines} 行）。{hint}";
        }
        var kb = FormatKb(CountRenderedBytes(lines, 0, count));
        return $"{numbered}\n\n--- 已截断：以上约 {kb} KB，达到默认输出预算。{hint}";
    }

    /// <summary>
    /// 从缓存结果按行号切片返回（每行标注行号）；单次受字符预算（LinesMaxOutputChars UTF-8 字节）与行数软上限（LinesMaxCount）约束，
    /// 超出时截断并提示剩余范围（总行数由头部字段提供，此处不再重复）。
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
        var (count, limit) = CountLinesWithinBudget(lines, start - 1, LinesMaxOutputChars, LinesMaxCount);
        // 本次请求实际可达的最大行号（受数据末尾约束）；与 actualEnd 相等即无预算/上限截断
        var maxRequestedEnd = Math.Min(end, lines.Count);
        var actualEnd = Math.Min(start + count - 1, maxRequestedEnd);
        var range = NumberLines(lines.Skip(start - 1).Take(actualEnd - start + 1).ToList(), start);
        if (actualEnd < maxRequestedEnd)
        {
            // 截断时 Limit 必为 Chars/Lines 之一（End 时 count 取到行尾，actualEnd==maxRequestedEnd）
            var message = limit == BudgetLimit.Lines
                ? $"--- 已截断：请求范围 {start}-{end} 超过单次行数上限（{LinesMaxCount} 行），已返回 {start}-{actualEnd}（{actualEnd - start + 1} 行）。剩余 {actualEnd + 1}-{maxRequestedEnd} 可再次使用 lines 参数拉取 ---"
                : $"--- 已截断：请求范围 {start}-{end} 超过单次输出预算（约 {LinesMaxOutputChars / 1024} KB），已返回 {start}-{actualEnd}（{actualEnd - start + 1} 行）。剩余 {actualEnd + 1}-{maxRequestedEnd} 可再次使用 lines 参数拉取 ---";
            return $"{range}\n\n{message}";
        }
        return range;
    }

    /// <summary>
    /// 按 \n 或 \r\n 拆分行，去掉末尾空行；行内 \r 残留一并去除。
    /// </summary>
    /// <param name="text">反编译结果原文。</param>
    /// <returns>去除末尾空行与 \r 残留的行列表。</returns>
    public static List<string> SplitLines(string text)
    {
        var lines = text.Replace("\r\n", "\n").Replace('\r', '\n').Split('\n').ToList();
        while (lines.Count > 0 && lines[^1] == "") lines.RemoveAt(lines.Count - 1);
        return lines;
    }

    /// <summary>
    /// 统计反编译文本中未解析的 //IL_ 注释行数（动态类型/异常路径等无法可靠反编译的片段），供头部信息块提示仅结构参考。
    /// </summary>
    /// <param name="lines">纯净行列表（未加行号前）。</param>
    /// <returns>含子串 "//IL_" 的纯净行数。</returns>
    public static int CountIlUnresolvedLines(List<string> lines)
        => lines.Count(l => l.Contains("//IL_", StringComparison.Ordinal));

    /// <summary>
    /// 检测反编译文本是否含未解析的 //IL_ 注释（动态类型/异常路径等无法可靠反编译的片段），供头部信息块提示仅结构参考。
    /// </summary>
    /// <param name="lines">纯净行列表（未加行号前）。</param>
    /// <returns>任一纯净行包含子串 "//IL_" 即返回 true。</returns>
    public static bool ContainsIlUnresolved(List<string> lines)
        => CountIlUnresolvedLines(lines) > 0;

    /// <summary>
    /// 计算结果总字符数（每行长度 + 换行符），供 DecompileCache 统计缓存占用（与渲染口径无关，仅粗估体积）。
    /// </summary>
    /// <param name="lines">反编译结果行列表。</param>
    /// <returns>总字符数。</returns>
    public static long CountBytes(List<string> lines)
    {
        long n = 0;
        foreach (var line in lines) n += line.Length + 1;
        return n;
    }

    /// <summary>
    /// 计算单行经 NumberLines 渲染后的真实输出字节成本：行号位数 + 1(tab) + UTF8字节(行内容) + 1(换行)。
    /// 行号为该行在最终输出中的实际行号（1-based）。预算判据与宿主 opencode 的 UTF-8 字节截断同口径。
    /// </summary>
    private static int LineCost(string line, int lineNo)
        => lineNo.ToString().Length + 1 + Encoding.UTF8.GetByteCount(line) + 1;

    /// <summary>
    /// 从 startIndex 起（0-based）取行，直到字符预算或行数软上限先到达（至少返回 1 行）；返回实际行数与终止原因。
    /// 每行成本按 NumberLines 渲染后的真实输出字节计算：行号位数 + 1(tab) + UTF8字节(行内容) + 1(换行)，
    /// 行号为该行在最终输出中的实际行号（startIndex + i + 1）。预算判据与宿主字节截断同口径。
    /// </summary>
    private static (int Count, BudgetLimit Limit) CountLinesWithinBudget(List<string> lines, int startIndex, int maxChars, int maxLines)
    {
        var count = 0;
        var chars = 0;
        for (var i = startIndex; i < lines.Count; i++)
        {
            if (count >= maxLines) return (count, BudgetLimit.Lines);
            var lineNo = i + 1;
            var cost = LineCost(lines[i], lineNo);
            if (count > 0 && chars + cost > maxChars) return (count, BudgetLimit.Chars); // 至少返回 1 行
            chars += cost;
            count++;
        }
        return (count, BudgetLimit.End);
    }

    /// <summary>
    /// 从 startIndex 起（0-based）累计 count 行的真实渲染输出字节（与 CountLinesWithinBudget 同口径，行号从 startIndex + 1 起）。
    /// </summary>
    private static int CountRenderedBytes(List<string> lines, int startIndex, int count)
    {
        var bytes = 0;
        for (var i = startIndex; i < startIndex + count && i < lines.Count; i++)
        {
            bytes += LineCost(lines[i], i + 1);
        }
        return bytes;
    }

    /// <summary>
    /// 字节数格式化为一位小数的 KB 文本（如 "7.8 KB"、"8.0 KB"），截断提示与头部「当前输出」「剩余」字段共用。
    /// </summary>
    private static string FormatKb(long bytes)
        => Math.Round(bytes / 1024.0, 1).ToString("0.0", System.Globalization.CultureInfo.InvariantCulture);

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
    /// 生成头部信息块：程序集 / 目标 两行 + 总量与当前输出/剩余字段行 + 分隔线；输出含 //IL_ 未解析注释时在分隔线前附计数提示行，
    /// 结果经元数据降级解析（ctx.Degraded > 0）时附降级解析计数提示行。
    /// 总量：反编译为「总行数」，列类型同时给出「匹配实体」与「总行数」（每行一个实体，行数=实体数）；当前输出与剩余统一按「行」定位。 头部为纯文本、不带行号前缀，避免与源码行号混淆。
    /// </summary>
    private static string BuildHeader(FormatContext ctx, List<string> lines, string linesParam)
    {
        var sb = new StringBuilder();
        sb.Append("程序集: ").Append(ctx.AssemblyPath).Append('\n');
        sb.Append("目标:   ").Append(ctx.Target).Append('\n');
        if (ctx.IsCached) sb.Append("缓存:   命中（重复查询成本低）\n");
        sb.Append(DescribeStats(ctx, lines.Count)).Append('\n');
        sb.Append(DescribeCurrent(lines, linesParam));
        var remaining = DescribeRemaining(lines, linesParam);
        if (remaining is not null) sb.Append('\n').Append(remaining);
        var ilUnresolved = CountIlUnresolvedLines(lines);
        if (ilUnresolved > 0) sb.Append('\n').Append($"提示: 输出含 {ilUnresolved} 处 //IL_ 未解析注释（动态类型/异常路径），仅供结构参考");
        if (ctx.Degraded > 0) sb.Append('\n').Append($"提示: 本结果含 {ctx.Degraded} 处降级解析（部分方法体 IL 未完全解码，仅供结构参考）");
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
    /// 当前输出字段：本次实际返回的行号范围、数量与 KB（真实渲染字节，与 FormatHead/SliceLines 同口径）；空结果、越界、预算截断时附说明。
    /// </summary>
    private static string DescribeCurrent(List<string> lines, string linesParam)
    {
        var total = lines.Count;
        if (total == 0) return "当前输出: 无";
        if (string.IsNullOrEmpty(linesParam))
        {
            var (count, limit) = CountLinesWithinBudget(lines, 0, DefaultMaxOutputChars, DefaultMaxLines);
            var kb = FormatKb(CountRenderedBytes(lines, 0, count));
            if (count >= total) return $"当前输出: 1-{count}（{count} 行，{kb} KB）";
            var reason = limit == BudgetLimit.Lines
                ? $"已截断：超过默认行数软上限（{DefaultMaxLines} 行）"
                : $"已截断：超过默认预算约 {DefaultMaxOutputChars / 1024} KB";
            return $"当前输出: 1-{count}（{count} 行，{kb} KB，{reason}）";
        }
        var (start, end, error) = ParseLines(linesParam);
        if (error is not null) return $"当前输出: 无效（{error}）";
        if (start > total) return $"当前输出: 无效（起始行 {start} 超出总行数 {total}）";
        var maxRequestedEnd = Math.Min(end, total);
        var (count2, limit2) = CountLinesWithinBudget(lines, start - 1, LinesMaxOutputChars, LinesMaxCount);
        var actualEnd = Math.Min(start + count2 - 1, maxRequestedEnd);
        var kb2 = FormatKb(CountRenderedBytes(lines, start - 1, actualEnd - start + 1));
        if (actualEnd < maxRequestedEnd)
        {
            var reason = limit2 == BudgetLimit.Lines ? $"，已截断：超过行数上限（{LinesMaxCount} 行）" : "，已截断";
            return $"当前输出: {start}-{actualEnd}（{actualEnd - start + 1} 行，{kb2} KB{reason}）";
        }
        return $"当前输出: {start}-{actualEnd}（{actualEnd - start + 1} 行，{kb2} KB）";
    }

    /// <summary>
    /// 剩余字段：仅当本次输出因预算/行数截断时输出，告知剩余行数/KB 与建议的 lines 边界（边界经同一累计函数算出，照抄后恰好不超预算）。
    /// 剩余整体可一次获取时给全量范围；否则给首个可行段并提示需分次获取。
    /// </summary>
    private static string? DescribeRemaining(List<string> lines, string linesParam)
    {
        var total = lines.Count;
        if (total == 0) return null;
        bool truncated;
        int actualEnd;
        if (string.IsNullOrEmpty(linesParam))
        {
            (actualEnd, _) = CountLinesWithinBudget(lines, 0, DefaultMaxOutputChars, DefaultMaxLines);
            truncated = actualEnd < total;
        }
        else
        {
            var (start, end, error) = ParseLines(linesParam);
            if (error is not null || start > total) return null;
            var (count, _) = CountLinesWithinBudget(lines, start - 1, LinesMaxOutputChars, LinesMaxCount);
            var maxRequestedEnd = Math.Min(end, total);
            actualEnd = Math.Min(start + count - 1, maxRequestedEnd);
            truncated = actualEnd < maxRequestedEnd;
        }
        if (!truncated) return null; // 未截断无剩余
        var remainingCount = total - actualEnd;
        var remainingBytes = CountRenderedBytes(lines, actualEnd, remainingCount);
        var kb = FormatKb(remainingBytes);
        if (remainingBytes <= LinesMaxOutputChars && remainingCount <= LinesMaxCount)
        {
            return $"剩余:     {remainingCount} 行 / 约 {kb} KB，可一次获取：lines=\"{actualEnd + 1}-{total}\"";
        }
        var (fit, _) = CountLinesWithinBudget(lines, actualEnd, LinesMaxOutputChars, LinesMaxCount);
        return $"剩余:     {remainingCount} 行 / 约 {kb} KB，超过单次预算（约 {LinesMaxOutputChars / 1024} KB），需分次获取：先用 lines=\"{actualEnd + 1}-{actualEnd + fit}\"";
    }
}