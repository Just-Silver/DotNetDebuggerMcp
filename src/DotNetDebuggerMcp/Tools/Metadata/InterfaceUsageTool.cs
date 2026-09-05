using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Formatting;
using DotNetDebugger.Decompiler.Metadata;
using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Tools.Metadata;

/// <summary>
/// 查询 .NET 程序集（dll/exe）中指定接口的实现者与调用点组合视图。
/// </summary>
[McpServerToolType]
public static class InterfaceUsageTool
{
    /// <summary>
    /// 输出指定接口的使用情况组合视图：程序集内实现该接口的类型（includeIndirect 时含全部间接实现者）、 方法体调用接口成员的调用点（类型全名::成员名 → 接口成员名
    /// 行）与成员签名引用该接口的类型： 元数据读取（PEReader），经共享缓存秒回。实现者段复用 hierarchy 的后代枚举，调用点段经 InterfaceUsageScanner
    /// 反扫全部非编译器生成类型方法体，引用段与 dependencies 的反向扫描同构。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">接口类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套用 + 或 .，泛型带 arity）（必填）。</param>
    /// <param name="includeIndirect">是否包含全部间接实现者（接口的子接口、实现者及其子类等间接后代，默认 false）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的接口实现者/调用点/引用三段或错误提示文本。</returns>
    [McpServerTool]
    [Description("输出指定接口的使用情况（对接口的一次性组合视图，含 hierarchy 的实现者段、call_graph 的调用点段、dependencies 的引用段，无需再分别调用）：程序集内实现它的类型（includeIndirect=true 时含全部间接实现者，如子接口、实现者及其子类）、方法体调用接口成员的调用点（类型全名::成员名 → 接口成员名 行）、以及成员签名引用该接口的类型。空段输出（无）占位。未找到类型时返回相近类型名提示；非接口类型返回中文提示（查类的继承/后代请用 hierarchy）。" + ToolParameterText.FooterPagination)]
    public static Task<string> InterfaceUsage(
        [Description(ToolParameterText.AssemblyParam)] string assembly = "",
        [Description("接口类型全名（必填），格式与 list_types 输出一致")] string typeName = "",
        [Description("是否包含全部间接实现者（如接口的子接口、实现者及其子类，默认 false）")] bool includeIndirect = false,
        [Description(ToolParameterText.LinesParam)] string lines = "",
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
        var signature = $"{CacheSignatures.InterfaceUsage}{CacheSignatures.Separator}{typeName}{CacheSignatures.Separator}{includeIndirect}";
        InterfaceUsageScanner? scanner = null;
        return Task.FromResult(ToolExecutor.RunMetadataPe(assemblyFull, signature, lines, context, (pe, reader) =>
        {
            var candidates = MetadataNaming.FindTypes(reader, typeName);
            if (candidates.Count > 1) throw new InvalidOperationException(MetadataNaming.BuildAmbiguityMessage(reader, typeName, candidates, "该类型名在归一化后存在同名类型，请换用不含歧义的完整类型名"));
            if (candidates.Count == 0) throw new InvalidOperationException(MetadataNaming.BuildNotFoundMessage(reader, typeName));
            var type = reader.GetTypeDefinition(candidates[0]);
            if (!type.Attributes.HasFlag(TypeAttributes.Interface))
            {
                // 非接口类型：返回中文提示而非输出貌似有效的伪结果（避免 agent 误以为该类型是接口）
                throw new InvalidOperationException(
                    $"{MetadataNaming.FullName(reader, type)} 不是接口类型（interface_usage 仅适用于接口；查类的继承/后代请用 hierarchy）");
            }

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
    /// 反向扫描：遍历程序集全部类型（跳过编译器生成类型与自身），凡成员签名引用含目标类型全名的来源类型全名，按元数据枚举序收集。 与 <see
    /// cref="DependenciesTool"/> 的反向扫描同构——成员签名（方法参数/返回、字段、属性、事件类型）中出现该接口的类型。
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