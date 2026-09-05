using System.Diagnostics;
using System.Runtime.InteropServices;
using ClrDebug;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// P2 spike：验证 ClrDebug 0.4.2 + Microsoft.Diagnostics.DbgShim.win-x64 在 net10 能 attach 一个 .NET 子进程、
/// 收到 CreateProcess/ExitProcess 回调。走通即证明引擎主通道可行（spec §7 P2 Task 3）。
/// 需要 MTA 线程（ClrDebug 要求，research/06 A.1）。
/// </summary>
public sealed class SpikeAttachTests
{
    [Fact]
    public async Task Attach_DebugTarget_收到CreateProcess与ExitProcess()
    {
        var targetExe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(targetExe), $"DebugTarget.exe 不存在，请先运行 generate-testdata.ps1：{targetExe}");

        // 先启动目标进程：3 次迭代 + 5s 启动延迟（提供 attach 窗口，随后自然退出验证 ExitProcess）
        var psi = new ProcessStartInfo(targetExe, "3 5")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
        };
        using var target = Process.Start(psi)!;
        // 等待目标进入 Main 稳定区（避免 attach 太早，CLR 未加载）
        await Task.Delay(800);
        Assert.False(target.HasExited, "DebugTarget 提前退出");

        var sawCreate = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var sawExit = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        // 调试器工作必须在 MTA 线程（ClrDebug 硬性要求）
        Exception? mtaError = null;
        var mtaThread = new Thread(() =>
        {
            try
            {
                // 1. 加载 dbgshim：从测试输出目录找随包复制的 runtimes/win-x64/native/dbgshim.dll
                var dbgShimDll = FindDbgShimPath();
                var hModule = NativeLibrary.Load(dbgShimDll);
                var dbgshim = new DbgShim(hModule);

                // 2. attach 既有进程：拿 CLR 版本串 → 建 ICorDebug
                var clrs = dbgshim.EnumerateCLRs(target.Id);
                Assert.True(clrs.Items.Length > 0, "目标进程未加载 CLR（可能启动太早或非 .NET 进程）");
                var clr = clrs.Items[0];
                var version = dbgshim.CreateVersionStringFromModule(target.Id, clr.Path);
                var corDebug = dbgshim.CreateDebuggingInterfaceFromVersionEx(
                    CorDebugInterfaceVersion.CorDebugVersion_4_0, version);

                // 3. 回调 → 事件泵
                var cb = new CorDebugManagedCallback();
                cb.OnCreateProcess += (_, _) => sawCreate.TrySetResult(true);
                cb.OnExitProcess += (_, _) => sawExit.TrySetResult(true);
                // 其它事件一律 Continue（否则进程停死）；ExitProcess 后不 Continue
                cb.OnAnyEvent += (_, e) =>
                {
                    if (!sawExit.Task.IsCompleted)
                    {
                        try { e.Controller.Continue(false); }
                        catch { /* 进程已退出 */ }
                    }
                };

                corDebug.Initialize();
                corDebug.SetManagedHandler(cb);
                corDebug.DebugActiveProcess(target.Id, win32Attach: false);
                dbgshim.CloseCLREnumeration(clrs);
            }
            catch (Exception ex)
            {
                mtaError = ex;
                sawCreate.TrySetException(ex);
                sawExit.TrySetException(ex);
            }
        });
        mtaThread.SetApartmentState(ApartmentState.MTA);
        mtaThread.Start();

        // 等待 CreateProcess 回调 + 目标自然退出（n=2 约 20ms 后 done → exit）
        var create = await sawCreate.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(create, mtaError is null ? "未收到 CreateProcess 回调" : $"MTA 线程异常: {mtaError}");
        target.WaitForExit(10000);
        var exit = await sawExit.Task.WaitAsync(TimeSpan.FromSeconds(20));
        Assert.True(exit, "未收到 ExitProcess 回调");
        Assert.True(target.HasExited);
        mtaThread.Join(2000);
    }

    /// <summary>
    /// 在测试输出目录找随 Microsoft.Diagnostics.DbgShim.win-x64 包复制的 dbgshim.dll
    /// （RID 子包结构 runtimes/win-x64/native/dbgshim.dll）。
    /// </summary>
    private static string FindDbgShimPath()
    {
        var baseDir = AppContext.BaseDirectory;
        var candidates = new[]
        {
            Path.Combine(baseDir, "runtimes", "win-x64", "native", "dbgshim.dll"),
            Path.Combine(baseDir, "dbgshim.dll"),
        };
        foreach (var c in candidates) if (File.Exists(c)) return c;
        throw new FileNotFoundException(
            $"未找到 dbgshim.dll（搜索 {string.Join(", ", candidates)}）；请确认 Microsoft.Diagnostics.DbgShim.win-x64 包已随测试复制");
    }
}
