using System.Diagnostics;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using ClrDebug;
using DotNetDebugger.Engine.Engine;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using Xunit;

namespace DotNetDebugger.Engine.Tests;

/// <summary>
/// P9 回归（原 spike 转正）：launch 场景「Process.Start + RegisterForRuntimeStartup 蹲守 + 标准 attach」
/// 的时序护栏——零延迟目标（Work 毫秒级跑完）上断点必须命中，证明接管在 Main 之前（决定性证明，
/// 见 Session DebugSessionManager.LaunchAndAttachAsync 的同款实现）。
/// dbgshim CreateProcessForLaunch 不支持输出重定向（MCP stdio 会撕协议帧），故原生 launch 路径不可用——
/// 本测试同时锁住「自起进程 + 蹲守」这一选定路线的可用性。
/// </summary>
public sealed class LaunchRegisterStartupSpikeTests
{
    [Fact]
    public async Task RegisterStartup_回调后Attach_Pending断点命中()
    {
        var exe = TestPaths.DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        var computeToken = ReadMethodToken(dll, "Compute");
        Assert.True(computeToken > 0);

        // 1. Process.Start 重定向启动（无延迟：n=5, delay=0）
        var psi = new ProcessStartInfo(exe, "5 0")
        {
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true,
        };
        using var process = Process.Start(psi) ?? throw new InvalidOperationException("启动失败");
        process.OutputDataReceived += (_, _) => { };
        process.ErrorDataReceived += (_, _) => { };
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        var sw = Stopwatch.StartNew();

        // 2. 注册运行时启动回调（目标此刻 CLR 大概率尚未加载完——正是该 API 的设计用途）
        var shim = DbgShimLoader.Load();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PSTARTUP_CALLBACK cb = (pCordb, parameter, hr) => tcs.TrySetResult(true);
        var token = shim.RegisterForRuntimeStartup(process.Id, cb);

        var arrived = await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);
        var callbackMs = sw.ElapsedMilliseconds;

        // 3. 回调到达即标准 attach（进程停在初始同步点——Main 前）
        await using var session = await DebugSession.AttachAsync(process.Id, TestContext.Current.CancellationToken);
        var modulesAtAttach = await session.GetModulesAsync(TestContext.Current.CancellationToken);
        var targetModuleLoadedAtAttach = modulesAtAttach.Any(m => m.Name.Equals("DebugTarget.dll", StringComparison.OrdinalIgnoreCase));

        // 4. 设断点（此时可能 pending）→ continue → 必须命中（命中=接管在 Main 前）
        var bp = await session.SetBreakpointAsync("DebugTarget.dll", computeToken, 0, ct: TestContext.Current.CancellationToken);
        var boundAtSet = bp.IsBound;
        var events = new List<DebugEvent>();
        var reader = ConsumeAsync(session.Events, events);
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        // —— spike 断言与数据 ——
        Assert.True(arrived, "RegisterForRuntimeStartup 回调未在 15s 内到达");
        Assert.True(events.Any(e => e.Kind == DebugEventKind.BreakpointHit),
            "断点未命中——接管未在 Main 前完成（spike 失败）");
        var bpAfter = (await session.GetBreakpointsAsync(TestContext.Current.CancellationToken)).Single(b => b.Id == bp.Id);
        Assert.True(bpAfter.IsBound);

        Console.Error.WriteLine($"[spike] 启动→回调 {callbackMs}ms；attach 时 DebugTarget.dll 已加载={targetModuleLoadedAtAttach}；设断点时已绑定={boundAtSet}");

        await session.DisconnectAsync(TestContext.Current.CancellationToken);
        process.WaitForExit(10000);
        await reader.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    private static int ReadMethodToken(string dllPath, string methodName)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                if (mr.GetString(mr.GetMethodDefinition(mh).Name) == methodName)
                    return MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
    }
}
