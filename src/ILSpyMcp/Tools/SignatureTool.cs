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
/// 输出 .NET 程序集（dll/exe）中指定类型的全部成员签名（API 地图），每成员一行。
/// </summary>
[McpServerToolType]
public static class SignatureTool
{
    /// <summary>
    /// 输出指定类型全部成员的一行签名（API 地图）：元数据读取（PEReader），秒回，无需缓存与超时。
    /// </summary>
    /// <param name="assembly">要读取成员签名的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">目标类型的全限定名（必填），格式与 list_types 输出一致。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的成员签名列表或错误提示文本。</returns>
    [McpServerTool]
    [Description("输出指定类型全部成员（字段/方法/属性/事件）每成员一行 C# 签名，作为 API 地图：字段含访问级别与 static/readonly/const，属性合并 get/set 访问器、事件以 event 形式，泛型类型与方法均带泛型参数。typeName 为类型全名，格式与 list_types 输出一致（可直接复制）。结果默认只返回前约 8 KB，可用 lines 参数按行号范围拉取后续。每行行尾为成员元数据 token（如 0x06000005），可直接用于 decompile_member 的 token 参数反编译该成员。")]
    public static Task<string> Signature(
        [Description("要读取成员签名的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("目标类型的全限定名（必填），格式与 list_types 输出一致，例如 ILSpyMcp.Formatting.OutputFormatter")] string typeName = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 参数校验：typeName 必填
        if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数（类型全名，格式与 list_types 输出一致）。", out var typeNameError)) return Task.FromResult(typeNameError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 目标类型描述（参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的成员签名", IsListing: true);

        // 纯元数据读取并格式化（无缓存，秒回）
        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var typeHandle = MetadataNaming.FindType(reader, typeName);
            if (typeHandle is null) return Task.FromResult(MetadataNaming.BuildNotFoundMessage(reader, typeName));
            var signatureLines = SignatureRenderer.RenderTypeSignatures(reader, reader.GetTypeDefinition(typeHandle.Value));
            if (signatureLines.Count == 0) return Task.FromResult($"类型 {typeName} 无成员签名");
            return Task.FromResult(OutputFormatter.Format(signatureLines.ToList(), lines, context));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Task.FromResult($"无法读取程序集元数据：{ex.Message}");
        }
    }
}
