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
/// 反编译指定类型内单个或多个成员的实现体（成员级入口，如某个方法体）：按成员名在类型内定位成员并反编译合并输出，
/// 或按 token 直接反编译单个成员。定位的多个成员全部反编译并合并输出（行号连续，各成员前有 #MEMBER JSON 结构化分隔行），
/// 默认返回前约 8 KB、可用 lines 分页；定位数量超过上限时仅返回签名清单，不启动反编译。
/// </summary>
[McpServerToolType]
public static class DecompileMemberTool
{
    /// <summary>
    /// 按成员名子串在指定类型内定位并反编译成员（或按 token 直接反编译单个成员），经共享管道缓存与 lines 分页。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">在指定类型内搜索成员，全限定类型名（token 分支下可不填）。</param>
    /// <param name="memberName">成员名子串，忽略大小写；匹配到的成员全部反编译（token 分支下可不填）。</param>
    /// <param name="token">非空时按元数据 token 直接反编译单个成员（如清单中的 0x06000005），忽略 memberName。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多约 32 KB）；缺省返回前约 8 KB。</param>
    /// <param name="timeoutSeconds">本次反编译回源超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>匹配成员反编译合并结果（带行号）或错误提示文本。</returns>
    [McpServerTool]
    [Description("反编译指定类型内单个或多个成员的实现体到标准输出（成员级入口，如某个方法体；整类型源码请用 decompile 工具）。按 memberName 子串在 typeName 内定位成员（忽略大小写，适合只知道方法名、不知道完整文档 ID 的场景；默认排除属性/事件访问器）。定位到多个成员时全部反编译并合并输出，行号连续，各成员前有 #MEMBER JSON 结构化分隔行（格式 #MEMBER {\"name\":\"...\",\"token\":\"0x...\"}，token 可直接用于后续反编译）；超过 20 个时仅返回成员签名清单（每行 #MEMBER JSON，含 name/token/signature）不反编译。提供 token 参数时直接按元数据 token 反编译单个成员（忽略 memberName，typeName 可不填，清单与分隔行中的 token 均可直接用）。结果默认只返回前约 8 KB，可用 lines 参数分页（超限签名清单同样支持）；无匹配时返回相近成员名提示。")]
    public static async Task<string> DecompileMember(
        [Description("要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("在指定类型内搜索成员，全限定类型名，例如 System.Text.Json.JsonSerializer（必填；提供 token 时可不填）")] string typeName = "",
        [Description("成员名子串（忽略大小写），例如 SerializeAsync；匹配到的成员会全部反编译（必填；提供 token 时可不填）")] string memberName = "",
        [Description("指定则直接按元数据 token 反编译该成员（非必填，如清单中的 0x06000005）；提供时忽略 memberName，typeName 可不填")] string token = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        [Description("本次反编译超时秒数，默认 30；大程序集可调大")] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（进程内反编译，无安装前置）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return assemblyError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;

        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;

        // token 分支：非空时按元数据 token 直接反编译单个成员（token 全局唯一，无需 typeName 定位；头部保留 typeName 仅作目标描述）
        if (!string.IsNullOrWhiteSpace(token))
        {
            if (!ArgumentValidators.ValidateToken(token, out var tokenError)) return tokenError;
            var tokenContext = new FormatContext(assemblyFull, string.IsNullOrWhiteSpace(typeName)
                ? $"成员 {token}（按 token 反编译）"
                : $"类型 {typeName} 的成员 {token}（按 token 反编译）");
            return await ToolExecutor.RunPipelineAsync(
                new ToolCommand(assemblyFull, new DecompileRequest(DecompileKind.Member, token)),
                lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, tokenContext);
        }

        // 参数校验：typeName 与 memberName 均必填
        if (!ArgumentValidators.ValidateMemberNameSearch(typeName, memberName, out var argError)) return argError;

        // 纯元数据读取定位类型并枚举方法，按名字子串匹配；未命中类型/无匹配成员时直接返回提示，无匹配且存在相近名时附相近成员名
        var (typeFound, matches, similarNames) = MemberResolver.FindMembers(assemblyFull, typeName, memberName);
        if (!typeFound) return $"未找到类型 {typeName}";
        if (matches.Count == 0)
        {
            var message = $"类型 {typeName} 中未找到名称包含 \"{memberName}\" 的成员";
            if (similarNames.Count > 0) message += $"。相近成员：{string.Join("、", similarNames)}";
            return message;
        }

        // 匹配数超过上限：不反编译，仅返回成员签名清单（元数据秒回），避免为海量匹配逐一启动反编译
        if (matches.Count > AppConfig.MaxMemberMatches) return RenderSignatureList(assemblyFull, typeName, memberName, matches, lines);

        // 每个匹配成员一条命令：token 全局唯一，同一成员不同子串查询 token 相同 → 缓存签名相同 → 共享缓存；各命令独立缓存 key
        var commands = matches
            .Select(m => new ToolCommand(assemblyFull, new DecompileRequest(DecompileKind.Member, m.Token))
            {
                MemberName = m.Name,
                MemberToken = m.Token,
            })
            .ToArray();

        // 头部信息块：程序集绝对路径 + 目标描述（含匹配数）。不展示参数——对外工具没有 token 概念， 暴露内部 token 或反编译细节会误导
        // agent（agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, $"类型 {typeName} 的成员 {memberName}（{matches.Count} 个匹配）");

        // 走共享执行管道：各成员缓存/回源后合并（MemberName/MemberToken 非空时自动插 #MEMBER JSON 分隔行），统一行号与 lines 分页
        return await ToolExecutor.RunMergedAsync(commands, lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, context);
    }

    /// <summary>
    /// 匹配数超限时仅返回成员签名清单：重新打开程序集做纯元数据读取，凡 token 属于匹配集合的成员渲染一行
    /// `#MEMBER {name/token/signature}` JSON 行。按 token（而非方法名）匹配，避免同名重载成员被名字集合去重而丢失。
    /// 清单同样受 lines 分页控制（缺省返回前约 8 KB）。
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
                var name = reader.GetString(method.Name);
                var signature = SignatureRenderer.RenderMemberSignature(reader, type, method);
                signatureLines.Add($"#MEMBER {OutputFormatter.MemberJson(name, token, signature)}");
            }
            // 清单可能超过预算，统一走 lines 分页（缺省截断前约 8 KB，超限可用 lines 续读）
            return OutputFormatter.Format(signatureLines, lines, context);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return $"无法读取程序集元数据：{ex.Message}";
        }
    }
}
