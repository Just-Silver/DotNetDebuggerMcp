namespace SharpSight.ChildSession.Security;

/// <summary>
/// 自动登录子会话用的凭据。密码只驻留内存与当前用户的凭据库，不写入实例记录、命令行或日志。
/// </summary>
internal sealed record ChildCredential(string UserName, string Domain, string Password, string Source)
{
    internal static ChildCredential? TryCreate(string rawUserName, string password, string source)
    {
        var (userName, domain) = Split(rawUserName);
        return userName.Length == 0 || password.Length == 0
            ? null
            : new ChildCredential(userName, domain, password, source);
    }

    /// <summary>RDP 控件把域与用户名分开设置；UPN 保持整体作为用户名，不能再按 @ 拆域。</summary>
    internal static (string UserName, string Domain) Split(string rawUserName)
    {
        var value = rawUserName.Trim();
        if (value.Length == 0) return ("", "");
        var slash = value.LastIndexOf('\\');
        if (slash > 0 && slash < value.Length - 1)
            return (value[(slash + 1)..], value[..slash]);
        return (value, "");
    }
}
