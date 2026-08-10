using ILSpyMcp.Infrastructure;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace ILSpyMcp.Tools;

/// <summary>
/// 环境自检工具：报告 ilspycmd 是否安装、版本是否满足要求（>= 11.0，-m 单成员反编译所需）、当前 ilspymcp 是否有新版本。
/// 检查结果会话内缓存（环境变化需重启 CLI 才生效，重复检查无意义），首次调用执行完整检查，后续直接返回缓存。
/// </summary>
[McpServerToolType]
public static class CheckTool
{
    /// <summary>
    /// 检查运行环境是否可用。结果会话内缓存，仅首次真实检查（安装/版本变化需重启 CLI 进程才生效）。
    /// </summary>
    [McpServerTool]
    [Description("检查运行环境是否可用，不可用时返回具体缺口与修复建议。")]
    public static async Task<string> CheckStatus()
    {
        return await AppServices.StatusReport.Value;
    }
}
