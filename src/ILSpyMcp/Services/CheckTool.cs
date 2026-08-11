namespace ILSpyMcp.Services;

/// <summary>
/// 环境自检入口：报告内置反编译引擎版本（进程内 ICSharpCode.Decompiler，无需外部安装）与当前 ilspymcp 是否有新版本。 检查结果会话内缓存（环境变化需重启
/// CLI 才生效，重复检查无意义），首次调用执行完整检查，后续直接返回缓存。 非 MCP 工具——握手期已把完整报告注入 ServerInstructions，本入口供 CLI
/// -c/--check 调试使用。
/// </summary>
public static class CheckTool
{
    /// <summary>
    /// 检查运行环境是否可用。结果会话内缓存，仅首次真实检查（引擎/版本变化需重启 CLI 进程才生效）。
    /// </summary>
    public static async Task<string> CheckStatus()
    {
        return await AppServices.StatusReport.Value;
    }
}