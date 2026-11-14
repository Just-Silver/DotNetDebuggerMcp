namespace SharpSight.ChildSession.Contracts;

public sealed record ChildIdentity(
    string Instance, string Sid, int ParentSessionId, int ChildSessionId,
    int HolderPid, long HolderStartedTicks, int WorkerPid, long WorkerStartedTicks);
