namespace DotNetDebugger.Decompiler.Document;

/// <summary>一个反编译类型文档（代码视图展示源）。文本干净无行号前缀，Lines 按 1-based 索引。</summary>
public sealed record SourceDocument(
    string AssemblyPath,
    string TypeFullName,
    string Text,
    string[] Lines,
    IReadOnlyList<IlToLineEntry> Mapping,
    string? Error = null)
{
    public bool IsSuccess => Error is null;
}

/// <summary>一条「方法 token + IL 区间 → 反编译文本行列」映射（无 PDB 亦有效）。</summary>
public sealed record IlToLineEntry(
    int MethodToken,
    int IlOffset,       // 区间起始（含）
    int EndOffset,      // 区间结束（不含）
    int Line,           // 1-based
    int Column);
