using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Formatting;
using DotNetDebugger.Decompiler.Metadata;
using DotNetDebuggerMcp.Pipeline;
using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Globalization;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebuggerMcp.Tools.Metadata;

/// <summary>
/// 方法级正向调用序列 + 反编译组合：按 token 或 typeName+memberName 定位起始方法，扫描其方法体按 IL 序列出调用序列 （内部调用带成员
/// token，includeExternal=true 时保留跨程序集外部调用行），并对去重后的唯一内部成员逐条经共享管道反编译， 合并为「调用序列 + #MEMBER 分隔行 +
/// 成员体」的一次输出。纯元数据定位起始方法 + 进程内反编译组合。
/// </summary>
[McpServerToolType]
public static class CallChainTool
{
    /// <summary>
    /// 输出起始方法体的方法级正向调用序列，并对被调用的程序集内部成员反编译组合输出。
    /// </summary>
    /// <param name="assembly">要分析的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">起始方法所属类型全名，格式与 list_types 输出一致（提供 token 时可不填）。</param>
    /// <param name="memberName">起始方法名子串（忽略大小写）；匹配多个方法时返回 #MEMBER 签名清单（提供 token 时可不填）。</param>
    /// <param name="token">
    /// 起始方法元数据 token（取 signature 行尾或 #MEMBER 的 token）：按 token 直接定位，忽略 memberName，typeName 可不填。
    /// </param>
    /// <param name="includeExternal">是否在调用序列中保留跨程序集外部调用行（带程序集归属，默认 false）。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="timeoutSeconds">本次反编译回源超时秒数（默认 30）。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>调用序列 + 被调用成员反编译的合并输出（带行号）或错误提示文本。</returns>
    [McpServerTool]
    [Description("输出从指定起始方法出发的方法级正向调用序列（一条执行链），并对被调用的程序集内部成员逐条反编译组合输出（序列+成员体一次拿全）。反向『谁调用了某方法/某类型』请用 call_graph（本工具只做正向）。起始方法按 token 或 typeName+memberName 定位：名称匹配多个时先返回 #MEMBER 签名清单（定位步骤，不反编译），用其中 token 精确定位。序列行 序号. 类型::成员() + 内部成员 token。includeExternal=true 时保留跨程序集外部调用行（格式 全名::成员名 [程序集名]）并尝试展开为被调方法体子序列，解析失败的行尾标注终止。被调用的内部成员超过 20 个时仅返回其签名清单（不反编译）；否则各成员体前有 #MEMBER JSON 分隔行。" + ToolParameterText.FooterPagination)]
    public static async Task<string> CallChain(
        [Description(ToolParameterText.AssemblyParam)] string assembly = "",
        [Description("起始方法所属类型全名（格式与 list_types 输出一致）；提供 token 时可不填")] string typeName = "",
        [Description("起始方法名子串（忽略大小写）；匹配多个方法时返回 #MEMBER 签名清单，用其中 token 精确定位；提供 token 时可不填")] string memberName = "",
        [Description("起始方法元数据 token（如 0x06000005）：按 token 直接定位起始方法，忽略 memberName，typeName 可不填。默认空=不使用")] string token = "",
        [Description("是否保留并展开跨程序集外部调用（默认 false）。true 时外部调用行保留（格式 全名::成员名 [程序集名]）并尝试解析到磁盘程序集展开为被调方法体子序列，解析失败的行尾标注终止")] bool includeExternal = false,
        [Description(ToolParameterText.LinesParam)] string lines = "",
        [Description(ToolParameterText.TimeoutParam)] int timeoutSeconds = AppConfig.DefaultTimeoutSeconds,
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据定位 + 进程内反编译）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return assemblyError;
        // 参数校验：timeoutSeconds 必须为正整数（不允许永不超时）
        if (!ArgumentValidators.ValidateTimeoutSeconds(timeoutSeconds, out var timeoutError)) return timeoutError;
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return pathError;

        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();

            // 定位起始方法：token 分支 或 typeName+memberName 分支
            if (!string.IsNullOrWhiteSpace(token))
            {
                if (!ArgumentValidators.ValidateToken(token, out var tokenError)) return tokenError;
                var tokenHandle = ResolveMethodToken(reader, token);
                if (tokenHandle is null)
                {
                    return $"\"{token.Trim()}\" 不是方法的元数据 token（需 0x06 开头的方法定义 token，如 0x06000005）";
                }
                return await RunScanAsync(pe, tokenHandle.Value,
                    string.IsNullOrWhiteSpace(typeName)
                        ? $"方法 {token}（调用序列，按 token 定位）"
                        : $"类型 {typeName} 的方法 {token}（调用序列，按 token 定位）",
                    assemblyFull, includeExternal, lines, timeoutSeconds, cancellationToken);
            }

            if (!ArgumentValidators.ValidateRequired(typeName, "请指定 typeName 参数（起始方法所属类型全名，格式与 list_types 输出一致）。", out var typeError)) return typeError;
            if (!ArgumentValidators.ValidateRequired(memberName, "请指定 memberName 参数（起始方法名子串，忽略大小写）。", out var memberError)) return memberError;

            var search = MemberResolver.FindMembers(assemblyFull, typeName, memberName);
            if (!search.TypeFound) return MetadataNaming.BuildNotFoundMessage(reader, typeName);
            if (search.Matches.Count == 0)
            {
                var message = $"类型 {typeName} 中未找到名称包含 \"{memberName}\" 的方法";
                if (search.SimilarNames.Count > 0) message += $"。相近成员：{string.Join("、", search.SimilarNames)}";
                return message;
            }
            if (search.Matches.Count > 1)
            {
                // 多匹配：返回 #MEMBER 签名清单提示用 token 精确定位起始方法（不反编译）
                var multiContext = new FormatContext(assemblyFull,
                    $"类型 {typeName} 的成员 {memberName}（{search.Matches.Count} 个匹配，仅列出签名，可用 token 参数精确定位起始方法）");
                return OutputFormatter.Format(RenderSignatureList(reader, search.Matches), lines, multiContext);
            }
            var match = search.Matches[0];
            var startHandle = ResolveMethodToken(reader, match.Token);
            if (startHandle is null)
            {
                return $"匹配的成员 {match.Name} 不是方法，无法分析其调用序列";
            }
            return await RunScanAsync(pe, startHandle.Value,
                $"类型 {match.TypeName} 的成员 {memberName}（调用序列）",
                assemblyFull, includeExternal, lines, timeoutSeconds, cancellationToken);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return $"无法读取程序集元数据：{ex.Message}";
        }
    }

    /// <summary>
    /// 扫描起始方法体取调用序列，去重唯一内部成员并逐条反编译，合并为一次输出。
    /// </summary>
    private static async Task<string> RunScanAsync(PEReader pe, MethodDefinitionHandle startMethod,
        string targetDesc, string assemblyFull, bool includeExternal, string lines, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var scanner = new CallChainScanner(pe);
        var callSites = scanner.ScanMethod(startMethod);

        // 唯一内部成员：按 MemberToken 去重、保首现序（外部调用 MemberToken 为 null 不参与）
        var uniqueInternal = new List<CallSite>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var callSite in callSites)
        {
            if (callSite.IsExternal || callSite.MemberToken is null) continue;
            if (seen.Add(callSite.MemberToken)) uniqueInternal.Add(callSite);
        }

        var overLimit = uniqueInternal.Count > AppConfig.MaxMemberMatches;
        using var expander = includeExternal ? new ExternalCallExpander(assemblyFull) : null;
        var context = new FormatContext(assemblyFull, overLimit ? $"{targetDesc}（{AppText.OverLimitOnlySignatures}）" : targetDesc);
        var (merged, allCached, error) = await BuildMergedLinesAsync(callSites, uniqueInternal, includeExternal, expander, assemblyFull, timeoutSeconds, cancellationToken);
        if (error is not null) return error;
        // 降级计数须在展开完成后合并：expander.AbortedBodies 仅在其内部 Expand 执行后累计，展开前读取恒为 0
        var degraded = scanner.AbortedBodies + (expander?.AbortedBodies ?? 0);
        if (degraded > 0) context = context with { Degraded = degraded };
        if (allCached) context = context with { IsCached = true };
        return OutputFormatter.Format(merged!, lines, context);
    }

    /// <summary>
    /// 组装合并行：`方法体调用序列:` + 序列行 + 空行 + `被调用成员反编译:` + 每唯一内部成员 #MEMBER 分隔行 + 反编译体行。 匹配数超过上限时仅渲染 #MEMBER
    /// 签名清单（含 signature）不反编译；无内部调用时省略反编译段。 任一成员反编译失败/超时返回可重试错误提示（Error 非空、Lines 为 null），不抛异常。 返回
    /// (合并行列表, 是否全部缓存命中, 错误提示)。
    /// </summary>
    private static async Task<(List<string>? Lines, bool AllCached, string? Error)> BuildMergedLinesAsync(
        IReadOnlyList<CallSite> callSites, IReadOnlyList<CallSite> uniqueInternal, bool includeExternal,
        ExternalCallExpander? expander, string assemblyFull, int timeoutSeconds, CancellationToken cancellationToken)
    {
        var merged = new List<string>();
        merged.Add("方法体调用序列:");
        var index = 0;
        foreach (var callSite in callSites)
        {
            if (callSite.IsExternal && !includeExternal) continue;
            index++;
            var display = callSite.IsExternal
                ? $"{callSite.TypeFullName}::{callSite.MemberName} [{ShortAssemblyName(callSite)}]"
                : $"{callSite.TypeFullName}::{callSite.MemberName}()";
            var tokenPart = callSite.MemberToken is null ? "" : $"  {callSite.MemberToken}";
            var line = $"  {index}. {display}{tokenPart}";
            if (callSite.IsExternal && expander is not null)
            {
                // 跨程序集调用展开：可解析则外部调用行后缩进追加展开行；解析失败在行尾标注终止。 搜索目录为空数组：主 dll 同目录由 resolver 构造自带、CWD 由
                // expander 恒加，无需重复传
                var expansion = expander.Expand(callSite, Array.Empty<string>());
                if (expansion.Count == 0)
                {
                    line += $"（{string.Format(AppText.UnresolvedAssemblyAnnotation, ShortAssemblyName(callSite))}）";
                }
                merged.Add(line);
                foreach (var expansionLine in expansion)
                {
                    merged.Add("  " + expansionLine);
                }
            }
            else
            {
                merged.Add(line);
            }
        }
        if (index == 0) merged.Add("  " + SectionBuilder.EmptyPlaceholder);

        if (uniqueInternal.Count == 0)
        {
            return (merged, false, null);
        }
        merged.Add("");
        merged.Add("被调用成员反编译:");
        if (uniqueInternal.Count > AppConfig.MaxMemberMatches)
        {
            // 超过上限：仅返回 #MEMBER 签名清单（signature 来自扫描期渲染），不启动反编译
            foreach (var callSite in uniqueInternal)
            {
                merged.Add(OutputFormatter.MemberLine(callSite.MemberName, callSite.MemberToken!, callSite.Signature, callSite.TypeFullName));
            }
            return (merged, false, null);
        }

        var allCached = true;
        foreach (var callSite in uniqueInternal)
        {
            var command = new ToolCommand(assemblyFull, new DecompileRequest(DecompileKind.Member, callSite.MemberToken!))
            {
                MemberName = callSite.MemberName,
                MemberToken = callSite.MemberToken,
                MemberType = callSite.TypeFullName,
            };
            List<string> body;
            bool fromCache;
            try
            {
                (body, fromCache) = await AppServices.Pipeline.GetSourceLinesAsync(command, TimeSpan.FromSeconds(timeoutSeconds), cancellationToken);
            }
            catch (Exception ex)
            {
                // 任一成员反编译失败/超时：返回可重试提示文本而非抛异常（与 ExecuteMergedAsync 语义一致：丢弃已拼部分，不写缓存，同 key 可重试）。 底层
                // GetSourceLinesAsync 抛出的异常 Message 已带「反编译失败：」前缀（InProcessDecompiler 兜底），不再重复包装
                return (null, false, ex.Message.StartsWith(AppText.DecompileFailurePrefix, StringComparison.Ordinal) ? ex.Message : $"{AppText.DecompileFailurePrefix}{ex.Message}");
            }
            allCached &= fromCache;
            merged.Add(OutputFormatter.MemberLine(callSite.MemberName, callSite.MemberToken!, type: callSite.TypeFullName));
            merged.AddRange(body);
        }
        return (merged, allCached, null);
    }

    /// <summary>
    /// 外部调用的程序集短名（完整名首段）；归属未知时返回 "&lt;外部&gt;"。
    /// </summary>
    private static string ShortAssemblyName(CallSite callSite)
    {
        var fullName = callSite.AssemblyFullName;
        if (string.IsNullOrEmpty(fullName)) return "<外部>";
        var comma = fullName.IndexOf(',');
        return comma >= 0 ? fullName[..comma].Trim() : fullName;
    }

    /// <summary>
    /// 将成员匹配集合渲染为 #MEMBER 签名清单（含 name/token/signature/type），供多匹配提示。
    /// </summary>
    private static List<string> RenderSignatureList(MetadataReader reader, IReadOnlyList<MemberMatch> matches)
    {
        var lines = new List<string>();
        foreach (var match in matches)
        {
            var handle = MetadataTokens.EntityHandle(ParseTokenValue(match.Token));
            var typeHandle = MetadataNaming.FindType(reader, match.TypeName);
            var signature = typeHandle is not null
                ? SignatureRenderer.RenderSingleMember(reader, reader.GetTypeDefinition(typeHandle.Value), handle)
                : "";
            lines.Add(OutputFormatter.MemberLine(match.Name, match.Token, signature, match.TypeName));
        }
        return lines;
    }

    /// <summary>
    /// 解析元数据 token 文本为方法定义句柄：要求 Kind 为 MethodDefinition（0x06）且行号在 MethodDefinitions.Count 范围内； 否则返回 null。
    /// </summary>
    private static MethodDefinitionHandle? ResolveMethodToken(MetadataReader reader, string token)
    {
        var value = int.Parse(token.Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var row = value & 0x00FFFFFF;
        if ((value >> 24) != (int)HandleKind.MethodDefinition) return null;
        if (row < 1 || row > reader.MethodDefinitions.Count) return null;
        return MetadataTokens.MethodDefinitionHandle(row);
    }

    private static int ParseTokenValue(string token)
        => int.Parse(token.Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
}