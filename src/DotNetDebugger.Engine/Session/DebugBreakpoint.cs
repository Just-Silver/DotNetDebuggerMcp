using DotNetDebugger.Engine.Models;

namespace DotNetDebugger.Engine;

/// <summary>
/// 断点：按 ModuleName + MethodToken + IlOffset 定位（spec §4.1）。运行时绑定到 CorDebugFunctionBreakpoint。
/// MethodToken 为 mdMethodDef（0x06 开头），ModuleName 须与运行时模块名一致。
/// </summary>
public sealed class DebugBreakpoint
{
    internal DebugBreakpoint(int id, string moduleName, int methodToken, int ilOffset)
    {
        Id = id; ModuleName = moduleName; MethodToken = methodToken; IlOffset = ilOffset;
    }

    public int Id { get; }
    public string ModuleName { get; }
    public int MethodToken { get; }
    public int IlOffset { get; }

    /// <summary>运行时绑定（内部，仅引擎可写）。</summary>
    internal ClrDebug.CorDebugFunctionBreakpoint? RuntimeBreakpoint { get; set; }

    public FrameLocation ToLocation() => new(ModuleName, MethodToken, IlOffset);
    public override string ToString() => $"[{Id}] {ToLocation()}";
}
