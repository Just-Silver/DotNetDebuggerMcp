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
/// 查询 .NET 程序集（dll/exe）中指定类型的成员签名内部类型引用。
/// </summary>
[McpServerToolType]
public static class DependenciesTool
{
    /// <summary>
    /// 输出指定类型成员签名（方法参数/返回、字段、属性、事件类型）引用的程序集内部类型，以及程序集内引用它的类型：
    /// 元数据读取（PEReader），经共享缓存秒回。includeExternal 为 true 时追加输出跨程序集外部类型引用（带程序集归属）。
    /// 正向与反向都只覆盖成员签名引用，不含继承关系（由 hierarchy 工具覆盖）。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套用 + 或 .，泛型带 arity）（必填）。</param>
    /// <param name="includeExternal">是否同时输出跨程序集外部类型引用（如 BCL/NuGet，带程序集归属，默认 false）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的内部类型引用关系或错误提示文本。</returns>
    [McpServerTool]
    [Description("查询 .NET 程序集（dll/exe）中指定类型的成员签名内部引用：输出其成员签名（方法参数/返回、字段、属性、事件类型）引用的程序集内部类型（跨程序集类型不计），以及程序集内哪些类型的成员签名引用了它（反向扫描全部类型）。includeExternal 为 true 时追加第三段输出成员签名引用的跨程序集外部类型（如 BCL/NuGet，带程序集归属，格式 全名 [程序集名]，默认 false）。不含继承/接口关系——这类关系请用 hierarchy 工具。typeName 为类型全名，格式与 list_types 输出一致，可直接复制使用。结果可能为空（某段无引用时输出（无）占位）；大型程序集反向扫描可能较慢。结果默认只返回前约 8 KB，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> Dependencies(
        [Description("要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("类型全名（必填），格式与 list_types 输出一致（命名空间.类型，嵌套类型用 + 或 . 分隔，泛型类型带 arity 如 GenericBox`1），例如 ILSpyMcp.Caching.DecompileCache")] string typeName = "",
        [Description("是否同时输出跨程序集外部类型引用（如 BCL/NuGet，带程序集归属，格式 全名 [程序集名]，默认 false）")] bool includeExternal = false,
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 参数校验：typeName 必填
        if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数（类型全名，格式与 list_types 输出一致）。", out var typeError)) return Task.FromResult(typeError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的成员签名内部引用", IsListing: true);

        // 元数据读取经共享缓存（命中直接返回，头部标注缓存命中）；未找到类型以异常抛提示、不入缓存
        var signature = $"dependencies\u001F{typeName}\u001F{includeExternal}";
        return Task.FromResult(ToolExecutor.RunMetadata(assemblyFull, signature, lines, context, _ =>
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var handle = MetadataNaming.FindType(reader, typeName);
            if (handle is null) throw new InvalidOperationException(MetadataNaming.BuildNotFoundMessage(reader, typeName));
            var type = reader.GetTypeDefinition(handle.Value);

            var fullName = MetadataNaming.FullName(reader, type);
            var (references, external) = includeExternal
                ? ReferenceExtractor.ExtractMemberSignatureReferencesWithExternal(reader, type)
                : (ReferenceExtractor.ExtractMemberSignatureReferences(reader, type), Array.Empty<string>());
            var referrers = FindReferrers(reader, type, fullName);

            // 段落标题与全名均作为行进入 OutputFormatter（会被标注行号）；空段输出（无）占位
            var outputLines = new List<string>();
            outputLines.Add("成员签名引用的内部类型:");
            if (references.Count == 0) outputLines.Add("（无）");
            else outputLines.AddRange(references);
            if (includeExternal)
            {
                outputLines.Add("成员签名引用的外部类型:");
                if (external.Count == 0) outputLines.Add("（无）");
                else outputLines.AddRange(external);
            }
            outputLines.Add("程序集内引用此类型的类型:");
            if (referrers.Count == 0) outputLines.Add("（无）");
            else outputLines.AddRange(referrers);
            return outputLines;
        }, cancellationToken));
    }

    /// <summary>
    /// 反向扫描：遍历程序集全部类型（跳过编译器生成类型与自身），凡成员签名引用含目标类型全名的来源类型全名，按元数据枚举序收集。
    /// </summary>
    private static List<string> FindReferrers(MetadataReader reader, TypeDefinition type, string typeFullName)
    {
        var result = new List<string>();
        foreach (var handle in reader.TypeDefinitions)
        {
            var candidate = reader.GetTypeDefinition(handle);
            if (CompilerGeneratedFilter.IsCompilerGenerated(reader, candidate)) continue;
            var candidateName = MetadataNaming.FullName(reader, candidate);
            if (candidateName == typeFullName) continue;
            if (ReferenceExtractor.ExtractMemberSignatureReferences(reader, candidate).Contains(typeFullName))
            {
                result.Add(candidateName);
            }
        }
        return result;
    }
}
