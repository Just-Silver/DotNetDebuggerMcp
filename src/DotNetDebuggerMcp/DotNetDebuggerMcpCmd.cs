using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.DebugCli;
using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Tools;
using DotNetDebuggerMcp.UpdateCheck;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace DotNetDebuggerMcp;

/// <summary>
/// 命令行入口（McMaster.CommandLineUtils）。 无业务参数时启动 MCP 服务器（stdio 传输）；传入 -a/--assembly 等参数时
/// 以命令行形式直接执行反编译/列类型/写盘，复用与 MCP 工具相同的校验与执行逻辑，便于调试。
/// -v/--version 输出版本号，-h/--help 输出帮助信息。
/// </summary>
[HelpOption("-h|--help")]
[VersionOptionFromMember("-v|--version", Description = "显示 dotnet-debugger-mcp 版本号。", MemberName = nameof(Version))]
public class DotNetDebuggerMcpCmd
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
    /// hierarchy 包含全部间接后代（接口的所有实现者、基类的所有子孙）、interface_usage 包含全部间接实现者，需配合 -hc/-iu。
    /// </summary>
    [Option("-i|--indirect", "hierarchy 包含全部间接后代、interface_usage 包含全部间接实现者（接口的子接口、实现者及其子类），配合 -hc/-iu。", CommandOptionType.NoValue)]
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
    /// 输出指定接口的实现者与调用点组合视图，需配合 -t。
    /// </summary>
    [Option("-iu|--interfaceusage", "输出指定接口的实现者与调用点组合视图（配合 -t）。", CommandOptionType.NoValue)]
    public bool InterfaceUsage { get; }

    /// <summary>
    /// 输出指定泛型类型被具体实例化的使用点，需配合 -t。
    /// </summary>
    [Option("-gi|--genericinstantiations", "输出指定泛型类型被具体实例化的使用点（配合 -t）。", CommandOptionType.NoValue)]
    public bool GenericInstantiations { get; }

    /// <summary>
    /// 输出起始方法的方法级正向调用序列 + 被调用成员反编译，配合 -t/-mn 或 -tk。
    /// </summary>
    [Option("-cc|--callchain", "输出起始方法的方法级正向调用序列 + 被调用成员反编译（配合 -t -mn 或 -tk）。", CommandOptionType.NoValue)]
    public bool CallChain { get; }

    /// <summary>
    /// dependencies/call_graph 同时输出跨程序集外部类型引用，需配合 -d 或 -cg。
    /// </summary>
    [Option("-x|--external", "同时输出跨程序集外部类型引用（配合 -d/-cg/-cc）。", CommandOptionType.NoValue)]
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
    /// 按字符串字面量子串反查方法体中的成员（忽略大小写），需配合 -a；提供 -t 时限定在指定类型内。
    /// </summary>
    [Option("-ss|--searchstring <substring>", "按字符串字面量子串反查成员（忽略大小写，配合 -a；可选 -t 限定类型）。", CommandOptionType.SingleValue)]
    public string SearchString { get; } = null!;

    /// <summary>
    /// 追踪指定字段的读取/写入/取地址位置，需配合 -a；可选 -t 限定字段所属类型、-fn 指定字段名、-tk 指定字段 token。
    /// </summary>
    [Option("-fa|--fieldaccess", "追踪指定字段的读取/写入/取地址位置（配合 -a；可选 -t/-fn/-tk）。", CommandOptionType.NoValue)]
    public bool FieldAccess { get; }

    /// <summary>
    /// 字段名子串（忽略大小写），配合 -fa 定位字段；省略 -t 时跨程序集搜索。
    /// </summary>
    [Option("-fn|--fieldname <substring>", "字段名子串（忽略大小写，配合 -fa；可选 -t 限定类型，-tk 指定字段 token 时可不填）。", CommandOptionType.SingleValue)]
    public string FieldName { get; } = null!;

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
    /// 检查当前 dotnet-debugger-mcp 是否有新版本。无需 -a。
    /// </summary>
    [Option("-c|--check", "检查当前 dotnet-debugger-mcp 是否有新版本（查询 NuGet，结果会话内缓存，仅首次真实检查）。", CommandOptionType.NoValue)]
    public bool Check { get; }

    /// <summary>
    /// 一次性调试场景：启动并附加目标可执行程序（配合 -dbg-bp/-dbg-offset）。
    /// </summary>
    [Option("-dbg <exe>", "一次性调试场景：启动并附加目标可执行程序（配合 -dbg-bp/-dbg-offset，供手动验证动态调试引擎）。", CommandOptionType.SingleValue)]
    public string DebugTarget { get; } = null!;

    /// <summary>
    /// 断点方法 token（0x06000005，配合 -dbg）。
    /// </summary>
    [Option("-dbg-bp <token>", "断点方法 token（0x06000005，配合 -dbg）。", CommandOptionType.SingleValue)]
    public string DebugBreakpointToken { get; } = null!;

    /// <summary>
    /// 断点 IL offset（配合 -dbg/-dbg-bp，默认 0）。
    /// </summary>
    [Option("-dbg-offset <n>", "断点 IL offset（配合 -dbg/-dbg-bp，默认 0）。", CommandOptionType.SingleValue)]
    public int DebugBreakpointOffset { get; } = 0;

    /// <summary>
    /// 以 Web 模式启动：MCP 常驻同时起 Kestrel（浏览器看调试现场）；无 MCP 业务参数时页面可人工 launch/attach。
    /// </summary>
    [Option("--web", "以 Web 模式启动：MCP 常驻同时起 Kestrel（浏览器可看 agent 调试现场）；配合 --web-port 指定端口。", CommandOptionType.NoValue)]
    public bool Web { get; }

    /// <summary>
    /// Web 监听端口（配合 --web）。缺省 0 = 自动选空闲端口（启动后提示实际 URL 并拉起浏览器）。
    /// </summary>
    [Option("--web-port <port>", "Web 监听端口（配合 --web）。缺省 0 = 自动选空闲端口（防占用冲突，启动后提示实际 URL）。", CommandOptionType.SingleValue)]
    public int WebPort { get; } = 0;

    /// <summary>
    /// 版本号文本（由 -v/--version 触发输出），与 NuGet 包版本保持一致。
    /// </summary>
    public string Version => AppConfig.NuGetPackageId + " " + (AppConfig.CurrentVersion?.ToString(3) ?? "unknown");

    /// <summary>
    /// 命令行分发：-c 走环境自检，-ai 走 assembly_info，-p 走 decompile_to_project，-o 走 decompile_to_dir，
    /// -s/-hc/-d/-cg/-iu/-gi/-cc 分别走 signature/hierarchy/dependencies/call_graph/interface_usage/generic_instantiations/call_chain
    /// （-i 让 hierarchy 含间接后代、interface_usage 含全部间接实现者，
    /// -x 让 dependencies/call_graph 同时输出跨程序集外部类型引用、-cc 让 call_chain 保留外部调用行，
    /// -tk 配合 -cg 按方法 token 反向定位调用点、配合 -cc 直接定位起始方法），-l 走 list_types （-nc 提供类型名子串过滤、-ns
    /// 提供命名空间子串过滤），-ss 走 search_string（-t 限定类型），-fa 走 field_access （-fn 指定字段名、-tk 指定字段 token、-t
    /// 限定字段所属类型），-mn 走 decompile_member （-tt 按类型 token 精确定位，typeName 歧义消歧），否则走 decompile；均复用对应 MCP 工具的校验与执行逻辑。
    /// </summary>
    internal static async Task<string> DispatchCliAsync(
        string assembly, string typeName, string memberName, string entityTypes, string nameContains, string namespaceContains,
        string searchString, string fieldName,
        string outputDir, bool project, bool nestedDirectories, bool signatures, bool hierarchy, bool dependencies, bool callGraph,
        bool callChain, bool fieldAccess, bool external, bool indirect, bool assemblyInfo, bool interfaceUsage, bool genericInstantiations,
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
        if (interfaceUsage)
        {
            return await InterfaceUsageTool.InterfaceUsage(assembly, typeName, includeIndirect: indirect, lines, cancellationToken);
        }
        if (genericInstantiations)
        {
            return await GenericInstantiationTool.GenericInstantiations(assembly, typeName, lines, cancellationToken);
        }
        if (dependencies)
        {
            return await DependenciesTool.Dependencies(assembly, typeName, includeExternal: external, lines, cancellationToken);
        }
        if (callGraph)
        {
            return await CallGraphTool.CallGraph(assembly, typeName, token, includeExternal: external, lines, cancellationToken);
        }
        if (callChain)
        {
            return await CallChainTool.CallChain(assembly, typeName, memberName, token, includeExternal: external, lines, timeoutSeconds, cancellationToken);
        }
        if (fieldAccess)
        {
            return await FieldAccessTool.FieldAccess(assembly, typeName, fieldName, token, lines, cancellationToken);
        }
        if (!string.IsNullOrEmpty(searchString))
        {
            return await SearchStringTool.SearchString(assembly, searchString, typeName, lines, cancellationToken);
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
    /// 组装握手期注入 <c>ServerInstructions</c> 的上下文文本：Markdown 功能简介（<see cref="AppText.HandshakeFeatureIntro"/>，服务器简介/
    /// 何时使用/使用约定三块标题分节，触发条件导向——不逐条列工具）；更新报告非空时空行分隔追加（「## 更新状态」段自带标题）。
    /// 工作目录不再注入——客户端环境已在系统上下文中提供，简介的使用约定段仅提示相对路径基于当前工作目录。
    /// </summary>
    /// <param name="updateReport">
    /// 更新报告文本（由 <see cref="EnvironmentChecker.BuildHandshakeText"/> 得到，可为空）。
    /// </param>
    /// <returns>注入文本；报告为空时仅返回功能简介。</returns>
    internal static string BuildServerInstructions(string? updateReport)
    {
        var text = AppText.HandshakeFeatureIntro;
        if (!string.IsNullOrEmpty(updateReport))
        {
            text += Environment.NewLine + Environment.NewLine + updateReport;
        }
        return text;
    }

    /// <summary>
    /// 默认执行：未指定业务参数（-a 或 -c）时启动 MCP 服务器；否则以命令行模式执行并输出结果。
    /// </summary>
    private async Task<int> OnExecuteAsync(CommandLineApplication app)
    {
        if (!string.IsNullOrEmpty(DebugTarget))
        {
            // -dbg 一次性调试场景：供手动验证动态调试引擎（D11 P2 CLI 驱动）
            if (string.IsNullOrEmpty(DebugBreakpointToken))
            {
                Console.Error.WriteLine("请指定 -dbg-bp 断点方法 token（如 -dbg-bp 0x06000003）。");
                return 1;
            }
            var tokenText = DebugBreakpointToken.Replace("0x", "", StringComparison.OrdinalIgnoreCase);
            var token = int.Parse(tokenText, System.Globalization.NumberStyles.HexNumber);
            return await DebugCliRunner.RunAsync(DebugTarget, token, DebugBreakpointOffset,
                Path.GetDirectoryName(Path.GetFullPath(DebugTarget)), TimeoutSeconds, CancellationToken.None);
        }

        if (!string.IsNullOrEmpty(Assembly) || Check)
        {
            Console.WriteLine(await DispatchCliAsync(
                Assembly, TypeName, MemberName, EntityTypes, NameContains, NamespaceContains,
                SearchString, FieldName,
                OutputDir, Project, NestedDirectories, Signatures, Hierarchy, Dependencies, CallGraph,
                CallChain, FieldAccess, External, Indirect, AssemblyInfo, InterfaceUsage, GenericInstantiations,
                Token, TypeToken, Lines, TimeoutSeconds, Check));
            return 0;
        }

        Task? webTask = null;
        if (Web)
        {
            // --web 模式：注入共享调试会话管理器并起 Kestrel（Blazor Server 展示面）。
            // 双模式：MCP 常驻（agent 调试时浏览器看现场）与纯 Web（无 MCP 会话时页面人工 launch/attach）并存——
            // Web host 与 MCP host 并联，进程生命周期由二者共同决定（WhenAll：任一侧结束进程等另一侧自然完成）。
            DotNetDebugger.Web.WebHostBootstrap.Configure(DebugSessionService.Manager, AgentViewService.Context);
            var webApp = DotNetDebugger.Web.WebHostBootstrap.Build(WebPort, Array.Empty<string>());
            // 起 Web（自动端口）→ 拉浏览器 → stderr 提示实际 URL → 等停；Web host 与 MCP host 并联（WhenAll）
            webTask = RunWebAsync(webApp);
        }

        var builder = Host.CreateApplicationBuilder(Array.Empty<string>());
        // stdout 只承载 MCP 协议消息：Host 默认注册的 Console 日志写 stdout，会与 JSON-RPC 响应
        // 在并发下交错撕坏协议帧，导致客户端永远等不到响应。这里清掉默认提供者后把全部日志显式路由到 stderr。
        builder.Logging.ClearProviders();
        builder.Logging.AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace);
        // 握手期先执行更新检查（报告 dotnet-debugger-mcp 是否有新版本），状态由 StatusReport 会话内缓存、与 CLI -c 同源；
        // 同步读磁盘缓存，无有效检查记录时返回空报告（不注入）。有新版本时注入文本带明确指令，要求 agent 在会话开始的回复中 主动告知用户并提供升级命令（陈述句会被 agent
        // 当作背景信息而不转述）；已是最新时仅注入状态行。 握手期始终注入 server 工作目录供相对路径（assembly/outputDir）解析，另附更新报告。
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
        var mcpTask = builder.Build().RunAsync();
        if (webTask is not null)
        {
            // --web 双 host 并联：任一侧结束，进程等另一侧自然完成（MCP 会话结束或 Web 手动停）。
            // 各自容错：一侧异常不拖垮另一侧（先完成的异常经 WhenAll 抛，由外层 catch 收口）。
            await Task.WhenAll(mcpTask, webTask);
        }
        else
        {
            await mcpTask;
        }
        return 0;

        // --web：起 Web host → 拉浏览器 → stderr 提示实际 URL → 等停（供 webTask 并联）
        async Task RunWebAsync(WebApplication webApp)
        {
            var url = await DotNetDebugger.Web.WebHostBootstrap.RunWithBrowserAsync(webApp);
            Console.Error.WriteLine($"[web] DotNet Debugger Web 已启动：{url}");
            await webApp.WaitForShutdownAsync();
        }
    }
}