using ILSpyMcp.Configuration;
using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Pipeline;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Globalization;
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
    /// 按成员名子串定位并反编译成员（或按 token 直接反编译单个成员）：typeName 非空时在指定类型内搜索，省略时跨程序集搜索，
    /// 经共享管道缓存与 lines 分页。
    /// </summary>
    /// <param name="assembly">要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">在指定类型内搜索成员，全限定类型名；省略时跨程序集按成员名搜索（token 分支下可不填）。</param>
    /// <param name="memberName">成员名子串，忽略大小写；匹配到的成员全部反编译（token 分支下可不填）。</param>
    /// <param name="token">非空时按元数据 token 直接反编译单个成员（如清单中的 0x06000005），忽略 memberName。</param>
    /// <param name="typeToken">非空时按类型定义 token 精确定位类型（typeName 歧义消歧），再在类型内按 memberName 搜索。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"（1-based 含两端，单次最多约 32 KB）；缺省返回前约 8 KB。</param>
    /// <param name="timeoutSeconds">本次反编译回源超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>匹配成员反编译合并结果（带行号）或错误提示文本。</returns>
    [McpServerTool]
    [Description("反编译指定类型内单个或多个成员的实现体到标准输出（成员级入口，如某个方法体；整类型源码请用 decompile 工具）。按 memberName 子串搜索成员（忽略大小写，适合只知道方法名、不知道完整文档 ID 的场景；默认排除属性/事件访问器）。提供 typeName 时在指定类型内定位；省略 typeName 时跨程序集搜索全部类型（#MEMBER 分隔行带 type 字段标注成员所属类型，供分辨同名成员归属）。typeName 存在歧义（命名空间与嵌套分隔的多种解释均命中）时返回歧义提示并列出候选类型（带 typeToken），可用 typeToken 参数精确定位后再搜索成员。定位到多个成员时全部反编译并合并输出，行号连续，各成员前有 #MEMBER JSON 结构化分隔行（格式 #MEMBER {\"name\":\"...\",\"token\":\"0x...\",\"type\":\"...\"}，token 可直接用于后续反编译）；超过 20 个时仅返回成员签名清单（每行 #MEMBER JSON，含 name/token/signature/type）不反编译。提供 token 参数时直接按元数据 token 反编译单个成员（忽略 memberName，typeName 可不填，清单与分隔行中的 token 均可直接用）；提供 typeToken 参数时按类型定义 token（0x02 开头）精确定位类型（typeName 歧义消歧，typeName 可不填）。结果默认只返回前约 8 KB，可用 lines 参数分页（超限签名清单同样支持）；无匹配时返回相近成员名提示。")]
    public static async Task<string> DecompileMember(
        [Description("要反编译的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("在指定类型内搜索成员，全限定类型名，例如 System.Text.Json.JsonSerializer（可选；省略时跨程序集搜索，提供 token 时可不填）")] string typeName = "",
        [Description("成员名子串（忽略大小写），例如 SerializeAsync；匹配到的成员会全部反编译（必填；提供 token 时可不填）")] string memberName = "",
        [Description("指定则直接按元数据 token 反编译该成员（非必填，如清单中的 0x06000005）；提供时忽略 memberName，typeName 可不填")] string token = "",
        [Description("指定则按类型定义 token（0x02 开头，如歧义提示中列出的 token）精确定位类型再搜索成员（非必填；typeName 存在歧义时用于消歧，提供时 typeName 可不填）")] string typeToken = "",
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
            return await RunByTokenAsync(assemblyFull, typeName, token, lines, timeoutSeconds, cancellationToken);
        }

        // 参数校验：memberName 必填（typeName 允许为空，省略时跨程序集按成员名搜索）
        if (!ArgumentValidators.ValidateMemberNameSearch(typeName, memberName, out var argError)) return argError;

        // 纯元数据读取定位成员：typeName 为空走跨程序集搜索，否则在指定类型内搜索；typeToken 非空时按类型 token 精确定位
        // （typeName 歧义消歧）；未命中类型/歧义/无匹配成员时直接返回提示，无匹配且存在相近名时附相近成员名
        var (matches, similarNames, effectiveTypeName, locateError) = LocateMembers(assemblyFull, typeName, typeToken, memberName);
        if (locateError is not null) return locateError;

        if (matches.Count == 0)
        {
            var message = effectiveTypeName is null
                ? $"程序集中未找到名称包含 \"{memberName}\" 的成员"
                : $"类型 {effectiveTypeName} 中未找到名称包含 \"{memberName}\" 的成员";
            if (similarNames.Count > 0) message += $"。相近成员：{string.Join("、", similarNames)}";
            return message;
        }

        // 匹配数超过上限：不反编译，仅返回成员签名清单（元数据秒回），避免为海量匹配逐一启动反编译
        if (matches.Count > AppConfig.MaxMemberMatches) return RenderSignatureList(assemblyFull, effectiveTypeName ?? "", memberName, matches, lines);

        // 每个匹配成员一条命令：token 全局唯一，同一成员不同子串查询 token 相同 → 缓存签名相同 → 共享缓存；各命令独立缓存 key
        var commands = matches
            .Select(m => new ToolCommand(assemblyFull, new DecompileRequest(DecompileKind.Member, m.Token))
            {
                MemberName = m.Name,
                MemberToken = m.Token,
                MemberType = m.TypeName,
            })
            .ToArray();

        // 头部信息块：程序集绝对路径 + 目标描述（含匹配数）。不展示参数——对外工具没有 token 概念， 暴露内部 token 或反编译细节会误导
        // agent（agent 面对的是 MCP 命名参数）
        var context = new FormatContext(assemblyFull, effectiveTypeName is null
            ? $"成员 {memberName}（跨程序集，{matches.Count} 个匹配）"
            : $"类型 {effectiveTypeName} 的成员 {memberName}（{matches.Count} 个匹配）");

        // 走共享执行管道：各成员缓存/回源后合并（MemberName/MemberToken 非空时自动插 #MEMBER JSON 分隔行），统一行号与 lines 分页
        return await ToolExecutor.RunMergedAsync(commands, lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, context);
    }

    /// <summary>
    /// token 分支：非空时按元数据 token 直接反编译单个成员（token 全局唯一，无需 typeName 定位；头部保留 typeName 仅作目标描述）。
    /// </summary>
    private static async Task<string> RunByTokenAsync(string assemblyFull, string typeName, string token, string lines, int timeoutSeconds, CancellationToken cancellationToken)
    {
        if (!ArgumentValidators.ValidateToken(token, out var tokenError)) return tokenError;
        var tokenContext = new FormatContext(assemblyFull, string.IsNullOrWhiteSpace(typeName)
            ? $"成员 {token}（按 token 反编译）"
            : $"类型 {typeName} 的成员 {token}（按 token 反编译）");
        return await ToolExecutor.RunPipelineAsync(
            new ToolCommand(assemblyFull, new DecompileRequest(DecompileKind.Member, token)),
            lines, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken, tokenContext);
    }

    /// <summary>
    /// 纯元数据读取定位成员：typeToken 非空时按类型定义 token 精确定位（typeName 歧义消歧），typeName 为空走跨程序集搜索，
    /// 否则先用 FindTypes 判定歧义/未找到（歧义返回歧义提示、未找到返回附相近类型名的未找到提示），唯一候选再在类型内按
    /// memberName 搜索（Error 非空，元数据秒回）；IO 类异常同样以 Error 返回「无法读取程序集元数据」提示。
    /// EffectiveTypeName 非空表示本次是类型内搜索（供调用方做目标描述与「未找到」消息），typeToken/typeName 定位成功时返回类型全名。
    /// </summary>
    private static (IReadOnlyList<MemberMatch> Matches, IReadOnlyList<string> SimilarNames, string? EffectiveTypeName, string? Error)
        LocateMembers(string assemblyFull, string typeName, string typeToken, string memberName)
    {
        // typeToken 分支：非空时按类型定义 token 精确定位类型（typeName 歧义消歧），再在类型内按 memberName 搜索
        if (!string.IsNullOrWhiteSpace(typeToken))
        {
            return LocateMembersByTypeToken(assemblyFull, typeToken, memberName);
        }
        if (string.IsNullOrWhiteSpace(typeName))
        {
            var r = MemberResolver.FindMembersAcrossAssembly(assemblyFull, memberName);
            return (r.Matches, r.SimilarNames, null, null);
        }
        // typeName 分支：先用 FindTypes 判定歧义/未找到——不能先经 MemberResolver.FindMembers（其内部用 FindType 取首个
        // 候选，会吞掉歧义直达首个类型），否则歧义提示永远不可达；唯一候选再用其全名在类型内按 memberName 搜索
        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var candidates = MetadataNaming.FindTypes(reader, typeName);
            if (candidates.Count > 1)
            {
                return (Array.Empty<MemberMatch>(), Array.Empty<string>(), null, MetadataNaming.BuildAmbiguityMessage(reader, typeName, candidates));
            }
            if (candidates.Count == 0)
            {
                return (Array.Empty<MemberMatch>(), Array.Empty<string>(), null, MetadataNaming.BuildNotFoundMessage(reader, typeName));
            }
            // 唯一候选：用其全名在类型内按 memberName 搜索（编译器生成类型经 FindMembers 过滤为未找到类型）
            var fullName = MetadataNaming.FullName(reader, reader.GetTypeDefinition(candidates[0]));
            var inType = MemberResolver.FindMembers(assemblyFull, fullName, memberName);
            if (!inType.TypeFound)
            {
                return (Array.Empty<MemberMatch>(), Array.Empty<string>(), null, MetadataNaming.BuildNotFoundMessage(reader, typeName));
            }
            return (inType.Matches, inType.SimilarNames, fullName, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return (Array.Empty<MemberMatch>(), Array.Empty<string>(), null, $"无法读取程序集元数据：{ex.Message}");
        }
    }

    /// <summary>
    /// typeToken 分支：按类型定义 token 精确定位类型并取该类型全名作 EffectiveTypeName，再在类型内按 memberName 搜索。
    /// token 格式先经 <see cref="ArgumentValidators.ValidateToken"/> 校验；Kind 非 TypeDefinition（0x02）或行号越界时返回中文提示。
    /// </summary>
    private static (IReadOnlyList<MemberMatch> Matches, IReadOnlyList<string> SimilarNames, string? EffectiveTypeName, string? Error)
        LocateMembersByTypeToken(string assemblyFull, string typeToken, string memberName)
    {
        if (!ArgumentValidators.ValidateToken(typeToken, out var typeTokenError))
        {
            return (Array.Empty<MemberMatch>(), Array.Empty<string>(), null, typeTokenError);
        }
        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var handle = ResolveTypeToken(reader, typeToken);
            if (handle is null)
            {
                return (Array.Empty<MemberMatch>(), Array.Empty<string>(), null,
                    $"\"{typeToken.Trim()}\" 不是类型定义的元数据 token（typeToken 需为类型 token，0x02 开头，如 0x02000004）");
            }
            var fullName = MetadataNaming.FullName(reader, reader.GetTypeDefinition(handle.Value));
            var r = MemberResolver.FindMembers(assemblyFull, fullName, memberName);
            return (r.Matches, r.SimilarNames, fullName, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return (Array.Empty<MemberMatch>(), Array.Empty<string>(), null, $"无法读取程序集元数据：{ex.Message}");
        }
    }

    /// <summary>
    /// 将 typeToken 文本解析为 TypeDefinition 句柄：格式已由 <see cref="ArgumentValidators.ValidateToken"/> 校验，
    /// 要求 Kind 为 TypeDefinition（0x02）且行号在 TypeDefinitions.Count 范围内；否则返回 null。
    /// </summary>
    private static TypeDefinitionHandle? ResolveTypeToken(MetadataReader reader, string typeToken)
    {
        var value = int.Parse(typeToken.Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var row = value & 0x00FFFFFF;
        if ((value >> 24) != (int)HandleKind.TypeDefinition) return null;
        if (row < 1 || row > reader.TypeDefinitions.Count) return null;
        return MetadataTokens.TypeDefinitionHandle(row);
    }

    /// <summary>
    /// 匹配数超限时仅返回成员签名清单：元数据读取经共享缓存（重复查询直接命中），遍历全部非编译器生成类型的全部成员
    /// （字段/方法/属性/事件），凡 token 属于匹配集合的成员渲染一行 `#MEMBER {name/token/signature/type}` JSON 行（type 为该成员所属类型全名）。
    /// 按 token（而非成员名）匹配，避免同名重载成员被名字集合去重而丢失；签名经 <see cref="SignatureRenderer.RenderSingleMember"/> 渲染，
    /// 支持字段/方法/属性/事件四类成员。清单同样受 lines 分页控制（缺省返回前约 8 KB）。
    /// </summary>
    private static string RenderSignatureList(string assemblyFull, string typeName, string memberName, IReadOnlyList<MemberMatch> matches, string lines)
    {
        var context = new FormatContext(assemblyFull, $"成员 {memberName}（{matches.Count} 个匹配，超过上限 {AppConfig.MaxMemberMatches}，仅列出签名）");
        return ToolExecutor.RunMetadataPe(assemblyFull, $"member-signatures\u001F{typeName}\u001F{memberName}", lines, context, (pe, reader) =>
        {
            var tokens = matches.Select(m => m.Token).ToHashSet();
            var signatureLines = new List<string>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if (CompilerGeneratedFilter.IsCompilerGenerated(reader, type)) continue;
                var renderedTypeName = MetadataNaming.FullName(reader, type);
                foreach (var memberHandle in EnumerateMemberHandles(type))
                {
                    var token = $"0x{MetadataTokens.GetToken(memberHandle):x8}";
                    if (!tokens.Contains(token)) continue;
                    var signature = SignatureRenderer.RenderSingleMember(reader, type, memberHandle);
                    var name = MemberName(reader, memberHandle);
                    signatureLines.Add($"#MEMBER {OutputFormatter.MemberJson(name, token, signature, renderedTypeName)}");
                }
            }
            // 清单可能超过预算，统一走 lines 分页（缺省截断前约 8 KB，超限可用 lines 续读）
            return signatureLines;
        }, default);
    }

    /// <summary>
    /// 按字段→方法→属性→事件顺序枚举类型全部成员句柄（与搜索/签名渲染顺序一致，保证清单行序稳定）。
    /// </summary>
    private static IEnumerable<EntityHandle> EnumerateMemberHandles(TypeDefinition type)
    {
        foreach (var handle in type.GetFields()) yield return handle;
        foreach (var handle in type.GetMethods()) yield return handle;
        foreach (var handle in type.GetProperties()) yield return handle;
        foreach (var handle in type.GetEvents()) yield return handle;
    }

    /// <summary>
    /// 取成员元数据原始名（字段/方法取 Name，属性/事件取 Name）；未知句柄类型返回占位符。
    /// </summary>
    private static string MemberName(MetadataReader reader, EntityHandle handle)
        => handle.Kind switch
        {
            HandleKind.FieldDefinition => reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
            HandleKind.MethodDefinition => reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
            HandleKind.PropertyDefinition => reader.GetString(reader.GetPropertyDefinition((PropertyDefinitionHandle)handle).Name),
            HandleKind.EventDefinition => reader.GetString(reader.GetEventDefinition((EventDefinitionHandle)handle).Name),
            _ => "<unknown>",
        };
}
