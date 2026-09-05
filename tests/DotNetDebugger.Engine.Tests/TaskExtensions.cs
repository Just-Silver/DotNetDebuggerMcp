namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// 测试用 Task 扩展：xUnit1031 禁止在测试里用阻塞的 <c>task.Wait(ms)</c>，
/// 而 <c>WaitAsync</c> 超时会抛 TimeoutException——提供「限时等待、超时静默」的原语义替代（事件读者排水用）。
/// </summary>
public static class TaskExtensions
{
    /// <summary>最多等待 <paramref name="milliseconds"/> 毫秒，超时静默返回（不抛 TimeoutException）。</summary>
    public static async Task WaitBounded(this Task task, int milliseconds, CancellationToken ct)
    {
        try { await task.WaitAsync(TimeSpan.FromMilliseconds(milliseconds), ct); }
        catch (TimeoutException) { }
    }
}
