using ILSpyMcp.Infrastructure;
using ILSpyMcp.Tools;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ILSpyMcp;

/// <summary>
/// 命令行入口（McMaster.CommandLineUtils，与 ilspycmd 实现方式一致）。 无业务参数时启动 MCP 服务器（stdio 传输）；传入 -a/--assembly
/// 等参数时 以命令行形式直接执行反编译/列类型/写盘，复用与 MCP 工具相同的校验与执行逻辑，便于调试。
/// -v/--version 输出版本号，-h/--help 输出帮助信息。
/// </summary>
[HelpOption("-h|--help")]
[VersionOptionFromMember("-v|--version", Description = "显示 ilspymcp 版本号。", MemberName = nameof(Version))]
public class ILSpyMcpCmd
{
    /// <summary>
    /// 要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径。指定后进入命令行模式。
    /// </summary>
    [Option("-a|--assembly <file>", "要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径。", CommandOptionType.SingleValue)]
    public string Assembly { get; } = null!;

    /// <summary>
    /// 仅反编译指定全限定类型名，例如 System.String。
    /// </summary>
    [Option("-t|--type <type-name>", "仅反编译指定全限定类型名，例如 System.String。", CommandOptionType.SingleValue)]
    public string TypeName { get; } = null!;

    /// <summary>
    /// 在指定类型内按成员名子串搜索并反编译匹配的成员，需配合 -t。
    /// </summary>
    [Option("-mn|--membername <substring>", "在指定类型内按成员名子串搜索并反编译匹配的成员（需配合 -t 指定类型）。", CommandOptionType.SingleValue)]
    public string MemberName { get; } = null!;

    /// <summary>
    /// C# 语言版本，如 CSharp8_0、CSharp12_0、CSharp13_0、Latest。
    /// </summary>
    [Option("-lv|--languageversion <version>", "C# 语言版本，如 CSharp8_0、CSharp12_0、CSharp13_0、Latest。", CommandOptionType.SingleValue)]
    public string LanguageVersion { get; } = null!;

    /// <summary>
    /// 列出程序集中的实体类型：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 csi。
    /// </summary>
    [Option("-l|--list <entity-types>", "列出程序集中的实体类型：c=class, i=interface, s=struct, d=delegate, e=enum；可组合多个字母，如 csi。", CommandOptionType.SingleValue)]
    public string EntityTypes { get; } = null!;

    /// <summary>
    /// 反编译写入的目录；指定后结果写入磁盘而非标准输出。
    /// </summary>
    [Option("-o|--outputdir <directory>", "反编译写入的目录；指定后结果写入磁盘而非标准输出。", CommandOptionType.SingleValue)]
    public string OutputDir { get; } = null!;

    /// <summary>
    /// 以可编译项目形式反编译（每个类型一个源码文件），需配合 -o。
    /// </summary>
    [Option("-p|--project", "以可编译项目形式反编译（每个类型一个源码文件），需配合 -o。", CommandOptionType.NoValue)]
    public bool Project { get; }

    /// <summary>
    /// 输出到目录时按命名空间使用嵌套目录。
    /// </summary>
    [Option("--nested-directories", "输出到目录时按命名空间使用嵌套目录。", CommandOptionType.NoValue)]
    public bool NestedDirectories { get; }

    /// <summary>
    /// 按行号范围读取结果，格式 start-end（1-based 含两端，单次最多 500 行）。
    /// </summary>
    [Option("-ln|--lines <start-end>", "按行号范围读取结果，格式 start-end（1-based 含两端，单次最多 500 行），如 200-400。", CommandOptionType.SingleValue)]
    public string Lines { get; } = null!;

    /// <summary>
    /// 本次操作超时秒数，默认 30；大程序集可调大。
    /// </summary>
    [Option("--timeout <seconds>", "本次操作超时秒数，默认 30；大程序集可调大。", CommandOptionType.SingleValue)]
    public int TimeoutSeconds { get; } = AppConfig.DefaultTimeoutSeconds;

    /// <summary>
    /// 版本号文本（由 -v/--version 触发输出），与 NuGet 包版本保持一致。
    /// </summary>
    public string Version => "ilspymcp " + (System.Reflection.Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "unknown");

    /// <summary>
    /// 命令行分发：-o 走 decompile_to_dir，-l 走 list_types，-mn 走 decompile_member，否则走 decompile；均复用对应 MCP 工具的校验与执行逻辑。
    /// </summary>
    internal static async Task<string> DispatchCliAsync(
        string assembly, string typeName, string memberName, string languageVersion, string entityTypes,
        string outputDir, bool project, bool nestedDirectories, string lines, int timeoutSeconds)
    {
        if (!string.IsNullOrEmpty(outputDir))
        {
            return await DecompileToDirTool.DecompileToDir(assembly, outputDir, project, typeName, nestedDirectories, languageVersion, timeoutSeconds);
        }
        if (!string.IsNullOrEmpty(entityTypes))
        {
            return await ListTypesTool.ListTypes(assembly, entityTypes, lines, timeoutSeconds);
        }
        if (!string.IsNullOrEmpty(memberName))
        {
            return await DecompileMemberTool.DecompileMember(assembly, typeName, memberName, lines, languageVersion, timeoutSeconds);
        }
        return await DecompileTool.Decompile(assembly, typeName, lines, languageVersion, timeoutSeconds);
    }

    /// <summary>
    /// 默认执行：未指定业务参数时启动 MCP 服务器；否则以命令行模式执行并输出结果。
    /// </summary>
    private async Task<int> OnExecuteAsync(CommandLineApplication app)
    {
        if (!string.IsNullOrEmpty(Assembly))
        {
            Console.WriteLine(await DispatchCliAsync(
                Assembly, TypeName, MemberName, LanguageVersion, EntityTypes,
                OutputDir, Project, NestedDirectories, Lines, TimeoutSeconds));
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        builder.Services.AddMcpServer().WithStdioServerTransport().WithToolsFromAssembly();
        await builder.Build().RunAsync();
        return 0;
    }
}