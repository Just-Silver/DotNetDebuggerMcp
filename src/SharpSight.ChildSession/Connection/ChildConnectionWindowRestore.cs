namespace SharpSight.ChildSession.Connection;

/// <summary>
/// 连接窗还原的排队/等待/放弃逻辑（holder STA 与自动化回归共用的真实入口）。
/// 原生窗口操作由调用方提供的 <c>restore</c> 完成；投递边界由 <c>post</c> 完成（生产为 WinForms BeginInvoke）。
/// 迟到的已排队回调只读排队前保存的令牌，不得回读已释放的 CancellationTokenSource.Token。
/// </summary>
internal static class ChildConnectionWindowRestore
{
    /// <summary>单次还原等待上限（唯一预算定义；调用方只按请求截止再收紧）。</summary>
    internal static readonly TimeSpan Budget = TimeSpan.FromSeconds(2);

    internal static async Task<bool> RunAsync(Action<Action> post, Func<CancellationToken, bool> restore,
        CancellationToken ct)
    {
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(Budget);
        // 排队前取值：方法超时/取消并释放 budget 后，迟到回调若再读 budget.Token 会抛 ObjectDisposedException。
        var token = budget.Token;
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try { post(() => Complete(completion, restore, token)); }
        catch (InvalidOperationException) { return false; }
        try { return await completion.Task.WaitAsync(token); }
        catch (OperationCanceledException)
        {
            await budget.CancelAsync();
            if (ct.IsCancellationRequested) { completion.TrySetCanceled(ct); throw; }
            // 放弃等待即结束任务：迟到回调的结果/异常不得再塞进无人观察的任务。
            completion.TrySetResult(false);
            return false;
        }
    }

    /// <summary>
    /// STA 侧回调的唯一出口：异常必须进入 Task/请求失败通道，不能逃到 WinForms ThreadException 默认对话框。
    /// 等待方已放弃时无人观察任务异常，改为留 stderr 诊断，既不静默也不弹窗。
    /// </summary>
    private static void Complete(TaskCompletionSource<bool> completion, Func<CancellationToken, bool> restore,
        CancellationToken token)
    {
        try
        {
            if (token.IsCancellationRequested) { completion.TrySetResult(false); return; }
            completion.TrySetResult(restore(token));
        }
        catch (Exception ex)
        {
            if (!completion.TrySetException(ex))
                Console.Error.WriteLine($"连接窗口还原回调失败（等待已放弃）：{ex.GetType().Name}：{ex.Message}");
        }
    }
}
