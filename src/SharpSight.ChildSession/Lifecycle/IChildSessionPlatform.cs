using SharpSight.ChildSession.Ipc;

namespace SharpSight.ChildSession.Lifecycle;

public sealed record ChildSystemStatus(bool Enabled, int ParentSessionId, int? ChildSessionId, bool ActiveXAvailable);

public interface IChildSessionPlatform
{
    ChildPeer Current { get; }
    ChildSystemStatus Probe();
    ChildPeer StartHolder(string root, string instance);
    bool IsAlive(ChildPeer peer);
    bool? IsActive(int sessionId);
    void RequestLogoff(int childSessionId);
    void CloseHolder(ChildPeer holder);
    void SetEnabled(bool enabled);
}
