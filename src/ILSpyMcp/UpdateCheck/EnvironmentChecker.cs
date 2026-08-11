using ICSharpCode.Decompiler.CSharp;
using ILSpyMcp.Configuration;

namespace ILSpyMcp.UpdateCheck;

/// <summary>
/// 环境自检报告组装：内置反编译引擎版本（进程内 ICSharpCode.Decompiler，无需外部安装）+ 当前 ilspymcp 是否有新版本。 报告由 <see
/// cref="ILSpyMcp.Services.AppServices.StatusReport"/> 会话内缓存，仅首次真实执行；NuGet 段同步读磁盘缓存，无有效检查记录时留白。
/// 依赖以参数传入（更新检查器），不反向引用 Services 层。
/// </summary>
internal static class EnvironmentChecker
{
    /// <summary>
    /// 组装环境自检报告（首次调用时执行，结果由 <see cref="ILSpyMcp.Services.AppServices.StatusReport"/> 缓存）。
    /// </summary>
    /// <param name="updater">NuGet 新版本检查器（NuGet 段经其同步读磁盘缓存）。</param>
    /// <returns>中文环境自检报告文本。</returns>
    public static Task<string> BuildReportAsync(UpdateChecker updater)
    {
        var lines = new List<string>();
        // 进程内反编译引擎恒就绪（无需外部安装），环境状态恒为就绪
        lines.Add("环境状态: 就绪");
        var engineVersion = typeof(CSharpDecompiler).Assembly.GetName().Version;
        lines.Add($"反编译引擎: 内置 ICSharpCode.Decompiler {engineVersion?.ToString(3) ?? "未知"}（进程内反编译，无需外部安装）");

        // NuGet 新版本检查：同步读磁盘缓存（零网络），无有效检查记录时该段留白；网络刷新由握手后台 RefreshIfStaleAsync 承担
        var nugetLine = updater.GetCachedNuGetLine();
        if (nugetLine is not null) lines.Add(nugetLine);

        return Task.FromResult(string.Join(Environment.NewLine, lines));
    }
}
