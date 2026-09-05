using ICSharpCode.Decompiler;
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.CSharp.Syntax;
using ICSharpCode.Decompiler.Metadata;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace DotNetDebugger.Decompiler.Document;

/// <summary>
/// 反编译文档服务：对指定程序集类型产出干净反编译文本 + 「方法 token + IL offset → 反编译文本行号」的语句级映射。
/// 供 Web 代码视图停点语句高亮 / 断点定位（spec §6）。
///
/// 关键产出管线（探针实测确证，勿改）：反编译文本输出必须经 TextWriterTokenWriter 包
/// TokenWriter.WrapInWriterThatSetsLocationsInAST —— writer 写文本时把真实行列回写 AST 节点，
/// 之后 CreateSequencePoints 才对无 PDB 程序集产出有效语句级映射（裸 tree.ToString() 节点位置留
/// (0,0)，映射全错）。此路径与 ILSpy 官方 PDB 生成器 / dnSpyEx 同源。
/// </summary>
public static class DocumentService
{
    /// <summary>
    /// 反编译指定类型为文档（文本 + 行数组 + IL→行映射）。错误返回 Error 中文提示，不抛异常。
    /// </summary>
    public static SourceDocument GetTypeDocument(string assemblyPath, string typeFullName)
    {
        try
        {
            using var module = OpenModule(assemblyPath);
            var resolver = new UniversalAssemblyResolver(assemblyPath, false, module.Metadata.DetectTargetFrameworkId());
            var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
            var decompiler = new CSharpDecompiler(assemblyPath, resolver, settings);

            var handle = MetadataNaming.FindType(module.Metadata, typeFullName);
            if (handle is null)
                return new SourceDocument(assemblyPath, typeFullName, "", [], [], $"未找到类型 {typeFullName}");

            var typeDef = module.Metadata.GetTypeDefinition(handle.Value);
            var fullName = new ICSharpCode.Decompiler.TypeSystem.FullTypeName(MetadataNaming.FullName(module.Metadata, typeDef));
            var tree = decompiler.DecompileType(fullName);

            // 关键：位置回写 writer 输出（勿用 tree.ToString()——节点位置留 (0,0) 导致映射全错）
            var sw = new StringWriter();
            var raw = new TextWriterTokenWriter(sw) { IndentationString = "\t" };
            var locWriter = TokenWriter.WrapInWriterThatSetsLocationsInAST(raw);
            tree.AcceptVisitor(new CSharpOutputVisitor(locWriter, settings.CSharpFormattingOptions));
            var text = sw.ToString();

            var mapping = BuildMapping(decompiler, tree);
            return new SourceDocument(assemblyPath, typeFullName, text,
                text.Replace("\r\n", "\n").Split('\n'), mapping);
        }
        catch (OperationCanceledException)
        {
            return new SourceDocument(assemblyPath, typeFullName, "", [], [], "反编译已取消");
        }
        catch (Exception ex)
        {
            return new SourceDocument(assemblyPath, typeFullName, "", [], [], $"反编译失败：{ex.Message}");
        }
    }

    /// <summary>
    /// 停点定位：IL offset → 反编译文本行号。二分查 [IlOffset, EndOffset) 区间；无命中返回 null。
    /// </summary>
    public static int? GetLineForIlOffset(SourceDocument doc, int methodToken, int ilOffset)
    {
        var mapping = doc.Mapping;
        // 映射按 MethodToken 升序、MethodToken 内 IlOffset 升序（BuildMapping 已排序）→ 先定位 token 段再二分
        int lo = 0, hi = mapping.Count;
        while (lo < hi)
        {
            int mid = (lo + hi) / 2;
            if (mapping[mid].MethodToken < methodToken) lo = mid + 1;
            else hi = mid;
        }
        for (var i = lo; i < mapping.Count && mapping[i].MethodToken == methodToken; i++)
        {
            var e = mapping[i];
            if (ilOffset >= e.IlOffset && ilOffset < e.EndOffset) return e.Line;
        }
        return null;
    }

    /// <summary>
    /// 反向（设断点）：文本行 → 该行所属映射条目的 (MethodToken, IlStart)。
    /// 行被多个条目覆盖时取 IlOffset 最小者；无命中返回 null。
    /// </summary>
    public static (int MethodToken, int IlStart)? GetIlStartForLine(SourceDocument doc, int line)
    {
        (int MethodToken, int IlStart)? best = null;
        foreach (var e in doc.Mapping)
        {
            if (e.Line == line)
            {
                if (best is null || e.IlOffset < best.Value.IlStart)
                    best = (e.MethodToken, e.IlOffset);
            }
        }
        return best;
    }

    /// <summary>CreateSequencePoints → 序列化可见 sequence point 为条目（token 取 func.Method）。</summary>
    private static IReadOnlyList<IlToLineEntry> BuildMapping(CSharpDecompiler decompiler, SyntaxTree tree)
    {
        var dict = decompiler.CreateSequencePoints(tree);
        var entries = new List<IlToLineEntry>();
        foreach (var (func, sps) in dict)
        {
            if (func.Method is null || func.Method.MetadataToken.IsNil) continue;
            var token = MetadataTokens.GetToken(func.Method.MetadataToken);
            foreach (var sp in sps)
            {
                if (sp.IsHidden) continue;
                entries.Add(new IlToLineEntry(token, sp.Offset, sp.EndOffset, sp.StartLine, sp.StartColumn));
            }
        }
        return entries.OrderBy(e => e.MethodToken).ThenBy(e => e.IlOffset).ToList();
    }

    /// <summary>安全打开程序集（自建 FileStream 传给 PEFile；解析失败抛异常由调用方 catch）。</summary>
    private static PEFile OpenModule(string assemblyPath)
    {
        var stream = new FileStream(assemblyPath, FileMode.Open, FileAccess.Read, FileShare.Read);
        return new PEFile(assemblyPath, stream);
    }
}
