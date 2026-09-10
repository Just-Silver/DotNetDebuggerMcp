using DotNetDebuggerMcp.Tests;
using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using System.Diagnostics;
using System.Reflection.Metadata;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// MCP 调试工具端到端测试：真实子进程宿主 + DebugTarget 目标，
/// 验证 debug_launch → breakpoint（token/typeName+line/sourcePath+line/typeName+memberName）→ continue → 命中 → stack/variables 闭环，
/// 以及 debug_run_to（一次性断点命中自动移除 / 退出清理）闭环。
/// </summary>
public sealed class DebugMcpToolsTests
{
    internal static string DebugTargetExe => Path.Combine(
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
        // V4 文案契约：wait 返回须报告「最近停点」现场（agent 据此直接接 stack/variables 观察）
        Assert.Contains("最近停点", wait.Text());
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
        // V4 文案契约：step 提交后返回须含 debug_wait 引导（agent 知道用 debug_wait 等单步停下的新停点）
        Assert.Contains("debug_wait", step.Text());
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

        // $exception 伪根也可直接用于 debug_evaluate（此前仅 debug_variables/debug_object 支持）
        var evalExc = await CallAsync(mcp, "debug_evaluate",
            new Dictionary<string, object?> { ["expression"] = "$exception._message" });
        Assert.True(evalExc.IsError != true, evalExc.Text());
        Assert.Contains("value is zero", evalExc.Text());

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
    public async Task MemberBreakpoint_Set_TypeNameMemberName_SingleHitListingAndNonMethodHints()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        // E 测试素材：Program 的方法 Compute/Work/WorkBag/WorkScores + Bag 的字段（A/S/Password/Token）
        var computeToken = ReadMethodToken(dll, "Compute");
        var workToken = ReadMethodToken(dll, "Work");
        var workBagToken = ReadMethodToken(dll, "WorkBag");
        var workScoresToken = ReadMethodToken(dll, "WorkScores");
        Assert.True(computeToken > 0 && workToken > 0 && workBagToken > 0 && workScoresToken > 0);

        await using var mcp = await ConnectAsync();

        // launch（delay 8s 提供操作窗口）；先 continue 再设断点（CI 实录：launch 返回时模块登记可能缺目标模块）
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // 1. 单方法唯一命中：typeName+memberName（省缺 moduleName——跨已加载模块扫描路径）+ 设断点命中 Compute。
        //    continue 后模块登记是异步的——set 过早会报「在已加载模块中未找到类型」，轮询重试等模块就绪
        var setText = await RetrySetUntilAsync(mcp,
            new Dictionary<string, object?> { ["typeName"] = "DebugTarget.Program", ["memberName"] = "Compute" }, "断点已设");
        Assert.Contains("成员 Compute", setText);
        Assert.Contains($"DebugTarget.dll!0x{computeToken:x8}+0x0", setText); // 位置文案：类型 成员 → 模块!token+0x0
        var bpId = ParseBreakpointId(setText);
        await WaitBoundAsync(mcp, bpId);

        // 2. 多方法匹配：memberName="Work" 子串命中 Work/WorkBag/WorkScores 3 个 → #MEMBER 清单 + 未设断点提示
        var multi = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["typeName"] = "DebugTarget.Program", ["memberName"] = "Work" });
        Assert.True(multi.IsError != true, multi.Text());
        Assert.Contains("匹配 3 个方法成员", multi.Text());
        Assert.Contains("#MEMBER", multi.Text());
        Assert.Contains("Work", multi.Text());
        Assert.Contains("WorkBag", multi.Text());
        Assert.Contains("WorkScores", multi.Text());
        Assert.Contains("未设断点", multi.Text());
        // 清单 token 可闭环 methodToken 重设：与元数据解析的 3 个方法 token 一致
        var listingTokens = System.Text.RegularExpressions.Regex.Matches(multi.Text(), @"""token"":""(0x[0-9a-f]+)""")
            .Select(m => m.Groups[1].Value).OrderBy(t => t, StringComparer.Ordinal).ToArray();
        var expectedTokens = new[] { workToken, workBagToken, workScoresToken }
            .Select(t => $"0x{t:x8}").OrderBy(t => t, StringComparer.Ordinal).ToArray();
        Assert.Equal(expectedTokens, listingTokens);
        // 清单不产生断点：当前只有第 1 步的 Compute 断点
        var listAfterMulti = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("断点列表（1 个）", listAfterMulti.Text());

        // 3. 非方法成员：Bag 字段（0x04）→ 不能设方法断点提示。
        //    DB1 后 Bag 含 A/S/Password/Token——单字母/常见词子串会命中多字段走「均非方法」分支，
        //    取唯一命中的字段 Token 验证单命中字段提示。
        var field = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["typeName"] = "DebugTarget.Bag", ["memberName"] = "Token" });
        Assert.True(field.IsError != true, field.Text());
        Assert.Contains("成员 Token 是字段", field.Text());
        Assert.Contains("不能设方法断点", field.Text());

        // 4. 未找到 + 相近名：Program 内无 "Wrok"，相近成员 Work
        var missing = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["typeName"] = "DebugTarget.Program", ["memberName"] = "Wrok" });
        Assert.True(missing.IsError != true, missing.Text());
        Assert.Contains("未找到名称含 \"Wrok\" 的方法成员", missing.Text());
        Assert.Contains("相近成员", missing.Text());

        // 5. 缺 typeName 校验：memberName 单独给 → 成员级需类型全名
        var noType = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["memberName"] = "Compute" });
        Assert.Contains("请提供 typeName", noType.Text());

        // 6. delay 结束后进 Work → 循环内 Compute 命中停住（第 1 步断点闭环）
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("已停下", wait.Text());
        Assert.Contains("breakpoint", wait.Text());
        var stack = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.Contains($"0x{computeToken:x8}", stack.Text()); // 栈帧含 Compute 方法 token → 命中的正是成员定位的方法

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
    public async Task DebugProcesses_ChildChainAnnotation_And_AttachSwitchesSession()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await ConnectAsync();

        // spawn 模式：Main 自起同 exe 的 sleep 子进程（存活 ~16s：delay 8s + sleep 分支 8s），父等待子退出
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} spawn 0", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        Assert.Contains("已启动", launch.Text());
        var parentMatch = System.Text.RegularExpressions.Regex.Match(launch.Text(), @"目标 pid=(\d+)");
        Assert.True(parentMatch.Success, $"launch 返回未含目标 pid: {launch.Text()}");
        var parentPid = int.Parse(parentMatch.Groups[1].Value);

        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // 轮询 debug_output 直到 spawn 行出现，解析子进程 pid（子进程窗口有限，尽早取到）
        var childPid = 0;
        var deadline = DateTime.UtcNow.AddSeconds(20);
        while (DateTime.UtcNow < deadline)
        {
            var o = await CallAsync(mcp, "debug_output", new Dictionary<string, object?> { ["lines"] = 100 });
            Assert.True(o.IsError != true, o.Text());
            var m = System.Text.RegularExpressions.Regex.Match(o.Text(), @"spawned child pid=(\d+)");
            if (m.Success) { childPid = int.Parse(m.Groups[1].Value); break; }
            await Task.Delay(250, TestContext.Current.CancellationToken);
        }
        Assert.True(childPid > 0, "目标输出未出现 spawned child pid（spawn 分支未生效？）");

        // debug_processes（D2）：当前会话目标的子进程行尾标注 + 返回尾部引导；会话目标行仍标 ← 当前会话
        var proc = await CallAsync(mcp, "debug_processes",
            new Dictionary<string, object?> { ["filter"] = "DebugTarget" });
        Assert.True(proc.IsError != true, proc.Text());
        Assert.True(proc.Text().Contains($"pid={childPid}"), proc.Text());
        Assert.True(proc.Text().Contains($"pid={parentPid}"), proc.Text());
        Assert.True(proc.Text().Contains($"← 会话目标({parentPid}) 的子进程（父 {parentPid}）"), proc.Text());
        Assert.True(proc.Text().Contains("← 当前会话"), proc.Text());
        Assert.True(proc.Text().Contains("发现 1 个会话目标的 .NET 子进程链"), proc.Text());
        Assert.True(proc.Text().Contains("debug_attach <childPid> 单独调试"), proc.Text());
        Assert.True(proc.Text().Contains("子进程输出不在 debug_output 范围"), proc.Text());

        // 停当前会话（父进程继续独立运行等待子退出）→ attach 子进程 → 会话切换
        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
        Assert.Contains("已断开", disc.Text());

        var att = await CallAsync(mcp, "debug_attach", new Dictionary<string, object?> { ["processId"] = childPid });
        Assert.True(att.IsError != true, att.Text());
        Assert.Contains("已附加", att.Text());
        Assert.Contains($"pid={childPid}", att.Text());

        var state = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
        Assert.True(state.IsError != true, state.Text());
        Assert.Contains($"目标 pid: {childPid}", state.Text()); // 会话已切到子进程

        await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
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

    [Fact]
    public async Task RunTo_LaunchContinueHit_AutoRemovesTempBreakpoint()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workToken = ReadMethodToken(dll, "Work");
        Assert.True(workToken > 0);

        // 目标：Work 方法入口的反编译视图首语句行（与 3a 行断点测试同源坐标，保证可定位可命中）
        var doc = DotNetDebugger.Decompiler.Document.DocumentService.GetTypeDocument(dll, "DebugTarget.Program");
        Assert.True(doc.IsSuccess, doc.Error);
        var workFirstLine = DotNetDebugger.Decompiler.Document.DocumentService.GetMethodFirstLine(doc, workToken);
        Assert.True(workFirstLine.GetValueOrDefault() > 0);

        await using var mcp = await ConnectAsync();

        // launch（delay 8s 提供操作窗口）；先 continue 再 run_to（CI 实录：launch 返回时模块登记可能缺目标模块）
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        Assert.Contains("已启动", launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // run_to：typeName+line（moduleName 省缺跨模块解析）。定位需模块已登记——轮询重试等就绪
        // （与 RetrySetUntilAsync 同背景：continue 后模块登记是异步的，过早 run_to 会报「在已加载模块中未找到类型」）
        string runToText = "";
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            runToText = (await CallAsync(mcp, "debug_run_to",
                new Dictionary<string, object?> { ["typeName"] = "DebugTarget.Program", ["line"] = workFirstLine })).Text();
            if (runToText.Contains("已运行到目标")) break;   // 成功
            if (runToText.Contains("未能在")) { await Task.Delay(250, TestContext.Current.CancellationToken); continue; } // 模块未就绪，重试
            break; // 其它终态（超时等）直接断言暴露
        }
        Assert.Contains("已运行到目标", runToText);
        Assert.Contains("自动移除", runToText);

        // 命中现场：进程停在 Work（栈帧含 Work token）——run_to 停点可直接接 debug_stack/debug_variables
        var stack = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.True(stack.IsError != true, stack.Text());
        Assert.Contains($"0x{workToken:x8}", stack.Text());

        // 临时断点已自动移除：debug_breakpoint_list 应为空（无残留）
        var list = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("无断点", list.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task RunTo_Timeout_AutoRemovesTempBreakpointAndHonestMessage()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workToken = ReadMethodToken(dll, "Work");
        Assert.True(workToken > 0);

        // Work 入口反编译视图首语句行（run_to 定位目标）
        var doc = DotNetDebugger.Decompiler.Document.DocumentService.GetTypeDocument(dll, "DebugTarget.Program");
        Assert.True(doc.IsSuccess, doc.Error);
        var workFirstLine = DotNetDebugger.Decompiler.Document.DocumentService.GetMethodFirstLine(doc, workToken);
        Assert.True(workFirstLine.GetValueOrDefault() > 0);

        await using var mcp = await ConnectAsync();

        // launch 长 delay（15s）提供「进程 Running 但暂未到目标」的纯超时窗口：delay 期间 Work 尚未被调用。
        // run_to Work（typeName+line）配 timeoutSeconds=2 → 2s 内不命中 → 走超时分支返回提示。
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 3 15", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        Assert.Contains("已启动", launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // run_to 定位需模块已登记（typeName 跨模块扫描）——轮询重试等模块就绪（delay 15s 窗口内必然）
        string runToText = "";
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (DateTime.UtcNow < deadline)
        {
            runToText = (await CallAsync(mcp, "debug_run_to",
                new Dictionary<string, object?> { ["typeName"] = "DebugTarget.Program", ["line"] = workFirstLine, ["timeoutSeconds"] = 2 })).Text();
            if (runToText.Contains("未命中")) break;   // 模块就绪 + 超时
            if (runToText.Contains("未能在")) { await Task.Delay(250, TestContext.Current.CancellationToken); continue; } // 模块未就绪，重试
            break;
        }
        Assert.Contains("未命中", runToText);
        Assert.Contains("当前 运行中", runToText);

        // 临时断点已自动清理：无残留
        var list = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains("无断点", list.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task RunTo_FromStoppedBreakpoint_ContinuesAndHitsTarget()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workBagToken = ReadMethodToken(dll, "WorkBag");
        var workScoresToken = ReadMethodToken(dll, "WorkScores");
        Assert.True(workBagToken > 0 && workScoresToken > 0);

        await using var mcp = await ConnectAsync();

        // bag 模式（delay 8s 操作窗口）：Main 依次 WorkBag(5轮) → WorkScores → return。
        // 先停 WorkBag 入口（进程 Stopped）→ run_to WorkScores（WorkBag 结束后才执行）→
        // run_to 看到 Stopped 自动 continue 放行 → WorkBag 跑完 → WorkScores 命中。这是 VS run-to-cursor 的对等场景。
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} bag 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // 停 WorkBag 入口（token 断点；竞态下 set 返回 pending——等绑定终态）
        var bpSet = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{workBagToken:x8}", ["ilOffset"] = 0 });
        Assert.True(bpSet.IsError != true, bpSet.Text());
        var workBagBpId = ParseBreakpointId(bpSet.Text());
        await WaitBoundAsync(mcp, workBagBpId);
        var waitBag = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", waitBag.Text());

        // 此刻 Stopped 于 WorkBag → run_to WorkScores（memberName 定位）。run_to 内部应自动 continue 放行。
        var runTo = await CallAsync(mcp, "debug_run_to",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["typeName"] = "DebugTarget.Program", ["memberName"] = "WorkScores", ["timeoutSeconds"] = 25 });
        Assert.True(runTo.IsError != true, runTo.Text());
        Assert.Contains("已运行到目标", runTo.Text());
        Assert.Contains("自动移除", runTo.Text());

        // 停在 WorkScores（栈帧含其 token）
        var stack = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.True(stack.IsError != true, stack.Text());
        Assert.Contains($"0x{workScoresToken:x8}", stack.Text());

        // run_to 临时断点已移除；WorkBag 常驻断点仍在（恰好 1 个，无残留新断点）
        var list = await CallAsync(mcp, "debug_breakpoint_list", new Dictionary<string, object?>());
        Assert.Contains($"id={workBagBpId} ", list.Text());
        Assert.Contains("断点列表（1 个）", list.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task RunTo_AfterUnwaitedStep_ReachesTarget()
    {
        // 回归：单步后立即 run_to（不先 debug_wait 等单步停点）——残留的单步完成事件/事件缓冲滞后
        // 不应让 run_to 误判「停在 STEP_NORMAL，尚未到目标」。run_to 须继续等到真正目标命中。
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workBagToken = ReadMethodToken(dll, "WorkBag");
        var workScoresToken = ReadMethodToken(dll, "WorkScores");
        Assert.True(workBagToken > 0 && workScoresToken > 0);

        await using var mcp = await ConnectAsync();

        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} bag 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        var bpSet = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{workBagToken:x8}", ["ilOffset"] = 0 });
        Assert.True(bpSet.IsError != true, bpSet.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(bpSet.Text()));
        var waitBag = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", waitBag.Text());

        // 单步一步（不等待其停点）→ 立刻 run_to：此刻单步完成事件可能仍在途/未消费
        var step = await CallAsync(mcp, "debug_step", new Dictionary<string, object?> { ["stepType"] = "over" });
        Assert.True(step.IsError != true, step.Text());

        var runTo = await CallAsync(mcp, "debug_run_to",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["typeName"] = "DebugTarget.Program", ["memberName"] = "WorkScores", ["timeoutSeconds"] = 25 });
        Assert.True(runTo.IsError != true, runTo.Text());
        Assert.Contains("已运行到目标", runTo.Text());

        var stack = await CallAsync(mcp, "debug_stack", new Dictionary<string, object?>());
        Assert.Contains($"0x{workScoresToken:x8}", stack.Text());

        await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
    }

    [Fact]
    public async Task DebugTerminate_KillsTargetAndClosesSession()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await ConnectAsync();

        // sleep 30：长活目标，供 terminate 收口
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} sleep 30", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        var pid = int.Parse(System.Text.RegularExpressions.Regex.Match(launch.Text(), @"目标 pid=(\d+)").Groups[1].Value);
        Assert.True(pid > 0, launch.Text());

        var term = await CallAsync(mcp, "debug_terminate", new Dictionary<string, object?> { ["exitCode"] = 7 });
        Assert.True(term.IsError != true, term.Text());
        Assert.Contains("已终止目标进程", term.Text());

        // 目标进程确已结束
        var deadline = DateTime.UtcNow.AddSeconds(8);
        while (DateTime.UtcNow < deadline && System.Diagnostics.Process.GetProcesses().Any(p => p.Id == pid))
            await Task.Delay(100, TestContext.Current.CancellationToken);
        Assert.False(System.Diagnostics.Process.GetProcesses().Any(p => p.Id == pid), "debug_terminate 后目标进程仍在");

        // 会话已关闭：无活动会话
        var state = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
        Assert.Contains("无活动调试会话", state.Text());
    }

    [Fact]
    public async Task SourceLineBreakpoint_NotFound_AggregatesWithoutBlamingOneModule()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await ConnectAsync();
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 1 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // 源文件在任何已加载模块的 PDB 里都不存在 → 应聚合说明「已扫描 N 个模块」，而非把某个随机模块
        // （如最后遍历到的 Wpf.Ui.Yin.dll）的「PDB 中未找到源文件」当成结论（CoreMes 实证的误导）
        var set = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "NoSuchSourceFile_xyz.cs", ["line"] = 1 });
        Assert.True(set.IsError != true, set.Text());
        Assert.Contains("均未包含", set.Text());
        Assert.DoesNotContain("未找到源文件 \"NoSuchSourceFile_xyz.cs\"（模块", set.Text());

        await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
    }

    [Fact]
    public async Task DebugTimeline_LaunchExit_LogStateRowsChronological()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await ConnectAsync();
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 2 0", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // 等进程退出（wait 返回「进程已退出」）
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("进程已退出", wait.Text());

        // 退出标记行由 server 侧 process.Exited 异步追加——轮询 timeline 直到落齐
        string tl = "";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var r = await CallAsync(mcp, "debug_timeline", new Dictionary<string, object?> { ["lines"] = 200 });
            Assert.True(r.IsError != true, r.Text());
            tl = r.Text();
            if (tl.Contains("[进程已退出 exitCode=0]")) break;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        Assert.Contains("[进程已退出 exitCode=0]", tl); // log 行现成标记
        Assert.Contains("时间线 目标 pid=", tl);
        Assert.Contains("共 ", tl);
        Assert.Contains("日志", tl);
        Assert.Contains("显示 ", tl);
        Assert.Contains("log ", tl);    // 目标输出（log）行
        Assert.Contains("state ", tl);  // 会话状态（state）事件行
        AssertChronologicalRows(tl);    // 时间升序

        await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
    }

    [Fact]
    public async Task DebugTimeline_TraceAndException_TrcAndExcRows()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var computeToken = ReadMethodToken(dll, "Compute");
        Assert.True(computeToken > 0);

        await using var mcp = await ConnectAsync();
        // throw 模式 + 8s delay（操作窗口）：Work(1) 后 ThrowIfZero(0) 抛 DivideByZeroException
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 1 throw 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        // trace 断点：Work(1) 内 Compute 命中 1 次（不停，记轨迹入时间线 trc 行）
        var set = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{computeToken:x8}", ["ilOffset"] = 0, ["mode"] = "trace" });
        Assert.True(set.IsError != true, set.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(set.Text()));

        // 异常断点：类型匹配 → ThrowIfZero 的异常命中停住
        var exc = await CallAsync(mcp, "debug_exceptions",
            new Dictionary<string, object?> { ["typeName"] = "DivideByZeroException" });
        Assert.True(exc.IsError != true, exc.Text());

        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("System.DivideByZeroException", wait.Text());

        // 时间线应含 trc（trace 命中）与 exc（异常命中）行
        string tl = "";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var r = await CallAsync(mcp, "debug_timeline", new Dictionary<string, object?> { ["lines"] = 200 });
            Assert.True(r.IsError != true, r.Text());
            tl = r.Text();
            if (tl.Contains(" trc ") && tl.Contains(" exc ")) break;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        Assert.Contains(" trc ", tl);
        Assert.Contains(" exc ", tl);
        Assert.Contains("DivideByZeroException", tl);
        AssertChronologicalRows(tl);

        await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
    }

    [Fact]
    public async Task DebugState_Exited_ShowsExitCode()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await ConnectAsync();
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 1 0", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("进程已退出", wait.Text());

        // exitCode 由 server 侧 process.Exited 捕获——轮询 debug_state 直到 Exited 行显示 exitCode=0
        string st = "";
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var r = await CallAsync(mcp, "debug_state", new Dictionary<string, object?>());
            Assert.True(r.IsError != true, r.Text());
            st = r.Text();
            if (st.Contains("exitCode=0")) break;
            await Task.Delay(200, TestContext.Current.CancellationToken);
        }
        Assert.Contains("（目标已退出：exitCode=0）", st);

        await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
    }

    [Fact]
    public async Task DebugTimeline_NoSessionAndBadKind_ReturnHints()
    {
        await using var mcp = await ConnectAsync();

        // 无会话：提示建立会话
        var noSession = await CallAsync(mcp, "debug_timeline", new Dictionary<string, object?>());
        Assert.True(noSession.IsError != true, noSession.Text());
        Assert.Contains("无活动调试会话", noSession.Text());

        // 建会话后 kind 非法：返回可选清单提示（kind 校验在有会话时执行）
        var exe = DebugTargetExe;
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 2 0", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        var bad = await CallAsync(mcp, "debug_timeline", new Dictionary<string, object?> { ["kind"] = "bogus" });
        Assert.True(bad.IsError != true, bad.Text());
        Assert.Contains("kind 无效", bad.Text());
        Assert.Contains("all/log/act/bp", bad.Text());

        // kind=log 合法：冻结在 Main 前无输出 → 空时间线也正常返回（头部总量 0 行）
        var onlyLog = await CallAsync(mcp, "debug_timeline", new Dictionary<string, object?> { ["kind"] = "log" });
        Assert.True(onlyLog.IsError != true, onlyLog.Text());
        Assert.Contains("时间线 目标 pid=", onlyLog.Text());

        await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
    }

    [Fact]
    public async Task DebugSet_StopSetValue_ReadbackAndBehaviorChange()
    {
        var exe = DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var dll = Path.ChangeExtension(exe, ".dll");
        var runToken = ReadMethodToken(dll, "Run");
        Assert.True(runToken > 0, "未找到 WriteProbe.Run token（请确认 generate-testdata.ps1 已生成 WriteProbe）");

        await using var mcp = await ConnectAsync();

        // probe 模式：delay 5s 提供操作窗口 → WriteProbe.Run(h{N=5,Tag=tagA}, alt, {3,1,4}, 0)
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} probe 5", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());

        // 错误面①：未 Stopped（launch 冻结在 Main 前）调 debug_set → 提示先到停点
        var notStopped = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "h.N", ["value"] = "99" });
        Assert.True(notStopped.IsError != true, notStopped.Text());
        Assert.Contains("未停在断点/异常", notStopped.Text());

        // 先 continue 再设断点（CI 实录：launch 返回时模块登记可能缺目标模块——attach 竞速窗口）
        var cont = await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        Assert.True(cont.IsError != true, cont.Text());

        var bp = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{runToken:x8}", ["ilOffset"] = 0 });
        Assert.True(bp.IsError != true, bp.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(bp.Text()));

        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("已停下", wait.Text());

        // 写前 debug_variables 正常（写不影响读链路的前置确认）
        var varsBefore = await CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.True(varsBefore.IsError != true, varsBefore.Text());
        Assert.Contains("局部变量", varsBefore.Text());

        // 正路径：h.N → 99，返回含 原值/新值 回显
        var set = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "h.N", ["value"] = "99" });
        Assert.True(set.IsError != true, set.Text());
        Assert.Contains("已改", set.Text());
        Assert.Contains("原值 5", set.Text());
        Assert.Contains("新值 99", set.Text());

        // 复核：debug_evaluate h.N 读回 99；debug_variables 停点后仍正常可读
        var eval = await CallAsync(mcp, "debug_evaluate",
            new Dictionary<string, object?> { ["expression"] = "h.N" });
        Assert.True(eval.IsError != true, eval.Text());
        Assert.Contains("99", eval.Text());
        var varsAfter = await CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.True(varsAfter.IsError != true, varsAfter.Text());
        Assert.Contains("局部变量", varsAfter.Text());

        // 错误面②：path 非法（字面量非路径）
        var badPath = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "1", ["value"] = "2" });
        Assert.True(badPath.IsError != true, badPath.Text());
        Assert.Contains("不是有效路径", badPath.Text());

        // 错误面③：value 是路径形态但目标是值类型 → 引擎报「值类型请给字面量」（v1 语义：abc 被解析为对象路径）
        var badValue = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "h.N", ["value"] = "abc" });
        Assert.True(badValue.IsError != true, badValue.Text());
        Assert.Contains("是值类型，请给字面量", badValue.Text());

        // 错误面④：readonly 字段拒绝
        var readOnly = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "h.FixedVal", ["value"] = "1" });
        Assert.True(readOnly.IsError != true, readOnly.Text());
        Assert.Contains("readonly", readOnly.Text());

        // 错误面⑤：decimal 对象字段整值写 v1 降级（如实返回中文，不静默接受后写坏内存）
        var decWrite = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "h.Amount", ["value"] = "12.5" });
        Assert.True(decWrite.IsError != true, decWrite.Text());
        Assert.Contains("System.Decimal", decWrite.Text());
        Assert.Contains("暂不支持写", decWrite.Text());

        // 引用置空 + 重定向（同帧路径文法）：h.Tag=null → h.Tag=alt.Tag
        var nullTag = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "h.Tag", ["value"] = "null" });
        Assert.True(nullTag.IsError != true, nullTag.Text());
        Assert.Contains("新值 null", nullTag.Text());
        var redirect = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "h.Tag", ["value"] = "alt.Tag" });
        Assert.True(redirect.IsError != true, redirect.Text());
        Assert.Contains("tagB", redirect.Text());

        // continue → 行为生效（改 N=99 + Tag=tagB 后 ToString 输出 N=99…tagB…）→ 轮询输出
        var cont2 = await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        Assert.True(cont2.IsError != true, cont2.Text());

        var outputDeadline = DateTime.UtcNow.AddSeconds(15);
        string outText = "";
        while (DateTime.UtcNow < outputDeadline)
        {
            var o = await CallAsync(mcp, "debug_output", new Dictionary<string, object?> { ["lines"] = 100 });
            Assert.True(o.IsError != true, o.Text());
            outText = o.Text();
            if (outText.Contains("N=99")) break;
            await Task.Delay(300, TestContext.Current.CancellationToken);
        }
        Assert.Contains("N=99", outText); // 改值生效到行为（二分定位核心闭环）
        Assert.Contains("tagB", outText); // 重定向后 Tag 渲染为 tagB

        // 目标自然退出，断开清理
        var waitExit = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("进程已退出", waitExit.Text());
        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task DebugObject_DrillTree_DepthRenderScalarErrorAndNotStopped()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var drillToken = ReadMethodToken(dll, "Drill");
        Assert.True(drillToken > 0, "未找到 Drill token（请确认 generate-testdata.ps1 已生成 D1 drill 样本）");

        await using var mcp = await ConnectAsync();

        // drill 模式：delay 5s 提供操作窗口 → Drill(a, {3,1,4}, ghost:null)（a→b→c→a 成环）
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} drill 5", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());

        // 错误面①：未 Stopped（launch 冻结在 Main 前）调 debug_object → 提示先到停点
        var notStopped = await CallAsync(mcp, "debug_object",
            new Dictionary<string, object?> { ["path"] = "root" });
        Assert.True(notStopped.IsError != true, notStopped.Text());
        Assert.Contains("未停在断点/异常", notStopped.Text());

        // 先 continue 再设 Drill 入口断点（CI 实录：launch 返回时模块登记可能缺目标模块）
        var cont = await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        Assert.True(cont.IsError != true, cont.Text());

        var bp = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["moduleName"] = "DebugTarget.dll", ["methodToken"] = $"0x{drillToken:x8}", ["ilOffset"] = 0 });
        Assert.True(bp.IsError != true, bp.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(bp.Text()));

        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.True(wait.IsError != true, wait.Text());
        Assert.Contains("已停下", wait.Text());

        // 正路径：默认 depth=2 → root 对象 children（含 Next）；环被 <cyclic> 截住不死循环
        var d1 = await CallAsync(mcp, "debug_object", new Dictionary<string, object?> { ["path"] = "root" });
        Assert.True(d1.IsError != true, d1.Text());
        Assert.Contains("对象 root", d1.Text());
        Assert.Contains("Next", d1.Text());

        // 正路径：depth=3 → Name/Value 递归两层展开，正常返回
        var d3 = await CallAsync(mcp, "debug_object", new Dictionary<string, object?> { ["path"] = "root", ["depth"] = 3 });
        Assert.True(d3.IsError != true, d3.Text());
        Assert.Contains("对象 root", d3.Text());
        Assert.Contains("Name", d3.Text());
        Assert.Contains("Value", d3.Text());

        // 正路径：数组目标 → 返回头应标「数组」（而非「对象」）
        var arr = await CallAsync(mcp, "debug_object", new Dictionary<string, object?> { ["path"] = "arr", ["depth"] = 2 });
        Assert.True(arr.IsError != true, arr.Text());
        Assert.Contains("数组 arr", arr.Text());

        // 错误面②：标量终值（root.Value 是 int）→ 中文「不是对象/数组」
        var scalar = await CallAsync(mcp, "debug_object", new Dictionary<string, object?> { ["path"] = "root.Value" });
        Assert.True(scalar.IsError != true, scalar.Text());
        Assert.Contains("不是对象/数组", scalar.Text());

        // 错误面③：path 非法（非路径形态）→ 中文提示
        var badPath = await CallAsync(mcp, "debug_object", new Dictionary<string, object?> { ["path"] = "1" });
        Assert.True(badPath.IsError != true, badPath.Text());
        Assert.Contains("不是有效路径", badPath.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task DebugVariablesEvaluate_SensitiveFieldsRedacted_PlaceholderAndNotice()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workBagToken = ReadMethodToken(dll, "WorkBag");
        Assert.True(workBagToken > 0);

        // 停点坐标：WorkBag 循环体语句行（b/n/i 全存活）
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

        const string notice = "疑似凭据已脱敏——请用该出口提供的类型/长度/null 等非敏感信息判断，勿读原始值";
        const string ph = "[已脱敏:疑似凭据]";

        await using var mcp = await ConnectAsync();

        // bag 模式：WorkBag(new Bag { A = 7, S = "sx", Password = "hunter2", Token = "Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc" }, 5)
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} bag 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        var set = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = loopTarget.ActualLine });
        Assert.True(set.IsError != true, set.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(set.Text()));
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", wait.Text());

        // debug_variables：Password/Token 占位符 + 顶部计数提示；普通字段原样；原始凭据不出现在输出
        var vars = await CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.True(vars.IsError != true, vars.Text());
        Assert.Contains("局部变量/参数", vars.Text());
        Assert.Contains($"2 个值{notice}", vars.Text());
        Assert.Contains($"Password = {ph}", vars.Text());
        Assert.Contains($"Token = {ph}", vars.Text());
        Assert.Contains("A = 7", vars.Text());
        Assert.Contains("S = \"sx\"", vars.Text());
        Assert.DoesNotContain("hunter2", vars.Text());
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", vars.Text());

        // debug_evaluate 表达式级：b.Password / b.Token 末段名敏感 → 整值占位符 + 行内单次提示（类型保留供判读）
        var pw = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.Password" });
        Assert.True(pw.IsError != true, pw.Text());
        Assert.Contains($"b.Password = {ph}", pw.Text());
        Assert.Contains("（System.String）", pw.Text());
        Assert.Contains($"（{notice}）", pw.Text());
        Assert.DoesNotContain("hunter2", pw.Text());

        var tk = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.Token" });
        Assert.True(tk.IsError != true, tk.Text());
        Assert.Contains($"b.Token = {ph}", tk.Text());
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", tk.Text());
        Assert.DoesNotContain("Bearer ", tk.Text());

        // 普通字段/表达式不受影响
        var a = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.A" });
        Assert.Contains("表达式: b.A = 7（System.Int32）", a.Text());
        Assert.DoesNotContain(notice, a.Text());
        var s = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b.S" });
        Assert.Contains("= \"sx\"（System.String）", s.Text());

        // 整对象求值：children 逐字段名脱敏（Password/Token 行占位符，父对象行不敏感不动）
        var whole = await CallAsync(mcp, "debug_evaluate", new Dictionary<string, object?> { ["expression"] = "b" });
        Assert.True(whole.IsError != true, whole.Text());
        Assert.Contains($"Password = {ph}", whole.Text());
        Assert.Contains($"Token = {ph}", whole.Text());
        Assert.Contains("A = 7", whole.Text());
        Assert.Contains("S = \"sx\"", whole.Text());
        Assert.DoesNotContain("hunter2", whole.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task DebugSet_SensitivePath_RedactsOldAndNewDisplay()
    {
        var exe = DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workBagToken = ReadMethodToken(dll, "WorkBag");
        Assert.True(workBagToken > 0);

        // 停点坐标：WorkBag 循环体语句行（b/n/i 全存活）——与 DB1 脱敏 e2e 同源
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

        const string ph = "[已脱敏:疑似凭据]";
        const string notice = "疑似凭据已脱敏——请用该出口提供的类型/长度/null 等非敏感信息判断，勿读原始值";

        await using var mcp = await ConnectAsync();

        // bag 模式：WorkBag(new Bag { A = 7, S = "sx", Password = "hunter2", Token = "Bearer eyJ…" }, 5)
        var launch = await CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} bag 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        var set = await CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = loopTarget.ActualLine });
        Assert.True(set.IsError != true, set.Text());
        await WaitBoundAsync(mcp, ParseBreakpointId(set.Text()));
        var wait = await CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", wait.Text());

        // 敏感路径改写（引用重定向到 b.S：新值内容平凡 "sx"，仍应按路径名脱敏）→ 原/新明文均不得出现在回显
        var sensitive = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "b.Password", ["value"] = "b.S" });
        Assert.True(sensitive.IsError != true, sensitive.Text());
        Assert.Contains("已改", sensitive.Text());
        Assert.Contains($"原值 {ph}", sensitive.Text());
        Assert.Contains($"新值 {ph}", sensitive.Text());
        Assert.Contains(notice, sensitive.Text());
        Assert.DoesNotContain("hunter2", sensitive.Text()); // 旧值明文不泄露
        Assert.DoesNotContain("sx", sensitive.Text());       // 新值明文（重定向源值）不泄露

        // 非敏感路径输出照旧、不脱敏
        var normal = await CallAsync(mcp, "debug_set",
            new Dictionary<string, object?> { ["path"] = "b.A", ["value"] = "42" });
        Assert.True(normal.IsError != true, normal.Text());
        Assert.Contains("原值 7", normal.Text());
        Assert.Contains("新值 42", normal.Text());
        Assert.DoesNotContain(ph, normal.Text());

        var disc = await CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    /// <summary>时间线文本的每一行行首时间戳须单调不减（格式 [HH:mm:ss.fff] tag 固定宽）。</summary>
    private static void AssertChronologicalRows(string text)
    {
        var rows = text.Split('\n')
            .Select(l => l.TrimEnd('\r'))
            .Where(l => System.Text.RegularExpressions.Regex.IsMatch(l, @"^\[\d{2}:\d{2}:\d{2}\.\d{3}\]\s+[a-z]+\s+"))
            .ToArray();
        Assert.True(rows.Length > 0, $"时间线无行可校验:\n{text}");
        for (var i = 1; i < rows.Length; i++)
        {
            var t0 = rows[i - 1].Substring(1, 12); // HH:mm:ss.fff（固定宽度，字典序=时间序）
            var t1 = rows[i].Substring(1, 12);
            Assert.True(string.CompareOrdinal(t1, t0) >= 0, $"时间线行未按时间升序:\n{rows[i - 1]}\n{rows[i]}");
        }
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

    internal static async Task<CallToolResult> CallAsync(McpClient mcp, string tool, IReadOnlyDictionary<string, object?> args)
        => await mcp.CallToolAsync(tool, args, cancellationToken: TestContext.Current.CancellationToken);

    /// <summary>
    /// 等待断点进入「已绑定」状态（poll debug_breakpoint_list）。背景：launch 后先 continue 再 set 存在
    /// module-load 竞态窗口——set 返回「断点已登记」（pending，模块登记表暂缺该模块），随后 LoadModule/
    /// TrackModule 自动补绑。产品语义 pending→自动补绑→命中是完备闭环，故断言绑定状态而非「set 即已设」。
    /// 模块在 delay 窗口内必然加载，轮询毫秒级返回；上限防模块永不加载（此时断言失败暴露真问题）。
    /// </summary>
    internal static async Task WaitBoundAsync(McpClient mcp, int bpId, int timeoutSeconds = 15)
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
    internal static int ParseBreakpointId(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, @"id=(\d+)");
        Assert.True(m.Success, $"结果文本中未找到断点 id: {text}");
        return int.Parse(m.Groups[1].Value);
    }

    internal static async Task<McpClient> ConnectAsync()
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

    internal static int ReadMethodToken(string dllPath, string methodName)
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

