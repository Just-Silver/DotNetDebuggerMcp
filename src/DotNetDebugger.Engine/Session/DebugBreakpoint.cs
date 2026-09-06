using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Engine.Session;

/// <summary>断点行为模式（P5）：Stop=命中即停（默认）；Trace=命中不停，快照变量后自动继续（轨迹记录）。</summary>
public enum DebugBreakpointMode
{
    Stop,
    Trace,
}

/// <summary>
/// 断点：按 ModuleName + MethodToken + IlOffset 定位（spec §4.1）。运行时绑定到 CorDebugFunctionBreakpoint。
/// MethodToken 为 mdMethodDef（0x06 开头），ModuleName 须与运行时模块名一致。
/// P5：HitCount=开始生效的命中次数（第 N 次起每次都停/记录，默认 1）；Hits=已命中计数（pending 重绑后保留）。
/// </summary>
public sealed class DebugBreakpoint
{
    internal DebugBreakpoint(int id, string moduleName, int methodToken, int ilOffset, int hitCount = 1, DebugBreakpointMode mode = DebugBreakpointMode.Stop)
    {
        Id = id; ModuleName = moduleName; MethodToken = methodToken; IlOffset = ilOffset;
        HitCount = Math.Max(1, hitCount);
        Mode = mode;
    }

    public int Id { get; }
    public string ModuleName { get; }
    public int MethodToken { get; }
    public int IlOffset { get; }

    /// <summary>开始生效的命中次数（1=每次；N=第 N 次起每次都停/记录）。</summary>
    public int HitCount { get; }

    /// <summary>命中行为模式（Stop=停；Trace=不停，记轨迹）。</summary>
    public DebugBreakpointMode Mode { get; }

    /// <summary>已命中次数（引擎命中路径递增；与 HitCount 组合实现「第 N 次起生效」）。</summary>
    public int Hits { get; private set; }

    /// <summary>命中计数 +1（仅引擎命中路径调用，命令泵 MTA 单线程）。</summary>
    internal void RegisterHit() => Hits++;

    /// <summary>是否已绑定运行时断点（模块未加载时为 false 的 pending 断点，LoadModule 后自动转 true）。</summary>
    public bool IsBound => RuntimeBreakpoint is not null;

    /// <summary>运行时绑定（内部，仅引擎可写）。</summary>
    internal ClrDebug.CorDebugFunctionBreakpoint? RuntimeBreakpoint { get; set; }

    public FrameLocation ToLocation() => new(ModuleName, MethodToken, IlOffset);
    public override string ToString() => $"[{Id}] {ToLocation()}";
}
