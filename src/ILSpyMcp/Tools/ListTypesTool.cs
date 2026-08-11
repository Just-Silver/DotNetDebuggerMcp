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
    /// 列出指定类别的实体类型：纯元数据读取（PEReader），默认过滤编译器生成类型，不再依赖 ilspycmd 安装，无需缓存与超时。
    /// </summary>
    /// <param name="assembly">要列类型的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="list">实体类型类别组合：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 "csi"（必填）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多 500 行）；缺省返回前 200 行。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的类型列表或错误提示文本。</returns>
    [McpServerTool]
    [Description("列出 .NET 程序集（dll/exe）中指定类别的实体类型，纯元数据秒回、默认过滤编译器生成类型（async 状态机、显示类等），无需 ilspycmd 安装。输出每行带行号标注，可直接引用具体行。结果默认只返回前 200 行，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> ListTypes(
        [Description("要列类型的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("列出程序集中的实体类型：c=class, i=interface, s=struct, d=delegate, e=enum；可组合多个字母同时列出，例如 \"csi\"（必填）")] string list = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多 500 行），例如 \"200-400\"；缺省返回前 200 行")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取，不做 ilspycmd 安装检测）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 参数校验：list 必填且只能由 c/i/s/d/e 组成
        if (!ArgumentValidators.ValidateList(list, out var argError)) return Task.FromResult(argError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 类别描述（含英文名，参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"实体类别 {DescribeCategories(list)}", IsListing: true);

        // 纯元数据读取并格式化（无子进程、无缓存，秒回）
        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var typeList = TypeLister.ListTypes(reader, list);
            var lineList = new List<string>(typeList.Count);
            foreach (var (category, fullName) in typeList)
            {
                lineList.Add($"{CategoryNames[category]} {fullName}");
            }
            return Task.FromResult(OutputFormatter.Format(lineList, lines, context));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Task.FromResult($"无法读取程序集元数据：{ex.Message}");
        }
    }

    /// <summary>
    /// 将 list 字母组合转为「字母(英文名)」描述，如 "csi" → "c(class), i(interface), s(struct)"。
    /// </summary>
    private static string DescribeCategories(string list) => string.Join(", ", list.Select(ch => $"{ch}({CategoryNames[ch]})"));
}
