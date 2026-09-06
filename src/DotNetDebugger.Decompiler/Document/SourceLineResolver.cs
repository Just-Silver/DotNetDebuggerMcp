using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace DotNetDebugger.Decompiler.Document;

/// <summary>
/// PDB 源码行 → 断点落点（方法 token + IL offset）解析（P3-3b，纯 SRM，无 ICorDebug 依赖）：
/// 读模块旁 portable PDB，按归一化文档名匹配源文件，遍历序列点定位该行的语句。
/// 落点语义：优先精确落在该行（StartLine == line 的序列点，取最小 offset）；该行无独立序列点时，
/// 取「覆盖该行的方法」中 ≥ line 的最近序列点（落点为大括号/签名后的首条语句，与反编译行断点同款）。
/// </summary>
public static class SourceLineResolver
{
    /// <summary>解析结果：方法 token、IL offset、实际落点行（精确匹配时 == 请求行，回退时为最近语句行）。</summary>
    public sealed record SourceLineTarget(int MethodToken, int IlOffset, int ActualLine);

    /// <summary>
    /// 解析源码行断点落点。失败返回 null 且 <paramref name="error"/> 为中文提示；成功 error 为 null。
    /// PDB 缺失、文档名歧义、行无映射分别给出可诊断的提示。
    /// </summary>
    /// <param name="modulePath">模块磁盘路径（PDB 取同目录同名 .pdb）。</param>
    /// <param name="sourcePath">源文件路径：绝对/相对/仅文件名末段（如 Program.cs）均可。</param>
    /// <param name="line">源码行号（1-based）。</param>
    /// <param name="error">失败时的中文提示。</param>
    public static SourceLineTarget? Resolve(string modulePath, string sourcePath, int line, out string? error)
    {
        error = null;
        if (line <= 0)
        {
            error = "请提供源码行号（line，1-based）。";
            return null;
        }

        var pdbPath = Path.ChangeExtension(modulePath, ".pdb");
        if (!File.Exists(pdbPath))
        {
            error = $"模块 {Path.GetFileName(modulePath)} 旁无 PDB（{pdbPath}），无法按源码行定位；可用 typeName+line 按反编译行坐标下断点。";
            return null;
        }

        try
        {
            using var pdbFs = File.OpenRead(pdbPath);
            using var provider = MetadataReaderProvider.FromPortablePdbStream(pdbFs);
            var mr = provider.GetMetadataReader();

            // 方法枚举必须走 DLL 元数据（portable PDB 无 MethodDef 表），调试信息用同一 rid 句柄经 PDB reader 取
            using var dllFs = File.OpenRead(modulePath);
            using var pe = new PEReader(dllFs);
            var dllReader = pe.GetMetadataReader();

            // 文档名匹配：归一化 /→\、忽略大小写；输入允许绝对/相对路径或仅末段文件名（EndsWith("\input") 天然覆盖末段）
            var input = Normalize(sourcePath);
            var docMatches = new List<DocumentHandle>();
            foreach (var dh in mr.Documents)
            {
                var name = Normalize(mr.GetString(mr.GetDocument(dh).Name));
                if (name.Equals(input, StringComparison.OrdinalIgnoreCase)
                    || name.EndsWith("\\" + input, StringComparison.OrdinalIgnoreCase))
                {
                    docMatches.Add(dh);
                }
            }

            if (docMatches.Count == 0)
            {
                error = $"PDB 中未找到源文件 \"{sourcePath}\"（模块 {Path.GetFileName(modulePath)}）。";
                return null;
            }
            if (docMatches.Count > 1)
            {
                var names = docMatches.Select(dh => mr.GetString(mr.GetDocument(dh).Name)).Take(5);
                error = $"源文件 \"{sourcePath}\" 在 PDB 中有歧义（{docMatches.Count} 个匹配）：{string.Join("、", names)}。请提供更完整的路径。";
                return null;
            }

            var docHandle = docMatches[0];

            // 收集该文档上的全部语句序列点（跳过隐藏行）；token 取自 DLL 元数据
            var points = new List<(int Token, int Offset, int StartLine, int EndLine)>();
            foreach (var mh in dllReader.MethodDefinitions)
            {
                var token = MetadataTokens.GetToken(mh);
                try
                {
                    foreach (var sp in mr.GetMethodDebugInformation(mh).GetSequencePoints())
                    {
                        if (sp.Document != docHandle) continue;
                        if (sp.StartLine == SequencePoint.HiddenLine) continue;
                        points.Add((token, sp.Offset, sp.StartLine, sp.EndLine));
                    }
                }
                catch { /* 无调试信息的方法：跳过 */ }
            }

            if (points.Count == 0)
            {
                error = $"PDB 中源文件 \"{sourcePath}\" 无任何语句映射（可能为非用户代码或优化剔除）。";
                return null;
            }

            // 1) 精确：StartLine == line，多命中取 (offset, token) 最小（同方法多语句取第一条，多方法同行取序小者）
            var exact = points.Where(p => p.StartLine == line)
                .OrderBy(p => p.Offset).ThenBy(p => p.Token).FirstOrDefault();
            if (exact != default)
                return new SourceLineTarget(exact.Token, exact.Offset, line);

            // 2) 回退：覆盖该行（start <= line <= end）的方法内，≥ line 的最近序列点；全局取 (startLine, token, offset) 最小
            var coveringTokens = points.Where(p => p.StartLine <= line && p.EndLine >= line)
                .Select(p => p.Token).ToHashSet();
            var fallback = points.Where(p => coveringTokens.Contains(p.Token) && p.StartLine >= line)
                .OrderBy(p => p.StartLine).ThenBy(p => p.Token).ThenBy(p => p.Offset).FirstOrDefault();
            if (fallback != default)
                return new SourceLineTarget(fallback.Token, fallback.Offset, fallback.StartLine);

            error = $"源文件 \"{sourcePath}\" 第 {line} 行无语句映射（该方法行范围 {points.Min(p => p.StartLine)}-{points.Max(p => p.EndLine)}）。";
            return null;
        }
        catch (Exception ex)
        {
            error = $"读取 PDB 失败：{ex.Message}";
            return null;
        }
    }

    private static string Normalize(string path) => path.Replace('/', '\\').Trim();
}
