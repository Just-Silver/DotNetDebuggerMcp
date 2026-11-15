using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SharpSight.ChildSession.Security;

/// <summary>Windows 凭据管理器（advapi32 Cred*）的最小封装；只管读写目标，不决定使用哪个目标。</summary>
internal static class WindowsCredentialVault
{
    internal const uint Generic = 1;
    internal const uint DomainPassword = 2;

    private const uint PersistLocalMachine = 2;
    private const int ErrorNotFound = 1168;
    private const int ErrorInvalidParameter = 87;
    private const uint MaximumBlobBytes = 5 * 512;

    internal static (string UserName, string Password)? Read(string target, uint type)
    {
        if (!CredReadW(target, type, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (IsMissing(error, type)) return null;
            throw new Win32Exception(error, $"读取 Windows 凭据管理器失败（Win32={error}）。");
        }
        try
        {
            var value = Marshal.PtrToStructure<Credential>(pointer);
            if (value.CredentialBlobSize is 0 or > MaximumBlobBytes) return null;
            var bytes = new byte[value.CredentialBlobSize];
            Marshal.Copy(value.CredentialBlob, bytes, 0, bytes.Length);
            try
            {
                var password = Encoding.Unicode.GetString(bytes).TrimEnd('\0');
                return (value.UserName ?? "", password);
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        finally { CredFree(pointer); }
    }

    internal static bool Exists(string target, uint type)
    {
        if (!CredReadW(target, type, 0, out var pointer))
        {
            var error = Marshal.GetLastWin32Error();
            if (IsMissing(error, type)) return false;
            throw new Win32Exception(error, $"读取 Windows 凭据管理器失败（Win32={error}）。");
        }
        CredFree(pointer);
        return true;
    }

    internal static void Write(string target, uint type, string userName, string password, string comment)
    {
        var blob = Encoding.Unicode.GetBytes(password);
        if (blob.Length == 0 || blob.Length > MaximumBlobBytes - 2)
            throw new InvalidOperationException("密码长度超出 Windows 凭据库容量。");
        var handle = GCHandle.Alloc(blob, GCHandleType.Pinned);
        try
        {
            var value = new Credential
            {
                Type = type,
                TargetName = target,
                Comment = comment,
                CredentialBlobSize = (uint)blob.Length,
                CredentialBlob = handle.AddrOfPinnedObject(),
                Persist = PersistLocalMachine,
                UserName = userName,
            };
            if (!CredWriteW(ref value, 0))
                throw new Win32Exception(Marshal.GetLastWin32Error(), "写入 Windows 凭据管理器失败。");
        }
        finally
        {
            handle.Free();
            CryptographicOperations.ZeroMemory(blob);
        }
    }

    internal static bool Delete(string target, uint type)
    {
        if (CredDeleteW(target, type, 0)) return true;
        var error = Marshal.GetLastWin32Error();
        if (error == 0 || IsMissing(error, type)) return false;
        throw new Win32Exception(error, $"删除 Windows 凭据管理器中的凭据失败（Win32={error}）。");
    }

    /// <summary>
    /// CRED_TYPE_DOMAIN_PASSWORD 请求不存在的目标时返回 ERROR_INVALID_PARAMETER 而不是 ERROR_NOT_FOUND
    /// （本机实测）；两者都只代表「没有这个凭据」，不能因此让 probe/status 报错。
    /// </summary>
    private static bool IsMissing(int error, uint type)
        => error == ErrorNotFound || type == DomainPassword && error == ErrorInvalidParameter;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct Credential
    {
        public uint Flags;
        public uint Type;
        public string TargetName;
        public string Comment;
        public System.Runtime.InteropServices.ComTypes.FILETIME LastWritten;
        public uint CredentialBlobSize;
        public nint CredentialBlob;
        public uint Persist;
        public uint AttributeCount;
        public nint Attributes;
        public string TargetAlias;
        public string UserName;
    }

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredReadW(string target, uint type, uint flags, out nint credential);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredWriteW(ref Credential credential, uint flags);

    [DllImport("advapi32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern bool CredDeleteW(string target, uint type, uint flags);

    [DllImport("advapi32.dll")]
    private static extern void CredFree(nint buffer);
}
