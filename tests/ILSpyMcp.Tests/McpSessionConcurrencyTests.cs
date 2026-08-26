using System.Diagnostics;

using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;

using Xunit;
using Xunit.Abstractions;

namespace ILSpyMcp.Tests;

/// <summary>
/// MCP 会话级并发回归测试：以真实子进程启动 server（stdio 传输，与 agent 客户端同一路径）， 在同一会话上并发打入多个
/// tools/call，验证全部按时返回（不挂死）。 背景：agent 客户端内 12 路并发曾稳定挂死而内部单测全绿——缓存/管道内部
/// 并发已有覆盖，本文件补齐「传输层 + 会话分发」这一盲区。 目标程序集默认用 TestSamples.dll，可经环境变量
/// ILSPYMCP_CONCURRENCY_DLL 指向更大程序集提高复现保真度。
/// </summary>
public sealed class McpSessionConcurrencyTests
{
    /// <summary>
    /// 单个测试整体兜底超时：挂死时测试失败退出而非永久卡住（不需要人工取消）。
    /// </summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(60);

    private readonly ITestOutputHelper _output;

    public McpSessionConcurrencyTests(ITestOutputHelper output) => _output = output;

    /// <summary>
    /// 与实际挂死场景一致的 12 个 nameContains 过滤值。
    /// </summary>
    private static readonly string[] Filters = ["e", "i", "n", "o", "r", "s", "t", "a", "Net", "Client", "Server", "Serial"];

    [Fact]
    public async Task 同会话12路并发listTypes_全部按时返回()
    {
        var dll = ResolveTargetDll();
        _output.WriteLine($"目标程序集: {dll}");
        await using var mcp = await ConnectAsync();

        var sw = Stopwatch.StartNew();
        var finished = await RunConcurrentListTypesAsync(mcp, dll);
        _output.WriteLine($"12 路并发总耗时: {sw.ElapsedMilliseconds} ms");

        AssertResults(finished);
    }

    [Fact]
    public async Task 同会话第二轮并发listTypes_缓存命中也按时返回()
    {
        // 第一轮预热缓存后立刻再打一轮并发：排除「热缓存掩盖问题」，同时覆盖命中路径的并发
        var dll = ResolveTargetDll();
        await using var mcp = await ConnectAsync();

        await RunConcurrentListTypesAsync(mcp, dll);
        var sw = Stopwatch.StartNew();
        var finished = await RunConcurrentListTypesAsync(mcp, dll);
        _output.WriteLine($"第二轮（热缓存）12 路并发总耗时: {sw.ElapsedMilliseconds} ms");

        AssertResults(finished);
    }

