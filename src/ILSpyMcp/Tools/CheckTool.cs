using ILSpyMcp.Infrastructure;
using ModelContextProtocol.Server;

using System.ComponentModel;
using System.Reflection;

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

    /// <summary>
    /// 组装环境自检报告（首次调用时执行，结果由 <see cref="AppServices.StatusReport"/> 缓存）。
    /// </summary>
    /// <returns>中文环境自检报告文本。</returns>
    internal static async Task<string> BuildReportAsync()
    {
        var lines = new List<string>();
        var installed = await AppServices.Installer.CheckInstalledAsync();
        var version = installed ? AppServices.Installer.Version : null;
        var required = AppConfig.RequiredIlspyCmdVersion;
        var ready = installed && version is not null && version >= required;

        lines.Add(ready ? "环境状态: 就绪" : "环境状态: 存在缺口");

        if (!installed)
        {
            lines.Add("ilspycmd: 未安装。请执行 `dotnet tool install --global ilspycmd` 安装后重试（安装属于高风险操作，需用户手动确认执行）。");
            lines.Add($"成员反编译（-m）: 不可用（ilspycmd 未安装）。");
        }
        else if (version is null)
        {
            lines.Add("ilspycmd: 已安装，但版本解析失败，无法确认是否满足要求。");
            lines.Add($"成员反编译（-m）: 无法确认（需 >= {required}）。");
        }
        else
        {
            lines.Add($"ilspycmd: 已安装（版本 {version}）。");
            lines.Add(version >= required
                ? $"成员反编译（-m）: 可用（{version} >= {required}）。"
                : $"成员反编译（-m）: 不可用（{version} < {required}）。请执行 `dotnet tool update --global ilspycmd` 升级。");
        }

        // NuGet 新版本检查：网络失败/超时静默跳过该检查项，不影响反编译等核心功能
        var latest = await AppServices.NuGet.GetLatestStableVersionAsync(AppConfig.NuGetPackageId);
        if (latest is not null)
        {
            var current = Assembly.GetExecutingAssembly().GetName().Version;
            var currentText = current?.ToString(3) ?? "未知";
            var hasNewer = current is not null
                && Version.TryParse(latest, out var latestVer)
                && latestVer > current;
            lines.Add(hasNewer
                ? $"ilspymcp: 当前 {currentText}，NuGet 最新 {latest}。可执行 `dotnet tool update --global ilspymcp` 升级。"
                : $"ilspymcp: 当前 {currentText}，已是最新版本。");
        }

        return string.Join('\n', lines);
    }
}
