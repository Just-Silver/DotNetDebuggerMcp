using System.Diagnostics.CodeAnalysis;
using System.Globalization;

namespace ILSpyMcp.Validation;

/// <summary>
/// 工具参数共享校验：全部返回 bool + out error，失败时返回中文提示文本。
/// </summary>
public static class ArgumentValidators
{
    /// <summary>
    /// 校验 assembly 参数：必填且文件必须存在。
    /// </summary>
    /// <param name="assembly">程序集路径，缺省为空字符串。</param>
    /// <param name="error">校验失败时的错误提示；通过时为 null。</param>
    /// <returns>通过返回 true；失败返回 false 且 <paramref name="error"/> 非空。</returns>
    public static bool ValidateAssembly(string assembly, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrEmpty(assembly))
        {
            error = "请指定 assembly 参数（程序集路径，.dll 或 .exe）。";
            return false;
        }
        if (HasInvalidPathChars(assembly))
        {
            error = $"程序集路径非法：{assembly}";
            return false;
        }

        try
        {
            var fullpath = Path.GetFullPath(assembly);
            if (Directory.Exists(fullpath))
            {
                error = $"程序集路径是一个目录而非文件：{fullpath}";
                return false;
            }
            if (!File.Exists(fullpath))
            {
                error = $"程序集文件不存在：{fullpath}";
                return false;
            }
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException or IOException or UnauthorizedAccessException)
        {
            error = $"程序集路径非法：{assembly}";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 校验必填字符串参数。
    /// </summary>
    /// <param name="value">参数值，缺省为空字符串。</param>
    /// <param name="hint">参数为空时返回的错误提示。</param>
    /// <param name="error">校验失败时的错误提示；通过时为 null。</param>
    /// <returns>通过返回 true；失败返回 false 且 <paramref name="error"/> 非空。</returns>
    public static bool ValidateRequired(string value, string hint, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            error = hint;
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 校验 decompile_member 的搜索参数：memberName 必填；typeName 允许为空（省略时跨程序集按成员名搜索）。
    /// </summary>
    /// <param name="typeName">全限定类型名，可为空（跨程序集搜索）。</param>
    /// <param name="memberName">成员名子串，缺省为空字符串。</param>
    /// <param name="error">校验失败时的错误提示；通过时为 null。</param>
    /// <returns>通过返回 true；失败返回 false 且 <paramref name="error"/> 非空。</returns>
    public static bool ValidateMemberNameSearch(string typeName, string memberName, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(memberName))
        {
            error = "请指定 memberName 参数（成员名子串，忽略大小写；省略 typeName 时跨程序集搜索）。";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 校验 outputDir 参数：必填；路径非法或已存在同名文件时返回错误提示；目录不存在允许（写盘时会自动创建）。
    /// </summary>
    /// <param name="outputDir">输出目录，缺省为空字符串。</param>
    /// <param name="error">校验失败时的错误提示；通过时为 null。</param>
    /// <returns>通过返回 true；失败返回 false 且 <paramref name="error"/> 非空。</returns>
    public static bool ValidateOutputDir(string outputDir, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(outputDir))
        {
            error = "请指定 outputDir 参数（输出目录）。";
            return false;
        }
        if (HasInvalidPathChars(outputDir))
        {
            error = $"输出目录路径非法：{outputDir}";
            return false;
        }
        string fullpath;
        try
        {
            fullpath = Path.GetFullPath(outputDir, Environment.CurrentDirectory);
        }
        catch (Exception ex) when (ex is ArgumentException or PathTooLongException or NotSupportedException)
        {
            error = $"输出目录路径非法：{outputDir}";
            return false;
        }
        if (File.Exists(fullpath))
        {
            error = $"outputDir 已存在同名文件：{fullpath}";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 校验 timeoutSeconds 参数：必须为正整数（不允许永不超时）。
    /// </summary>
    /// <param name="value">超时秒数，缺省为默认值。</param>
    /// <param name="error">校验失败时的错误提示；通过时为 null。</param>
    /// <returns>通过返回 true；失败返回 false 且 <paramref name="error"/> 非空。</returns>
    public static bool ValidateTimeoutSeconds(int value, [NotNullWhen(false)] out string? error)
    {
        if (value < 1)
        {
            error = $"timeoutSeconds 必须为正整数（当前为 {value}），不允许永不超时。";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 校验 list 参数：必填且只能由 c/i/s/d/e 组成（可组合多个字母）。
    /// </summary>
    /// <param name="list">实体类型类别组合，缺省为空字符串。</param>
    /// <param name="error">校验失败时的错误提示；通过时为 null。</param>
    /// <returns>通过返回 true；失败返回 false 且 <paramref name="error"/> 非空。</returns>
    public static bool ValidateList(string list, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrEmpty(list))
        {
            error = "请指定 list 参数（实体类型类别：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 \"csi\"）。";
            return false;
        }
        if (list.Any(c => c is not ('c' or 'i' or 's' or 'd' or 'e')))
        {
            error = $"无效的 list 参数：\"{list}\"。合法值为 c/i/s/d/e 的组合，例如 \"csi\"。";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 校验 decompile_member 的 token 参数：0x 开头的十六进制（如 0x06000005）。行号是否越界由反编译引擎兜底校验。
    /// </summary>
    /// <param name="token">元数据 token，缺省为空字符串。</param>
    /// <param name="error">校验失败时的错误提示；通过时为 null。</param>
    /// <returns>通过返回 true；失败返回 false 且 <paramref name="error"/> 非空。</returns>
    public static bool ValidateToken(string token, [NotNullWhen(false)] out string? error)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            error = "请指定 token 参数（元数据 token，0x 开头的十六进制，如 0x06000005）。";
            return false;
        }
        var trimmed = token.Trim();
        if (!trimmed.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
            || !int.TryParse(trimmed.AsSpan(2), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out _))
        {
            error = $"\"{trimmed}\" 不是有效的元数据 token，应为 0x 开头的十六进制格式，如 0x06000005";
            return false;
        }
        error = null;
        return true;
    }

    /// <summary>
    /// 检测路径是否含 Windows 非法字符（控制字符与 &lt; &gt; " | ? *）。 .NET 10 的 GetFullPath
    /// 已不再对这些字符抛异常，但它们在文件系统层面仍非法， 提前识别可返回明确的「路径非法」提示而非「文件不存在」。
    /// </summary>
    /// <param name="path">待检测路径。</param>
    /// <returns>含非法字符返回 true，否则 false。</returns>
    private static bool HasInvalidPathChars(string path)
    {
        foreach (var c in path)
        {
            if (c < 32 || c is '"' or '<' or '>' or '|' or '?' or '*') return true;
        }
        return false;
    }
}