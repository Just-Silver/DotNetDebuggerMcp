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
/// 查询 .NET 程序集（dll/exe）中指定接口的实现者与调用点组合视图。
/// </summary>
[McpServerToolType]
public static class InterfaceUsageTool
{
    /// <summary>
    /// 输出指定接口的使用情况组合视图：程序集内实现该接口的类型（includeIndirect 时含全部间接实现者）、
    /// 方法体调用接口成员的调用点（类型全名::成员名 → 接口成员名 行）与成员签名引用该接口的类型：
    /// 元数据读取（PEReader），经共享缓存秒回。实现者段复用 hierarchy 的后代枚举，调用点段经
    /// InterfaceUsageScanner 反扫全部非编译器生成类型方法体，引用段与 dependencies 的反向扫描同构。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">接口类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套用 + 或 .，泛型带 arity）（必填）。</param>
    /// <param name="includeIndirect">是否包含全部间接实现者（接口的子接口、实现者及其子类等间接后代，默认 false）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的接口实现者/调用点/引用三段或错误提示文本。</returns>
    [McpServerTool]
    [Description("查询 .NET 程序集（dll/exe）中指定接口的使用情况，输出三段：程序集内实现该接口的类型（includeIndirect 为 true 时含全部间接实现者，如接口的子接口、实现者及其子类，一次返回全部、默认 false）；方法体调用接口成员的调用点——反扫全部非编译器生成类型方法体的调用指令，凡调用目标声明类型为接口成员时输出 类型全名::成员名 → 接口成员名 行（覆盖内部接口 MethodDef 直判与跨程序集外部接口 MemberRef 判定，含泛型实例化调用解包）；成员签名引用该接口的类型（签名级引用，与调用点互补，反映方法参数/返回等签名中直接用到该接口的类型）。空段输出（无）占位。typeName 为接口类型全名，格式与 list_types 输出一致，可直接复制使用。适用于回答「哪些类型实现了这个接口、程序集内哪些地方调用了它的成员」。大型程序集反向扫描可能较慢。结果默认只返回前约 8 KB，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> InterfaceUsage(
        [Description("要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("接口类型全名（必填），格式与 list_types 输出一致（命名空间.类型，嵌套类型用 + 或 . 分隔，泛型类型带 arity 如 IBox`1），例如 ILSpyMcp.Samples.IWorker")] string typeName = "",
        [Description("是否包含全部间接实现者（接口的子接口、实现者及其子类等间接后代，一次返回全部、免递归多次调用，默认 false）")] bool includeIndirect = false,
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 参数校验：typeName 必填
        if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数（接口类型全名，格式与 list_types 输出一致）。", out var typeError)) return Task.FromResult(typeError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"接口 {typeName} 的实现者与调用点", IsListing: true);

        // 元数据读取经共享缓存（命中直接返回，头部标注缓存命中）；未找到类型以异常抛提示、不入缓存
        var signature = $"interface-usage\u001F{typeName}\u001F{includeIndirect}";
        InterfaceUsageScanner? scanner = null;
        return Task.FromResult(ToolExecutor.RunMetadataPe(assemblyFull, signature, lines, context, (pe, reader) =>
        {
            var candidates = MetadataNaming.FindTypes(reader, typeName);
            if (candidates.Count > 1) throw new InvalidOperationException(MetadataNaming.BuildAmbiguityMessage(reader, typeName, candidates));
            if (candidates.Count == 0) throw new InvalidOperationException(MetadataNaming.BuildNotFoundMessage(reader, typeName));
            var type = reader.GetTypeDefinition(candidates[0]);

            var fullName = MetadataNaming.FullName(reader, type);
            var implementers = includeIndirect
                ? Hierarchy.GetDescendantsIncludingIndirect(reader, type, fullName)
                : Hierarchy.GetDescendants(reader, type, fullName);
            scanner = new InterfaceUsageScanner(pe);
            var callSites = scanner.FindCallSites(candidates[0], fullName);
            var referrers = FindReferrers(reader, type, fullName);

            // 段落标题与实体均作为行进入 OutputFormatter（会被标注行号）；空段输出（无）占位
            var outputLines = new List<string>();
            SectionBuilder.Append(outputLines, "实现该接口的类型:", implementers);
            SectionBuilder.Append(outputLines, "方法体调用接口成员的调用点:", callSites);
            SectionBuilder.Append(outputLines, "成员签名引用该接口的类型:", referrers);
            return outputLines;
        }, cancellationToken, degradedProvider: () => scanner?.AbortedBodies ?? 0));
    }

    /// <summary>
    /// 反向扫描：遍历程序集全部类型（跳过编译器生成类型与自身），凡成员签名引用含目标类型全名的来源类型全名，按元数据枚举序收集。
    /// 与 <see cref="DependenciesTool"/> 的反向扫描同构——成员签名（方法参数/返回、字段、属性、事件类型）中出现该接口的类型。
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
