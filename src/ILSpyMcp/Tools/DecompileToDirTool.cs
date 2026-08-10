using ILSpyMcp.Infrastructure;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;
using System.ComponentModel;

namespace ILSpyMcp.Tools;

/// <summary>
/// 将 .NET 程序集（dll/exe）反编译写入指定目录（全量或项目形式，可指定单个类型）。结果写入磁盘而非标准输出，不做行数截断。
/// </summary>
[McpServerToolType]
public static class DecompileToDirTool
{
    /// <summary>
    /// 反编译程序集写入指定目录（全量或项目形式），不经过缓存。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="outputDir">输出目录；反编译结果写入该目录而非标准输出（必填）。</param>
    /// <param name="project">以可编译项目形式反编译（每个类型一个源码文件）。</param>
    /// <param name="typeName">仅反编译指定全限定类型名；省略则反编译整个程序集。</param>
    /// <param name="nestedDirectories">输出到目录时按命名空间使用嵌套目录。</param>
    /// <param name="languageVersion">C# 语言版本，如 CSharp8_0、CSharp12_0、CSharp13_0、Latest。</param>
    /// <param name="timeoutSeconds">本次反编译写盘超时秒数（默认 30，全量写盘大程序集可调大）。</param>
    /// <returns>写入结果提示或错误提示文本。</returns>
    [McpServerTool]
    [Description("将 .NET 程序集（dll/exe）反编译写入指定目录（全量或项目形式，可指定单个类型）。结果写入磁盘而非标准输出，不做行数截断；读取源码请使用 opencode 内置工具。")]
    public static async Task<string> DecompileToDir(
        [Description("要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("输出目录；反编译结果写入该目录而非标准输出（必填）")] string outputDir = "",
        [Description("以可编译项目形式反编译（每个类型一个源码文件）")] bool project = false,
        [Description("仅反编译指定全限定类型名，例如 System.String；省略则反编译整个程序集")] string typeName = "",
        [Description("输出到目录时按命名空间使用嵌套目录")] bool nestedDirectories = false,
        [Description("C# 语言版本，如 CSharp8_0、CSharp12_0、CSharp13_0、Latest")] string languageVersion = "",
        [Description("本次反编译写盘超时秒数，默认 30；全量写盘大程序集可调大")] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds)
    {
        // 前置检查：ilspycmd 已安装且 assembly 参数有效；未通过直接返回提示
        if (await ToolPreflight.CheckAsync(assembly) is { } preflightError) return preflightError;
        // 参数校验：outputDir 必填且路径合法
        if (!ArgumentValidators.ValidateOutputDir(outputDir, out var argError)) return argError;
        // 参数校验：languageVersion 可选但需合法
        if (!ArgumentValidators.ValidateLanguageVersion(languageVersion, out var lvError)) return lvError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        // 由参数结构统一生成命令行（本工具不经缓存，签名字段不使用）
        var cwd = Environment.CurrentDirectory;
        string assemblyFull, outputFull;
        try
        {
            assemblyFull = Path.GetFullPath(assembly, cwd);
            outputFull = Path.GetFullPath(outputDir, cwd);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            return $"路径非法：{ex.Message}";
        }
        var command = new ToolCommand(ToolCommand.DefaultExecutable, assemblyFull,
            new ToolParameter("-o", outputFull),
            ToolParameter.Switch("-p", project),
            ToolParameter.Optional("-t", typeName),
            ToolParameter.Switch("--nested-directories", nestedDirectories),
            ToolParameter.Optional("-lv", languageVersion));

        // 执行反编译写盘；退出码非 0 时返回 stderr，成功则返回输出目录与文件计数（超时由 timeoutSeconds 参数控制，默认 30s）
        var result = await AppServices.Process.RunAsync(command.Executable, command.Args, cwd, TimeSpan.FromSeconds(timeoutSeconds));
        if (result.Code != 0) return $"ilspycmd 退出码: {result.Code}\n{result.Stderr}";
        // 枚举输出目录下文件总数供 agent 决策后续动作；枚举失败时退回基础提示，不拖垮工具
        try
        {
            var count = Directory.GetFiles(outputFull, "*", SearchOption.AllDirectories).Length;
            return $"已写入 {outputFull}（{count} 个文件，来源 {assemblyFull}）";
        }
        catch
        {
            return $"已写入 {outputFull}（来源 {assemblyFull}）";
        }
    }
}