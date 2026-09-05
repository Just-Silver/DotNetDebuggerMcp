using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// MCP 会话级并发回归测试：以真实子进程启动 server（stdio 传输，与 agent 客户端同一路径）， 在同一会话上并发打入多个 tools/call，验证全部按时返回（不挂死）。
/// 背景：agent 客户端内 12 路并发曾稳定挂死而内部单测全绿——缓存/管道内部 并发已有覆盖，本文件补齐「传输层 + 会话分发」这一盲区。 目标程序集默认用
/// TestSamples.dll，可经环境变量 DOTNETDEBUGGERMCP_CONCURRENCY_DLL 指向更大程序集提高复现保真度。
/// </summary>
public sealed class McpSessionConcurrencyTests
{
    /// <summary>
    /// 单个测试整体兜底超时：挂死时测试失败退出而非永久卡住（不需要人工取消）。
    /// </summary>
    private static readonly TimeSpan OverallTimeout = TimeSpan.FromSeconds(60);

    /// <summary>
    /// 与实际挂死场景一致的 12 个 nameContains 过滤值。
    /// </summary>
    private static readonly string[] Filters = ["e", "i", "n", "o", "r", "s", "t", "a", "Net", "Client", "Server", "Serial"];

    private readonly ITestOutputHelper _output;

    public McpSessionConcurrencyTests(ITestOutputHelper output) => _output = output;

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
                        cancellationToken: TestContext.Current.CancellationToken));
            }).ToArray();

            var finished = await Task.WhenAll(tasks).WaitAsync(OverallTimeout, cancellationToken: TestContext.Current.CancellationToken);
            _output.WriteLine($"第 {round} 轮 cache_stats 12 路并发耗时: {sw.ElapsedMilliseconds} ms");
            Assert.All(finished, f => Assert.True(f.Result.IsError != true));
        }
    }

    /// <summary>
    /// 差分测试：反编译工具走与元数据完全不同的执行路径（InProcessDecompiler + Task.Run + 超时包装）， 验证挂死是否为会话级而非 list_types/元数据读取特有。
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
                            ["typeName"] = $"{TestDataPaths.SamplesNamespace}.Class{i + 1:0000}",
                        },
                        cancellationToken: TestContext.Current.CancellationToken));
            }).ToArray();

            var finished = await Task.WhenAll(tasks).WaitAsync(OverallTimeout, cancellationToken: TestContext.Current.CancellationToken);
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
            ("signature", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataPaths.SamplesNamespace}.BigClass" }),
            ("hierarchy", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataPaths.SamplesNamespace}.DerivedClass" }),
            ("dependencies", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataPaths.SamplesNamespace}.Uses" }),
            ("call_graph", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataPaths.SamplesNamespace}.Caller" }),
            ("assembly_info", new Dictionary<string, object?> { ["assembly"] = dll }),
            ("search_string", new Dictionary<string, object?> { ["assembly"] = dll, ["search"] = "big" }),
            ("signature", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataPaths.SamplesNamespace}.Circle" }),
            ("hierarchy", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataPaths.SamplesNamespace}.IAnimal", ["includeIndirect"] = true }),
            ("list_types", new Dictionary<string, object?> { ["assembly"] = dll, ["list"] = "ide" }),
            ("call_graph", new Dictionary<string, object?> { ["assembly"] = dll, ["typeName"] = $"{TestDataPaths.SamplesNamespace}.GenericCaller" }),
        };

        var sw = Stopwatch.StartNew();
        var tasks = calls.Select(async c =>
        {
            var t0 = Stopwatch.StartNew();
            var result = await mcp.CallToolAsync(c.Tool, c.Args, cancellationToken: TestContext.Current.CancellationToken);
            return (c.Tool, ElapsedMs: t0.ElapsedMilliseconds, Result: result);
        }).ToArray();

        var finished = await Task.WhenAll(tasks).WaitAsync(OverallTimeout, cancellationToken: TestContext.Current.CancellationToken);
        _output.WriteLine($"12 路混合工具并发总耗时: {sw.ElapsedMilliseconds} ms");

        AssertResults(finished.Select(f => (f.Tool, f.ElapsedMs, f.Result)));
    }

    /// <summary>
    /// 解析目标程序集：环境变量 DOTNETDEBUGGERMCP_CONCURRENCY_DLL 优先（指向大程序集提高复现保真度），缺省 TestSamples.dll。
    /// </summary>
    private static string ResolveTargetDll()
    {
        var env = Environment.GetEnvironmentVariable("DOTNETDEBUGGERMCP_CONCURRENCY_DLL");
        return !string.IsNullOrWhiteSpace(env) && File.Exists(env) ? env : TestDataPaths.TestSamplesDll;
    }

    /// <summary>
    /// 以测试 bin 内随 ProjectReference 复制输出的 DotNetDebuggerMcp.dll 启动子进程（无参数即进 MCP stdio 模式）并完成握手。
    /// </summary>
    private static async Task<McpClient> ConnectAsync()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetDebuggerMcp.dll");
        if (!File.Exists(serverDll))
        {
            throw new FileNotFoundException($"测试输出目录未找到 server 程序集：{serverDll}（请先构建）");
        }
        var transport = new StdioClientTransport(new()
        {
            Name = "DotNetDebuggerMcp concurrency test client",
            Command = "dotnet",
            Arguments = [serverDll],
        });
        return await McpClient.CreateAsync(transport).WaitAsync(TimeSpan.FromSeconds(30));
    }

    /// <summary>
    /// 同一会话上并发发起 12 个 list_types 调用（模拟挂死场景参数），带整体兜底超时； 超时即测试失败并报告已完成调用数。
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
                cancellationToken: TestContext.Current.CancellationToken);
            completed.TryAdd(filter, 0);
            return (Filter: filter, ElapsedMs: t0.ElapsedMilliseconds, Result: result);
        }).ToArray();

        try
        {
            return await Task.WhenAll(tasks).WaitAsync(OverallTimeout, cancellationToken: TestContext.Current.CancellationToken);
        }
        catch (TimeoutException)
        {
            throw new TimeoutException(
                $"并发调用超时（{OverallTimeout.TotalSeconds}s）：已完成 {completed.Count}/{Filters.Length}（{string.Join(", ", completed.Keys)}）");
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
}