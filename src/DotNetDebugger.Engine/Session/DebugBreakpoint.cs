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
/// P7：Condition=P6 表达式子集条件（null=无条件；Hits 只数条件为真的通过次数，false/求值失败放行不计数）。
/// R6：源行型断点（SourcePath/SourceLine 非空）——登记时 MethodToken=0/ModuleName=待匹配模块提示，
/// 模块加载后由 ISourceLineBreakpointResolver 解析出 token+IL 再绑定（IsBound 转 true）；
/// 未解析前 IsBound=false（pending）。解析成功后 ModuleName/MethodToken/IlOffset 被填充为实际值。
/// </summary>
public sealed class DebugBreakpoint
{
    internal DebugBreakpoint(int id, string moduleName, int methodToken, int ilOffset, int hitCount = 1, DebugBreakpointMode mode = DebugBreakpointMode.Stop, string? condition = null)
    {
        Id = id; ModuleName = moduleName; MethodToken = methodToken; IlOffset = ilOffset;
        HitCount = Math.Max(1, hitCount);
        Mode = mode;
        Condition = string.IsNullOrWhiteSpace(condition) ? null : condition;
    }

    public int Id { get; }

    /// <summary>模块名（源行型未解析前为待匹配模块提示，可为空串=任意模块；解析成功后为实际模块名）。</summary>
    public string ModuleName { get; internal set; }

    /// <summary>方法 token（源行型未解析前为 0；解析成功后为实际 token）。</summary>
    public int MethodToken { get; internal set; }

    /// <summary>IL offset（源行型未解析前为 0）。</summary>
    public int IlOffset { get; internal set; }

    /// <summary>开始生效的命中次数（1=每次；N=第 N 次起每次都停/记录）。</summary>
    public int HitCount { get; }

    /// <summary>命中行为模式（Stop=停；Trace=不停，记轨迹）。</summary>
    public DebugBreakpointMode Mode { get; }

    /// <summary>条件表达式（P6 子集；null=无条件。条件先于计数：false/求值失败放行且不计入 Hits）。</summary>
    public string? Condition { get; }

    /// <summary>源文件路径（R6 源行型断点；null=普通 token 断点）。</summary>
    public string? SourcePath { get; internal set; }

    /// <summary>源码行号（R6 源行型断点；非 0 时表示源行定位）。</summary>
    public int SourceLine { get; internal set; }

    /// <summary>是否源行型断点（由 sourcePath+line 登记，模块加载后解析成 token 断点）。</summary>
    public bool IsSourceLine => SourcePath is not null;

    /// <summary>已命中次数（引擎命中路径递增；与 HitCount 组合实现「第 N 次起生效」）。</summary>
    public int Hits { get; private set; }

    /// <summary>命中计数 +1（仅引擎命中路径调用，命令泵 MTA 单线程）。</summary>
    internal void RegisterHit() => Hits++;

    /// <summary>是否已绑定运行时断点（模块未加载/源行未解析时为 false 的 pending 断点，LoadModule 后自动转 true）。</summary>
    public bool IsBound => RuntimeBreakpoint is not null;

    /// <summary>运行时绑定（内部，仅引擎可写）。</summary>
    internal ClrDebug.CorDebugFunctionBreakpoint? RuntimeBreakpoint { get; set; }

    public FrameLocation ToLocation() => new(ModuleName, MethodToken, IlOffset);
    public override string ToString() => IsSourceLine && MethodToken == 0
        ? $"[{Id}] source {SourcePath}:{SourceLine}（待解析绑定）"
        : $"[{Id}] {ToLocation()}";
}
