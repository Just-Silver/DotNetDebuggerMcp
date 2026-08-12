namespace ILSpyMcp.UpdateCheck;

/// <summary>
/// 环境自检报告组装：报告当前 ilspymcp 是否有新版本（NuGet 更新状态）。 报告由 <see cref="ILSpyMcp.Services.AppServices.StatusReport"/>
/// 会话内缓存，仅首次真实执行；同步读磁盘缓存，无有效检查记录时返回空报告（握手不注入）。 依赖以参数传入（更新检查器），不反向引用 Services 层。
/// </summary>
internal static class EnvironmentChecker
{
    /// <summary>
    /// 组装环境自检报告（首次调用时执行，结果由 <see cref="ILSpyMcp.Services.AppServices.StatusReport"/> 缓存）。
    /// </summary>
    /// <param name="updater">NuGet 新版本检查器（经其同步读磁盘缓存）。</param>
    /// <returns>中文环境自检报告文本；无有效检查记录时为空字符串。</returns>
    public static Task<string> BuildReportAsync(UpdateChecker updater)
    {
        // NuGet 新版本检查：同步读磁盘缓存（零网络），无有效检查记录时返回空报告；网络刷新由握手后台 RefreshIfStaleAsync 承担
        var nugetLine = updater.GetCachedNuGetLine();
        return Task.FromResult(nugetLine ?? "");
    }
}
