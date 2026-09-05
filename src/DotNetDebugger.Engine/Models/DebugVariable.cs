namespace DotNetDebugger.Engine.Models;

/// <summary>局部变量/参数：名称(槽位)+值。</summary>
public sealed record DebugVariable(string? Name, int Slot, DebugValue Value, bool IsArgument);
