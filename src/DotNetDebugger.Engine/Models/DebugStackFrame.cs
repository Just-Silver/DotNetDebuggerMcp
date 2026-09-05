namespace DotNetDebugger.Engine.Models;

/// <summary>栈帧：位置三元组 + 可选类型/方法名（由调用方填充，Engine 不反编译）。</summary>
public sealed record DebugStackFrame(FrameLocation Location, int FrameIndex)
{
    public string? TypeName { get; init; }
    public string? MethodName { get; init; }
}
