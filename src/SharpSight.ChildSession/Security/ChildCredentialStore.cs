namespace SharpSight.ChildSession.Security;

/// <summary>
/// 子会话自动登录凭据的存取策略：优先本产品保存的凭据，其次复用 Windows 自己保存的 loopback RDP 凭据。
/// 不遍历、不使用指向其他主机的 TERMSRV 凭据，也不把密码写入实例记录或日志。
/// </summary>
internal static class ChildCredentialStore
{
    internal const string Target = "DotNetDebuggerMcp/child-session";

    private static readonly string[] RemoteDesktopTargets = ["TERMSRV/localhost", "TERMSRV/127.0.0.1"];

    internal static ChildCredential? Load()
    {
        if (Read(Target, "已保存的子会话凭据") is { } saved) return saved;
        foreach (var target in RemoteDesktopTargets)
            if (Read(target, "RDP 已保存凭据") is { } fallback) return fallback;
        return null;
    }

    /// <summary>是否存在可用的自动登录凭据（用户名与密码均非空）；不向调用方返回密码。</summary>
    internal static bool HasSavedCredential() => Load() is not null;

    internal static void Save(ChildCredential credential)
    {
        ArgumentNullException.ThrowIfNull(credential);
        var userName = credential.Domain.Length == 0
            ? credential.UserName
            : credential.Domain + "\\" + credential.UserName;
        WindowsCredentialVault.Write(Target, WindowsCredentialVault.Generic, userName, credential.Password,
            "DotNetDebuggerMcp 子会话自动登录（由 Windows 凭据管理器保护）");
    }

    /// <summary>返回是否真的删除了本产品凭据；用户自行保存的 TERMSRV 凭据不动。</summary>
    internal static bool Delete()
        => WindowsCredentialVault.Delete(Target, WindowsCredentialVault.Generic) |
           WindowsCredentialVault.Delete(Target, WindowsCredentialVault.DomainPassword);

    private static ChildCredential? Read(string target, string source)
    {
        var value = WindowsCredentialVault.Read(target, WindowsCredentialVault.Generic) ??
                    WindowsCredentialVault.Read(target, WindowsCredentialVault.DomainPassword);
        return value is null ? null : ChildCredential.TryCreate(value.Value.UserName, value.Value.Password, source);
    }
}
