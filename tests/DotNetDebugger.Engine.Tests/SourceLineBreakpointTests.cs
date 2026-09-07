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
/// 源行断点（R6）集成测试：attach DebugTarget → SetSourceLineBreakpointAsync（注入桩 resolver）
/// → 模块已加载场景立即解析绑定并命中。桩 resolver 返回预置 Work token（Engine 测试不引
/// Decompiler/Session，resolver 由测试自实现——真实 PDB 解析路径由宿主 e2e 覆盖）。
/// </summary>
public sealed class SourceLineBreakpointTests
{
    /// <summary>桩 resolver：匹配 DebugTarget 模块与 DebugTarget.cs 源文件 → 返回预置 Work token+IL 0。</summary>
    private sealed class StubResolver : ISourceLineBreakpointResolver
    {
        private readonly int _workToken;
        public StubResolver(int workToken) => _workToken = workToken;
        public SourceLineResolveResult? Resolve(string modulePath, string sourcePath, int line)
            => Path.GetFileName(modulePath).Equals("DebugTarget.dll", StringComparison.OrdinalIgnoreCase)
               && sourcePath.EndsWith("DebugTarget.cs", StringComparison.OrdinalIgnoreCase)
                ? new SourceLineResolveResult(modulePath, "DebugTarget.dll", _workToken, 0, line)
                : null;
    }

    [Fact]
    public async Task SetSourceLine_ModuleLoaded_ResolvesAndHits()
    {
        var exe = TestPaths.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        using var target = DebugTargetProcess.Start("5 5");
        await Task.Delay(800, TestContext.Current.CancellationToken);
        Assert.False(target.HasExited);

        var workToken = ReadMethodToken(Path.ChangeExtension(exe, ".dll"), "Work");
        Assert.True(workToken > 0);

        var events = new List<DebugEvent>();
        await using var session = await DebugSession.AttachAsync(target.Id,
            sourceLineResolver: new StubResolver(workToken), ct: TestContext.Current.CancellationToken);
        var readerTask = ConsumeAsync(session.Events, events);
        await Task.Delay(200, TestContext.Current.CancellationToken); // 订阅追上缓冲

        // attach 已加载模块：源行断点应立即解析绑定（非 pending）
        var bp = await session.SetSourceLineBreakpointAsync("DebugTarget.cs", 10,
            ct: TestContext.Current.CancellationToken);
        Assert.True(bp.Id > 0);
        Assert.True(bp.IsBound, $"attach 已加载模块场景源行断点应立即绑定。状态: {bp}");
        Assert.Equal(workToken, bp.MethodToken);
        Assert.Equal("DebugTarget.dll", bp.ModuleName);

        await session.ContinueAsync(TestContext.Current.CancellationToken);

        // 等 BreakpointHit（进程 delay 后进 Work）
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline &&
               !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.Contains(events, e => e.Kind == DebugEventKind.BreakpointHit);
        var hit = events.Last(e => e.Kind == DebugEventKind.BreakpointHit);
        Assert.Equal(bp.Id, Assert.IsType<BreakpointHitPayload>(hit.Payload).BreakpointId);

        // 恢复后进程退出
        await session.ContinueAsync(TestContext.Current.CancellationToken);
        var exitDeadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < exitDeadline && !target.HasExited) await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.True(target.HasExited, "源行断点恢复后目标未退出");

        await readerTask.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    [Fact]
    public async Task SetSourceLine_NoResolver_ThrowsChineseHint()
    {
        var exe = TestPaths.DebugTargetExe;
        using var target = DebugTargetProcess.Start("1 3");
        await Task.Delay(800, TestContext.Current.CancellationToken);

        // 未注入 resolver（缺省）→ 中文提示
        await using var session = await DebugSession.AttachAsync(target.Id, ct: TestContext.Current.CancellationToken);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            session.SetSourceLineBreakpointAsync("DebugTarget.cs", 10, ct: TestContext.Current.CancellationToken));
        Assert.Contains("无源行解析器", ex.Message);
    }

    [Fact]
    public async Task SetSourceLine_ModuleNotLoadedYet_PendingThenAutoBindsAndHits()
    {
        // R6 核心：launch 早期（RegisterForRuntimeStartup 回调=Main 前）attach 时目标模块常未加载——
        // 源行断点登记为 pending，模块加载（TrackModule）后自动解析补设，continue 后命中。
        // 复刻 LaunchRegisterStartupSpikeTests 的蹲守流程（零延迟目标：n=5, delay=0，Work 毫秒级）。
        var exe = TestPaths.DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        var workToken = ReadMethodToken(dll, "Work");
        Assert.True(workToken > 0);

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

        var shim = DbgShimLoader.Load();
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        PSTARTUP_CALLBACK cb = (_, _, _) => tcs.TrySetResult(true);
        var token = shim.RegisterForRuntimeStartup(process.Id, cb);
        await tcs.Task.WaitAsync(TimeSpan.FromSeconds(15), TestContext.Current.CancellationToken);

        await using var session = await DebugSession.AttachAsync(process.Id,
            sourceLineResolver: new StubResolver(workToken), ct: TestContext.Current.CancellationToken);
        var boundAtSet = (await session.GetModulesAsync(TestContext.Current.CancellationToken))
            .Any(m => m.Name.Equals("DebugTarget.dll", StringComparison.OrdinalIgnoreCase));

        // 设源行断点：模块已加载→立即绑；未加载→pending（两者都必须最终命中）
        var bp = await session.SetSourceLineBreakpointAsync("DebugTarget.cs", 10, ct: TestContext.Current.CancellationToken);
        Assert.True(bp.Id > 0);
        Console.Error.WriteLine($"[r6] attach 时目标模块已加载={boundAtSet}；设源行断点后 IsBound={bp.IsBound}");

        var events = new List<DebugEvent>();
        var reader = ConsumeAsync(session.Events, events);
        await session.ContinueAsync(TestContext.Current.CancellationToken);

        // 兜底 20s 等命中（Work 毫秒级跑完，命中=源行断点已补设生效）
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline && !events.Any(e => e.Kind == DebugEventKind.BreakpointHit))
            await Task.Delay(100, TestContext.Current.CancellationToken);

        Assert.True(events.Any(e => e.Kind == DebugEventKind.BreakpointHit),
            "源行断点未命中（延迟补设未生效或接管未在 Main 前）");
        var bpAfter = (await session.GetBreakpointsAsync(TestContext.Current.CancellationToken)).Single(b => b.Id == bp.Id);
        Assert.True(bpAfter.IsBound, "源行断点最终应已绑定");
        Assert.Equal(workToken, bpAfter.MethodToken);
        Assert.Equal("DebugTarget.dll", bpAfter.ModuleName);

        try { shim.TryUnregisterForRuntimeStartup(token); } catch { }
        await session.DisconnectAsync(TestContext.Current.CancellationToken);
        process.WaitForExit(10000);
        await reader.WaitBounded(2000, TestContext.Current.CancellationToken);
    }

    private static async Task ConsumeAsync(IAsyncEnumerable<DebugEvent> src, List<DebugEvent> into)
    {
        await foreach (var e in src) { lock (into) into.Add(e); }
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
                var md = mr.GetMethodDefinition(mh);
                if (mr.GetString(md.Name) == methodName)
                    return MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }
}
