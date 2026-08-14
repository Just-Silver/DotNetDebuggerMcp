using ILSpyMcp.Configuration;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;
using ILSpyMcp.UpdateCheck;
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
    /// 仅反编译指定全限定类型名，例如 System.String；配合 -o 写盘时支持逗号分隔多个类型。
    /// </summary>
    [Option("-t|--type <type-name>", "仅反编译指定全限定类型名，例如 System.String；配合 -o 写盘时支持逗号分隔多个类型。", CommandOptionType.SingleValue)]
    public string TypeName { get; } = null!;

    /// <summary>
    /// 按成员名子串搜索并反编译匹配的成员：提供 -t 时在指定类型内搜索，省略 -t 时跨程序集搜索。
    /// </summary>
    [Option("-mn|--membername <substring>", "在指定类型内按成员名子串搜索并反编译匹配的成员（省略 -t 时跨程序集搜索）。", CommandOptionType.SingleValue)]
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
    /// 输出程序集概览信息（程序集名/版本/目标框架/引用/类型构成/入口点），配合 -a。
    /// </summary>
    [Option("-ai|--assembly-info", "输出程序集概览信息（配合 -a）。", CommandOptionType.NoValue)]
    public bool AssemblyInfo { get; }

    /// <summary>
    /// hierarchy 包含全部间接后代（接口的所有实现者、基类的所有子孙），需配合 -hc。
    /// </summary>
    [Option("-i|--indirect", "hierarchy 包含全部间接后代（接口的所有实现者、基类的所有子孙），配合 -hc。", CommandOptionType.NoValue)]
    public bool Indirect { get; }

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
    /// dependencies/call_graph 同时输出跨程序集外部类型引用，需配合 -d 或 -cg。
    /// </summary>
    [Option("-x|--external", "同时输出跨程序集外部类型引用（配合 -d/-cg）。", CommandOptionType.NoValue)]
    public bool External { get; }

    /// <summary>
    /// 按方法元数据 token 反向定位程序集内调用该方法的成员（配合 -cg；token 取 -s 行尾或 #MEMBER 的 token）。
    /// </summary>
    [Option("-tk|--token <token>", "按方法元数据 token 反向定位程序集内调用该方法的成员（配合 -cg；token 取 -s 行尾或 #MEMBER 的 token）。", CommandOptionType.SingleValue)]
    public string Token { get; } = null!;

    /// <summary>
    /// 按类型定义 token 精确定位类型（typeName 歧义消歧），配合 -mn 在类型内搜索成员。
    /// </summary>
    [Option("-tt|--typetoken <token>", "按类型定义 token（0x02 开头）精确定位类型（typeName 歧义消歧），配合 -mn 在类型内搜索成员（typeToken 取歧义提示中列出的 token）。", CommandOptionType.SingleValue)]
    public string TypeToken { get; } = null!;

    /// <summary>
    /// 列出程序集中的实体类型：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 csi。
    /// </summary>
    [Option("-l|--list <entity-types>", "列出程序集中的实体类型：c=class, i=interface, s=struct, d=delegate, e=enum；可组合多个字母，如 csi。", CommandOptionType.SingleValue)]
    public string EntityTypes { get; } = null!;

    /// <summary>
    /// 按类型名子串过滤 list_types 结果（忽略大小写），需配合 -l。
    /// </summary>
    [Option("-nc|--namecontains <substring>", "按类型名子串过滤 list_types 结果（忽略大小写），需配合 -l。", CommandOptionType.SingleValue)]
    public string NameContains { get; } = null!;

    /// <summary>
    /// 按命名空间子串过滤 list_types 结果（忽略大小写，嵌套类型按最外层声明类型命名空间归属），需配合 -l。
    /// </summary>
    [Option("-ns|--namespacecontains <substring>", "按命名空间子串过滤 list_types 结果（忽略大小写，嵌套类型按最外层声明类型命名空间归属），需配合 -l。", CommandOptionType.SingleValue)]
    public string NamespaceContains { get; } = null!;

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
    /// 以可编译项目形式反编译时按命名空间使用嵌套目录（仅对 -p 生效）。
    /// </summary>
    [Option("--nested-directories", "以项目形式反编译（-p）时按命名空间使用嵌套目录。", CommandOptionType.NoValue)]
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
    /// 命令行分发：-c 走环境自检，-ai 走 assembly_info，-p 走 decompile_to_project，-o 走 decompile_to_dir，
    /// -s/-hc/-d/-cg 分别走 signature/hierarchy/dependencies/call_graph（-i 让 hierarchy 含间接后代、
    /// -x 让 dependencies/call_graph 同时输出跨程序集外部类型引用，-tk 配合 -cg 按方法 token 反向定位调用点），-l 走 list_types
    /// （-nc 提供类型名子串过滤、-ns 提供命名空间子串过滤），-mn 走 decompile_member（-tt 按类型 token 精确定位，typeName 歧义消歧），
    /// 否则走 decompile；均复用对应 MCP 工具的校验与执行逻辑。
    /// </summary>
    internal static async Task<string> DispatchCliAsync(
        string assembly, string typeName, string memberName, string entityTypes, string nameContains, string namespaceContains,
        string outputDir, bool project, bool nestedDirectories, bool signatures, bool hierarchy, bool dependencies, bool callGraph,
        bool external, bool indirect, bool assemblyInfo,
        string token, string typeToken, string lines, int timeoutSeconds, bool check,
        CancellationToken cancellationToken = default)
    {
        if (check)
        {
            // CLI -c 是主动调试入口：先刷新 NuGet 磁盘缓存（TTL/退避内不联网、失败静默降级），再组装报告， 避免无缓存记录时 NuGet 段永远留白；握手路径不 await（后台刷新供下次会话），这里等结果
            await AppServices.Updater.RefreshIfStaleAsync();
            return await CheckTool.CheckStatus();
        }
        if (assemblyInfo)
        {
            return await AssemblyInfoTool.AssemblyInfo(assembly, lines, cancellationToken);
        }
        if (project && !string.IsNullOrEmpty(outputDir))
        {
            return await DecompileToProjectTool.DecompileToProject(assembly, outputDir, nestedDirectories, timeoutSeconds, cancellationToken);
        }
        if (!string.IsNullOrEmpty(outputDir))
        {
            return await DecompileToDirTool.DecompileToDir(assembly, outputDir, typeName, timeoutSeconds, cancellationToken);
        }
        if (signatures)
        {
            return await SignatureTool.Signature(assembly, typeName, lines, cancellationToken);
        }
        if (hierarchy)
        {
            return await HierarchyTool.Hierarchy(assembly, typeName, includeIndirect: indirect, lines, cancellationToken);
        }
        if (dependencies)
        {
            return await DependenciesTool.Dependencies(assembly, typeName, includeExternal: external, lines, cancellationToken);
        }
        if (callGraph)
        {
            return await CallGraphTool.CallGraph(assembly, typeName, token, includeExternal: external, lines, cancellationToken);
        }
        if (!string.IsNullOrEmpty(entityTypes))
        {
            return await ListTypesTool.ListTypes(assembly, entityTypes, nameContains, namespaceContains, lines, cancellationToken);
        }
        if (!string.IsNullOrEmpty(memberName))
        {
            return await DecompileMemberTool.DecompileMember(assembly, typeName, memberName, token: "", typeToken, lines, timeoutSeconds, cancellationToken);
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
                Assembly, TypeName, MemberName, EntityTypes, NameContains, NamespaceContains,
                OutputDir, Project, NestedDirectories, Signatures, Hierarchy, Dependencies, CallGraph,
                External, Indirect, AssemblyInfo,
                Token, TypeToken, Lines, TimeoutSeconds, Check));
            return 0;
        }

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        // 握手期先执行更新检查（报告 ilspymcp 是否有新版本），状态由 StatusReport 会话内缓存、与 CLI -c 同源；
        // 同步读磁盘缓存，无有效检查记录时返回空报告（不注入）。有新版本时注入文本带明确指令，要求 agent 在会话开始的回复中
        // 主动告知用户并提供升级命令（陈述句会被 agent 当作背景信息而不转述）；已是最新时仅注入状态行。
        // 握手期始终注入 server 工作目录供相对路径（assembly/outputDir）解析，另附更新报告。
        string report;
        try
        {
            var status = await AppServices.StatusReport.Value;
            report = EnvironmentChecker.BuildHandshakeText(status);
        }
        catch
        {
            report = ""; // 更新检查异常不阻断 MCP 启动：降级为不注入提示，核心反编译功能不受影响
        }
        var serverInstructions = BuildServerInstructions(report);
        builder.Services.AddMcpServer(o =>
        {
            if (!string.IsNullOrEmpty(serverInstructions)) o.ServerInstructions = serverInstructions;
        })
        .WithStdioServerTransport().WithToolsFromAssembly();
        // 后台 fire-and-forget 预检（TTL/退避内不联网）：刷新 NuGet 磁盘缓存供下一次会话使用，不 await 以免阻塞启动
        _ = AppServices.Updater.RefreshIfStaleAsync();
        await builder.Build().RunAsync();
        return 0;
    }

    /// <summary>
    /// 组装握手期注入 <c>ServerInstructions</c> 的上下文文本：首行恒为 server 进程当前工作目录，
    /// 供 agent 解析相对路径（assembly/outputDir）；其后可选追加 ilspymcp 更新报告（非空时换行拼接）。
    /// </summary>
    /// <param name="updateReport">更新报告文本（由 <see cref="EnvironmentChecker.BuildHandshakeText"/> 得到，可为空）。</param>
    /// <returns>注入文本；首行为 CWD 行，报告为空时仍返回该行。</returns>
    internal static string BuildServerInstructions(string? updateReport)
    {
        var text = $"当前工作目录: {Environment.CurrentDirectory}";
        if (!string.IsNullOrEmpty(updateReport))
        {
            text += Environment.NewLine + updateReport;
        }
        return text;
    }
}