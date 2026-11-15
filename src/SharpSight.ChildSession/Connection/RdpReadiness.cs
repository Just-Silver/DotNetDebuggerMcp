namespace SharpSight.ChildSession.Connection;

/// <summary>
/// 连接与登录必须在同一次 STA 往返里读取，否则两次询问之间可能跨过不同的连接代际。
/// </summary>
internal readonly record struct RdpReadiness(bool Observed, bool Connected, bool LoginCompleted, string? LoginEventError)
{
    internal static RdpReadiness Unconfirmed => new(false, false, false, null);
}
