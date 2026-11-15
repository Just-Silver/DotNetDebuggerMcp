using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace ChildSessionTarget;

/// <summary>
/// PoC：不经计划任务把进程投送进指定会话。
/// 记录三条候选路径（父进程属性 / 复制会话内进程令牌 / 普通启动）的成败与错误码，供正式实现取舍。
/// </summary>
internal static class NativeLaunch
{
    private const uint ProcessCreateProcess = 0x0080;
    private const uint ProcessQueryLimitedInformation = 0x1000;
    private const uint TokenAssignPrimary = 0x0001;
    private const uint TokenDuplicate = 0x0002;
    private const uint TokenQuery = 0x0008;
    private const int TokenPrimary = 1;
    private const int SecurityImpersonation = 2;
    private const uint CreateUnicodeEnvironment = 0x400;
    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateNewConsole = 0x10;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint CreateBreakawayFromJob = 0x01000000;
    private static readonly nint ParentProcessAttribute = 0x00020000;
    private static readonly nint MitigationPolicyAttribute = 0x00020007;

    public static int Run(int sessionId, string executable, string[] arguments)
    {
        executable = Path.GetFullPath(executable);
        var report = new Dictionary<string, object?>
        {
            ["session"] = sessionId,
            ["executable"] = executable,
            ["sizes"] = new { startupInfo = Marshal.SizeOf<StartupInfo>(), startupInfoEx = Marshal.SizeOf<StartupInfoEx>() },
            ["elevated"] = IsElevated(),
        };
        try
        {
            var parent = FindSessionParent(sessionId);
            report["parentPid"] = parent.Pid;
            report["parentImage"] = parent.Image;
            report["parentSameUser"] = parent.SameUser;
            report["control"] = TryPlainCreate(executable, arguments);
            report["parent"] = TryParentLaunch(parent.Pid, sessionId, executable, arguments);
            report["self"] = TryParentLaunch(Environment.ProcessId, sessionId, executable, arguments);
            report["attributeProbe"] = ProbeMitigation(executable, arguments);
            report["processToken"] = TryTokenLaunch(parent.Pid, executable, arguments);
            report["wtsToken"] = TryWtsToken(sessionId);
        }
        catch (Exception ex) { report["error"] = ex.ToString(); }
        var json = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true });
        Console.WriteLine(json);
        // UAC 提权进程拿不到调用端的 stdout 重定向，同时落盘一份固定路径供父进程读取。
        try { File.WriteAllText(Path.Combine(Path.GetTempPath(), $"native-launch-{sessionId}.json"), json); }
        catch (Exception) { }
        return 0;
    }

    private static Dictionary<string, object?> TryParentLaunch(int pid, int sessionId, string executable, string[] arguments)
    {
        var handle = OpenProcess(ProcessCreateProcess | ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return new() { ["ok"] = false, ["stage"] = "open", ["error"] = Marshal.GetLastWin32Error() };
        try
        {
            using var list = AttributeList.Create(handle, out var error);
            if (list is null) return new() { ["ok"] = false, ["stage"] = "attribute", ["error"] = error };
            return CreateWithParent(list.Pointer, CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow,
                sessionId, executable, arguments);
        }
        finally { CloseHandle(handle); }
    }

    /// <summary>参考实现的另一条路径：WTSQueryUserToken 需要 LocalSystem + SeTcb，普通令牌下应当失败。</summary>
    private static Dictionary<string, object?> TryWtsToken(int sessionId)
        => WTSQueryUserToken((uint)sessionId, out var token)
            ? new() { ["ok"] = true }
            : new() { ["ok"] = false, ["error"] = Marshal.GetLastWin32Error() };

    private static Dictionary<string, object?> TryTokenLaunch(int pid, string executable, string[] arguments)
    {
        var handle = OpenProcess(ProcessQueryLimitedInformation, false, pid);
        if (handle == IntPtr.Zero) return new() { ["ok"] = false, ["stage"] = "open", ["error"] = Marshal.GetLastWin32Error() };
        try { return TryProcessToken(handle, executable, arguments); }
        finally { CloseHandle(handle); }
    }

    /// <summary>优先 explorer/userinit 这类同用户、可交互的会话内进程；winlogon/csrss 等系统进程不作为父进程。</summary>
    private static (int Pid, string Image, bool SameUser) FindSessionParent(int sessionId)
    {
        using var self = WindowsIdentity.GetCurrent();
        var currentSid = self.User?.Value;
        var best = (Pid: 0, Image: "", SameUser: false, Score: -1);
        foreach (var process in Process.GetProcesses())
        {
            try
            {
                if (process.SessionId != sessionId || process.Id == Environment.ProcessId) continue;
                var image = process.ProcessName;
                var score = Score(image);
                if (score <= 0) continue;
                var sameUser = SameUser(process, currentSid);
                if (sameUser) score += 20;
                if (score > best.Score) best = (process.Id, image, sameUser, score);
            }
            catch (Exception) { }
            finally { process.Dispose(); }
        }
        if (best.Pid == 0) throw new InvalidOperationException($"会话 {sessionId} 中没有可用的同用户进程作父进程。");
        return (best.Pid, best.Image, best.SameUser);
    }

    /// <summary>报告自身是否提权（TokenElevation），避免把「管理员组账户」误当成「提权进程」。</summary>
    private static bool IsElevated()
    {
        if (!OpenProcessToken(GetCurrentProcess(), TokenQuery, out var token)) return false;
        try
        {
            var buffer = Marshal.AllocHGlobal(4);
            try
            {
                return GetTokenInformation(token, 20, buffer, 4, out _) && Marshal.ReadInt32(buffer) != 0;
            }
            finally { Marshal.FreeHGlobal(buffer); }
        }
        finally { CloseHandle(token); }
    }

    private static int Score(string image) => image.ToLowerInvariant() switch
    {
        "explorer" => 100,
        "userinit" => 80,
        "sihost" => 60,
        "runtimebroker" => 40,
        "winlogon" or "csrss" or "smss" or "services" or "lsass" => 0,
        _ => 10,
    };

    private static bool SameUser(Process process, string? currentSid)
    {
        if (currentSid is null) return false;
        if (!OpenProcessToken(process.Handle, TokenQuery, out var token)) return false;
        try
        {
            using var identity = new WindowsIdentity(token);
            return identity.User?.Value == currentSid;
        }
        catch (Exception) { return false; }
        finally { CloseHandle(token); }
    }

    private static Dictionary<string, object?> CreateWithParent(nint list, uint flags, int sessionId,
        string executable, string[] arguments)
    {
        if (list == IntPtr.Zero) return new() { ["ok"] = false, ["stage"] = "attribute" };
        var startup = new StartupInfoEx
        {
            StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfoEx>() },
            AttributeList = list,
        };
        // lpDesktop 留空：让新进程继承父进程（目标会话）的窗口站/桌面，不写死本会话的 winsta0\default。
        var command = new StringBuilder(Quote(executable, arguments));
        var ok = CreateProcessW(executable, command, 0, 0, false, flags, 0,
            Path.GetDirectoryName(executable), ref startup, out var info);
        if (!ok) return new() { ["ok"] = false, ["error"] = Marshal.GetLastWin32Error() };
        var actual = SessionOf(info.ProcessId);
        if (actual != sessionId) TerminateProcess(info.Process, 1);
        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        return new() { ["ok"] = true, ["pid"] = info.ProcessId, ["actualSession"] = actual, ["sessionMatches"] = actual == sessionId };
    }

    private static Dictionary<string, object?> TryPlainCreate(string executable, string[] arguments)
    {
        var startup = new StartupInfoEx { StartupInfo = new StartupInfo { cb = Marshal.SizeOf<StartupInfo>() } };
        var command = new StringBuilder(Quote(executable, arguments));
        var ok = CreateProcessW(executable, command, 0, 0, false, CreateUnicodeEnvironment | CreateNoWindow,
            0, Path.GetDirectoryName(executable), ref startup, out var info);
        if (!ok) return new() { ["ok"] = false, ["error"] = Marshal.GetLastWin32Error() };
        CloseHandle(info.Thread);
        CloseHandle(info.Process);
        return new() { ["ok"] = true, ["pid"] = info.ProcessId, ["session"] = SessionOf(info.ProcessId) };
    }

    /// <summary>探针：无关属性（DEP 缓解策略）能通过，说明属性列表管路本身没问题。</summary>
    private static Dictionary<string, object?> ProbeMitigation(string executable, string[] arguments)
    {
        using var list = AttributeList.Create(MitigationPolicyAttribute, 0x1, out var error);
        return list is null
            ? new() { ["ok"] = false, ["stage"] = "attribute", ["error"] = error }
            : CreateWithParent(list.Pointer, CreateUnicodeEnvironment | ExtendedStartupInfoPresent | CreateNoWindow,
                SessionOf(Environment.ProcessId), executable, arguments);
    }

    /// <summary>对照路径：复制会话内进程令牌再 CreateProcessAsUser（本机无 SeIncreaseQuota，预期失败）。</summary>
    private static Dictionary<string, object?> TryProcessToken(nint process, string executable, string[] arguments)
    {
        if (!OpenProcessToken(process, TokenDuplicate | TokenQuery | TokenAssignPrimary, out var raw))
            return new() { ["ok"] = false, ["stage"] = "openToken", ["error"] = Marshal.GetLastWin32Error() };
        try
        {
            if (!DuplicateTokenEx(raw, 0xF01FF, 0, SecurityImpersonation, TokenPrimary, out var primary))
                return new() { ["ok"] = false, ["stage"] = "duplicate", ["error"] = Marshal.GetLastWin32Error() };
            try
            {
                CreateEnvironmentBlock(out var environment, primary, false);
                try
                {
                    var startup = new StartupInfo
                    {
                        cb = Marshal.SizeOf<StartupInfo>(),
                        Desktop = "winsta0\\default",
                        Flags = 1,
                        ShowWindow = 1,
                    };
                    var command = new StringBuilder(Quote(executable, arguments));
                    var ok = CreateProcessAsUserW(primary, null, command, 0, 0, false,
                        CreateUnicodeEnvironment | CreateNoWindow, environment,
                        Path.GetDirectoryName(executable), ref startup, out var info);
                    if (!ok) return new() { ["ok"] = false, ["stage"] = "create", ["error"] = Marshal.GetLastWin32Error() };
                    CloseHandle(info.Thread);
                    CloseHandle(info.Process);
                    return new() { ["ok"] = true, ["pid"] = info.ProcessId, ["session"] = SessionOf(info.ProcessId) };
                }
                finally { if (environment != 0) DestroyEnvironmentBlock(environment); }
            }
            finally { CloseHandle(primary); }
        }
        finally { CloseHandle(raw); }
    }

    private static int SessionOf(int processId)
        => ProcessIdToSessionId(processId, out var session) ? session : -1;

    private static string Quote(string executable, string[] arguments)
        => string.Join(' ', new[] { executable }.Concat(arguments).Select(Quote));

    private static string Quote(string value) => value.Contains(' ') || value.Contains('"')
        ? "\"" + value.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""
        : value;

    /// <summary>
    /// 属性列表与属性值必须一起存活到 CreateProcess 之后（属性值存的是指针），
    /// 所以句柄副本放在非托管内存里，不能指向托管栈上的临时变量。
    /// </summary>
    private sealed class AttributeList : IDisposable
    {
        private readonly nint _value;
        internal nint Pointer { get; }

        private AttributeList(nint pointer, nint value) { Pointer = pointer; _value = value; }

        internal static AttributeList? Create(nint attribute, nint value, out int error)
        {
            error = 0;
            nint size = 0;
            InitializeProcThreadAttributeList(0, 1, 0, ref size);
            if (size == 0) { error = Marshal.GetLastWin32Error(); return null; }
            var pointer = Marshal.AllocHGlobal(size);
            if (!InitializeProcThreadAttributeList(pointer, 1, 0, ref size))
            {
                error = Marshal.GetLastWin32Error();
                Marshal.FreeHGlobal(pointer);
                return null;
            }
            var slot = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(slot, value);
            if (!UpdateProcThreadAttribute(pointer, 0, attribute, slot, (nint)IntPtr.Size, 0, 0))
            {
                error = Marshal.GetLastWin32Error();
                DeleteProcThreadAttributeList(pointer);
                Marshal.FreeHGlobal(pointer);
                Marshal.FreeHGlobal(slot);
                return null;
            }
            return new AttributeList(pointer, slot);
        }

        internal static AttributeList? Create(nint parent, out int error) => Create(ParentProcessAttribute, parent, out error);

        public void Dispose()
        {
            DeleteProcThreadAttributeList(Pointer);
            Marshal.FreeHGlobal(Pointer);
            Marshal.FreeHGlobal(_value);
        }
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct StartupInfo
    {
        public int cb;
        public string? Reserved;
        public string? Desktop;
        public string? Title;
        public int X, Y, XSize, YSize, XChars, YChars, Fill, Flags;
        public short ShowWindow, Reserved2;
        public nint Reserved2Data, StdInput, StdOutput, StdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct StartupInfoEx
    {
        public StartupInfo StartupInfo;
        public nint AttributeList;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ProcessInformation
    {
        public nint Process, Thread;
        public int ProcessId, ThreadId;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern nint OpenProcess(uint access, [MarshalAs(UnmanagedType.Bool)] bool inherit, int processId);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool CloseHandle(nint handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool TerminateProcess(nint process, uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool ProcessIdToSessionId(int processId, out int sessionId);

    [DllImport("kernel32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessW(string application, StringBuilder command, nint processAttributes,
        nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags, nint environment,
        string? directory, ref StartupInfoEx startup, out ProcessInformation information);

    [DllImport("advapi32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    private static extern bool CreateProcessAsUserW(nint token, string? application, StringBuilder command,
        nint processAttributes, nint threadAttributes, [MarshalAs(UnmanagedType.Bool)] bool inherit, uint flags,
        nint environment, string? directory, ref StartupInfo startup, out ProcessInformation information);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool InitializeProcThreadAttributeList(nint list, int count, int flags, ref nint size);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool UpdateProcThreadAttribute(nint list, uint flags, nint attribute, nint value,
        nint size, nint previous, nint returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(nint list);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool OpenProcessToken(nint process, uint access, out nint token);

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool GetTokenInformation(nint token, int kind, nint information, int length, out int returned);

    [DllImport("kernel32.dll")]
    private static extern nint GetCurrentProcess();

    [DllImport("advapi32.dll", SetLastError = true)]
    private static extern bool DuplicateTokenEx(nint existing, uint access, nint attributes,
        int impersonationLevel, int type, out nint token);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool CreateEnvironmentBlock(out nint environment, nint token,
        [MarshalAs(UnmanagedType.Bool)] bool inherit);

    [DllImport("userenv.dll", SetLastError = true)]
    private static extern bool DestroyEnvironmentBlock(nint environment);

    [DllImport("wtsapi32.dll", SetLastError = true)]
    private static extern bool WTSQueryUserToken(uint sessionId, out nint token);
}
