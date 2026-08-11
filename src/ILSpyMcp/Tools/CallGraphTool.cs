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
/// 查询 .NET 程序集（dll/exe）中指定类型的方法体调用关系。
/// </summary>
[McpServerToolType]
public static class CallGraphTool
{
    /// <summary>
    /// 输出指定类型全部方法体 IL 调用指令（call/callvirt/newobj/ldftn/ldvirtftn/jmp/calli）引用的程序集内部类型，
    /// 以及程序集内方法体调用了它的类型：纯元数据读取（PEReader），秒回，无需 ilspycmd 安装。
    /// 与 dependencies 的成员签名引用互补：本工具基于方法体执行流（行为级），签名级引用另见 dependencies。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套用 + 或 .，泛型带 arity）（必填）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前 200 行。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的方法体调用关系或错误提示文本。</returns>
    [McpServerTool]
    [Description("查询 .NET 程序集（dll/exe）中指定类型的方法体调用关系：扫描其全部方法体 IL 的调用指令（call/callvirt/newobj/ldftn/ldvirtftn/jmp/calli），输出被调用的程序集内部类型（跨程序集类型不计、编译器生成类型不计），以及程序集内哪些类型的方法体调用了它（反向扫描全部类型）。与 dependencies 的成员签名引用不同，本工具反映的是执行流中的实际调用。纯元数据秒回、无需 ilspycmd 安装。typeName 为类型全名，格式与 list_types 输出一致，可直接复制使用。结果可能为空（某段无调用时输出（无）占位）；反向扫描需解码全部方法体 IL，大型程序集可能较慢。结果默认只返回前 200 行，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> CallGraph(
        [Description("要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("类型全名（必填），格式与 list_types 输出一致（命名空间.类型，嵌套类型用 + 或 . 分隔，泛型类型带 arity 如 GenericBox`1），例如 ILSpyMcp.Caching.DecompileCache")] string typeName = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多 500 行），例如 \"200-400\"；缺省返回前 200 行")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取，不做 ilspycmd 安装检测）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 参数校验：typeName 必填
        if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数（类型全名，格式与 list_types 输出一致）。", out var typeError)) return Task.FromResult(typeError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的方法体调用关系", IsListing: true);

        // 纯元数据读取并格式化（无子进程、无缓存，秒回）
        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var handle = MetadataNaming.FindType(reader, typeName);
            if (handle is null) return Task.FromResult($"未找到类型 {typeName}");
            var type = reader.GetTypeDefinition(handle.Value);

            var fullName = MetadataNaming.FullName(reader, type);
            var calls = CallGraphExtractor.ExtractMethodBodyCallTypes(pe, type);
            var callers = CallGraphExtractor.FindCallers(pe, type, fullName);

            // 段落标题与全名均作为行进入 OutputFormatter（会被标注行号）；空段输出（无）占位
            var outputLines = new List<string>();
            outputLines.Add("方法体调用的内部类型:");
            if (calls.Count == 0) outputLines.Add("（无）");
            else outputLines.AddRange(calls);
            outputLines.Add("程序集内方法体调用此类型的类型:");
            if (callers.Count == 0) outputLines.Add("（无）");
            else outputLines.AddRange(callers);
            return Task.FromResult(OutputFormatter.Format(outputLines, lines, context));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Task.FromResult($"无法读取程序集元数据：{ex.Message}");
        }
    }
}
