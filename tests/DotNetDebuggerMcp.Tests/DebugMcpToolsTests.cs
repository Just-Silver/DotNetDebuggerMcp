using DotNetDebuggerMcp.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Reflection.Metadata;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// MCP 调试工具端到端测试：真实子进程宿主 + DebugTarget 目标，
/// 验证 debug_launch → breakpoint → continue → 命中 → stack/variables 闭环。
/// </summary>
public sealed class DebugMcpToolsTests
{
    private static string DebugTargetExe => Path.Combine(
        Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!, "DebugTarget.exe");

    [Fact]
    public async Task DebugTools_LaunchBreakpointContinueInspect_ClosesLoop()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        // Work 的 token 从 DebugTarget.dll 元数据读（0x06000003 稳定，但用元数据解析更稳）
        var dll = Path.ChangeExtension(exe, ".dll");
        var workToken = ReadMethodToken(dll, "Work");
        Assert.True(workToken > 0);

        await using var mcp = await ConnectAsync();

        // 1. debug_launch：启动 DebugTarget（3 迭代 + 8s delay 供操作窗口），异步返回
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 8", ["timeoutSeconds"] = 20 });
        if (launch.IsError == true) Console.WriteLine($"[diag] launch error: {launch.Text()}");
        Assert.True(launch.IsError != true, launch.Text());
        Assert.Contains("已启动", launch.Text());
        // D：空 workingDirectory 的 launch 返回应报告实际生效工作目录（默认=exe 所在目录）
        Assert.Contains("工作目录", launch.Text());
        var exeDir = Path.GetDirectoryName(exe);
        Assert.NotNull(exeDir);
        Assert.Contains(exeDir, launch.Text());

        // 1b. debug_output：launch 会话可拉目标输出（P9 冻结在 Main 前，此刻多为暂无输出——start 行断言在第 5 步 continue 之后）
        var output = await CallAsync(mcp, "debug_output",
            new Dictionary<string, object?> { ["lines"] = 10 });
        Assert.True(output.IsError != true, output.Text());
        Assert.True(output.Text().Contains("目标输出") || output.Text().Contains("暂无输出"), output.Text());

