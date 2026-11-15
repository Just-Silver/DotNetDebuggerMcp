using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Text;

namespace SharpSight.ChildSession.Security;

/// <summary>
/// 首次配置用的 Windows 安全凭据窗口（credui）。只接受密码输入，不改变系统登录方式或安全策略。
/// 同一进程同一时刻只允许一个窗口，避免重复调用叠窗口。
/// </summary>
internal static class ChildCredentialPrompt
{
    private const uint Flags = 0x00040008; // GENERIC_CREDENTIALS | EXCLUDE_CERTIFICATES
    private const uint Cancelled = 1223;
    private const uint ErrorNoSuchLogonSession = 1312;
    private const int UserNameCapacity = 514;
    private const int PasswordCapacity = 512;

    private static int _open;

    internal static bool IsOpen => Volatile.Read(ref _open) == 1;

    internal static ChildCredentialPromptResult AskAndSave(nint owner, string suggestedUserName)
    {
        if (Interlocked.CompareExchange(ref _open, 1, 0) != 0)
            throw new InvalidOperationException("凭据窗口已打开，请先在桌面完成输入。");
        try
        {
            var credential = Ask(owner, suggestedUserName);
            if (credential is null) return new(null, false, "已取消凭据输入，未保存；后续仍使用系统连接窗口认证。");
            try
            {
                ChildCredentialStore.Save(credential);
                return new(credential, true, "凭据已保存到当前用户的 Windows 凭据管理器，后续连接不再询问。");
            }
            catch (Exception ex) when (ex is Win32Exception or InvalidOperationException)
            {
                return new(credential, false, $"凭据可用于本次连接，但未能保存：{ex.Message}");
            }
        }
        finally { Interlocked.Exchange(ref _open, 0); }
    }

    /// <summary>凭据窗口会阻塞到用户完成或取消，因此单独占用一个 STA 线程；调用方可用自己的截止放弃等待。</summary>
    internal static Task<ChildCredentialPromptResult> AskAndSaveOnStaThreadAsync(nint owner, string suggestedUserName)
    {
        if (IsOpen) return Task.FromException<ChildCredentialPromptResult>(
            new InvalidOperationException("凭据窗口已打开，请先在桌面完成输入。"));
        var completion = new TaskCompletionSource<ChildCredentialPromptResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { completion.TrySetResult(AskAndSave(owner, suggestedUserName)); }
            catch (Exception ex) { completion.TrySetException(ex); }
        }) { IsBackground = true, Name = "Child-Credential-STA" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task;
    }

    private static ChildCredential? Ask(nint owner, string suggestedUserName)
    {
        var info = new CredentialUiInfo
        {
            Size = Marshal.SizeOf<CredentialUiInfo>(),
            Owner = owner,
            Message = "输入一次 Windows 账户密码，用于自动登录本机子会话。用户名可写 计算机名\\用户、域名\\用户 或 MicrosoftAccount\\邮箱。保存后由 Windows 凭据管理器（DPAPI）保护，后续连接不再询问。",
            Caption = "DotNetDebuggerMcp 子会话",
        };
        var userName = new StringBuilder(string.IsNullOrWhiteSpace(suggestedUserName) ? "" : suggestedUserName,
            UserNameCapacity);
        var password = new StringBuilder(PasswordCapacity);
        var save = false;
        var result = CredUIPromptForCredentialsW(ref info, ChildCredentialStore.Target, 0, 0,
            userName, UserNameCapacity, password, PasswordCapacity, ref save, Flags);
        try
        {
            if (result == Cancelled) return null;
            if (result == ErrorNoSuchLogonSession)
                throw new InvalidOperationException("系统没有可用的登录会话，无法显示凭据窗口。");
            if (result != 0)
                throw new Win32Exception((int)result, "凭据输入窗口失败。");
            return ChildCredential.TryCreate(userName.ToString(), password.ToString(), "用户输入");
        }
        finally
        {
            // 尽力清理进程内明文缓冲；托管字符串仍由 CLR 管理，不能承诺零残留。
            for (var i = 0; i < password.Length; i++) password[i] = '\0';
            password.Clear();
        }
    }

    internal sealed record ChildCredentialPromptResult(ChildCredential? Credential, bool Saved, string Message);

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct CredentialUiInfo
    {
        public int Size;
        public nint Owner;
        [MarshalAs(UnmanagedType.LPWStr)] public string Message;
        [MarshalAs(UnmanagedType.LPWStr)] public string Caption;
        public nint Banner;
    }

    [DllImport("credui.dll", CharSet = CharSet.Unicode)]
    private static extern uint CredUIPromptForCredentialsW(ref CredentialUiInfo info, string targetName,
        nint context, uint authenticationError, StringBuilder userName, int userNameSize,
        StringBuilder password, int passwordSize, [MarshalAs(UnmanagedType.Bool)] ref bool save, uint flags);
}
