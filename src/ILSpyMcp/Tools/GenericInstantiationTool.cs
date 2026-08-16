using ILSpyMcp.Configuration;
using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Tools;

/// <summary>
/// 查询 .NET 程序集（dll/exe）中指定泛型类型被具体实例化的使用点组合视图。
/// </summary>
[McpServerToolType]
public static class GenericInstantiationTool
{
    /// <summary>
    /// 输出指定泛型类型在程序集内的实例化使用点：成员签名中的实例化（字段/方法/属性/事件签名中按具体类型参数实例化该
    /// 泛型类型的位置）与方法体调用中的实例化（方法体调用该泛型类型的泛型方法/实例化该泛型类型的位置）：元数据读取（PEReader），
    /// 经共享缓存秒回。目标定位兼容 list_types 全名（可带 arity）与短名/无 arity 输入（GenericBox 命中 GenericBox`1）。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">泛型类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套用 + 或 .，泛型可带 arity 或省略）（必填）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的实例化使用点两段或错误提示文本。</returns>
    [McpServerTool]
    [Description("输出指定泛型类型在程序集内被具体实例化的使用点两段：成员签名中的实例化（如 GenericBox<int>）与方法体调用中的实例化（如 new GenericBox<int>()、调用泛型方法 Echo<int>）。空段输出（无）占位。typeName 可带 arity（GenericBox`1）也可省略（GenericBox，短名亦命中）。" + ToolParameterText.FooterPagination)]
    public static Task<string> GenericInstantiations(
        [Description(ToolParameterText.AssemblyParam)] string assembly = "",
        [Description("泛型类型全名（必填），可带 arity（GenericBox`1）或省略（GenericBox），短名亦命中；格式与 list_types 输出一致")] string typeName = "",
        [Description(ToolParameterText.LinesParam)] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 参数校验：typeName 必填
        if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数（泛型类型全名，格式与 list_types 输出一致）。", out var typeError)) return Task.FromResult(typeError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"泛型类型 {typeName} 的实例化使用点", IsListing: true);

        // 元数据读取经共享缓存（命中直接返回，头部标注缓存命中）；未找到类型以异常抛提示、不入缓存
        var signature = $"{CacheSignatures.GenericInstantiations}{CacheSignatures.Separator}{typeName}";
        GenericInstantiationScanner? scanner = null;
        return Task.FromResult(ToolExecutor.RunMetadataPe(assemblyFull, signature, lines, context, (pe, _) =>
        {
            scanner = new GenericInstantiationScanner(pe);
            var result = scanner.Find(typeName);

            // 段落标题与实体均作为行进入 OutputFormatter（会被标注行号）；空段输出（无）占位
            var outputLines = new List<string>();
            SectionBuilder.Append(outputLines, "成员签名中的泛型实例化:", result.SignatureHits);
            SectionBuilder.Append(outputLines, "方法体调用中的泛型实例化:", result.CallHits);
            return outputLines;
        }, cancellationToken, degradedProvider: () => scanner?.AbortedBodies ?? 0));
    }
}
