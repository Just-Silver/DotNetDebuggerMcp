using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;

namespace DotNetDebuggerMcp.DebugCli;

/// <summary>
/// CLI -dbg 一次性调试场景（供手动验证引擎，非 MCP）：launch 目标 → 设断点 → Continue → 命中后打印
/// 栈与变量 → Continue 到退出。结果写 stdout（CLI 模式）；错误走 stderr。
/// </summary>
public static class DebugCliRunner
{
    /// <summary>
    /// 一次性调试主流程：起目标进程（attach 稳定区模式）→ 设断点 → Continue → 等命中 →
    /// 打印命中线程调用栈与局部变量/参数 → 恢复执行至退出。
    /// </summary>
    /// <param name="exePathWithArgs">目标 exe 路径（可含启动参数，空格拆分；参数供构造 attach 稳定窗口）。</param>
    /// <param name="methodToken">断点方法 token（mdMethodDef，0x06 开头）。</param>
    /// <param name="ilOffset">断点 IL 偏移。</param>
    /// <param name="workingDirectory">目标进程工作目录；空则用当前目录。</param>
    /// <param name="timeoutSeconds">断点命中与退出的兜底超时秒数。</param>
    /// <param name="ct">取消令牌。</param>
    /// <returns>退出码：0 成功，1 失败（原因已写 stderr）。</returns>
    public static async Task<int> RunAsync(string exePathWithArgs, int methodToken, int ilOffset,
        string? workingDirectory, int timeoutSeconds, CancellationToken ct)
    {
        // v1 用 attach 模式（与集成测试一致）：先启动目标进程，等其进入稳定区后 attach。
        // launch 早期断点（挂起启动 → 模块加载 → 设断点）涉及 pending 断点延迟绑定，列 v2。
        // exePathWithArgs 可含启动参数（如 "DebugTarget.exe 5 5" 的 delay 参数供 attach 窗口），空格拆分。
        var parts = exePathWithArgs.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var exePath = parts[0];
        var args = parts.Length > 1 ? string.Join(' ', parts[1..]) : "";

        var psi = new System.Diagnostics.ProcessStartInfo(Path.GetFullPath(exePath), args)
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        };
        if (!string.IsNullOrEmpty(workingDirectory)) psi.WorkingDirectory = workingDirectory;
        using var process = System.Diagnostics.Process.Start(psi);
        if (process is null) { Console.Error.WriteLine($"无法启动目标: {exePath}"); return 1; }
        // 排空 stdout/stderr 防阻塞（AGENTS 铁律同源教训）
        var outSink = new System.Text.StringBuilder();
        var errSink = new System.Text.StringBuilder();
        process.OutputDataReceived += (_, e) => { if (e.Data is not null) lock (outSink) outSink.AppendLine(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (e.Data is not null) lock (errSink) errSink.AppendLine(e.Data); };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();

        // 等目标进入 Main 稳定区（CLR 已加载、模块可枚举）
        await Task.Delay(800);
        if (process.HasExited)
        {
            Console.Error.WriteLine($"目标进程过早退出（{exePath}）。调试目标需有启动延迟（attach 窗口），如 DebugTarget 传 delay 参数。");
            return 1;
        }

        // 目标模块名 = exe 文件名（DebugTarget.exe → DebugTarget.dll）
        var moduleName = Path.GetFileNameWithoutExtension(exePath) + ".dll";

        try
        {
            await using var session = await DebugSession.AttachAsync(process.Id, ct);
            Console.WriteLine($"已附加: {exePath} (pid={process.Id})");

            var events = new List<DebugEvent>();
            var readerTask = ConsumeAsync(session.Events, events);
            await Task.Delay(200); // 让订阅追上缓冲事件

            var bp = await session.SetBreakpointAsync(moduleName, methodToken, ilOffset);
            Console.WriteLine($"断点已设: [{bp.Id}] {bp}");

            // attach 后进程停在初始同步点：首次 Continue 启动
            await session.ContinueAsync();

            // 等断点命中（兜底超时）
            var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < deadline && !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
                await Task.Delay(100);

            var hit = events.LastOrDefault(e => e.Kind == DebugEventKind.BreakpointHit);
            if (hit is null)
            {
                Console.Error.WriteLine($"超时未命中断点（{timeoutSeconds}s）");
                return 1;
            }
            var payload = (BreakpointHitPayload)hit.Payload!;
            Console.WriteLine($"断点命中: bp={payload.BreakpointId} thread={payload.ThreadId} top={payload.TopFrame}");

            // 读命中线程的调用栈与变量
            var frames = await session.GetStackFramesAsync(payload.ThreadId, ct);
            Console.WriteLine($"调用栈（{frames.Count} 帧）:");
            foreach (var f in frames.Take(10))
                Console.WriteLine($"  {f.FrameIndex}: {f.Location}");

            var vars = await session.GetVariablesAsync(payload.ThreadId, ct);
            Console.WriteLine("局部变量/参数:");
            foreach (var (scope, list) in vars)
                foreach (var v in list)
                    Console.WriteLine($"  [{scope}] slot{v.Slot} = {v.Value.Display}");

            // 恢复执行到退出
            await session.ContinueAsync();
            Console.WriteLine("已恢复执行，等待进程退出...");
            var exitDeadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
            while (DateTime.UtcNow < exitDeadline && !events.Any(e => e.Kind == DebugEventKind.SessionStateChanged
                    && (e.Payload as SessionStateChangedPayload)?.State == DebugSessionState.Exited))
                await Task.Delay(100);
            Console.WriteLine("完成");
            return 0;
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"调试失败: {ex.Message}");
            return 1;
        }
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }
}
