using DotNetDebuggerMcp.Configuration;
using DotNetDebugger.Decompiler.Decompiler;
using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Decompile;

/// <summary>
/// 将 .NET 程序集（dll/exe）反编译写入指定目录（全量或指定类型，typeName 支持逗号分隔多个类型）。结果写入磁盘而非标准输出，不做输出量截断。
/// </summary>
[McpServerToolType]
public static class DecompileToDirTool
{
    /// <summary>
    /// 反编译程序集写入指定目录（全量或单个类型），不经过缓存。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="outputDir">输出目录；反编译结果写入该目录而非标准输出（必填）。</param>
    /// <param name="typeName">指定则仅反编译该全限定类型名；省略则反编译整个程序集（project=true 时忽略）。</param>
    /// <param name="project">true=可编译项目形式（含 csproj 与命名空间目录）；false=单文件布局。</param>
    /// <param name="nestedDirectories">项目模式下是否按命名空间嵌套目录（默认 true）。</param>
    /// <param name="timeoutSeconds">本次反编译写盘超时秒数（默认 30，全量写盘大程序集可调大）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>写入结果提示或错误提示文本。</returns>
    [McpServerTool]
    [Description("将程序集反编译写入指定目录（结果在磁盘而非标准输出，不受输出量截断，写盘后可直接读取源码文件）。默认单文件布局：每个类型一个 {TypeName}.decompiled.cs（typeName 支持逗号分隔多类型；省略则全量）。project=true 时改为可编译项目形式（含 {程序集名}.csproj、按命名空间嵌套目录）；项目模式下 typeName 不生效（整程序集）。未找到的类型在结果中提示，部分成功也算成功。")]
    public static async Task<string> DecompileToDir(
        [Description(ToolParameterText.AssemblyParam)] string assembly = "",
        [Description("输出目录，反编译结果写入该目录而非标准输出（必填）")] string outputDir = "",
        [Description("仅反编译指定类型，类型全名；支持逗号分隔多个类型批量写盘（每个类型一个 .decompiled.cs 文件）；省略则反编译整个程序集（默认空=全量；project=true 时忽略）")] string typeName = "",
        [Description("是否以可编译项目形式输出（含 csproj 与命名空间目录，默认 false=单文件布局）")] bool project = false,
        [Description("项目模式下是否按命名空间嵌套目录（默认 true；project=false 时忽略）")] bool nestedDirectories = true,
        [Description(ToolParameterText.DiskTimeoutParam)] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        if (project)
        {
            return await ToolExecutor.RunToDisk(assembly, outputDir, timeoutSeconds, cancellationToken,
                (assemblyFull, outputFull, ct) => InProcessDecompiler.DecompileToProject(
                    assemblyFull, outputFull, nestedDirectories, ct));
        }

        return await ToolExecutor.RunToDisk(assembly, outputDir, timeoutSeconds, cancellationToken,
            (assemblyFull, outputFull, ct) => InProcessDecompiler.DecompileToDir(
                assemblyFull, outputFull, string.IsNullOrEmpty(typeName) ? null : typeName, ct));
    }
}