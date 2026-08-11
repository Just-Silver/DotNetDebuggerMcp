using ILSpyMcp.Configuration;
using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Pipeline;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Tools;

/// <summary>
/// 按成员名在指定类型内搜索并反编译匹配的成员：适合只知道方法名、给不出完整文档 ID 的场景。 匹配的多个成员全部反编译并合并输出（行号连续），默认返回前 200 行、可用 lines 分页； 匹配数超过上限时仅返回签名清单，不启动反编译。
/// </summary>
[McpServerToolType]
public static class DecompileMemberTool
{
    /// <summary>
    /// 按成员名子串在指定类型内搜索并反编译匹配的成员，经共享管道缓存与 lines 分页。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">在指定类型内搜索成员，全限定类型名（必填）。</param>
    /// <param name="memberName">成员名子串，忽略大小写；匹配到的成员全部反编译（必填）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多 500 行）；缺省返回前 200 行。</param>
    /// <param name="timeoutSeconds">本次反编译回源超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>匹配成员反编译合并结果（带行号）或错误提示文本。</returns>
    [McpServerTool]
    [Description("按成员名子串在指定类型内搜索并反编译匹配的成员（忽略大小写，适合只知道方法名、不知道完整文档 ID 的场景；默认排除属性/事件访问器）。匹配到多个成员时全部反编译并合并输出，行号连续，各成员前有 === 名字 (token) === 分隔行；匹配数超过 20 时仅返回成员签名清单（每行 签名 [token]）不反编译。结果默认只返回前 200 行，可用 lines 参数分页（超限签名清单同样支持）；无匹配时返回相近成员名提示。")]
    public static async Task<string> DecompileMember(
        [Description("要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("在指定类型内搜索成员，全限定类型名，例如 System.Text.Json.JsonSerializer（必填）")] string typeName = "",
        [Description("成员名子串（忽略大小写），例如 SerializeAsync；匹配到的成员会全部反编译（必填）")] string memberName = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多 500 行），例如 \"200-400\"；缺省返回前 200 行")] string lines = "",
        [Description("本次反编译回源超时秒数，默认 30；大程序集可调大")] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 前置检查：ilspycmd 已安装且 assembly 参数有效；未通过直接返回提示
        if (await ToolPreflight.CheckAsync(assembly) is { } preflightError) return preflightError;
        // 参数校验：typeName 与 memberName 均必填
        if (!ArgumentValidators.ValidateMemberNameSearch(typeName, memberName, out var argError)) return argError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;

        // 纯元数据读取定位类型并枚举方法，按名字子串匹配；未命中类型/无匹配成员时直接返回提示，无匹配且存在相近名时附相近成员名
        var (typeFound, matches, similarNames) = MemberResolver.FindMembers(assemblyFull, typeName, memberName);
        if (!typeFound) return $"未找到类型 {typeName}";
        if (matches.Count == 0)
        {
            var message = $"类型 {typeName} 中未找到名称包含 \"{memberName}\" 的成员";
            if (similarNames.Count > 0) message += $"。相近成员：{string.Join("、", similarNames)}";
            return message;
        }

        // 匹配数超过上限：不反编译，仅返回成员签名清单（纯元数据秒回），避免为海量匹配逐一启动 ilspycmd 子进程
        if (matches.Count > AppConfig.MaxMemberMatches) return RenderSignatureList(assemblyFull, typeName, memberName, matches, lines);

        // 每个匹配成员一条命令：token 全局唯一，ilspycmd 的 -t 与 -m 互斥，故仅传 -m <token>；各命令独立缓存 key，同一成员不同子串查询共享缓存
        var commands = matches
            .Select(m => new ToolCommand(ToolCommand.DefaultExecutable, assemblyFull,
                new ToolParameter("-m", m.Token)) { DisplayName = $"{m.Name} ({m.Token})" })
            .ToArray();

        // 头部信息块：程序集绝对路径 + 目标描述（含匹配数）。不展示参数——对外工具没有 -m/token 概念， 暴露内部 token 或 ilspycmd 参数会误导
        // agent（agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的成员 {memberName}（{matches.Count} 个匹配）");

        // 走共享执行管道：各成员缓存/回源后合并（DisplayName 非空时自动插 === 分隔行），统一行号与 lines 分页
        return await ToolExecutor.RunMergedAsync(commands, lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, context);
    }

    /// <summary>
    /// 匹配数超限时仅返回成员签名清单：重新打开程序集做纯元数据读取，凡 token 属于匹配集合的成员渲染一行签名并附 token。
    /// 按 token（而非方法名）匹配，避免同名重载成员被名字集合去重而丢失。清单同样受 lines 分页控制（缺省返回前 200 行）。
    /// </summary>
    private static string RenderSignatureList(string assemblyFull, string typeName, string memberName, IReadOnlyList<MemberMatch> matches, string lines)
    {
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的成员 {memberName}（{matches.Count} 个匹配，超过上限 {AppConfig.MaxMemberMatches}，仅列出签名）");
        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var typeHandle = MetadataNaming.FindType(reader, typeName);
            if (typeHandle is null) return $"未找到类型 {typeName}";
            var type = reader.GetTypeDefinition(typeHandle.Value);

            var tokens = matches.Select(m => m.Token).ToHashSet();
            var signatureLines = new List<string>();
            foreach (var methodHandle in type.GetMethods())
            {
                var token = $"0x{MetadataTokens.GetToken(methodHandle):x8}";
                if (!tokens.Contains(token)) continue;
                var method = reader.GetMethodDefinition(methodHandle);
                var signature = SignatureRenderer.RenderMemberSignature(reader, type, method);
                signatureLines.Add($"{signature}  [{token}]");
            }
            // 清单可能超过 200 行，统一走 lines 分页（缺省截断前 200 行，超限可用 lines 续读）
            return OutputFormatter.Format(signatureLines, lines, context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return $"无法读取程序集元数据：{ex.Message}";
        }
    }
}
