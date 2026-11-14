using System.Drawing;

namespace SharpSight.ChildSession.Input;

public sealed record ChildInputEvent(string Kind, int X = 0, int Y = 0, ushort Key = 0,
    bool Up = false, string Button = "left", int Delta = 0);

public interface IChildInputPlatform
{
    int SessionId { get; }
    bool IsInteractive { get; }
    ResolvedChildTarget Resolve(int processId, string hwnd);
    Task<ResolvedChildTarget> ActivateAsync(ResolvedChildTarget target, CancellationToken ct);
    bool IsForeground(ResolvedChildTarget target);
    bool PointBelongsTo(ResolvedChildTarget target, Point point);
    bool IsKeyDown(ushort key);
    bool Submit(ChildInputEvent input, ulong marker);
}
