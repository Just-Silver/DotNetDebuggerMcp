namespace DotNetDebugger.Engine.Models;

public sealed record DebugThreadInfo(int ThreadId, int OsThreadId, string? Name, long TaskId);
