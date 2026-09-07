using DotNetDebugger.Decompiler.Document;
using DotNetDebugger.Engine.Session;

namespace DotNetDebugger.Session;

/// <summary>
/// 源行断点解析器（R6）：Session 实现 ISourceLineBreakpointResolver（Engine 定义接口、Session 提供实现——
/// Session 引 Decompiler，包 SourceLineResolver 的 PDB 解析能力；Engine 不引 Decompiler 保持边界）。
/// 纯磁盘读、线程安全、可重复调用；解析失败返回 null（调用方保持 pending）。
/// </summary>
public sealed class SourceLineBreakpointResolver : ISourceLineBreakpointResolver
{
    public static SourceLineBreakpointResolver Instance { get; } = new();

    public SourceLineResolveResult? Resolve(string modulePath, string sourcePath, int line)
    {
        var target = SourceLineResolver.Resolve(modulePath, sourcePath, line, out _);
        if (target is null) return null;
        return new SourceLineResolveResult(
            modulePath,
            System.IO.Path.GetFileName(modulePath),
            target.MethodToken,
            target.IlOffset,
            target.ActualLine);
    }
}
