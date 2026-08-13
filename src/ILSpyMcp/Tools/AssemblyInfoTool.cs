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
/// 输出 .NET 程序集（dll/exe）的概览信息：程序集名与版本、目标框架、引用的程序集清单、实体类型计数与入口点。
/// </summary>
[McpServerToolType]
public static class AssemblyInfoTool
{
    /// <summary>
    /// 输出程序集概览：元数据读取（PEReader），秒回，无需缓存与超时。作为 agent 接触陌生程序集的第一站，
    /// 先概览（名称/版本/目标框架/引用/类型构成/入口点）再决定深入哪个类型。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的程序集概览或错误提示文本。</returns>
    [McpServerTool]
    [Description("输出 .NET 程序集（dll/exe）的概览信息：程序集名与版本、目标框架、引用的程序集清单（名+版本）、实体类型计数（class/interface/struct/delegate/enum，过滤编译器生成类型）与入口点。纯元数据读取秒回，适合作为接触陌生程序集的第一站；输出每行带行号标注，默认返回前约 8 KB，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> AssemblyInfo(
        [Description("要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, "程序集信息");

        // 纯元数据读取并格式化（无缓存，秒回）
        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var asm = reader.GetAssemblyDefinition();
            var (byCategory, gen, total) = TypeLister.CountCategories(reader);
            var output = new List<string>
            {
                $"程序集: {reader.GetString(asm.Name)}",
                $"版本: {asm.Version}",
                $"目标框架: {AssemblyInfoReader.GetTargetFramework(reader) ?? "<未知>"}",
                $"类型总数: {total}（实体 {total - gen}，编译器生成 {gen}）",
                $"  class: {byCategory['c']}, interface: {byCategory['i']}, struct: {byCategory['s']}, delegate: {byCategory['d']}, enum: {byCategory['e']}",
                "引用的程序集:",
            };
            foreach (var (name, version) in AssemblyInfoReader.GetReferences(reader)) output.Add($"  {name} {version}");
            output.Add($"入口点: {AssemblyInfoReader.GetEntryPoint(pe, reader) ?? "<无或无法解析>"}");
            return Task.FromResult(OutputFormatter.Format(output, lines, context));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Task.FromResult($"无法读取程序集元数据：{ex.Message}");
        }
    }
}
