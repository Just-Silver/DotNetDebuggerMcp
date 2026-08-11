using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;

// 工具方法名 Hierarchy 与 Metadata.Hierarchy 类同名会遮蔽，此处显式别名
using MetadataHierarchy = ILSpyMcp.Metadata.Hierarchy;

namespace ILSpyMcp.Tools;

/// <summary>
/// 查询 .NET 程序集（dll/exe）中指定类型的继承/接口关系。
/// </summary>
[McpServerToolType]
public static class HierarchyTool
{
    /// <summary>
    /// 输出指定类型的基类链、实现的接口与程序集内直接继承/实现它的类型：纯元数据读取（PEReader），秒回，无需 ilspycmd 安装。
    /// 基类链从目标类型上溯到 System.Object，接口列表为 InterfaceImplementations 表，后代为程序集内直接基类/直接接口等于目标类型的类型
    /// （跳过编译器生成类型）。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套用 + 或 .，泛型带 arity）（必填）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的继承/接口关系或错误提示文本。</returns>
    [McpServerTool]
    [Description("查询 .NET 程序集（dll/exe）中指定类型的继承/接口关系：输出基类链（上溯到 System.Object）、类型实现的接口、以及程序集内直接继承它或实现其接口的类型。纯元数据秒回、无需 ilspycmd 安装。typeName 为类型全名，格式与 list_types 输出一致，可直接复制使用。结果默认只返回前 200 行。")]
    public static Task<string> Hierarchy(
        [Description("要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("类型全名（必填），格式与 list_types 输出一致（命名空间.类型，嵌套类型用 + 或 . 分隔，泛型类型带 arity 如 GenericBox`1），例如 ILSpyMcp.Processes.ProcessRunner")] string typeName = "",
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
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的继承/接口关系", IsListing: true);

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
            var baseChain = MetadataHierarchy.GetBaseChain(reader, type);
            var interfaces = MetadataHierarchy.GetInterfaces(reader, type);
            var descendants = MetadataHierarchy.GetDescendants(reader, type, fullName);
            if (baseChain.Count == 0 && interfaces.Count == 0 && descendants.Count == 0)
            {
                return Task.FromResult($"类型 {typeName} 无继承与接口信息");
            }

            // 段落标题与全名均作为行进入 OutputFormatter（会被标注行号）；各段为空时省略对应段落
            var lines = new List<string>();
            if (baseChain.Count > 0)
            {
                lines.Add("基类链:");
                lines.AddRange(baseChain);
            }
            if (interfaces.Count > 0)
            {
                lines.Add("接口:");
                lines.AddRange(interfaces);
            }
            if (descendants.Count > 0)
            {
                lines.Add("程序集内继承/实现此类型的类型:");
                lines.AddRange(descendants);
            }
            return Task.FromResult(OutputFormatter.Format(lines, "", context));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return Task.FromResult($"无法读取程序集元数据：{ex.Message}");
        }
    }
}