/// <summary>
/// DB2 debug_variables names 按名白名单端到端：bag 模式停 WorkBag 循环体——白名单命中（忽略大小写）/
/// 未知名零值反馈/超 50 拒绝/空=全量回归/与 DB1 脱敏叠加交叉；异常停点 names="$exception" 只返异常节。
/// </summary>
public sealed class DebugVariablesNamesWhitelistTests
{
    private const string RedactPh = "[已脱敏:疑似凭据]";

    [Fact]
    public async Task DebugVariablesNames_BagFrame_WhitelistHitCaseUnknownRejectRedactionCross()
    {
        var exe = DebugMcpToolsTests.DebugTargetExe;
        var dll = Path.ChangeExtension(exe, ".dll");
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        var workBagToken = DebugMcpToolsTests.ReadMethodToken(dll, "WorkBag");
        Assert.True(workBagToken > 0);

        // 停点坐标：WorkBag 循环体语句行（b/n/i 全存活；入口 IL0 局部未初始化不可靠）
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

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();

        // bag 模式：WorkBag(new Bag { A=7, S="sx", Password="hunter2", Token="Bearer eyJ…" }, 5)，delay 8s 操作窗口
        var launch = await DebugMcpToolsTests.CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} bag 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        await DebugMcpToolsTests.CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());

        var set = await DebugMcpToolsTests.CallAsync(mcp, "debug_breakpoint_set",
            new Dictionary<string, object?> { ["sourcePath"] = "DebugTarget.cs", ["line"] = loopTarget.ActualLine });
        Assert.True(set.IsError != true, set.Text());
        await DebugMcpToolsTests.WaitBoundAsync(mcp, DebugMcpToolsTests.ParseBreakpointId(set.Text()));
        var wait = await DebugMcpToolsTests.CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", wait.Text());

        // 1. 空 names = 全量回归（头不带白名单；locals 与 arguments 节都在）
        var full = await DebugMcpToolsTests.CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.True(full.IsError != true, full.Text());
        Assert.Contains("局部变量/参数", full.Text());
        Assert.DoesNotContain("白名单", full.Text());
        Assert.Contains("[locals]", full.Text());
        Assert.Contains("i = ", full.Text());
        Assert.Contains("[arguments]", full.Text());
        Assert.Contains("n = 5", full.Text());

        // 2. names="b,n"：只渲染命中项（arguments 节），未请求的 local i 不出现
        var two = await DebugMcpToolsTests.CallAsync(mcp, "debug_variables",
            new Dictionary<string, object?> { ["names"] = "b,n" });
        Assert.True(two.IsError != true, two.Text());
        Assert.Contains("白名单 2 项 → 命中 2 项", two.Text());
        Assert.Contains("[arguments]", two.Text());
        Assert.DoesNotContain("[locals]", two.Text());
        Assert.Contains("n = 5", two.Text());
        Assert.DoesNotContain("i = ", two.Text());
        Assert.DoesNotContain("hunter2", two.Text());

        // 3. 忽略大小写：names="B,N" 同命中
        var ci = await DebugMcpToolsTests.CallAsync(mcp, "debug_variables",
            new Dictionary<string, object?> { ["names"] = "B,N" });
        Assert.True(ci.IsError != true, ci.Text());
        Assert.Contains("白名单 2 项 → 命中 2 项", ci.Text());
        Assert.Contains("n = 5", ci.Text());

        // 4. 未知名零值反馈：names="b,NoSuch" → b 命中 + 未找到 NoSuch + 可用名清单（不含值）
        var unknown = await DebugMcpToolsTests.CallAsync(mcp, "debug_variables",
            new Dictionary<string, object?> { ["names"] = "b,NoSuch" });
        Assert.True(unknown.IsError != true, unknown.Text());
        Assert.Contains("白名单 2 项 → 命中 1 项", unknown.Text());
        Assert.Contains("未找到：NoSuch", unknown.Text());
        var avail = ExtractAvailableNames(unknown.Text());
        Assert.NotNull(avail);
        Assert.Contains("b", avail!);
        Assert.Contains("i", avail!);
        Assert.Contains("n", avail!);
        Assert.DoesNotContain("NoSuch =", unknown.Text());

        // 5. 白名单项数 >50 → 中文拒绝（防整帧当白名单）
        var many = string.Join(",", Enumerable.Range(1, 51).Select(i => "v" + i));
        var over = await DebugMcpToolsTests.CallAsync(mcp, "debug_variables",
            new Dictionary<string, object?> { ["names"] = many });
        Assert.True(over.IsError != true, over.Text());
        Assert.Contains("names 项数 51 超上限 50", over.Text());
        Assert.DoesNotContain("命中", over.Text());

        // 6. 白名单 + DB1 脱敏叠加：names="b" 命中的对象 children 敏感字段仍被脱敏（交叉断言）
        var one = await DebugMcpToolsTests.CallAsync(mcp, "debug_variables",
            new Dictionary<string, object?> { ["names"] = "b" });
        Assert.True(one.IsError != true, one.Text());
        Assert.Contains("白名单 1 项 → 命中 1 项", one.Text());
        Assert.Contains($"Password = {RedactPh}", one.Text());
        Assert.Contains($"Token = {RedactPh}", one.Text());
        Assert.DoesNotContain("hunter2", one.Text());
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", one.Text());
        Assert.DoesNotContain("n = 5", one.Text()); // 白名单窄化：未请求 n 不得出现

        var disc = await DebugMcpToolsTests.CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public async Task DebugVariablesNames_ExceptionStop_PseudoVariableScopeOnly()
    {
        var exe = DebugMcpToolsTests.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();

        // throw 模式 + 8s delay：Work 后 ThrowIfZero 抛 DivideByZeroException("value is zero")
        var launch = await DebugMcpToolsTests.CallAsync(mcp, "debug_launch",
            new Dictionary<string, object?> { ["commandLine"] = $"{exe} 1 throw 8", ["timeoutSeconds"] = 20 });
        Assert.True(launch.IsError != true, launch.Text());
        var set = await DebugMcpToolsTests.CallAsync(mcp, "debug_exceptions",
            new Dictionary<string, object?> { ["typeName"] = "DivideByZeroException" });
        Assert.True(set.IsError != true, set.Text());
        await DebugMcpToolsTests.CallAsync(mcp, "debug_continue", new Dictionary<string, object?>());
        var wait = await DebugMcpToolsTests.CallAsync(mcp, "debug_wait",
            new Dictionary<string, object?> { ["waitSeconds"] = 20, ["outputLines"] = 0, ["contextLines"] = 0 });
        Assert.Contains("已停下", wait.Text());

        // names="$exception"：只返回异常节（$exception 伪变量命中），locals/arguments 节不渲染
        var exc = await DebugMcpToolsTests.CallAsync(mcp, "debug_variables",
            new Dictionary<string, object?> { ["names"] = "$exception" });
        Assert.True(exc.IsError != true, exc.Text());
        Assert.Contains("白名单 1 项 → 命中 1 项", exc.Text());
        Assert.Contains("[exception]", exc.Text());
        Assert.Contains("$exception", exc.Text());
        Assert.Contains("System.DivideByZeroException", exc.Text());
        Assert.Contains("value is zero", exc.Text());
        Assert.DoesNotContain("[locals]", exc.Text());
        Assert.DoesNotContain("[arguments]", exc.Text());

        // 对照：空 names 全量仍含 exception 节头（白名单不破坏全量路径）
        var full = await DebugMcpToolsTests.CallAsync(mcp, "debug_variables", new Dictionary<string, object?>());
        Assert.True(full.IsError != true, full.Text());
        Assert.Contains("[exception]", full.Text());
        Assert.DoesNotContain("白名单", full.Text());

        var disc = await DebugMcpToolsTests.CallAsync(mcp, "debug_disconnect", new Dictionary<string, object?>());
        Assert.True(disc.IsError != true, disc.Text());
    }

    [Fact]
    public void DebugVariablesNames_Helpers_SplitNames_NormalizeDedupAndBlank()
    {
        Assert.Null(DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.SplitNames(""));
        Assert.Null(DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.SplitNames("   "));
        Assert.Null(DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.SplitNames(null!));
        var parsed = DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.SplitNames(" b,  b ,,n ,NoSuch,n ,");
        Assert.NotNull(parsed);
        Assert.Equal(new[] { "b", "n", "NoSuch" }, parsed!); // trim/去空/忽略大小写去重，保留首现大小写
    }

    [Fact]
    public void DebugVariablesNames_Helpers_MatchName_ByNameSlotAndException_IgnoreCase()
    {
        var vName = new DotNetDebugger.Engine.Models.DebugVariable("Alpha", 0, DotNetDebugger.Engine.Models.DebugValue.Scalar("1"), false);
        var vSlot = new DotNetDebugger.Engine.Models.DebugVariable(null, 3, DotNetDebugger.Engine.Models.DebugValue.Scalar("x"), false);
        var vExc = new DotNetDebugger.Engine.Models.DebugVariable("$exception", -1, DotNetDebugger.Engine.Models.DebugValue.Scalar("e"), false);
        Assert.True(DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.MatchName(vName, new List<string> { "alpha" }));
        Assert.True(DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.MatchName(vSlot, new List<string> { "slot3" }));
        Assert.True(DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.MatchName(vExc, new List<string> { "$EXCEPTION" }));
        Assert.False(DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.MatchName(vName, new List<string> { "alphi" }));
        Assert.False(DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.MatchName(vSlot, new List<string> { "slot03" })); // slotN 精确匹配无前导零
    }

    [Fact]
    public void DebugVariablesNames_Helpers_Render_CrossScopeSameDisplayName_And_UnknownFeedbackZeroValue()
    {
        // locals 与 arguments 的 slot0 均为无名（Slot 索引从 0 各自编号）→ 展示名相同 → 同名跨作用域两节都返回
        var vars = new Dictionary<string, IReadOnlyList<DotNetDebugger.Engine.Models.DebugVariable>>
        {
            ["locals"] = new List<DotNetDebugger.Engine.Models.DebugVariable>
            {
                new(null, 0, DotNetDebugger.Engine.Models.DebugValue.Scalar("loc"), false),
            },
            ["arguments"] = new List<DotNetDebugger.Engine.Models.DebugVariable>
            {
                new(null, 0, DotNetDebugger.Engine.Models.DebugValue.Scalar("arg"), true),
                new("n", 1, DotNetDebugger.Engine.Models.DebugValue.Scalar("5"), true),
            },
        };

        var (lines, hits, redacted) = DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.BuildVariablesLines(vars, new List<string> { "slot0" });
        Assert.Equal(2, hits);
        Assert.Equal(0, redacted);
        Assert.Contains("[locals]", lines);
        Assert.Contains(lines, l => l.Contains("slot0 = loc"));
        Assert.Contains("[arguments]", lines);
        Assert.Contains(lines, l => l.Contains("slot0 = arg"));
        Assert.DoesNotContain("n = 5", string.Join('\n', lines)); // 未请求名不渲染

        // 未知名 → 零值反馈：附当前帧可用名、不含任何值；命中项仍正常渲染并计数
        var (lines2, hits2, _) = DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.BuildVariablesLines(vars, new List<string> { "slot0", "zzz" });
        Assert.Equal(2, hits2);
        Assert.Contains(lines2, l => l.Contains("slot0 = arg"));
        var fb = lines2.First(l => l.StartsWith("未找到："));
        Assert.StartsWith("未找到：zzz（", fb);
        Assert.Contains("可用名：slot0、n——", fb);
        Assert.DoesNotContain("=", fb);   // 未知名行不含值

        // 全量模式（requested null）回归：节头 + 全量渲染，hits 恒 0（DebugVariables 头部不用）
        var (linesFull, hitsFull, _) = DotNetDebuggerMcp.Tools.Debugger.DebugInspectTool.BuildVariablesLines(vars, null);
        Assert.Equal(0, hitsFull);
        Assert.Contains("n = 5", string.Join('\n', linesFull));
        Assert.Equal(5, linesFull.Count); // 2 节头 + 3 变量行
    }

    /// <summary>从未知名反馈文本提取「当前帧可用名：…——」中的名清单（分号分隔）；未匹配返回 null。</summary>
    private static string[]? ExtractAvailableNames(string text)
    {
        var m = System.Text.RegularExpressions.Regex.Match(text, "可用名：([^（—]*?)——");
        if (!m.Success) return null;
        return m.Groups[1].Value.Split('、', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    }
}

internal static class CallToolResultExtensions
{
    public static string Text(this CallToolResult result)
        => string.Join(Environment.NewLine, result.Content.OfType<TextContentBlock>().Select(b => b.Text));
}

