namespace SharpSight.UiAutomation;

internal interface IUiExecutionBoundary
{
    Task<T> ExecuteAsync<T>(Func<T> core, TimeSpan timeout, CancellationToken ct);
}

internal sealed class UiExecutionBoundary : IUiExecutionBoundary
{
    public Task<T> ExecuteAsync<T>(Func<T> core, TimeSpan timeout, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        // COM调用无法可靠中断；调用方必须持有客户端和执行门直到真实任务完成。
        return Task.Run(core, CancellationToken.None);
    }
}
