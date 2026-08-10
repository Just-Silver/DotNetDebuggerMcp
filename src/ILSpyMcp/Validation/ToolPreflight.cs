using ILSpyMcp.Processes;
using ILSpyMcp.Services;

namespace ILSpyMcp.Validation;

/// <summary>
/// 工具调用前置检查：安装检测 + assembly 参数校验。三个工具共用同一入口，避免前置逻辑各写一份、新工具照抄时漏步。
/// </summary>
public static class ToolPreflight
{
    /// <summary>
    /// 执行前置检查：ilspycmd 未安装或 assembly 校验失败时返回中文提示文本，通过返回 null。
    /// </summary>
    /// <param name="assembly">程序集路径。</param>
    /// <returns>未通过时返回直接交给用户的提示文本；通过为 null。</returns>
    public static async Task<string?> CheckAsync(string assembly)
    {
        if (!await AppServices.Installer.CheckInstalledAsync()) return InstallChecker.InstallHint;
        if (!ArgumentValidators.ValidateAssembly(assembly, out var argError)) return argError;
        return null;
    }
}