    /// <summary>
    /// 差分测试：cache_stats 全程不访问任何程序集文件（连 assembly 参数都没有）， 若该场景并发也挂死则彻底排除文件 IO 因素。
    /// </summary>
    [Fact]
    public async Task 同会话两轮并发cacheStats_无文件读取也按时返回()
    {
        await using var mcp = await ConnectAsync();
        for (var round = 1; round <= 2; round++)
        {
            var sw = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, 12).Select(async _ =>
            {
                var t0 = Stopwatch.StartNew();
                return (ElapsedMs: t0.ElapsedMilliseconds,
                    Result: await mcp.CallToolAsync("cache_stats", new Dictionary<string, object?>(),
                        cancellationToken: CancellationToken.None));
            }).ToArray();

            var finished = await Task.WhenAll(tasks).WaitAsync(OverallTimeout);
            _output.WriteLine($"第 {round} 轮 cache_stats 12 路并发耗时: {sw.ElapsedMilliseconds} ms");
            Assert.All(finished, f => Assert.True(f.Result.IsError != true));
        }
    }

    /// <summary>
    /// 差分测试：反编译工具走与元数据完全不同的执行路径（InProcessDecompiler + Task.Run + 超时包装）， 验证挂死是否为会话级而非
    /// list_types/元数据读取特有。
    /// </summary>
    [Fact]
    public async Task 同会话两轮并发decompile_全部按时返回()
    {
        var dll = TestDataPaths.TestSamplesDll;
        await using var mcp = await ConnectAsync();
        for (var round = 1; round <= 2; round++)
        {
            var sw = Stopwatch.StartNew();
            var tasks = Enumerable.Range(0, 12).Select(async i =>
            {
                var t0 = Stopwatch.StartNew();
                return (Index: i, ElapsedMs: t0.ElapsedMilliseconds,
                    Result: await mcp.CallToolAsync("decompile",
                        new Dictionary<string, object?>
                        {
                            ["assembly"] = dll,
                            ["typeName"] = $"ILSpyMcp.Samples.Class{i + 1:0000}",
                        },
                        cancellationToken: CancellationToken.None));
            }).ToArray();

            var finished = await Task.WhenAll(tasks).WaitAsync(OverallTimeout);
            _output.WriteLine($"第 {round} 轮 decompile 12 路并发耗时: {sw.ElapsedMilliseconds} ms");
            Assert.All(finished, f => Assert.True(f.Result.IsError != true));
        }
    }

    [Fact]
    public async Task 同会话混合工具并发_全部按时返回()
    {
        // 更贴近真实 agent 用法：不同元数据工具混打同一程序集
        var dll = TestDataPaths.TestSamplesDll;
        await using var mcp = await ConnectAsync();

        var calls = new (string Tool, IReadOnlyDictionary<string, object?> Args)[]
        {
            ("list_types", new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "c" }),
            ("list_types", new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "csi", ["nameContains"] = "Class" }),
            ("signature", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.BigClass" }),
            ("hierarchy", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.DerivedClass" }),
            ("dependencies", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.Uses" }),
            ("call_graph", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.Caller" }),
            ("assembly_info", new Dictionary<string, object?> { ["assembly"] = dll }),
            ("search_string", new Dictionary<string, object?> { ["assembly"] = dll, ["search"] = "big" }),
            ("signature", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.Circle" }),
            ("hierarchy", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.IAnimal", ["includeIndirect"] = true }),
            ("list_types", new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "ide" }),
            ("call_graph", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = "ILSpyMcp.Samples.GenericCaller" }),
        };

        var sw = Stopwatch.StartNew();
        var tasks = calls.Select(async c =>
        {
            var t0 = Stopwatch.StartNew();
            var result = await mcp.CallToolAsync(c.Tool, c.Args, cancellationToken: CancellationToken.None);
            return (c.Tool, ElapsedMs: t0.ElapsedMilliseconds, Result: result);
        }).ToArray();

        var finished = await Task.WhenAll(tasks).WaitAsync(OverallTimeout);
        _output.WriteLine($"12 路混合工具并发总耗时: {sw.ElapsedMilliseconds} ms");

        AssertResults(finished.Select(f => (f.Tool, f.ElapsedMs, f.Result)));
    }

    /// <summary>
    /// 同一会话上并发发起 12 个 list_types 调用（模拟挂死场景参数），带整体兜底超时； 超时即现场取证：记录已完成调用数并对
    /// server 子进程与本测试进程抓托管线程栈（dotnet-stack），栈文件落盘后把路径放进异常消息。
    /// </summary>
    private async Task<(string Filter, long ElapsedMs, CallToolResult Result)[]> RunConcurrentListTypesAsync(
        McpClient mcp, string dll)
    {
        var completed = new System.Collections.Concurrent.ConcurrentDictionary<string, byte>();
        var tasks = Filters.Select(async filter =>
        {
            var t0 = Stopwatch.StartNew();
            var result = await mcp.CallToolAsync("list_types",
                new Dictionary<string, object?>
                {
                    ["assembly"] = dll,
                    ["list"] = "cside",
                    ["nameContains"] = filter,
                },
                cancellationToken: CancellationToken.None);
            completed.TryAdd(filter, 0);
            return (Filter: filter, ElapsedMs: t0.ElapsedMilliseconds, Result: result);
        }).ToArray();

        try
        {
            return await Task.WhenAll(tasks).WaitAsync(OverallTimeout);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(DiagnoseHang(completed));
        }
    }

    /// <summary>
    /// 断言每个调用未出错、返回非空文本，并打印逐调用耗时。
    /// </summary>
    private void AssertResults(IEnumerable<(string Name, long ElapsedMs, CallToolResult Result)> results)
    {
        foreach (var (name, elapsedMs, result) in results)
        {
            var text = string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(b => b.Text));
            _output.WriteLine($"  {name}: {elapsedMs} ms, {text.Length} 字符, IsError={result.IsError}");
            Assert.True(result.IsError != true, $"工具 {name} 返回框架级错误");
            Assert.False(string.IsNullOrWhiteSpace(text), $"工具 {name} 返回空文本");
        }
    }

    /// <summary>
    /// 解析目标程序集：环境变量 ILSPYMCP_CONCURRENCY_DLL 优先（指向大程序集提高复现保真度），缺省 TestSamples.dll。
    /// </summary>
    private static string ResolveTargetDll()
    {
        var env = Environment.GetEnvironmentVariable("ILSPYMCP_CONCURRENCY_DLL");
        return !string.IsNullOrWhiteSpace(env) && File.Exists(env) ? env : TestDataPaths.TestSamplesDll;
    }

    /// <summary>
    /// 以测试 bin 内随 ProjectReference 复制输出的 ILSpyMcp.dll 启动子进程（无参数即进 MCP stdio 模式）并完成握手。
    /// </summary>
    private static async Task<McpClient> ConnectAsync()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "ILSpyMcp.dll");
        if (!File.Exists(serverDll))
        {
            throw new FileNotFoundException($"测试输出目录未找到 server 程序集：{serverDll}（请先构建）");
        }
        var transport = new StdioClientTransport(new()
        {
            Name = "ILSpyMcp concurrency test client",
            Command = "dotnet",
            Arguments = [serverDll],
        });
        return await McpClient.CreateAsync(transport).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 挂死现场取证：对疑似 server 子进程（命令行含 ILSpyMcp.dll 的 dotnet 进程）与本测试进程执行 dotnet-stack report， 栈全文落盘
    /// %TEMP%\opencode-concurrency-debug\，异常消息携带完成情况、进程清单与栈文件路径。
    /// </summary>
    private static string DiagnoseHang(System.Collections.Concurrent.ConcurrentDictionary<string, byte> completed)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"并发调用挂死：已完成 {completed.Count}/{Filters.Length}（{string.Join(", ", completed.Keys)}）");
        try
        {
            var outDir = Path.Combine(Path.GetTempPath(), "opencode-concurrency-debug");
            Directory.CreateDirectory(outDir);

            var processes = new List<(int Pid, string Cmd)>();
            using (var searcher = new System.Management.ManagementObjectSearcher(
                "SELECT ProcessId, Name, CommandLine FROM Win32_Process"))
            {
                foreach (var o in searcher.Get())
                {
                    var cmdLine = o["CommandLine"] as string ?? "";
                    if (!cmdLine.Contains("ILSpyMcp.dll", StringComparison.OrdinalIgnoreCase)) continue;
                    processes.Add((Convert.ToInt32(o["ProcessId"]), $"{o["Name"]} :: {cmdLine}"));
                }
            }

            foreach (var (pid, cmd) in processes)
            {
                sb.AppendLine($"候选 server 进程: pid={pid} {Truncate(cmd, 160)}");
            }

            foreach (var (pid, _) in processes.Take(2))
            {
                CaptureStack(pid, outDir, sb);
            }

            // 测试进程自身（客户端侧）也抓一份
            CaptureStack(Environment.ProcessId, outDir, sb);
        }
        catch (Exception ex)
        {
            sb.AppendLine($"取证失败: {ex.Message}");
        }
        return sb.ToString();
    }

    /// <summary>
    /// 对指定进程执行 dotnet-stack report，全文写入 outDir\stack-{pid}.txt，异常消息追加线程帧摘要。
    /// </summary>
    private static void CaptureStack(int pid, string outDir, System.Text.StringBuilder sb)
    {
        var tool = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".dotnet", "tools");
        var exe = Path.Combine(tool, OperatingSystem.IsWindows() ? "dotnet-stack.exe" : "dotnet-stack");
        if (!File.Exists(exe))
        {
            sb.AppendLine($"pid={pid}: 未找到 dotnet-stack，跳过抓栈");
            return;
        }

        var psi = new ProcessStartInfo(exe, $"report -p {pid}")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        using var p = Process.Start(psi)!;
        var text = p.StandardOutput.ReadToEnd();
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit(60000);

        var file = Path.Combine(outDir, $"stack-{pid}.txt");
        File.WriteAllText(file, text + Environment.NewLine + err);
        sb.AppendLine($"pid={pid} 栈已保存: {file}");
        // 只取每个线程第一行（帧顶），避免异常消息过长
        var topFrames = text.Split('\n')
            .Where(l => l.Contains(" at ") || l.StartsWith("Thread ("))
            .Take(80);
        sb.AppendLine(string.Join(Environment.NewLine, topFrames));
    }

    private static string Truncate(string s, int n) => s.Length <= n ? s : s[..n] + "...";
}
