using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
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
/// 追踪指定字段在程序集内的读取/写入/取地址位置（字段级反查）：按 fieldToken 或 typeName+fieldName 定位字段后，
/// 反向扫描全部类型方法体的字段访问指令，输出读取/写入/取地址三段来源成员（类型全名::成员签名）。
/// </summary>
[McpServerToolType]
public static class FieldAccessTool
{
    /// <summary>
    /// 追踪指定字段的读取/写入/取地址位置：fieldToken 非空时按字段元数据 token 直接定位；
    /// 否则按 fieldName 子串搜索（typeName 非空在类型内、空则跨程序集），匹配多个字段时返回 #MEMBER 签名清单提示用 fieldToken。
    /// 定位成功后反向扫描全部非编译器生成类型方法体的字段访问指令，输出三段；经共享缓存秒回。
    /// </summary>
    /// <param name="assembly">要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="typeName">字段所属类型全名，格式与 list_types 输出一致；省略时跨程序集按 fieldName 搜索（fieldToken 分支下可不填）。</param>
    /// <param name="fieldName">字段名子串，忽略大小写；匹配多个字段时返回 #MEMBER 签名清单提示用 fieldToken（fieldToken 分支下可不填）。</param>
    /// <param name="fieldToken">字段元数据 token（0x04 开头，取 signature 行尾或 #MEMBER 分隔行的 token）；非空时按 token 直接定位字段，忽略 fieldName。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的字段读取/写入/取地址三段或错误提示文本。</returns>
    [McpServerTool]
    [Description("追踪 .NET 程序集（dll/exe）中指定字段的读取/写入/取地址位置：反向扫描全部类型方法体的字段访问指令（ldfld/ldsfld 读取、stfld/stsfld 写入、ldflda/ldsflda 取地址），输出三段 类型全名::成员签名 来源成员（空段输出（无）占位）。字段定位方式：提供 fieldToken（字段元数据 token，0x04 开头，取 signature 行尾或 #MEMBER 分隔行的 token，如 0x04000005）直接定位；否则按 fieldName 子串搜索（忽略大小写），提供 typeName 时仅在指定类型内搜索，省略时跨程序集搜索全部类型。字段名匹配多个字段时返回 #MEMBER 签名清单（含 name/token/type），取目标字段的 token 再用 fieldToken 参数精确定位。typeName 为类型全名，格式与 list_types 输出一致。适用于追踪字段的读写点、判断字段是否仍被使用。结果默认只返回前约 8 KB，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> FieldAccess(
        [Description("要查询的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("字段所属类型全名，格式与 list_types 输出一致（命名空间.类型，嵌套类型用 + 或 . 分隔，泛型类型带 arity），例如 ILSpyMcp.Samples.FieldHolder；省略时跨程序集按 fieldName 搜索全部类型（提供 fieldToken 时可不填）")] string typeName = "",
        [Description("字段名子串（忽略大小写），例如 Data；匹配多个字段时返回 #MEMBER 签名清单，用其中 token 作 fieldToken 精确定位（必填；提供 fieldToken 时可不填）")] string fieldName = "",
        [Description("字段元数据 token（0x04 开头，取 signature 行尾或 #MEMBER 分隔行的 token，如 0x04000005）：提供时按 token 直接定位字段，忽略 fieldName，typeName 可不填；默认空=按 fieldName 搜索")] string fieldToken = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 定位字段：fieldToken 分支优先（ValidateToken + Kind==FieldDefinition + row 界校验），否则 fieldName 搜索
        var (matches, byToken, locateError) = LocateField(assemblyFull, typeName, fieldName, fieldToken);
        if (locateError is not null) return Task.FromResult(locateError);

        // 匹配多个字段：不反扫（无唯一目标），返回 #MEMBER 签名清单提示用 fieldToken 精确定位
        if (matches.Count > 1) return Task.FromResult(RenderFieldList(assemblyFull, matches, typeName, fieldName, lines));

        // 唯一字段：反向扫描字段访问点（经共享缓存秒回；降级解析计数接入）
        return RunScan(assemblyFull, typeName, fieldName, byToken, matches[0], lines, cancellationToken);
    }

    /// <summary>
    /// 定位字段定义：fieldToken 非空走 token 分支（校验格式 + Kind == FieldDefinition + row 在 FieldDefinitions 范围内），
    /// 否则按 fieldName 子串搜索（typeName 非空在类型内、空跨程序集），过滤字段（0x04）token。
    /// 未找到类型/歧义/无匹配字段时返回中文提示文本。
    /// </summary>
    private static (List<MemberMatch> Matches, bool ByToken, string? Error) LocateField(
        string assemblyFull, string typeName, string fieldName, string fieldToken)
    {
        // fieldToken 分支：按 token 直接定位单个字段
        if (!string.IsNullOrWhiteSpace(fieldToken))
        {
            if (!ArgumentValidators.ValidateToken(fieldToken, out var tokenError))
            {
                return (new List<MemberMatch>(), true, tokenError);
            }
            try
            {
                using var fs = File.OpenRead(assemblyFull);
                using var pe = new PEReader(fs);
                var reader = pe.GetMetadataReader();
                var handle = ResolveFieldToken(reader, fieldToken);
                if (handle is null)
                {
                    return (new List<MemberMatch>(), true,
                        $"\"{fieldToken.Trim()}\" 不是字段定义的元数据 token（fieldToken 需为字段 token，0x04 开头，如 0x04000005）");
                }
                var field = reader.GetFieldDefinition(handle.Value);
                var declaringType = reader.GetTypeDefinition(field.GetDeclaringType());
                var token = $"0x{MetadataTokens.GetToken(handle.Value):x8}";
                return (new List<MemberMatch> { new(reader.GetString(field.Name), token, MetadataNaming.FullName(reader, declaringType)) }, true, null);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
            {
                return (new List<MemberMatch>(), true, $"无法读取程序集元数据：{ex.Message}");
            }
        }

        // fieldName 分支：必填
        if (!ArgumentValidators.ValidateRequired(fieldName, "请指定 fieldName 参数（字段名子串，忽略大小写；省略 typeName 时跨程序集搜索）。", out var fieldNameError))
        {
            return (new List<MemberMatch>(), false, fieldNameError);
        }

        // 跨程序集搜索：typeName 为空
        if (string.IsNullOrWhiteSpace(typeName))
        {
            var r = MemberResolver.FindMembersAcrossAssembly(assemblyFull, fieldName);
            var fields = r.Matches.Where(m => m.Token.StartsWith("0x04", StringComparison.Ordinal)).ToList();
            if (fields.Count == 0)
            {
                var msg = $"程序集中未找到字段名包含 \"{fieldName}\" 的字段";
                if (r.SimilarNames.Count > 0) msg += $"。相近成员：{string.Join("、", r.SimilarNames)}";
                return (new List<MemberMatch>(), false, msg);
            }
            return (fields, false, null);
        }

        // 类型内搜索：先用 FindTypes 判定歧义/未找到（不能先经 FindMembers——其内部取首个候选会吞掉歧义），
        // 唯一候选再用其全名在类型内按 fieldName 搜索
        try
        {
            using var fs = File.OpenRead(assemblyFull);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var candidates = MetadataNaming.FindTypes(reader, typeName);
            if (candidates.Count > 1)
            {
                return (new List<MemberMatch>(), false, MetadataNaming.BuildAmbiguityMessage(reader, typeName, candidates, "可用 fieldToken 精确定位"));
            }
            if (candidates.Count == 0)
            {
                return (new List<MemberMatch>(), false, MetadataNaming.BuildNotFoundMessage(reader, typeName));
            }
            var fullName = MetadataNaming.FullName(reader, reader.GetTypeDefinition(candidates[0]));
            var r = MemberResolver.FindMembers(assemblyFull, fullName, fieldName);
            if (!r.TypeFound)
            {
                return (new List<MemberMatch>(), false, MetadataNaming.BuildNotFoundMessage(reader, typeName));
            }
            var fields = r.Matches.Where(m => m.Token.StartsWith("0x04", StringComparison.Ordinal)).ToList();
            if (fields.Count == 0)
            {
                var msg = $"类型 {fullName} 中未找到字段名包含 \"{fieldName}\" 的字段";
                if (r.SimilarNames.Count > 0) msg += $"。相近成员：{string.Join("、", r.SimilarNames)}";
                return (new List<MemberMatch>(), false, msg);
            }
            return (fields, false, null);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or BadImageFormatException)
        {
            return (new List<MemberMatch>(), false, $"无法读取程序集元数据：{ex.Message}");
        }
    }

    /// <summary>
    /// 将 fieldToken 文本解析为字段定义句柄：格式已由 <see cref="ArgumentValidators.ValidateToken"/> 校验，
    /// 要求 Kind 为 FieldDefinition（0x04）且行号在 FieldDefinitions.Count 范围内；否则返回 null。
    /// </summary>
    private static FieldDefinitionHandle? ResolveFieldToken(MetadataReader reader, string fieldToken)
    {
        var value = int.Parse(fieldToken.Trim().AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        var row = value & 0x00FFFFFF;
        if ((value >> 24) != (int)HandleKind.FieldDefinition) return null;
        if (row < 1 || row > reader.FieldDefinitions.Count) return null;
        return MetadataTokens.FieldDefinitionHandle(row);
    }

    /// <summary>
    /// 匹配多个字段时仅返回 #MEMBER 签名清单：元数据读取经共享缓存（重复查询直接命中），凡字段 token 属于匹配集合的
    /// 字段渲染一行 `#MEMBER {name/token/signature/type}` JSON 行，agent 取目标字段的 token 再用 fieldToken 参数精确定位。
    /// </summary>
    private static string RenderFieldList(string assemblyFull, IReadOnlyList<MemberMatch> matches, string typeName, string fieldName, string lines)
    {
        var context = new FormatContext(assemblyFull, $"字段 {fieldName}（{matches.Count} 个匹配，请用 fieldToken 精确定位）", IsListing: true);
        return ToolExecutor.RunMetadataPe(assemblyFull, $"field-access-list\u001F{typeName}\u001F{fieldName}", lines, context, (pe, reader) =>
        {
            var tokens = matches.Select(m => m.Token).ToHashSet(StringComparer.Ordinal);
            var signatureLines = new List<string>();
            foreach (var handle in reader.TypeDefinitions)
            {
                var type = reader.GetTypeDefinition(handle);
                if (CompilerGeneratedFilter.IsCompilerGenerated(reader, type)) continue;
                var renderedTypeName = MetadataNaming.FullName(reader, type);
                foreach (var fieldHandle in type.GetFields())
                {
                    var token = $"0x{MetadataTokens.GetToken(fieldHandle):x8}";
                    if (!tokens.Contains(token)) continue;
                    var signature = SignatureRenderer.RenderSingleMember(reader, type, fieldHandle);
                    var name = reader.GetString(reader.GetFieldDefinition(fieldHandle).Name);
                    signatureLines.Add($"#MEMBER {OutputFormatter.MemberJson(name, token, signature, renderedTypeName)}");
                }
            }
            return signatureLines;
        }, default);
    }

    /// <summary>
    /// 唯一字段定位成功后的反向扫描：反扫全部非编译器生成类型方法体的字段访问指令，输出读取/写入/取地址三段，
    /// 经共享缓存秒回（缓存签名含已解析的字段 token），降级解析计数接入头部提示。
    /// </summary>
    private static Task<string> RunScan(string assemblyFull, string typeName, string fieldName, bool byToken,
        MemberMatch match, string lines, CancellationToken cancellationToken)
    {
        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var targetDesc = byToken
            ? string.IsNullOrWhiteSpace(typeName) ? $"字段 {match.Token}（按 token 定位）" : $"类型 {typeName} 的字段 {match.Token}（按 token 定位）"
            : string.IsNullOrWhiteSpace(typeName) ? $"字段 {fieldName}（跨程序集）" : $"类型 {match.TypeName} 的字段 {fieldName}";
        var context = new FormatContext(assemblyFull, targetDesc, IsListing: true);

        // 元数据读取经共享缓存（命中直接返回，头部标注缓存命中）；未找到字段/类型以异常抛提示、不入缓存
        var signature = $"field-access\u001F{match.Token}\u001F{typeName}\u001F{fieldName}";
        FieldAccessScanner? scanner = null;
        return Task.FromResult(ToolExecutor.RunMetadataPe(assemblyFull, signature, lines, context, (pe, reader) =>
        {
            var handle = ResolveFieldToken(reader, match.Token);
            if (handle is null) throw new InvalidOperationException($"\"{match.Token}\" 不是有效的字段 token");
            scanner = new FieldAccessScanner(pe);
            var result = scanner.Scan(handle.Value);

            // 段落标题与来源行均作为行进入 OutputFormatter（会被标注行号）；空段输出（无）占位
            var outputLines = new List<string>();
            SectionBuilder.Append(outputLines, "读取该字段的成员:", result.Reads);
            SectionBuilder.Append(outputLines, "写入该字段的成员:", result.Writes);
            SectionBuilder.Append(outputLines, "取地址的成员:", result.Addresses);
            return outputLines;
        }, cancellationToken, degradedProvider: () => scanner?.AbortedBodies ?? 0));
    }
}
