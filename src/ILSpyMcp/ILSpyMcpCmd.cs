using ILSpyMcp.Configuration;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace ILSpyMcp;

/// <summary>
/// 命令行入口（McMaster.CommandLineUtils）。 无业务参数时启动 MCP 服务器（stdio 传输）；传入 -a/--assembly
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
    /// 输出指定类型全部成员签名（API 地图），需配合 -t。
    /// </summary>
    [Option("-s|--signatures", "输出指定类型全部成员签名（配合 -t）。", CommandOptionType.NoValue)]
    public bool Signatures { get; }

    /// <summary>
    /// 输出指定类型继承/接口关系，需配合 -t。
    /// </summary>
    [Option("-hc|--hierarchy", "输出指定类型继承/接口关系（配合 -t）。", CommandOptionType.NoValue)]
    public bool Hierarchy { get; }

    /// <summary>
    /// 输出指定类型成员签名内部引用，需配合 -t。
    /// </summary>
    [Option("-d|--dependencies", "输出指定类型成员签名内部引用（配合 -t）。", CommandOptionType.NoValue)]
    public bool Dependencies { get; }

    /// <summary>
    /// 输出指定类型方法体调用关系，需配合 -t。
    /// </summary>
    [Option("-cg|--callgraph", "输出指定类型方法体调用关系（配合 -t）。", CommandOptionType.NoValue)]
    public bool CallGraph { get; }

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
    /// 按行号范围读取结果，格式 start-end（1-based 含两端，单次最多约 32 KB）。
    /// </summary>
    [Option("-ln|--lines <start-end>", "按行号范围读取结果，格式 start-end（1-based 含两端，单次最多约 32 KB），如 200-400。", CommandOptionType.SingleValue)]
    public string Lines { get; } = null!;

    /// <summary>
    /// 本次操作超时秒数，默认 30；大程序集可调大。
    /// </summary>
    [Option("--timeout <seconds>", "本次操作超时秒数，默认 30；大程序集可调大。", CommandOptionType.SingleValue)]
    public int TimeoutSeconds { get; } = AppConfig.DefaultTimeoutSeconds;

    /// <summary>
    /// 检查当前 ilspymcp 是否有新版本。无需 -a。
    /// </summary>
    [Option("-c|--check", "检查当前 ilspymcp 是否有新版本（查询 NuGet，结果会话内缓存，仅首次真实检查）。", CommandOptionType.NoValue)]
    public bool Check { get; }

    /// <summary>
    /// 版本号文本（由 -v/--version 触发输出），与 NuGet 包版本保持一致。
    /// </summary>
    public string Version => AppConfig.NuGetPackageId + " " + (AppConfig.CurrentVersion?.ToString(3) ?? "unknown");

    /// <summary>
    /// 命令行分发：-c 走环境自检，-p 走 decompile_to_project，-o 走 decompile_to_dir，-s/-hc/-d/-cg 分别走 signature/hierarchy/
    /// dependencies/call_graph，-l 走 list_types，-mn 走 decompile_member，否则走 decompile；均复用对应 MCP 工具的校验与执行逻辑。
    /// </summary>
    internal static async Task<string> DispatchCliAsync(
        string assembly, string typeName, string memberName, string entityTypes,
        string outputDir, bool project, bool nestedDirectories, bool signatures, bool hierarchy, bool dependencies, bool callGraph,
        string lines, int timeoutSeconds, bool check,
        CancellationToken cancellationToken = default)
    {
        if (check)
        {
            // CLI -c 是主动调试入口：先刷新 NuGet 磁盘缓存（TTL/退避内不联网、失败静默降级），再组装报告， 避免无缓存记录时 NuGet 段永远留白；握手路径不 await（后台刷新供下次会话），这里等结果
            await AppServices.Updater.RefreshIfStaleAsync();
            return await CheckTool.CheckStatus();
        }
        if (project && !string.IsNullOrEmpty(outputDir))
        {
            return await DecompileToProjectTool.DecompileToProject(assembly, outputDir, nestedDirectories, timeoutSeconds, cancellationToken);
        }
        if (!string.IsNullOrEmpty(outputDir))
        {
            return await DecompileToDirTool.DecompileToDir(assembly, outputDir, typeName, nestedDirectories, timeoutSeconds, cancellationToken);
        }
        if (signatures)
        {
            return await SignatureTool.Signature(assembly, typeName, lines, cancellationToken);
        }
        if (hierarchy)
        {
            return await HierarchyTool.Hierarchy(assembly, typeName, lines, cancellationToken);
        }
        if (dependencies)
        {
            return await DependenciesTool.Dependencies(assembly, typeName, lines, cancellationToken);
        }
        if (callGraph)
        {
            return await CallGraphTool.CallGraph(assembly, typeName, lines, cancellationToken);
        }
        if (!string.IsNullOrEmpty(entityTypes))
        {
            return await ListTypesTool.ListTypes(assembly, entityTypes, lines, cancellationToken);
        }
        if (!string.IsNullOrEmpty(memberName))
        {
            return await DecompileMemberTool.DecompileMember(assembly, typeName, memberName, token: "", lines, timeoutSeconds, cancellationToken);
        }
        return await DecompileTool.Decompile(assembly, typeName, lines, timeoutSeconds, cancellationToken);
    }

    /// <summary>
    /// 默认执行：未指定业务参数（-a 或 -c）时启动 MCP 服务器；否则以命令行模式执行并输出结果。
    /// </summary>
    private async Task<int> OnExecuteAsync(CommandLineApplication app)
    {
        if (!string.IsNullOrEmpty(Assembly) || Check)
        {
            Console.WriteLine(await DispatchCliAsync(
                Assembly, TypeName, MemberName, EntityTypes,
                OutputDir, Project, NestedDirectories, Signatures, Hierarchy, Dependencies, CallGraph,
                Lines, TimeoutSeconds, Check));
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        // 握手期先执行更新检查（报告 ilspymcp 是否有新版本），报告由 StatusReport 会话内缓存、与 CLI -c 同源；
        // 同步读磁盘缓存，无有效检查记录时返回空报告（不注入）。报告注入 ServerInstructions，让 agent 会话起始即可感知版本更新状态
        string report;
        try
        {
            report = await AppServices.StatusReport.Value;
        }
        catch
        {
            report = ""; // 更新检查异常不阻断 MCP 启动：降级为不注入提示，核心反编译功能不受影响
        }
        builder.Services.AddMcpServer(o =>
        {
            if (!string.IsNullOrEmpty(report)) o.ServerInstructions = report;
        })
        .WithStdioServerTransport().WithToolsFromAssembly();
        // 后台 fire-and-forget 预检（TTL/退避内不联网）：刷新 NuGet 磁盘缓存供下一次会话使用，不 await 以免阻塞启动
        _ = AppServices.Updater.RefreshIfStaleAsync();
        await builder.Build().RunAsync();
        return 0;
    }
}