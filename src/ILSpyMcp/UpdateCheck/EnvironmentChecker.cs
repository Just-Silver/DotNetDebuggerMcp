namespace ILSpyMcp.UpdateCheck;

/// <summary>
/// 环境自检状态组装：报告当前 ilspymcp 是否有新版本（NuGet 更新状态）。 状态由 <see cref="ILSpyMcp.Services.AppServices.StatusReport"/>
/// 会话内缓存，仅首次真实执行；同步读磁盘缓存，无有效检查记录时状态为 null（握手不注入）。 依赖以参数传入（更新检查器），不反向引用 Services 层。
/// </summary>
internal static class EnvironmentChecker
{
    /// <summary>
    /// 组装环境自检状态（首次调用时执行，结果由 <see cref="ILSpyMcp.Services.AppServices.StatusReport"/> 缓存）。
    /// </summary>
    /// <param name="updater">NuGet 新版本检查器（经其同步读磁盘缓存）。</param>
    /// <returns>NuGet 更新状态；无有效检查记录时为 null。</returns>
    public static Task<UpdateChecker.NuGetUpdateStatus?> BuildStatusAsync(UpdateChecker updater)
    {
        // NuGet 新版本检查：同步读磁盘缓存（零网络），无有效检查记录时返回 null；网络刷新由握手后台 RefreshIfStaleAsync 承担
        return Task.FromResult(updater.GetCachedNuGetStatus());
    }

    /// <summary>
    /// 组装 MCP 握手注入文本：有新版本时在报告前附加明确指令，要求 agent 在会话开始的回复中主动告知用户并提供升级命令；
    /// 已是最新时仅注入状态行（背景信息，不打扰用户）；无有效检查记录返回空字符串（不注入）。
    /// </summary>
    /// <param name="status">NuGet 更新状态（由 <see cref="BuildStatusAsync"/> 得到）。</param>
    /// <returns>注入 ServerInstructions 的提示文本；无有效检查记录时为空字符串。</returns>
    public static string BuildHandshakeText(UpdateChecker.NuGetUpdateStatus? status)
    {
        if (status is null) return "";
        return status.HasNewVersion
            ? $"{HandshakeNoticePrefix}{status.Line}"
            : status.Line;
    }

    /// <summary>
    /// 握手注入中新版本分支的指令前缀：显式要求 agent 主动转述，ServerInstructions 是背景上下文，陈述句不会让 agent 主动告知用户。
    /// </summary>
    private const string HandshakeNoticePrefix = "【更新提示】ilspymcp 有新版本，请在会话开始的第一条回复中主动告知用户（不要等用户询问），转述以下内容：";
}
