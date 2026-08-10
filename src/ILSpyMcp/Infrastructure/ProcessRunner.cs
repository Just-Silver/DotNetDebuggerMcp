using System.Diagnostics;
using System.IO;
using System.Text;

namespace ILSpyMcp.Infrastructure;

/// <summary>
/// 子进程执行结果。
/// </summary>
/// <param name="Code">进程退出码；启动失败/超时为 -1。</param>
/// <param name="Stdout">标准输出内容。</param>
/// <param name="Stderr">标准错误内容（含失败提示）。</param>
public readonly record struct ProcessResult(int Code, string Stdout, string Stderr);

/// <summary>
/// 子进程执行抽象，便于测试注入 fake。
/// </summary>
public interface IProcessRunner
{
    /// <summary>
    /// 执行子进程并等待结束；失败返回退出码 -1 附提示，不抛异常。
    /// </summary>
    /// <param name="executable">可执行文件名（或完整路径）。</param>
    /// <param name="args">传递给可执行文件的参数。</param>
    /// <param name="cwd">进程工作目录。</param>
    /// <param name="timeout">可选超时；超时终止进程树并返回 -1。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>进程执行结果。</returns>
    Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string cwd, TimeSpan? timeout = null, CancellationToken cancellationToken = default);
}

/// <summary>
/// 通用子进程执行器：重定向 stdout/stderr，可配置超时（超时终止整个进程树）。 启动失败/超时返回退出码 -1 并附提示文本，不抛异常。
/// </summary>
public sealed class ProcessRunner : IProcessRunner
{
    /// <inheritdoc/>
    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string cwd, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        var psi = new ProcessStartInfo(executable)
        {
            WorkingDirectory = cwd,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (var arg in args) psi.ArgumentList.Add(arg);

        using var proc = new Process { StartInfo = psi };
        try
        {
            proc.Start();
        }
        catch (Exception ex) when (ex is System.ComponentModel.Win32Exception or InvalidOperationException)
        {
            return new ProcessResult(-1, "", $"无法启动进程 {executable}：{ex.Message}");
        }

        // stdout 流式读取并设字节上限：超过 MaxOutputBytes 即丢弃后续输出但仍 drain 至 EOF，
        // 避免子进程因管道阻塞卡死；主流程判定 OverCap 后返回错误提示而非崩进程。
        var stdoutTask = ReadCappedAsync(proc.StandardOutput, AppConfig.MaxOutputBytes, cancellationToken);
        var stderrTask = proc.StandardError.ReadToEndAsync();

        try
        {
            if (timeout is { } t)
            {
                using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                cts.CancelAfter(t);
                await proc.WaitForExitAsync(cts.Token);
            }
            else
            {
                await proc.WaitForExitAsync(cancellationToken);
            }
        }
        catch (OperationCanceledException) when (timeout is { } t && !cancellationToken.IsCancellationRequested)
        {
            TryKill(proc);
            await DrainReadsAsync(stdoutTask, stderrTask);
            return new ProcessResult(-1, "", $"进程执行超时（超过 {t.TotalSeconds:0.#} 秒），已终止（{executable}）");
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            TryKill(proc);
            await DrainReadsAsync(stdoutTask, stderrTask);
            return new ProcessResult(-1, "", "进程执行被取消");
        }
        catch (OperationCanceledException)
        {
            TryKill(proc);
            await DrainReadsAsync(stdoutTask, stderrTask);
            return new ProcessResult(-1, "", "进程执行被取消");
        }

        // 进程已退出，等待读流收尾后判定输出上限
        await DrainReadsAsync(stdoutTask, stderrTask);
        var stdout = await stdoutTask;
        var stderr = await stderrTask;
        if (stdout.OverCap)
        {
            var mb = AppConfig.MaxOutputBytes / 1024 / 1024;
            return new ProcessResult(-1, "", $"反编译输出超过上限（{mb}MB），已终止；建议改用 decompile_to_dir 工具反编译到本地目录");
        }
        return new ProcessResult(proc.ExitCode, stdout.Text, stderr);
    }

    /// <summary>
    /// 流式读取 stdout 并限制累计字节：超过 maxBytes 后丢弃后续但仍读取至 EOF（避免子进程管道阻塞卡死）。
    /// UTF-16 下每 char 占 2 字节，按 (charCount * 2) 估算字节数判定上限。
    /// </summary>
    /// <param name="reader">子进程的标准输出读取器。</param>
    /// <param name="maxBytes">累计字节上限。</param>
    /// <param name="cancellationToken">取消令牌（超时/外部取消时由调用方传入）。</param>
    /// <returns>读取文本与是否超限标志。</returns>
    internal static async Task<(string Text, bool OverCap)> ReadCappedAsync(System.IO.StreamReader reader, long maxBytes, CancellationToken cancellationToken)
    {
        var sb = new StringBuilder();
        var buffer = new char[4096];
        bool overCap = false;
        int read;
        while ((read = await reader.ReadAsync(buffer.AsMemory(), cancellationToken).ConfigureAwait(false)) > 0)
        {
            if (overCap) continue; // 超限后丢弃但仍读取，让子进程解除管道阻塞、自然退出
            if ((sb.Length + read) * 2L > maxBytes)
            {
                overCap = true;
                continue;
            }
            sb.Append(buffer, 0, read);
        }
        return (sb.ToString(), overCap);
    }

    /// <summary>
    /// 等待 stdout/stderr 读取任务结束，避免进程被终止后读取任务以未观察异常结束；读取失败忽略（结果本就要丢弃）。
    /// </summary>
    /// <param name="stdoutTask">标准输出读取任务。</param>
    /// <param name="stderrTask">标准错误读取任务。</param>
    private static async Task DrainReadsAsync(Task stdoutTask, Task stderrTask)
    {
        try { await Task.WhenAll(stdoutTask, stderrTask).ConfigureAwait(false); }
        catch { /* 进程已终止、流已关闭，读取异常可忽略 */ }
    }

    /// <summary>
    /// 终止进程及其子进程树（进程可能已自行退出，忽略失败）。
    /// </summary>
    /// <param name="proc">要终止的进程。</param>
    private static void TryKill(Process proc)
    {
        try { proc.Kill(entireProcessTree: true); }
        catch { /* 进程可能已退出 */ }
        proc.WaitForExit(5000);
    }
}