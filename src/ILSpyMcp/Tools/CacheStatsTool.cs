using ILSpyMcp.Formatting;
using ILSpyMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Globalization;
using System.Text;

namespace ILSpyMcp.Tools;

/// <summary>
/// 输出进程内共享缓存的状态：总占用/上限、条目数、命中率与逐条目占用明细。供用户评估缓存大小设置（MaxCacheBytes）是否合适、
/// 定位占用大头；无程序集参数（缓存是进程级全局的，与具体程序集无关）。
/// </summary>
[McpServerToolType]
public static class CacheStatsTool
{
    /// <summary>
    /// 缓存签名前缀到工具名的映射（签名由工具经 \u001F 拼接，前缀用于辨识来源工具）。
    /// </summary>
    private static readonly IReadOnlyDictionary<string, string> ToolNames = new Dictionary<string, string>
    {
        ["type"] = "decompile",
        ["member"] = "decompile_member",
        ["whole-module"] = "decompile（整模块）",
        ["list-types"] = "list_types",
        ["signature"] = "signature",
        ["hierarchy"] = "hierarchy",
        ["dependencies"] = "dependencies",
        ["call-graph"] = "call_graph",
        ["call-graph-token"] = "call_graph(token)",
        ["assembly-info"] = "assembly_info",
    };

    /// <summary>
    /// 输出缓存状态报告：统计块（占用/上限、条目数、命中率）+ 条目明细（按占用降序，带行号、支持 lines 分页）。
    /// </summary>
    /// <param name="lines">按行号范围读取条目明细，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>缓存状态文本。</returns>
    [McpServerTool]
    [Description("查看进程内共享缓存的占用状态：当前占用与上限（据此判断缓存大小设置是否合适）、缓存条目数、命中率（会话启动以来的累计命中/未命中），以及每条缓存条目的占用大小（按占用降序，含来源工具、参数签名与程序集），定位缓存大头。无程序集参数，直接调用即可。结果默认只返回前约 8 KB，可用 lines 参数按行号范围拉取条目明细。")]
    public static Task<string> CacheStats(
        [Description("按行号范围读取条目明细，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var stats = AppServices.Cache.GetStats();

        var sb = new StringBuilder();
        sb.Append("缓存状态:").Append('\n');
        sb.Append("  当前占用: ").Append(FormatBytes(stats.TotalBytes))
            .Append(" / ").Append(FormatBytes(stats.MaxBytes))
            .Append("（").Append(FormatPercent(Percent(stats.TotalBytes, stats.MaxBytes))).Append("%）").Append('\n');
        sb.Append("  条目数: ").Append(stats.EntryCount).Append('\n');
        sb.Append("  命中率: ").Append(DescribeHits(stats.HitCount, stats.MissCount));

        if (stats.Entries.Count == 0)
        {
            return Task.FromResult(sb.Append("\n条目明细: （无）").ToString());
        }

        // 条目明细：按占用降序，标注来源工具、参数签名与程序集文件名，经 OutputFormatter 加行号并支持 lines 分页
        var detail = stats.Entries
            .OrderByDescending(e => e.Bytes)
            .Select(e => $"{FormatBytes(e.Bytes)}\t命中 {e.Hits} 次\t{DescribeSignature(e.Signature)}\t{Path.GetFileName(e.AssemblyPath)}")
            .ToList();
        return Task.FromResult(sb.Append("\n条目明细（按占用降序）:\n").Append(OutputFormatter.Format(detail, lines)).ToString());
    }

    /// <summary>
    /// 命中率描述：无查询时提示暂无；否则给命中/未命中次数与命中率百分比。
    /// </summary>
    private static string DescribeHits(long hits, long misses)
    {
        var total = hits + misses;
        return total == 0
            ? "暂无查询"
            : $"命中 {hits} 次，未命中 {misses} 次（{FormatPercent(Percent(hits, total))}%）";
    }

    /// <summary>
    /// 把缓存签名（工具前缀 + \u001F + 参数）渲染为可读形式：工具名 + 冒号 + 参数；参数内 \u001F 分隔替换为竖线便于阅读；
    /// 无参数时只显示工具名；未知前缀原样展示。
    /// </summary>
    private static string DescribeSignature(string signature)
    {
        var sep = signature.IndexOf('\u001F');
        if (sep < 0) return ToolNames.TryGetValue(signature, out var name) ? name : signature;
        var prefix = signature[..sep];
        var rest = signature[(sep + 1)..].Replace("\u001F", " | ");
        var tool = ToolNames.TryGetValue(prefix, out var toolName) ? toolName : prefix;
        return string.IsNullOrEmpty(rest) ? tool : $"{tool}: {rest}";
    }

    /// <summary>
    /// 字节数格式化为人类可读文本（B/KB/MB，一位小数，InvariantCulture 避免区域小数分隔差异）。
    /// </summary>
    private static string FormatBytes(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{(bytes / 1024.0).ToString("0.0", CultureInfo.InvariantCulture)} KB";
        return $"{(bytes / (1024.0 * 1024.0)).ToString("0.0", CultureInfo.InvariantCulture)} MB";
    }

    /// <summary>
    /// 百分比计算（避免 0/0 返回 NaN）。
    /// </summary>
    private static double Percent(long part, long whole)
        => whole == 0 ? 0 : part * 100.0 / whole;

    /// <summary>
    /// 百分比格式化为一位小数（InvariantCulture）。
    /// </summary>
    private static string FormatPercent(double percent)
        => percent.ToString("0.0", CultureInfo.InvariantCulture);
}
