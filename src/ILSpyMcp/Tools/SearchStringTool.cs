using ILSpyMcp.Configuration;
using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Validation;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text;

namespace ILSpyMcp.Tools;

/// <summary>
/// 在 .NET 程序集（dll/exe）中按字符串字面量子串反查成员。
/// </summary>
[McpServerToolType]
public static class SearchStringTool
{
    /// <summary>
    /// 按字符串字面量子串反查成员：扫描全部（或 typeName 指定）类型方法体 IL 的 ldstr 指令，
    /// 按子串忽略大小写匹配用户字符串（业务文案/SQL 片段/配置 Key 等），输出每行
    /// 类型全名::成员签名 + 转义后的字符串值 + 成员 token（token 可直接用于 decompile_member 的 token 参数反编译）。
    /// </summary>
    /// <param name="assembly">要反查的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）。</param>
    /// <param name="search">要搜索的字符串字面量子串（忽略大小写，必填），如 "配置Key"、"order by"。</param>
    /// <param name="typeName">限定在指定类型内反查，类型全名（格式与 list_types 输出一致）；省略时跨程序集全部类型。</param>
    /// <param name="lines">按行号范围读取结果，格式 "start-end"；缺省返回前约 8 KB。</param>
    /// <param name="cancellationToken">取消令牌（MCP 客户端取消调用时由框架注入）。</param>
    /// <returns>带行号的命中成员行或错误提示文本。</returns>
    [McpServerTool]
    [Description("在 .NET 程序集（dll/exe）的方法体中按字符串字面量子串反查成员：扫描全部（或 typeName 指定）类型方法体的 ldstr 指令，按子串忽略大小写匹配用户字符串（如业务文案、SQL 片段、配置 Key），输出每行 {类型全名}::{成员签名}  \"转义后的字符串值\"  {成员token}（token 可直接用于 decompile_member 的 token 参数反编译对应成员）。typeName 为类型全名（格式与 list_types 输出一致），非空时仅在指定类型内反查；省略时跨程序集全部类型。适用于按文案/配置 Key 反查代码位置，无需反编译全文。匹配数可能很多（同一成员内多个匹配字符串各占一行）；结果默认只返回前约 8 KB，可用 lines 参数按行号范围拉取后续。")]
    public static Task<string> SearchString(
        [Description("要反查的程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）")] string assembly = "",
        [Description("要搜索的字符串字面量子串（忽略大小写，必填），例如 \"配置Key\"、\"order by\"")] string search = "",
        [Description("限定在指定类型内反查，类型全名（格式与 list_types 输出一致，如 ILSpyMcp.Caching.DecompileCache）；省略时跨程序集全部类型（默认空=全程序集）")] string typeName = "",
        [Description("按行号范围读取结果，格式 \"start-end\"（1-based 含两端，单次最多约 32 KB），例如 \"200-400\"；缺省返回前约 8 KB")] string lines = "",
        CancellationToken cancellationToken = default)
    {
        // 参数校验：assembly 必填且文件存在（本工具纯元数据读取）
        if (!ArgumentValidators.ValidateAssembly(assembly, out var assemblyError)) return Task.FromResult(assemblyError);
        // 参数校验：search 必填
        if (!ArgumentValidators.ValidateRequired(search, "请指定 search 参数（字符串子串，忽略大小写）。", out var searchError)) return Task.FromResult(searchError);

        // 解析程序集绝对路径
        if (ToolExecutor.ResolveAssembly(assembly, out var assemblyFull) is { } pathError) return Task.FromResult(pathError);
        cancellationToken.ThrowIfCancellationRequested();

        // 头部信息块：程序集绝对路径 + 目标描述（参数不展示——agent 面对的是 MCP 命名参数）
        var target = string.IsNullOrEmpty(typeName) ? $"字符串字面量含 {search}" : $"类型 {typeName} 内字符串字面量含 {search}";
        var context = new FormatContext(assemblyFull, target, IsListing: true);

        // 元数据读取经共享缓存（命中直接返回，头部标注缓存命中）；typeName 歧义/未找到以异常抛提示、不入缓存
        var signature = $"{CacheSignatures.SearchString}{CacheSignatures.Separator}{search}{CacheSignatures.Separator}{typeName}";
        var aborted = 0;
        return Task.FromResult(ToolExecutor.RunMetadataPe(assemblyFull, signature, lines, context, (pe, reader) =>
        {
            var scanner = new StringLiteralScanner(pe);
            TypeDefinitionHandle? onlyType = null;
            if (!string.IsNullOrEmpty(typeName))
            {
                var candidates = MetadataNaming.FindTypes(reader, typeName);
                if (candidates.Count > 1) throw new InvalidOperationException(MetadataNaming.BuildAmbiguityMessage(reader, typeName, candidates, "该类型名在归一化后存在同名类型，请换用不含歧义的完整类型名"));
                if (candidates.Count == 0) throw new InvalidOperationException(MetadataNaming.BuildNotFoundMessage(reader, typeName));
                onlyType = candidates[0];
            }

            var hits = scanner.Scan(search, onlyType);
            aborted = scanner.AbortedBodies;
            var outputLines = new List<string>(hits.Count);
            foreach (var hit in hits)
            {
                outputLines.Add($"{hit.TypeFullName}::{hit.MemberSignature}  \"{EscapeValue(hit.Value)}\"  {hit.MemberToken}");
            }
            return outputLines;
        }, cancellationToken, degradedProvider: () => aborted));
    }

    /// <summary>
    /// 转义输出行内字符串字面量：\ " \n \r \t 转义，保持行格式可解析。
    /// </summary>
    private static string EscapeValue(string value)
    {
        var sb = new StringBuilder(value.Length);
        foreach (var c in value)
        {
            switch (c)
            {
                case '\\': sb.Append("\\\\"); break;
                case '"': sb.Append("\\\""); break;
                case '\n': sb.Append("\\n"); break;
                case '\r': sb.Append("\\r"); break;
                case '\t': sb.Append("\\t"); break;
                default: sb.Append(c); break;
            }
        }
        return sb.ToString();
    }
}
