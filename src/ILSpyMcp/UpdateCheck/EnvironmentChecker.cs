using ILSpyMcp.Configuration;
using ILSpyMcp.Processes;

namespace ILSpyMcp.UpdateCheck;

/// <summary>
/// 环境自检报告组装：ilspycmd 是否安装/版本是否满足要求（&gt;= 11，-m 单成员反编译所需）、当前 ilspymcp 是否有新版本。 报告由 <see
/// cref="ILSpyMcp.Services.AppServices.StatusReport"/> 会话内缓存，仅首次真实执行；NuGet 段同步读磁盘缓存，无有效检查记录时留白。
/// 依赖以参数传入（安装检测器 + 更新检查器），不反向引用 Services 层。
/// </summary>
internal static class EnvironmentChecker
{
    /// <summary>
    /// 组装环境自检报告（首次调用时执行，结果由 <see cref="ILSpyMcp.Services.AppServices.StatusReport"/> 缓存）。
    /// </summary>
    /// <param name="installer">ilspycmd 安装检测器。</param>
    /// <param name="updater">NuGet 新版本检查器（NuGet 段经其同步读磁盘缓存）。</param>
    /// <returns>中文环境自检报告文本。</returns>
    public static async Task<string> BuildReportAsync(InstallChecker installer, UpdateChecker updater)
    {
        var lines = new List<string>();
        var installed = await installer.CheckInstalledAsync();
        var version = installed ? installer.Version : null;
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

        // NuGet 新版本检查：同步读磁盘缓存（零网络），无有效检查记录时该段留白；网络刷新由握手后台 RefreshIfStaleAsync 承担
        var nugetLine = updater.GetCachedNuGetLine();
        if (nugetLine is not null) lines.Add(nugetLine);

        return string.Join(Environment.NewLine, lines);
    }
}
