using ILSpyMcp.Caching;
using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Pipeline;
using ILSpyMcp.Services;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// 共享执行管道用例：缓存命中/并发单飞/lines 分页/合并/超时语义。多数用例经 <see cref="AppServices.ConfigureForTest"/>
/// 注入小缓存走真实进程内反编译（tests/TestData 下测试程序集）；依赖可观测回源次数或制造失败的用例（并发单飞、合并失败、超时）
/// 直接以本地 ToolPipeline + 反编译探针验证，不依赖真实反编译。与 CheckToolTests 同属 AppServices collection，串行执行避免静态状态竞态。
/// </summary>
[Collection("AppServices")]
public class ToolPipelineTests
{
    private const string TypeMembers = "ILSpyMcp.Samples.Members";
    private const string TypeProps = "ILSpyMcp.Samples.Props";
    private const string TypeBigClass = "ILSpyMcp.Samples.BigClass";
    private const string TypeNoSuch = "No.Such.Type";

    private static readonly string SamplesDll = TestDataPaths.TestSamplesDll;

    /// <summary>
    /// 以 1MB 小缓存重建 AppServices（Cache/Pipeline 同源），测试结束恢复默认。
    /// </summary>
    private static void Init()
    {
        AppServices.ConfigureForTest(new DecompileCache(1 * 1024 * 1024));
    }