        // 2. pending 断点：错误模块名不再报错，登记待绑定；list 可见、remove 可删
        var pending = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "NoSuchModule.dll", ["methodToken"] = $"0x{workToken:x8}", ["ilOffset"] = 0 });
        Assert.True(pending.IsError != true, pending.Text());
        Assert.Contains("断点已登记", pending.Text());
        var listPending = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("未绑定", listPending.Text());
        var pendingId = ParseBreakpointId(pending.Text());
        var rmPending = await CallAsync(mcp, "debug_breakpoint_remove",
            new Dictionary<string, object?> { ["breakpointId"] = pendingId });
        Assert.True(rmPending.IsError != true, rmPending.Text());

        // 3. debug_continue 先行（CI 实录：launch 返回时模块登记可能缺目标模块——attach 竞速窗口；
        // 先进入运行，8s delay 即设断点窗口）
        var cont = await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        Assert.True(cont.IsError != true, cont.Text());
        Assert.Contains("已继续", cont.Text());

        // 4. 设断点：Work 入口。module-load 竞态下可能返回「断点已登记（pending）」（登记表暂缺该模块）——
        // 语义上 pending 随后自动补绑并命中（排查探针实测 100% 闭环），故等待绑定到「已绑定」再断言，
        // 而非要求 set 立即返回「断点已设」。若 15s 内未绑定（模块未加载/token 无效）WaitBoundAsync 断言失败。
        var bp = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{workToken:x8}", ["ilOffset"] = 0 });
        Assert.True(bp.IsError != true, bp.Text());
        var bpId = ParseBreakpointId(bp.Text());
        await WaitBoundAsync(mcp, bpId);
        var list = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("断点列表（1 个）", list.Text());
        Assert.Contains("已绑定", list.Text());

        // 5. debug_wait：阻塞等停点（Work 命中；上限 20s 内应返回已停下）；默认附目标输出
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("已停下", wait.Text());
        Assert.Contains("breakpoint", wait.Text());
        Assert.Contains("目标输出", wait.Text());
        Assert.Contains("[DebugTarget] start", wait.Text());
        Assert.Contains("停点上下文", wait.Text()); // P4：默认附当前语句反编译上下文
        Assert.Contains("← 当前语句", wait.Text());

        // 6. debug_state 确认 Stopped（默认附停点上下文；Work 停点是 DebugTarget.Program——普通同步类型）
        var st = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
        Assert.Contains("已停止", st.Text());
        // B① 负路径：普通同步类型停点上下文不得带「编译器生成 async 状态机」备注
        Assert.Contains("停点上下文", st.Text());
        Assert.DoesNotContain("编译器生成 async 状态机", st.Text());

        // 7. debug_stack：读调用栈（应含 Work 帧，真名 类型.方法 + token 后缀）
        var stack = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.True(stack.IsError != true, stack.Text());
        Assert.Contains("调用栈", stack.Text());
        Assert.Contains("DebugTarget.Program.Work", stack.Text()); // C：帧名真名化（类型.方法）
        Assert.Contains($"0x{workToken:x8}", stack.Text()); // token 后缀保留（下断点闭环）
        // C④ 负路径：DebugTarget 全同步无状态机——正常方法帧不得带「(状态机 X)」标注
        Assert.DoesNotContain("状态机", stack.Text());

        // 7b. debug_step 负路径（B①）：停在普通同步方法（Work），step 返回不得附 async 状态机引导
        var step = await CallAsync(mcp, "debug_step", new Dictionary<string, object?> { ["stepType"] = "into" });
        Assert.True(step.IsError != true, step.Text());
        Assert.Contains("已提交 step into", step.Text());
        Assert.DoesNotContain("async 状态机帧", step.Text());

        // 8. debug_variables：读局部变量（step 后仍处停点——wait 等 step 完成的新停点）
        var waitStep = await CallAsync(mcp, "debug_wait", new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", waitStep.Text());
        var vars = await CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.True(vars.IsError != true, vars.Text());
        Assert.Contains("局部变量", vars.Text());

        // 9. debug_disconnect 清理
        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task ExceptionFilter_Match_StopsWithMessageAndExceptionVariable()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await ConnectAsync();

        // throw 模式 + 8s delay（操作窗口）：Work 后抛 DivideByZeroException("value is zero")
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 1 throw 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());

        // 短名过滤（.短名 结尾匹配）→ 命中
        var set = await CallAsync(mcp, "debug_exceptions",
            new Dictionary<string, object?> { ["typeName"] = "DivideByZeroException" });
        Assert.True(set.IsError != true, set.Text());
        Assert.Contains("已设异常断点", set.Text());

        var cont = await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        Assert.True(cont.IsError != true, cont.Text());

        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("已停下", wait.Text());
        Assert.Contains("System.DivideByZeroException", wait.Text());
        Assert.Contains("value is zero", wait.Text()); // 停点现场附异常 Message

        // $exception 伪变量：类型 + Message + 一级字段
        var vars = await CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.True(vars.IsError != true, vars.Text());
        Assert.Contains("$exception", vars.Text());
        Assert.Contains("System.DivideByZeroException", vars.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task LineBreakpoints_TypeNameLine_And_SourceLine_Hit()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workToken = ReadMethodToken(dll, "Work");
        Assert.True(workToken > 0);

        // 3a 坐标：Work 方法的反编译视图首语句行（与映射同源，行号必然有效）
        var doc = DotNetDebugger.Decompiler.Document.DocumentService.GetTypeDocument(dll, "DebugTarget.Program");
        Assert.True(doc.IsSuccess, doc.Error);
        var workFirstLine = DotNetDebugger.Decompiler.Document.DocumentService.GetMethodFirstLine(doc, workToken);
        Assert.True(workFirstLine.GetValueOrDefault() > 0);

        // 3b 坐标：Work 方法内、与 3a 落点不同 IL 位置的源码行（循环体行，每轮迭代经过），动态找防脚本漂移
        var firstTarget = DotNetDebugger.Decompiler.Document.DocumentService.GetBreakpointTargetAtLine(doc, workFirstLine!.Value);
        Assert.True(firstTarget is not null);
        DotNetDebugger.Decompiler.Document.SourceLineResolver.SourceLineTarget? sourceTarget = null;
        for (var l = 1; l <= 80 && sourceTarget is null; l++)
        {
            var t = DotNetDebugger.Decompiler.Document.SourceLineResolver.Resolve(dll, "DebugTarget.cs", l, out _);
            if (t is not null && t.MethodToken == workToken && t.IlOffset != firstTarget.Value.IlOffset) sourceTarget = t;
        }
        Assert.True(sourceTarget is not null, "未在 DebugTarget.cs 中找到 Work 方法的另一语句源码行");

        await using var mcp = await ConnectAsync();

        // launch（delay 8s 提供操作窗口）；先 continue 再设行断点（CI 实录：launch 返回时模块登记可能缺目标模块）
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // 3a：typeName+line 设断点（省缺 moduleName，跨模块解析）。
        // continue 后模块登记是异步的——set 过早会报「在已加载模块中未找到类型」，轮询重试等模块就绪
        var setAText = await RetrySetUntilAsync(mcp,
            new Dictionary<string, object?> { ["typeName"] = "DebugTarget.Program", ["line"] = workFirstLine }, "断点已设");
        Assert.Contains("DebugTarget.dll", setAText);

        // 命中 3a 断点（delay 结束进 Work）
        var waitA = await CallAsync(mcp, "debug_wait", new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0 });
        Assert.Contains("已停下", waitA.Text());
        var stack = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.Contains($"0x{workToken:x8}", stack.Text()); // 栈帧以 token 形式展示，命中方法即 Work

        // 3b：sourcePath+line 设断点（Work 方法内靠后源码行），continue 后循环迭代再命中。
        // 竞态下 set 可能返回「断点已登记（延迟绑定）」（含 id，模块加载后自动补绑命中）——等「已绑定」终态
        var setB = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = sourceTarget.ActualLine });
        Assert.True(setB.IsError != true, setB.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(setB.Text()));

        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        var waitB = await CallAsync(mcp, "debug_wait", new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0 });
        Assert.Contains("已停下", waitB.Text());
        var stackB = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.Contains($"0x{workToken:x8}", stackB.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task TraceBreakpoint_RecordsWithoutStopping()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workToken = ReadMethodToken(dll, "Work");

        // trace 行：Work 循环体内、异于入口的语句行（循环 3 次 → 轨迹 3 条）
        var doc = DotNetDebugger.Decompiler.Document.DocumentService.GetTypeDocument(dll, "DebugTarget.Program");
        Assert.True(doc.IsSuccess, doc.Error);
        var workFirstLine = DotNetDebugger.Decompiler.Document.DocumentService.GetMethodFirstLine(doc, workToken);
        var firstTarget = DotNetDebugger.Decompiler.Document.DocumentService.GetBreakpointTargetAtLine(doc, workFirstLine!.Value);
        Assert.True(firstTarget is not null);
        DotNetDebugger.Decompiler.Document.SourceLineResolver.SourceLineTarget? sourceTarget = null;
        for (var l = 1; l <= 80 && sourceTarget is null; l++)
        {
            var t = DotNetDebugger.Decompiler.Document.SourceLineResolver.Resolve(dll, "DebugTarget.cs", l, out _);
            if (t is not null && t.MethodToken == workToken && t.IlOffset != firstTarget.Value.IlOffset) sourceTarget = t;
        }
        Assert.True(sourceTarget is not null);

        await using var mcp = await ConnectAsync();
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());

        // 先 continue 再设 trace 断点（CI 实录：launch 返回时模块登记可能缺目标模块；delay 8s = 设断点窗口）
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // sourcePath+line 需模块已登记（PDB 解析）；continue 后登记异步——竞态下 set 返回「断点已登记（延迟）」，
        // 模块加载后自动补绑并命中（含 id），故 set 一次取 id → 等「已绑定」终态
        var set = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = sourceTarget.ActualLine, ["mode"] = "trace" });
        Assert.True(set.IsError != true, set.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(set.Text()));

        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        // trace 不停：Work 3 次循环后进程跑完退出，wait 返回退出+整批轨迹
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("进程已退出", wait.Text());
        Assert.Contains("trace 轨迹（3 条", wait.Text());
        Assert.Contains("[arguments]", wait.Text());

        var list = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("[trace]", list.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task DebugProcesses_ListsWithoutError()
    {
        await using var mcp = await ConnectAsync();
        var r = await CallAsync(mcp, "debug_processes", new Dictionary<string, object?>());
        Assert.True(r.IsError != true, r.Text());
        Assert.DoesNotContain("列出进程失败", r.Text());
        // 输出形态：要么进程列表（pid= 行），要么明确的空态提示
        Assert.True(r.Text().Contains("pid=") || r.Text().Contains("未发现"), r.Text());
    }

    [Fact]
    public async Task DebugState_ConcurrentQueries_AllReturn()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe));

        await using var mcp = await ConnectAsync();
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 6", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());

        // 单会话内并发 12 路 debug_state：验证 Session 管理器线程安全 + stdio 不撕帧
        var tasks = Enumerable.Range(0, 12).Select(async _ =>
        {
            var r = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
            return r;
        }).ToArray();
        var results = await Task.WhenAll(tasks).WaitAsync(TimeSpan.FromSeconds(20), TestContext.Current.CancellationToken);
        Assert.All(results, r => Assert.True(r.IsError != true));
        Assert.All(results, r => Assert.Contains("会话状态", r.Text()));

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task DebugEvaluate_PathsComparisonsAndErrors()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workBagToken = ReadMethodToken(dll, "WorkBag");
        var workScoresToken = ReadMethodToken(dll, "WorkScores");
        Assert.True(workBagToken > 0 && workScoresToken > 0);

        // 停点坐标：WorkBag 循环体内语句行（b/n/i 全存活；入口 IL0 局部未初始化不可靠）
        var doc = DotNetDebugger.Decompiler.Document.DocumentService.GetTypeDocument(dll, "DebugTarget.Program");
        Assert.True(doc.IsSuccess, doc.Error);
        var bagFirstLine = DotNetDebugger.Decompiler.Document.DocumentService.GetMethodFirstLine(doc, workBagToken);
        var entryTarget = DotNetDebugger.Decompiler.Document.DocumentService.GetBreakpointTargetAtLine(doc, bagFirstLine!.Value);
        Assert.True(entryTarget is not null);
        DotNetDebugger.Decompiler.Document.SourceLineResolver.SourceLineTarget? loopTarget = null;
        for (var l = 1; l <= 80 && loopTarget is null; l++)
        {
            var t = DotNetDebugger.Decompiler.Document.SourceLineResolver.Resolve(dll, "DebugTarget.cs", l, out _);
            if (t is not null && t.MethodToken == workBagToken && t.IlOffset != entryTarget.Value.IlOffset) loopTarget = t;
        }
        Assert.True(loopTarget is not null, "未找到 WorkBag 循环体源码行");

        await using var mcp = await ConnectAsync();

        // 无会话前置校验
        var noSession = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "a" });
        Assert.Contains("无活动调试会话", noSession.Text());

        // bag 模式：WorkBag(new Bag { A = 7, S = "sx" }, 5)，delay 8s 提供操作窗口
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} bag 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());

        // launch 初始同步点（非断点停点）：求值被前置校验拦截
        var notStopped = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "a" });
        Assert.DoesNotContain("表达式:", notStopped.Text());

        // 先 continue 再设行断点（CI 实录：launch 返回时模块登记可能缺目标模块；delay 8s = 设断点窗口）
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // sourcePath+line 断点：continue 后模块登记是异步的——竞态下 set 返回「断点已登记（延迟绑定）」
        // （非失败，含断点 id，模块加载后自动补绑命中）。故 set 一次取 id → 等「已绑定」终态，不重复 set。
        var setText = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = loopTarget.ActualLine });
        Assert.True(setText.IsError != true, setText.Text());
        var setBpId = ParseBreakpointId(setText.Text());
        await WaitBoundAsync(mcp, setBpId);
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", wait.Text());

        // 字段链 + 字符串索引 + 标量（与 debug_variables 同款展示）
        var field = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.A" });
        Assert.Contains("表达式: b.A = 7（System.Int32）", field.Text());
        var str = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.S" });
        Assert.Contains("= \"sx\"（System.String）", str.Text());
        var charAt = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.S[0]" });
        Assert.Contains("= \"s\"（System.String）", charAt.Text());
        var arg = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "n" });
        Assert.Contains("= 5（System.Int32）", arg.Text());

        // 比较 / 一元 !（True/False 文本）
        var cmp = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "i < n" });
        Assert.Contains("= True（System.Boolean）", cmp.Text());
        var eq = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.S == \"sx\"" });
        Assert.Contains("= True", eq.Text());
        var ne = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.A != 7" });
        Assert.Contains("= False", ne.Text());
        var not = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "!true" });
        Assert.Contains("= False", not.Text()); // 括号不在 v1 文法（spec §4），一元 ! 以字面量/裸路径验证
        var notFalse = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "!false" });
        Assert.Contains("= True", notFalse.Text());
        var paren = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "!(i > 0)" });
        Assert.Contains("不支持", paren.Text());

        // 对象终值：children 一级与 debug_variables 一致
        var whole = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b" });
        Assert.Contains("A = 7", whole.Text());
        Assert.Contains("S = \"sx\"", whole.Text());
        var vars = await CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.Contains("A = 7", vars.Text());

        // 错误语义：缺字段附可用清单 / 未知根附可用变量 / 语法越子集
        var missing = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.Missing" });
        Assert.Contains("无此字段", missing.Text());
        Assert.Contains("A, S", missing.Text());
        var unknown = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "zzz" });
        Assert.Contains("栈顶帧无变量", unknown.Text());
        Assert.Contains("b", unknown.Text());
        var arithmetic = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.A + 1" });
        Assert.Contains("不支持", arithmetic.Text());
        var oob = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.S[9]" });
        Assert.Contains("越界", oob.Text());

        // 数组任意下标：删 WorkBag 行断点 → 锚 WorkScores 入口（scores={3,1,4,1,5}）
        var rm = await CallAsync(mcp, "debug_breakpoint_remove",
            new Dictionary<string, object?> { ["breakpointId"] = setBpId });
        Assert.True(rm.IsError != true, rm.Text());
        var setScores = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{workScoresToken:x8}", ["ilOffset"] = 0 });
        Assert.True(setScores.IsError != true, setScores.Text());
        // module-load 竞态：set 可能返回 pending——等待绑定到「已绑定」（pending 随后自动补绑并命中）
        await WaitBoundAsync(mcp, ParseBreakpointId(setScores.Text()));
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        var waitScores = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", waitScores.Text());
        var deep = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "scores[3]" });
        Assert.Contains("= 1（System.Int32）", deep.Text());
        var arrOob = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "scores[9]" });
        Assert.Contains("越界", arrOob.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task DebugConditionalBreakpoint_ConditionStopsAndFailureFeedback()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workBagToken = ReadMethodToken(dll, "WorkBag");
        Assert.True(workBagToken > 0);

        // 停点坐标：WorkBag 循环体语句行（i/b/n 存活，每轮经过）——与 P6 求值测试同源解析
        var doc = DotNetDebugger.Decompiler.Document.DocumentService.GetTypeDocument(dll, "DebugTarget.Program");
        Assert.True(doc.IsSuccess, doc.Error);
        var bagFirstLine = DotNetDebugger.Decompiler.Document.DocumentService.GetMethodFirstLine(doc, workBagToken);
        var entryTarget = DotNetDebugger.Decompiler.Document.DocumentService.GetBreakpointTargetAtLine(doc, bagFirstLine!.Value);
        Assert.True(entryTarget is not null);
        DotNetDebugger.Decompiler.Document.SourceLineResolver.SourceLineTarget? loopTarget = null;
        for (var l = 1; l <= 80 && loopTarget is null; l++)
        {
            var t = DotNetDebugger.Decompiler.Document.SourceLineResolver.Resolve(dll, "DebugTarget.cs", l, out _);
            if (t is not null && t.MethodToken == workBagToken && t.IlOffset != entryTarget.Value.IlOffset) loopTarget = t;
        }
        Assert.True(loopTarget is not null, "未找到 WorkBag 循环体源码行");

        await using var mcp = await ConnectAsync();
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} bag 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());

        // 1. 语法错当场拒绝（parser 校验，断点不设）
        var badSyntax = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = loopTarget.ActualLine, ["condition"] = "b.A +" });
        Assert.Contains("条件表达式无效", badSyntax.Text());
        Assert.Contains("不支持", badSyntax.Text());
        var emptyList = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("无断点", emptyList.Text());

        // 先 continue 再设行断点（CI 实录：launch 返回时模块登记可能缺目标模块；delay 8s = 设断点窗口）
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // 2. 条件 i == 2：前两轮放行，第 3 轮（i=2）才停——flagship 场景。
        // sourcePath+line 需模块已登记（PDB 解析）——竞态下 set 返回「断点已登记（延迟）」（含 id，
        // 模块加载后自动补绑），故 set 一次取 id → 等「已绑定」终态（delay 窗口内必然早于 WorkBag 进入）
        var setCond = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = loopTarget.ActualLine, ["condition"] = "i == 2" });
        Assert.True(setCond.IsError != true, setCond.Text());
        Assert.Contains("[条件: i == 2", setCond.Text());
        var condBpId = ParseBreakpointId(setCond.Text());
        await WaitBoundAsync(mcp, condBpId);
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", wait.Text());
        var iVar = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "i" });
        Assert.Contains("= 2（System.Int32）", iVar.Text());
        var list = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("条件: i == 2", list.Text());
        Assert.Contains("条件为真 1 次", list.Text());

        // 3. 求值失败反馈：条件引用不存在的字段（语法合法、命中时语义失败）→ 放行至退出，wait 附未通过计数。
        // set 一次取 id → 等「已绑定」（竞态 pending 会补绑；此刻模块已登记故必为「断点已设」立即绑定）
        var setFail = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = loopTarget.ActualLine, ["condition"] = "b.Missing == 1" });
        Assert.True(setFail.IsError != true, setFail.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(setFail.Text()));
        var rm = await CallAsync(mcp, "debug_breakpoint_remove",
            new Dictionary<string, object?> { ["breakpointId"] = condBpId });
        Assert.True(rm.IsError != true, rm.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        var waitExit = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("进程已退出", waitExit.Text());
        // i=3、i=4 两轮失败（断点在停于 i=2 后才设）
        Assert.Contains("条件求值未通过 2 次", waitExit.Text());
        Assert.Contains(".Missing", waitExit.Text());
        Assert.Contains("无此字段", waitExit.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task AsyncStateMachine_MoveNextBreakpoint_ShowsAnnotationAndGuidance()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        // 正路径素材：DebugTarget 的 async 方法 RunAsync（generate-testdata.ps1 内嵌）产编译器状态机 <RunAsync>d__N。
        // 读其 MoveNext 方法 token（状态机类型是嵌套 TypeDef，Name 含 "<RunAsync>"）——对 MoveNext 入口下断点，
        // 命中即物理停在状态机帧内（Task 3 引导/标注要拦截的场景）。
        var moveNextToken = ReadMethodTokenInType(dll, "<RunAsync>", "MoveNext");
        Assert.True(moveNextToken > 0, "DebugTarget 未找到 <RunAsync>d__N.MoveNext——generate-testdata.ps1 需已加 RunAsync 并重跑");
        var runAsyncToken = ReadMethodToken(dll, "RunAsync");
        Assert.True(runAsyncToken > 0, "DebugTarget 未找到 RunAsync 外壳方法");

        await using var mcp = await ConnectAsync();

        // launch async 模式（delay 8s 提供操作窗口）：RunAsync 经 GetAwaiter().GetResult() 同步等待
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} async 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        Assert.Contains("已启动", launch.Text());

        // 先 continue（进入 delay 窗口）再设 MoveNext 断点（CI 实录：launch 返回时模块登记可能缺目标模块）
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        var bp = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{moveNextToken:x8}", ["ilOffset"] = 0 });
        Assert.True(bp.IsError != true, bp.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(bp.Text()));

        // 进程 delay 结束进 RunAsync → await 挂起 → continuation 在 MoveNext → 入口断点命中停住
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("已停下", wait.Text());
        Assert.Contains("breakpoint", wait.Text());

        // C④ 正路径：debug_stack 状态机帧应带 "<RunAsync>" + "(状态机 RunAsync)" 标注
        var stack = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.True(stack.IsError != true, stack.Text());
        Assert.Contains("<RunAsync>", stack.Text());
        Assert.Contains("(状态机 RunAsync)", stack.Text());
        Assert.Contains("MoveNext", stack.Text());

        // B① 正路径：debug_state 停点上下文应带「编译器生成 async 状态机（对应 async 方法 RunAsync）」备注
        var st = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
        Assert.True(st.IsError != true, st.Text());
        // 状态机帧的备注独立于 doc 渲染（StopContextRenderer 对状态机直接返回备注，不尝试渲染 MoveNext）
        Assert.Contains("编译器生成 async 状态机", st.Text());
        Assert.Contains("RunAsync", st.Text());

        // B① 正路径：debug_step 在状态机帧内提交 → 返回附「async 状态机帧」引导
        var step = await CallAsync(mcp, "debug_step", new Dictionary<string, object?> { ["stepType"] = "into" });
        Assert.True(step.IsError != true, step.Text());
        Assert.Contains("已提交 step into", step.Text());
        Assert.Contains("async 状态机帧", step.Text());
        Assert.Contains("RunAsync", step.Text());

        // step 完成后进程会退出（async after 后 Main 结束）——等退出或停点后清理
        var waitStep = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", waitStep.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    /// <summary>读指定名称子串的嵌套类型内某方法 token（状态机等编译器生成类型是嵌套 TypeDef）。</summary>
    private static int ReadMethodTokenInType(string dllPath, string typeNameSubstring, string methodName)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            if (!mr.GetString(td.Name).Contains(typeNameSubstring, StringComparison.Ordinal)) continue;
            foreach (var mh in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mh);
                if (mr.GetString(md.Name) == methodName)
                    return System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }

    private static async Task<CallToolResult> CallAsync(McpClient mcp, string tool, IReadOnlyDictionary<string, object?> args)
        => await mcp.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// 等待断点进入「已绑定」状态（poll debug_breakpoint_list）。背景：launch 后先 continue 再 set 存在
    /// module-load 竞态窗口——set 返回「断点已登记」（pending，模块登记表暂缺该模块），随后 LoadModule/
    /// TrackModule 自动补绑。产品语义 pending→自动补绑→命中是完备闭环，故断言绑定状态而非「set 即已设」。
    /// 模块在 delay 窗口内必然加载，轮询毫秒级返回；上限防模块永不加载（此时断言失败暴露真问题）。
    /// </summary>
    private static async Task WaitBoundAsync(McpClient mcp, int bpId, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            var list = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
            Assert.True(list.IsError != true, list.Text());
            last = list.Text();
            var line = last.Split('\n').FirstOrDefault(l => l.Contains($"id={bpId} "));
            if (line is not null && line.Contains("已绑定")) return;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"断点 {bpId} 在 {timeoutSeconds}s 内未绑定（模块可能未加载或 token 无效）。最近 debug_breakpoint_list：{last}");
    }

    /// <summary>
    /// 反复调 debug_breakpoint_set 直到结果文本含 successMarker（模块/类型未就绪的错误/提示文本会被重试）。
    /// 背景：typeName+line 定位要求模块已登记（已加载模块列表扫描）——刚 continue 的目标其模块登记是异步的，
    /// set 过早会返回「在已加载模块中未找到类型」等提示而非错误；轮询重试等模块就绪（delay 窗口内必然），
    /// 成功即「断点已设」（typeName+line 仅在模块已登记时才能定位成功）。
    /// </summary>
    private static async Task<string> RetrySetUntilAsync(McpClient mcp, IReadOnlyDictionary<string, object?> args, string successMarker, int timeoutSeconds = 15)
    {
        var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
        string last = "";
        while (DateTime.UtcNow < deadline)
        {
            var r = await CallAsync(mcp, "debug_breakpoint_set", args);
            Assert.True(r.IsError != true, r.Text());
            last = r.Text();
            if (last.Contains(successMarker)) return last;
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        Assert.Fail($"debug_breakpoint_set 在 {timeoutSeconds}s 内未返回「{successMarker}」（模块/类型迟迟未就绪？）。最后结果：{last}");
        return last;
    }

    /// <summary>从断点设置结果文本（"断点已设: id=N ..."）解析断点 id。</summary>
    private static int ParseBreakpointId(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"id=(\d+)");
        Assert.True(m.Success, $"结果文本中未找到断点 id: {text}");
        return int.Parse(m.Groups[1].Value);
    }

    private static async Task<McpClient> ConnectAsync()
    {
        var serverDll = Path.Combine(AppContext.BaseDirectory, "DotNetDebuggerMcp.dll");
        var transport = new StdioClientTransport(new()
        {
            Name = "DotNetDebuggerMcp debug tools test client",
            Command = "dotnet",
            Arguments = [serverDll],
        });
        return await McpClient.CreateAsync(transport).WaitAsync(TimeSpan.FromSeconds(30));
    }

    private static int ReadMethodToken(string dllPath, string methodName)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new System.Reflection.PortableExecutable.PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mh);
                if (mr.GetString(md.Name) == methodName)
                    return System.Reflection.Metadata.Ecma335.MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }
}

internal static class CallToolResultExtensions
{
    public static string Text(this CallToolResult result)
        => string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}

