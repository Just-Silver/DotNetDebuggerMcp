using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Tools;

/// <summary>
/// 列出 .NET 程序集（dll/exe）中指定类别的实体类型到标准输出。
/// </summary>
[McpServerToolType]
public static class ListTypesTool
{
    /// <summary>
    /// list 字母到实体类别英文名的映射，用于头部目标描述与结果行的类别名前缀。
    /// </summary>
    private static readonly IReadOnlyDictionary<char, string> CategoryNames = new Dictionary<char, string>
    {
        ['c'] = "class",
        ['i'] = "interface",
        ['s'] = "struct",
        ['d'] = "delegate",
        ['e'] = "enum",
    };

    /// <summary>
    /// 列出指定类别的实体类型：元数据读取（PEReader），默认过滤编译器生成类型，经共享缓存秒回。
    /// </summary>
    /// <param name="assembly">要列类型的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="list">实体类型类别组合：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 "csi"（必填）。</param>
    /// <param name="nameContains">类型名子串过滤（忽略大小写，默认空=不过滤），如 "Box" 只返回名称含 Box 的类型。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多约 32 KB）；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的类型列表或错误提示文本。</returns>
    [McpServerTool]
    [Description("列出 .NET 程序集（dll/exe）中指定类别的实体类型，默认过滤编译器生成类型（async 状态机、显示类等）。支持 nameContains 参数按类型名子串过滤（忽略大小写），大型程序集按名定位类型免分页扫全量。输出每行带行号标注，可直接引用具体行。结果默认只返回前约 8 KB，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> ListTypes(
        [Description("要列类型的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("列出程序集中的实体类型：c=class, i=interface, s=struct, d=delegate, e=enum；可组合多个字母同时列出，例如 \"csi\"（必填）")] string list = "",
        [Description("类型名子串过滤，忽略大小写（默认空=不过滤），例如 \"Box\" 只返回名称含 Box 的类型")] string nameContains = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 参数校验：list 必填且只能由 c/i/s/d/e 组成
        if (!ArgumentValidators.ValidateList(list, out var argError)) return Task.FromResult(argError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 类别描述（含英文名，参数不展示——agent 面对的是 MCP 命名参数）
        var target = $"实体类别 {DescribeCategories(list)}";
        if (!string.IsNullOrEmpty(nameContains)) target += $"，名称含 {nameContains}";
        var context = new FormatContext(assemblyFull, target, IsListing: true);

        // 元数据读取经共享缓存（命中直接返回，头部标注缓存命中）
        var signature = $"list-types\u001F{list}\u001F{nameContains}";
        return Task.FromResult(ToolExecutor.RunMetadataPe(assemblyFull, signature, lines, context, (pe, reader) =>
        {
            var typeList = TypeLister.ListTypes(reader, list, string.IsNullOrEmpty(nameContains) ? null : nameContains);
            var lineList = new List<string>(typeList.Count);
            foreach (var (category, fullName) in typeList)
            {
                lineList.Add($"{CategoryNames[category]} {fullName}");
            }
            return lineList;
        }, cancellationToken));
    }

    /// <summary>
    /// 将 list 字母组合转为「字母(英文名)」描述，如 "csi" → "c(class), i(interface), s(struct)"。
    /// </summary>
    private static string DescribeCategories(string list) => string.Join(", ", list.Select(ch => $"{ch}({CategoryNames[ch]})"));
}
