namespace DotNetDebugger.Engine.Session;

/// <summary>
/// 源行断点解析结果（R6）：模块磁盘路径 + 方法 token + IL offset + 实际落点行。
/// </summary>
public sealed record SourceLineResolveResult(string ModulePath, string ModuleName, int MethodToken, int IlOffset, int ActualLine);

/// <summary>
/// 源行断点解析器契约（R6 依赖倒置）：Engine 定义、Session 实现（Session 引 Decompiler，包 SourceLineResolver）——
/// Engine 不引 Decompiler/不解析类型名（AGENTS 边界），而源行断点（sourcePath+line → token+IL）需要 PDB 解析。
/// 解析器在模块加载（TrackModule）时被引擎调用：对新加载模块尝试解析 pending 源行断点。
/// 实现要求：纯磁盘读（PDB），可并发/重复调用，失败返回 null（调用方保持 pending）。
/// </summary>
public interface ISourceLineBreakpointResolver
{
    /// <summary>在指定模块磁盘路径上解析源文件+行 → 断点落点。失败（无 PDB/无此源文件/行无映射）返回 null。</summary>
    SourceLineResolveResult? Resolve(string modulePath, string sourcePath, int line);
}
