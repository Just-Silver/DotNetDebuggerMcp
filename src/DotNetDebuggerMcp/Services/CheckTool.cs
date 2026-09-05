namespace DotNetDebuggerMcp.Services;

/// <summary>
/// 更新检查入口：报告当前 ilspymcp 是否有新版本。 检查结果会话内缓存（重启 CLI 才重新检查，重复检查无意义），首次调用执行完整检查，后续直接返回缓存。 非 MCP
/// 工具——握手期已把报告注入 ServerInstructions，本入口供 CLI -c/--check 调试使用。
/// </summary>
public static class CheckTool
{
    /// <summary>
    /// 检查 ilspymcp 是否有新版本。结果会话内缓存，仅首次真实检查。 输出保持为朴素状态行（CLI -c 供人阅读），指令式提示仅握手注入使用。
    /// </summary>
    public static async Task<string> CheckStatus()
    {
        var status = await AppServices.StatusReport.Value;
        return status?.Line ?? "";
    }
}