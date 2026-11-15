using System.Security.Principal;
using SharpSight.ChildSession.Security;

namespace SharpSight.ChildSession.Connection;

internal sealed class ChildConnectionWindow : Form
{
    private static readonly RdpReadiness DisconnectedReadiness = new(true, false, false, null);
    private readonly RdpActiveXHost _rdp = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer _displayTimer = new() { Interval = 400 };
    private DateTimeOffset? _connectedAt;
    private int _appliedWidth;
    private int _appliedHeight;
    internal TaskCompletionSource ShownAndConnected { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    internal bool WasClosed { get; private set; }
    internal bool CredentialApplied => _rdp.CredentialApplied;
    internal string? CredentialError => _rdp.CredentialError;
    internal bool CredentialPrompted { get; private set; }
    internal string? CredentialSource { get; private set; }

    internal ChildConnectionWindow()
    {
        Text = "DotNetDebuggerMcp — 子会话（关闭会断开连接；必要时可在此认证）";
        ClientSize = new Size(1024, 768);
        Controls.Add(_rdp);
        Shown += (_, _) =>
        {
            try
            {
                _rdp.ConnectChild(LoadCredential());
                _displayTimer.Start();
                ShownAndConnected.TrySetResult();
            }
            catch (Exception ex) { ShownAndConnected.TrySetException(ex); Close(); }
        };
        _displayTimer.Tick += (_, _) => FlushDisplaySettings();
        FormClosed += (_, _) => { WasClosed = true; _displayTimer.Dispose(); };
    }

    /// <summary>
    /// 子会话桌面必须跟着客户区走，否则拖动窗口只会露出灰边。
    /// 连续 Resize 由短周期定时器合并；未连接/最小化时不发，避免过早调用拿到 E_UNEXPECTED。
    /// </summary>
    private void FlushDisplaySettings()
    {
        if (WasClosed || IsDisposed || WindowState == FormWindowState.Minimized) return;
        if (!_rdp.Connected) { _connectedAt = null; return; }
        _connectedAt ??= DateTimeOffset.UtcNow;
        var size = ClientSize;
        if (!RdpActiveXHost.ShouldUpdateDisplay(size.Width, size.Height, _appliedWidth, _appliedHeight,
                DateTimeOffset.UtcNow - _connectedAt.Value))
            return;
        if (!_rdp.ApplyDisplaySettings(size.Width, size.Height)) return;
        _appliedWidth = size.Width;
        _appliedHeight = size.Height;
    }

    /// <summary>
    /// 只有已保存凭据才在首连前注入；没有凭据时先让系统尝试自动登录，
    /// 避免在本来免密的机器上无端打断用户。凭据库错误也不能让实例直接 Faulted。
    /// </summary>
    private ChildCredential? LoadCredential()
    {
        try
        {
            var credential = ChildCredentialStore.Load();
            CredentialSource = credential?.Source;
            return credential;
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            Console.Error.WriteLine($"读取已保存的子会话凭据失败，改由系统连接窗口认证：{ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// 认证持续等待时的一次补配：在 STA 上弹出安全凭据窗口，保存成功后断开并按新凭据重连。
    /// 一个持有器只主动询问一次；取消后仍保留系统连接窗口的手工认证路径。
    /// </summary>
    internal Task<bool> PromptCredentialAsync()
    {
        if (WasClosed || IsDisposed) return Task.FromResult(false);
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() =>
            {
                try { completion.TrySetResult(PromptCredentialOnSta()); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(false); }
        return completion.Task;
    }

    private bool PromptCredentialOnSta()
    {
        if (WasClosed || IsDisposed || CredentialPrompted) return false;
        CredentialPrompted = true;
        if (!_rdp.DisconnectChild())
        {
            Console.Error.WriteLine("连接未能断开，跳过凭据补配以免同时存在两条连接；请继续在系统连接窗口认证。");
            return false;
        }
        ChildCredentialPrompt.ChildCredentialPromptResult result;
        try
        {
            result = ChildCredentialPrompt.AskAndSave(Handle, WindowsIdentity.GetCurrent().Name);
        }
        catch
        {
            // 取消/失败也要把系统连接窗恢复出来，不能把用户留在无连接状态。
            _rdp.WaitForDisconnected(TimeSpan.FromSeconds(3));
            _rdp.ConnectChild(null);
            throw;
        }
        CredentialSource = result.Credential?.Source;
        Console.Error.WriteLine(result.Message);
        _rdp.WaitForDisconnected(TimeSpan.FromSeconds(3));
        _rdp.ConnectChild(result.Credential);
        return result.Credential is not null;
    }

    protected override bool ShowWithoutActivation => true;

    internal Task<bool> IsConnectedAsync() => ReadRdpAsync(rdp => rdp.Connected, false);

    /// <summary>连接与登录共用一个往返，避免两次 BeginInvoke 之间跨过不同连接代际。</summary>
    internal Task<RdpReadiness> ReadReadinessAsync() => ReadRdpAsync(rdp => rdp.Readiness, DisconnectedReadiness);

    /// <summary>
    /// GUI 请求前确认连接窗非最小化：已最小化时在自身 STA 用 SW_SHOWNOACTIVATE 还原，不激活、不置顶、不发父侧输入。
    /// 已关窗/无句柄/未还原/超预算返回 false（未确认可用）；传入令牌已取消时抛 OperationCanceledException，
    /// 由既有调用方按取消传播而非「未确认」处理；STA 回调内的原生异常以任务异常上抛，调用方须映射为未执行提示。
    /// </summary>
    internal Task<bool> EnsureVisibleWithoutActivationAsync(CancellationToken ct)
        => ChildConnectionWindowRestore.RunAsync(callback => BeginInvoke(callback), RestoreOnSta, ct);

    private bool RestoreOnSta(CancellationToken ct)
    {
        if (ct.IsCancellationRequested || WasClosed || IsDisposed) return false;
        if (!IsHandleCreated) return false;
        var handle = Handle;
        if (!ChildConnectionWindowNative.IsMinimized(handle)) return true;
        ChildConnectionWindowNative.ShowWithoutActivation(handle);
        var restored = !ChildConnectionWindowNative.IsMinimized(handle);
        if (restored) Console.Error.WriteLine("连接窗口曾被最小化，已按不激活方式还原。");
        return restored;
    }

    private Task<T> ReadRdpAsync<T>(Func<RdpActiveXHost, T> read, T disconnected)
    {
        if (WasClosed || IsDisposed) return Task.FromResult(disconnected);
        var completion = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            BeginInvoke(() =>
            {
                try { completion.TrySetResult(read(_rdp)); }
                catch (Exception ex) { completion.TrySetException(ex); }
            });
        }
        catch (InvalidOperationException) { completion.TrySetResult(disconnected); }
        return completion.Task;
    }

    internal void RequestClose()
    {
        if (!IsDisposed && IsHandleCreated) BeginInvoke(Close);
    }
}