    /// <summary>
    /// 类型反编译请求对应的缓存 key（用于断言缓存是否写入/命中）。
    /// </summary>
    private static CacheKey KeyForType(string typeName)
        => AppServices.Cache.BuildKey(SamplesDll, new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Type, typeName)).Signature);

    /// <summary>
    /// 经共享管道反编译指定类型（默认前 200 行），带头部信息块上下文（与工具层行为一致）。
    /// </summary>
    private static async Task<ToolPipelineResult> ExecuteTypeAsync(string typeName, string lines = "")
    {
        var command = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Type, typeName));
        var context = new FormatContext(SamplesDll, $"类型 {typeName}");
        return await AppServices.Pipeline.ExecuteAsync(command, lines, context: context);
    }

    [Fact]
    public async Task 首次调用_回源并返回格式化结果()
    {
        Init();
        try
        {
            var result = await ExecuteTypeAsync(TypeMembers);

            Assert.StartsWith("程序集: ", result.Text); // 头部信息块在前
            Assert.Contains($"目标:   类型 {TypeMembers}", result.Text);
            Assert.Contains("class Members", result.Text); // 进程内真实反编译产物
            Assert.Contains("1\t", result.Text); // 源码带行号
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 二次调用_命中缓存不再回源()
    {
        Init();
        try
        {
            var key = KeyForType(TypeBigClass);
            Assert.Null(AppServices.Cache.Get(key)); // 首调前无缓存

            var first = await ExecuteTypeAsync(TypeBigClass);
            Assert.NotNull(AppServices.Cache.Get(key)); // 首调已写缓存
            Assert.True(AppServices.Cache.Get(key)!.Count > 200); // 缓存内是全量行，供后续 lines 切片复用

            var second = await ExecuteTypeAsync(TypeBigClass);
            Assert.StartsWith("程序集: ", second.Text); // 缓存命中同样带头部上下文
            Assert.Equal(first.Text, second.Text);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 超时结果_不入缓存且后续可重试()
    {
        // 反编译探针经门闩阻塞模拟慢反编译：零超时必超时且后台不残留真实反编译（只等待门闩，随后放行即结束）
        var gate = new ManualResetEventSlim(initialState: true);
        var cache = new DecompileCache();
        var probe = new Func<ToolCommand, string>(_ =>
        {
            gate.Wait();
            return "public class TimedOut { }";
        });
        var pipeline = new ToolPipeline(cache, probe);
        var command = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Type, "TimedOut"));
        var key = cache.BuildKey(command.Assembly, command.Signature);

        gate.Reset(); // 阻塞探针：首调（零超时）必超时
        try
        {
            var miss = await pipeline.ExecuteAsync(command, "", TimeSpan.Zero);
            Assert.Contains("反编译失败", miss.Text); // 零超时 → 超时提示转错误
            Assert.Null(cache.Get(key)); // 超时不写缓存
        }
        finally
        {
            gate.Set(); // 放行后台探针，避免残留阻塞线程
        }

        var hit = await pipeline.ExecuteAsync(command, "", TimeSpan.FromSeconds(60));
        Assert.DoesNotContain("反编译失败", hit.Text); // 重试成功
        Assert.NotNull(cache.Get(key));

        var cached = await pipeline.ExecuteAsync(command, "", TimeSpan.Zero);
        Assert.Equal(hit.Text, cached.Text); // 命中缓存，不再受零超时影响
    }

    [Fact]
    public async Task 不同类型_各自独立回源()
    {
        Init();
        try
        {
            await ExecuteTypeAsync(TypeMembers);
            await ExecuteTypeAsync(TypeProps);

            Assert.NotNull(AppServices.Cache.Get(KeyForType(TypeMembers)));
            Assert.NotNull(AppServices.Cache.Get(KeyForType(TypeProps)));
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 默认返回前200行_超出提示截断()
    {
        Init();
        try
        {
            var result = await ExecuteTypeAsync(TypeBigClass);

            Assert.Contains("已截断", result.Text);
            Assert.Contains("可用 lines", result.Text);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 指定lines_按行号切片()
    {
        Init();
        try
        {
            var result = await ExecuteTypeAsync(TypeBigClass, lines: "400-500");

            Assert.Contains("\n400\t", result.Text); // 切片从请求起始行号标注（头部信息块后）
            Assert.Contains($"目标:   类型 {TypeBigClass}", result.Text); // 头部信息块仍前置
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 未找到类型_返回提示不抛异常()
    {
        Init();
        try
        {
            var result = await ExecuteTypeAsync(TypeNoSuch);

            Assert.Contains($"未找到类型 {TypeNoSuch}", result.Text);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 未找到类型_错误提示不入缓存且后续可重试()
    {
        Init();
        try
        {
            var key = KeyForType(TypeNoSuch);
            Assert.Null(AppServices.Cache.Get(key));

            var first = await ExecuteTypeAsync(TypeNoSuch);
            Assert.Contains($"未找到类型 {TypeNoSuch}", first.Text); // 错误提示（非反编译结果，走错误转提示路径）
            Assert.Null(AppServices.Cache.Get(key)); // 错误提示不入缓存

            var second = await ExecuteTypeAsync(TypeNoSuch);
            Assert.Equal(first.Text, second.Text); // 同 key 再次回源结果一致（非从缓存读出）
            Assert.Null(AppServices.Cache.Get(key)); // 仍未入缓存，后续可重试
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 空程序集路径_返回提示不抛异常()
    {
        Init();
        try
        {
            // 绕过工具层校验直接调 pipeline，空路径触发 BuildKey 的 Path.GetFullPath 抛异常
            var command = new ToolCommand("", new DecompileRequest(DecompileKind.Type, "X"));
            var result = await AppServices.Pipeline.ExecuteAsync(command, "");

            Assert.Contains("反编译失败", result.Text);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 并发同key_仅回源一次且结果一致()
    {
        // 计数探针：并发单飞回归护栏——同 key 并发者只允许触发一次回源，否则 CallCount 断言失败
        int callCount = 0;
        var cache = new DecompileCache();
        var probe = new Func<ToolCommand, string>(_ =>
        {
            Interlocked.Increment(ref callCount);
            return "public class Concurrent { public void M() { } }";
        });
        var pipeline = new ToolPipeline(cache, probe);
        var command = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Type, "Concurrent"));

        var tasks = Enumerable.Range(0, 8).Select(_ => pipeline.ExecuteAsync(command, "")).ToArray();
        var results = await Task.WhenAll(tasks);

        Assert.All(results, r => Assert.Equal(results[0].Text, r.Text)); // 并发者结果一致
        Assert.Equal(1, callCount); // 并发单飞：同 key 仅回源一次（8 并发者各回源一次会让本断言失败）
        Assert.NotNull(cache.Get(cache.BuildKey(command.Assembly, command.Signature))); // 回源结果已写缓存
    }

    [Fact]
    public async Task ExecuteMergedAsync_多条成员命令_分隔行计入行号且结果一致()
    {
        Init();
        try
        {
            var (found, matches, _) = MemberResolver.FindMembers(SamplesDll, TypeBigClass, "BigHelper");
            Assert.True(found);
            var commands = matches
                .Select(m => new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Member, m.Token)) { DisplayName = $"{m.Name} ({m.Token})" })
                .ToArray();
            Assert.NotEmpty(commands);

            var result = await AppServices.Pipeline.ExecuteMergedAsync(commands, "");

            Assert.StartsWith("1\t=== BigHelper (", result.Text); // 分隔行计入行号且为首行
            Assert.Contains("=== BigHelper2 (", result.Text);
            Assert.Contains("BigHelper", result.Text);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task ExecuteMergedAsync_任一命令失败_整体返回错误且不输出部分结果()
    {
        // 探针按目标名区分成功/失败命令：验证合并执行任一命令失败即整体返回错误、丢弃已成功的部分结果
        var cache = new DecompileCache();
        var probe = new Func<ToolCommand, string>(cmd =>
            cmd.Request.Target == "Bad" ? "未找到类型 Bad" : $"public class {cmd.Request.Target} {{ }}");
        var pipeline = new ToolPipeline(cache, probe);
        var ok = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Type, "Ok")) { DisplayName = "Ok" };
        var bad = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Type, "Bad")) { DisplayName = "Bad" };

        var result = await pipeline.ExecuteMergedAsync(new[] { ok, bad }, "");

        Assert.Contains("反编译失败", result.Text); // 任一命令失败即整体返回错误提示
        Assert.Contains("未找到类型 Bad", result.Text);
        Assert.DoesNotContain("=== Ok ===", result.Text); // 已成功的部分不输出（合并列表整体丢弃）
        Assert.DoesNotContain("public class Ok", result.Text);
        Assert.Null(cache.Get(cache.BuildKey(bad.Assembly, bad.Signature))); // 失败命令的错误提示不入缓存，可重试
    }

    [Fact]
    public async Task ExecuteMergedAsync_再次调用_各命令均命中缓存()
    {
        Init();
        try
        {
            var (_, matches, _) = MemberResolver.FindMembers(SamplesDll, TypeBigClass, "BigHelper");
            var commands = matches
                .Select(m => new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Member, m.Token)))
                .ToArray();

            var first = await AppServices.Pipeline.ExecuteMergedAsync(commands, "");
            var second = await AppServices.Pipeline.ExecuteMergedAsync(commands, "");

            Assert.Equal(first.Text, second.Text);
            foreach (var cmd in commands)
            {
                Assert.NotNull(AppServices.Cache.Get(AppServices.Cache.BuildKey(SamplesDll, cmd.Signature)));
            }
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 同一成员不同子串查询_共享缓存()
    {
        Init();
        try
        {
            var (_, viaFull, _) = MemberResolver.FindMembers(SamplesDll, TypeBigClass, "BigHelper");
            var (_, viaPart, _) = MemberResolver.FindMembers(SamplesDll, TypeBigClass, "BigHe");
            var full = viaFull.First(m => m.Name == "BigHelper");
            var part = viaPart.First(m => m.Name == "BigHelper");
            Assert.Equal(full.Token, part.Token); // 同一成员 token 相同

            var cmdFull = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Member, full.Token));
            var cmdPart = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Member, part.Token));
            Assert.Equal(cmdFull.Signature, cmdPart.Signature); // 签名相同 → 共享缓存 key（原语义保留）

            var r1 = await AppServices.Pipeline.ExecuteAsync(cmdFull, "");
            var r2 = await AppServices.Pipeline.ExecuteAsync(cmdPart, "");

            Assert.Equal(r1.Text, r2.Text);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task WholeModule_反编译整个程序集()
    {
        Init();
        try
        {
            var command = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.WholeModule, ""));
            var context = new FormatContext(SamplesDll, "整个程序集");
            var result = await AppServices.Pipeline.ExecuteAsync(command, "", context: context);

            Assert.Contains("using System", result.Text); // 整模块反编译产物（using 头）
            Assert.Contains("已截断", result.Text); // 652 个类型远超 200 行默认上限
            Assert.DoesNotContain("反编译失败", result.Text);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public void ToolCommand_签名由Kind与Target派生()
    {
        var type = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Type, "A"));
        var member = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.Member, "0x06000005"));
        var whole = new ToolCommand(SamplesDll, new DecompileRequest(DecompileKind.WholeModule, ""));

        Assert.Equal("type\u001FA", type.Signature);
        Assert.Equal("member\u001F0x06000005", member.Signature);
        Assert.Equal("whole-module", whole.Signature);
        Assert.Equal(SamplesDll, type.Assembly);
        Assert.Equal(DecompileKind.Member, member.Request.Kind);
    }
}
