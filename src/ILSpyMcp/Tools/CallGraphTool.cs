using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace ILSpyMcp.Tools;

/// <summary>
/// 查询 .NET 程序集（dll/exe）中指定类型的方法体调用关系。
/// </summary>
[McpServerToolType]
public static class CallGraphTool
{
    /// <summary>
    /// 输出指定类型全部方法体 IL 调用指令（call/callvirt/newobj/ldftn/ldvirtftn/jmp/calli）引用的程序集内部类型，
    /// 以及程序集内方法体调用了它的类型：元数据读取（PEReader），经共享缓存秒回。
    /// includeExternal 为 true 时追加输出方法体调用的跨程序集外部类型（带程序集归属）。
    /// 与 dependencies 的成员签名引用互补：本工具基于方法体执行流（行为级），签名级引用另见 dependencies。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套用 + 或 .，泛型带 arity）（类型级反查必填；提供 token 时可不填）。</param>
    /// <param name="token">方法元数据 token（取 signature 行尾或 #MEMBER 的 token）：按 token 反向定位程序集内调用该方法的成员（方法级调用点）。</param>
    /// <param name="includeExternal">是否同时输出跨程序集外部类型引用（如 BCL/NuGet，带程序集归属，默认 false；token 分支下忽略）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的方法体调用关系或错误提示文本。</returns>
    [McpServerTool]
    [Description("查询 .NET 程序集（dll/exe）中指定类型的方法体调用关系：扫描其全部方法体的调用指令，输出被调用的程序集内部类型（编译器生成类型不计），以及程序集内哪些类型的方法体调用了它（反向扫描全部类型）。includeExternal 为 true 时追加第三段输出方法体调用的跨程序集外部类型（如 BCL/NuGet，带程序集归属，格式 全名 [程序集名]，默认 false）。与 dependencies 的成员签名引用不同，本工具反映的是执行流中的实际调用。typeName 为类型全名，格式与 list_types 输出一致，可直接复制使用。另提供 token 参数做方法级细化：传入方法 token（取 signature 行尾或 #MEMBER 分隔行的 token，如 0x06000005）时反向定位程序集内哪些方法体调用了这个具体方法，输出 类型全名::成员签名 调用点行，typeName 可不填、includeExternal 忽略。结果可能为空（某段无调用时输出（无）占位）；大型程序集反向扫描可能较慢。结果默认只返回前约 8 KB，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> CallGraph(
        [Description("要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套类型用 + 或 . 分隔，泛型类型带 arity 如 GenericBox`1），例如 ILSpyMcp.Caching.DecompileCache；类型级反查必填，提供 token 时可不填")] string typeName = "",
        [Description("方法元数据 token（取 signature 行尾或 #MEMBER 分隔行的 token，如 0x06000005）：按 token 反向定位程序集内哪些方法体调用了这个具体方法，输出 类型全名::成员签名 调用点行；提供时 typeName 可不填、includeExternal 忽略。默认空=不启用，走类型级反查")] string token = "",
        [Description("是否同时输出跨程序集外部类型引用（如 BCL/NuGet，带程序集归属，格式 全名 [程序集名]，默认 false）")] bool includeExternal = false,
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // token 分支：按方法 token 反向定位调用点（typeName 可不填、includeExternal 忽略）
        if (!string.IsNullOrEmpty(token))
        {
            if (!ArgumentValidators.ValidateToken(token, out var tokenError)) return Task.FromResult(tokenError);
            return RunTokenCallGraph(assembly, typeName, token, lines, cancellationToken);
        }
        // 参数校验：typeName 必填
        if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数（类型全名，格式与 list_types 输出一致）。", out var typeError)) return Task.FromResult(typeError);
        return RunTypeCallGraph(assembly, typeName, includeExternal, lines, cancellationToken);
    }

    /// <summary>
    /// 类型级反查：扫描指定类型方法体调用关系（内部类型 + 可选外部类型 + 反向调用者），经共享缓存秒回。
    /// </summary>
    private static Task<string> RunTypeCallGraph(string assembly, string typeName, bool includeExternal, string lines, CancellationToken cancellationToken)
    {
        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的方法体调用关系", IsListing: true);

        // 元数据读取经共享缓存（命中直接返回，头部标注缓存命中）；未找到类型以异常抛提示、不入缓存
        var signature = $"call-graph\u001F{typeName}\u001F{includeExternal}";
        return Task.FromResult(ToolExecutor.RunMetadataPe(assemblyFull, signature, lines, context, (pe, reader) =>
        {
            var handle = MetadataNaming.FindType(reader, typeName);
            if (handle is null) throw new InvalidOperationException(MetadataNaming.BuildNotFoundMessage(reader, typeName));
            var type = reader.GetTypeDefinition(handle.Value);

            var fullName = MetadataNaming.FullName(reader, type);
            var (calls, external) = includeExternal
                ? CallGraphExtractor.ExtractMethodBodyCallTypesWithExternal(pe, type)
                : (CallGraphExtractor.ExtractMethodBodyCallTypes(pe, type), Array.Empty<string>());
            var callers = CallGraphExtractor.FindCallers(pe, type, fullName);

            // 段落标题与全名均作为行进入 OutputFormatter（会被标注行号）；空段输出（无）占位
            var outputLines = new List<string>();
            SectionBuilder.Append(outputLines, "方法体调用的内部类型:", calls);
            if (includeExternal) SectionBuilder.Append(outputLines, "方法体调用的外部类型:", external);
            SectionBuilder.Append(outputLines, "程序集内方法体调用此类型的类型:", callers);
            return outputLines;
        }, cancellationToken));
    }

    /// <summary>
    /// 方法级细化：按 token 反向定位程序集内调用该方法的成员（调用点），经共享缓存秒回。
    /// </summary>
    private static Task<string> RunTokenCallGraph(string assembly, string typeName, string token, string lines, CancellationToken cancellationToken)
    {
        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var tokenAssemblyFull) is { } tokenPathError) return Task.FromResult(tokenPathError);
        cancellationToken.ThrowIfCancellationRequested();

        var targetDesc = string.IsNullOrEmpty(typeName) ? $"方法 {token}（调用点）" : $"类型 {typeName} 的方法 {token}（调用点）";
        var tokenContext = new FormatContext(tokenAssemblyFull, targetDesc, IsListing: true);

        // 元数据读取经共享缓存（命中直接返回，头部标注缓存命中）
        var tokenSignature = $"call-graph-token\u001F{token}";
        return Task.FromResult(ToolExecutor.RunMetadataPe(tokenAssemblyFull, tokenSignature, lines, tokenContext, (pe, _) =>
        {
            var callers = CallGraphExtractor.FindMethodCallers(pe, token);
            var outputLines = new List<string>();
            SectionBuilder.Append(outputLines, "方法体调用此方法的成员:", callers);
            return outputLines;
        }, cancellationToken));
    }
}
