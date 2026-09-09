using DotNetDebugger.Decompiler.Metadata;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session;
using DotNetDebugger.Session.Models;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using System.Text;

namespace DotNetDebuggerMcp.Services;

/// <summary>
/// V1 debug_verify 执行器：把场景翻译成同进程直调 Session 的步骤序列（不走 MCP 往返）——
/// 解析场景 → build（可选，产物自动拿取 + commandLine 文件名校验）→ launch（可复现快照）→
/// 逐步骤执行（breakpoint 成员级定位→Session.SetBreakpointAsync；continue+WaitForStop；assert 判定）→
/// fail-fast → 结束 Close 断开 → PASS/FAIL 汇总。每步写 AgentActionLog。ui/set 预留步骤明确报依赖未就绪。
/// Engine/Session 零改动。
/// </summary>
internal static class VerifyService
{
    /// <summary>launch 等待 CLR 启动秒数（复验目标是整程序，宽容度高于手动 debug_launch 默认 30s）。</summary>
    private const int LaunchTimeoutSeconds = 60;

    /// <summary>build 分支产物路径含空格时的 v1 边界提示（Session 按空格切分 commandLine，exe 路径暂不支持空格）。</summary>
    private const string SpacePathBoundaryHint =
        "编译产物路径含空格，v1 启动器按空格切分命令暂不支持——请把工程放在不含空格的目录，或场景不写 build 改为 commandLine 直接启动。";

    /// <summary>执行场景并返回结果文本（PASS/FAIL 或中文错误提示；不抛异常）。</summary>
    public static async Task<string> VerifyAsync(string scenarioPath, CancellationToken ct)
    {
        VerifyScenario scenario;
        try
        {
            scenario = VerifyScenario.Parse(scenarioPath);
        }
        catch (VerifyFormatException ex)
        {
            return ex.Message;
        }

        var manager = DebugSessionService.Manager;
        var name = scenario.Name.Length > 0 ? scenario.Name : Path.GetFileNameWithoutExtension(scenarioPath);
        var header = $"debug_verify「{name}」";

        try
        {
            // ① 解析启动计划（build 分支重编译 + 产物自动定位 + 文件名校验；失败即停，绝不启动旧产物）
            var plan = await ResolveLaunchPlanAsync(scenario, ct);
            if (plan.Error is not null)
                return $"{header}：FAIL —— {plan.Error}";

            // ② 无会话则 launch；有会话先 Close 再重开（复现快照语义，目标独立运行）
            if (manager.Active is not null)
                await manager.CloseAsync(ct);
            ActiveDebugSession active;
            try
            {
                active = await manager.LaunchAndAttachAsync(plan.LaunchCommand!, LaunchTimeoutSeconds,
                    scenario.Target.WorkingDirectory, scenario.Target.Environment, ct);
            }
            catch (Exception ex)
            {
                return $"{header}：FAIL —— 启动失败：{ex.Message}";
            }
            manager.Actions.Log("debug_verify", $"launch {plan.LaunchCommand}", "ok");

            // ③ 逐步骤执行（fail-fast）
            var result = await ExecuteStepsAsync(scenario, active, plan.ManagedModulePath, header, scenarioPath, ct);

            // ④ 结束 Close（目标继续独立运行）
            try { await manager.CloseAsync(ct); } catch { }
            return result;
        }
        catch (OperationCanceledException)
        {
            try { await manager.CloseAsync(ct); } catch { }
            return $"{header}：FAIL —— 执行已取消。";
        }
        catch (Exception ex)
        {
            try { await manager.CloseAsync(ct); } catch { }
            return $"{header}：FAIL —— 执行异常：{ex.Message}";
        }
    }

    /// <summary>解析启动计划：build 分支（编译→产物→文件名校验→可启动 exe + 模块 dll）；无 build 走 commandLine 原样。</summary>
    private static async Task<LaunchPlan> ResolveLaunchPlanAsync(VerifyScenario scenario, CancellationToken ct)
    {
        if (scenario.Build is { } build)
        {
            // 1) 编译（只重编场景指定工程，默认输出；失败摘要即停——绝不启动旧产物）
            var outcome = await VerifyBuildRunner.RunAsync(build.Project, build.Configuration, build.TimeoutSeconds, ct);
            if (!outcome.Ok)
                return LaunchPlan.Fail($"重编译失败：{outcome.Summary}");
            managerLog($"build {build.Project} -c {build.Configuration}", "ok");

            // 2) 产物自动定位（SDK 现算，agent 不写路径）
            var targetPath = await VerifyBuildRunner.GetTargetPathAsync(build.Project, build.Configuration, ct);
            if (targetPath is null)
                return LaunchPlan.Fail("无法解析编译产物路径（build.project / configuration 名错？或编译未产出 TargetPath）。");
            if (!File.Exists(targetPath))
                return LaunchPlan.Fail($"编译产物不存在：{targetPath}。");

            // 3) commandLine 首段文件名与产物同名校验（忽略扩展名/大小写，agent 写 exe 文件名+参数）
            var firstToken = scenario.Target.CommandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)[0];
            if (!VerifyBuildRunner.ArtifactFileMatches(firstToken, targetPath))
                return LaunchPlan.Fail($"commandLine 首段 \"{firstToken}\" 与编译产物 {Path.GetFileName(targetPath)} 不一致——请写工程入口文件名（如 {Path.GetFileNameWithoutExtension(targetPath)}.exe）+ 参数。");

            var launchExe = VerifyBuildRunner.ResolveLaunchExecutable(targetPath);
            if (!File.Exists(launchExe))
                return LaunchPlan.Fail($"无法确定可启动产物（{launchExe} 不存在；console 工程应产出 apphost exe，纯库不可启动）。");
            if (launchExe.Contains(' '))
                return LaunchPlan.Fail(SpacePathBoundaryHint + $"(产物路径：{launchExe})");

            return LaunchPlan.Ok(launchExe + " " + RestArgs(scenario.Target.CommandLine), targetPath);
        }

        // 无 build：commandLine 完整路径 / 相对 CWD（维持 debug_launch 现语义，不搜 PATH）
        var parts = scenario.Target.CommandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (parts.Length == 0)
            return LaunchPlan.Fail("target.commandLine 为空。");
        var cmdExe = parts[0];
        if (!File.Exists(cmdExe) && !File.Exists(Path.GetFullPath(cmdExe)))
            return LaunchPlan.Fail($"目标文件不存在：{cmdExe}（相对 server 当前工作目录解析）。用 debug_launch 手动启动试试或检查路径。");

        // 断点成员级定位的目标模块：apphost exe 的同名 dll 优先（管理程序集），否则 exe 本身
        var fullExe = File.Exists(Path.GetFullPath(cmdExe)) ? Path.GetFullPath(cmdExe) : cmdExe;
        var dll = Path.ChangeExtension(fullExe, ".dll");
        return LaunchPlan.Ok(scenario.Target.CommandLine, File.Exists(dll) ? dll : fullExe);
    }

    /// <summary>取 commandLine 首段之后的参数段（首段已校验/替换为产物路径）。</summary>
    private static string RestArgs(string commandLine)
    {
        var parts = commandLine.Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        return parts.Length > 1 ? string.Join(' ', parts[1..]) : "";
    }

    private static void managerLog(string detail, string outcome)
        => DebugSessionService.Manager.Actions.Log("debug_verify", detail, outcome);

    /// <summary>逐步执行场景（fail-fast）。返回 PASS/FAIL 结果文本。</summary>
    private static async Task<string> ExecuteStepsAsync(VerifyScenario scenario, ActiveDebugSession active,
        string? managedModulePath, string header, string scenarioPath, CancellationToken ct)
    {
        var bpOrder = new List<DebugBreakpoint>();          // 场景内 breakpoint 步骤出现序（assert 引用 0-based 序）
        var exceptionHitCount = 0;
        var assertCount = 0;
        var assertsPassed = 0;
        var stepsExecuted = 0;

        foreach (var step in scenario.Steps)
        {
            switch (step.Kind)
            {
                case VerifyStepKind.Breakpoint:
                {
                    var resolve = ResolveMemberTarget(managedModulePath!, step);
                    if (resolve.ErrorMessage is not null)
                        return FailAt(header, step, resolve.ErrorMessage, active);
                    try
                    {
                        var bp = await active.Session.SetBreakpointAsync(resolve.ModuleName, resolve.Token, 0,
                            step.Hit, DebugBreakpointMode.Stop, condition: null, ct);
                        bpOrder.Add(bp);
                        managerLog($"第{step.Index}步 breakpoint {step.TypeName}.{step.MemberName}",
                            bp.IsBound ? $"id={bp.Id}" : $"id={bp.Id}（待绑定，模块加载后自动绑）");
                        stepsExecuted++;
                    }
                    catch (Exception ex)
                    {
                        return FailAt(header, step, $"设置断点失败：{ex.Message}", active);
                    }
                    break;
                }
                case VerifyStepKind.Continue:
                {
                    var state = active.Buffer.CurrentState;
                    if (state != DebugSessionState.Exited && state != DebugSessionState.Detached)
                    {
                        StopContext? stop = null;
                        try
                        {
                            if (state == DebugSessionState.Stopped)
                                await active.Session.ContinueAsync(ct);
                            stop = await active.Buffer.WaitForStopAsync(TimeSpan.FromSeconds(step.WaitSeconds), ct);
                        }
                        catch (Exception ex)
                        {
                            return FailAt(header, step, $"continue 失败：{ex.Message}", active);
                        }
                        var after = active.Buffer.CurrentState;
                        if (stop is null && after == DebugSessionState.Exited)
                        {
                            managerLog($"第{step.Index}步 continue（等停 {step.WaitSeconds}s）", "进程已退出");
                            stepsExecuted++;
                            break;
                        }
                        if (stop is null)
                            return FailAt(header, step,
                                $"等待 {step.WaitSeconds} 秒未停（当前状态 {StateText(after)}）——断点未命中或代码路径未走到。", active);
                        if (stop.Kind == DebugEventKind.ExceptionHit) exceptionHitCount++;
                        managerLog($"第{step.Index}步 continue（等停 {step.WaitSeconds}s）", $"stop={DescribeStop(stop)}");
                        stepsExecuted++;
                    }
                    else
                    {
                        managerLog($"第{step.Index}步 continue（等停 {step.WaitSeconds}s）", "进程已退出，跳过");
                        stepsExecuted++;
                    }
                    break;
                }
                case VerifyStepKind.Assert:
                {
                    assertCount++;
                    var ctx = BuildAssertContext(active, bpOrder, exceptionHitCount);
                    var (passed, reason) = VerifyAssertions.Evaluate(step, ctx);
                    managerLog($"第{step.Index}步 assert {DescribeAssert(step)}", passed ? "PASS" : $"FAIL {reason}");
                    if (!passed)
                        return FailAt(header, step, reason, active);
                    assertsPassed++;
                    stepsExecuted++;
                    break;
                }
                case VerifyStepKind.Ui:
                    return FailAt(header, step, $"步骤类型依赖未就绪（{step.Requires}）：ui.* 触发需 U1 能力，本版本未实现该步骤。", active);
                case VerifyStepKind.Set:
                    return FailAt(header, step, $"步骤类型依赖未就绪（{step.Requires}）：现场改值需 W1 能力，本版本未实现该步骤。", active);
            }
        }

        var assertSummary = assertCount > 0 ? $"断言 {assertsPassed}/{assertCount} 通过" : "无断言";
        return $"{header}：PASS —— 步骤 {stepsExecuted}/{scenario.Steps.Count} 完成，{assertSummary}。场景文件：{scenarioPath}";
    }

    private static string FailAt(string header, VerifyStep step, string reason, ActiveDebugSession active)
    {
        var sb = new StringBuilder();
        sb.Append($"{header}：FAIL —— 第 {step.Index} 步 {DescribeStep(step)}：{reason}");
        var tail = OutputTailText(active, 15);
        if (tail is not null)
            sb.Append(Environment.NewLine).Append(tail);
        return sb.ToString();
    }

    private static string? OutputTailText(ActiveDebugSession active, int maxLines)
    {
        if (active.Output is null) return null;
        var lines = active.Output.Tail(maxLines);
        if (lines.Count == 0) return null;
        var sb = new StringBuilder($"目标输出（最近 {lines.Count} 行）:");
        foreach (var line in lines)
            sb.Append('\n').Append($"[{line.Timestamp:HH:mm:ss.fff} {(line.Stream == DotNetDebugger.Session.ProcessOutputStream.Stderr ? "err" : "out")}] {line.Text}");
        return sb.ToString();
    }

    private static string DescribeStep(VerifyStep step) => step.Kind switch
    {
        VerifyStepKind.Breakpoint => $"breakpoint {step.TypeName}.{step.MemberName}",
        VerifyStepKind.Continue => $"continue（等停 {step.WaitSeconds}s）",
        VerifyStepKind.Assert => $"assert {DescribeAssert(step)}",
        VerifyStepKind.Ui => $"ui 步骤（{step.Requires}）",
        VerifyStepKind.Set => $"set 步骤（{step.Requires}）",
        _ => step.Kind.ToString(),
    };

    private static string DescribeAssert(VerifyStep step) => step.AssertKind switch
    {
        VerifyAssertKind.BreakpointHit => $"breakpointHit[{step.BreakpointIndex}]",
        VerifyAssertKind.Evaluate => step.ContainsText.Length > 0
            ? $"evaluate \"{step.Path}\" contains \"{step.ContainsText}\""
            : $"evaluate \"{step.Path}\" equals \"{step.EqualsText}\"",
        VerifyAssertKind.Output => $"output contains \"{step.ContainsText}\"{(step.Stream.Length > 0 ? $" stream={step.Stream}" : "")}",
        VerifyAssertKind.State => $"state {step.Expect}",
        VerifyAssertKind.NoException => "noException",
        _ => step.AssertKind.ToString(),
    };

    /// <summary>构建当前断言上下文（快照当前状态 + 闭包接 session 求值/输出）。</summary>
    private static VerifyAssertContext BuildAssertContext(ActiveDebugSession active,
        IReadOnlyList<DebugBreakpoint> bpOrder, int exceptionHitCount)
    {
        var tid = active.Buffer.StoppedThreadId;
        return new VerifyAssertContext
        {
            State = active.Buffer.CurrentState,
            LastStop = active.Buffer.LastStop,
            ExceptionHitCount = exceptionHitCount,
            BreakpointIdForIndex = index => index >= 0 && index < bpOrder.Count ? bpOrder[index].Id : null,
            EvaluatePath = path => ExpressionEvaluator.EvaluateAsync(active.Session, tid, path).GetAwaiter().GetResult(),
            OutputAvailable = active.Output is not null,
            OutputContains = (text, stream) =>
            {
                var lines = active.Output?.Tail(ProcessOutputCapture.MaxLines);
                if (lines is null) return false;
                return lines.Any(l =>
                    (stream.Length == 0
                        || (stream.Equals("err", StringComparison.OrdinalIgnoreCase) ? l.Stream == ProcessOutputStream.Stderr
                            : l.Stream == ProcessOutputStream.Stdout))
                    && l.Text.Contains(text, StringComparison.OrdinalIgnoreCase));
            },
        };
    }

    internal static string StateText(DebugSessionState state) => state switch
    {
        DebugSessionState.None => "未就绪 (None)",
        DebugSessionState.Launching => "启动中 (Launching)",
        DebugSessionState.Attaching => "附加中 (Attaching)",
        DebugSessionState.Running => "运行中 (Running)",
        DebugSessionState.Stopped => "已停止 (Stopped)",
        DebugSessionState.Exited => "已退出 (Exited)",
        DebugSessionState.Detached => "已断开 (Detached)",
        _ => state.ToString(),
    };

    internal static string DescribeStop(StopContext? stop)
    {
        if (stop is null) return "（无停点）";
        var text = $"{stop.Kind} thread={stop.ThreadId} top={stop.TopFrame} reason={stop.Reason}";
        if (stop.Message is not null) text += $" message=\"{stop.Message}\"";
        return text;
    }

    /// <summary>
    /// 成员级断点定位（Services 自含，不引用 Tools 层）：在目标模块（管理 dll/exe）磁盘文件上经
    /// MetadataNaming.FindTypes 判歧义/未找到 → MemberResolver.FindMembers 类型内按方法名子串定位 →
    /// 唯一 0x06 方法 token。返回模块名（运行时模块短名）与方法 token。
    /// </summary>
    internal static MemberTarget ResolveMemberTarget(string managedModulePath, VerifyStep step)
    {
        if (!File.Exists(managedModulePath))
            return MemberTarget.Error($"目标模块文件不存在：{managedModulePath}。");
        try
        {
            using var fs = File.OpenRead(managedModulePath);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var typeHits = MetadataNaming.FindTypes(reader, step.TypeName)
                .Select(h => MetadataNaming.FullName(reader, reader.GetTypeDefinition(h)))
                .Distinct().ToList();
            if (typeHits.Count == 0)
                return MemberTarget.Error(MetadataNaming.BuildNotFoundMessage(reader, step.TypeName));
            if (typeHits.Count > 1)
                return MemberTarget.Error($"类型 {step.TypeName} 在模块中有歧义，匹配：{string.Join("、", typeHits)}。请提供更精确的全名。");
            var fullName = typeHits[0];

            var search = MemberResolver.FindMembers(managedModulePath, fullName, step.MemberName);
            var methodCandidates = search.Matches.Where(m => m.Token.StartsWith("0x06", StringComparison.OrdinalIgnoreCase)).ToList();
            if (methodCandidates.Count == 0)
            {
                if (search.Matches.Count == 1)
                {
                    var only = search.Matches[0];
                    var kind = only.Token.StartsWith("0x04") ? "字段" : only.Token.StartsWith("0x17") ? "属性" : only.Token.StartsWith("0x14") ? "事件" : "成员";
                    return MemberTarget.Error($"成员 {only.Name} 是{kind}，不能设方法断点——场景 breakpoint.memberName 请写普通方法名。");
                }
                if (search.Matches.Count > 1)
                    return MemberTarget.Error($"类型 {fullName} 中名称含 \"{step.MemberName}\" 的 {search.Matches.Count} 个成员均非方法。");
                var message = $"类型 {fullName} 中未找到名称含 \"{step.MemberName}\" 的方法成员";
                if (search.SimilarNames.Count > 0) message += $"（相近成员：{string.Join("、", search.SimilarNames)}）";
                return MemberTarget.Error(message);
            }
            if (methodCandidates.Count > 1)
                return MemberTarget.Error($"类型 {fullName} 中名称含 \"{step.MemberName}\" 匹配 {methodCandidates.Count} 个方法（{string.Join("、", methodCandidates.Select(m => m.Name))}）——场景 breakpoint.memberName 请写唯一方法名。");
            if (!TryParseToken(methodCandidates[0].Token, out var token) || token == 0)
                return MemberTarget.Error($"无法解析方法 token：{methodCandidates[0].Token}。");
            return MemberTarget.Ok(Path.GetFileName(managedModulePath), token, fullName + "." + methodCandidates[0].Name);
        }
        catch (Exception ex)
        {
            return MemberTarget.Error($"按成员定位失败：{ex.Message}");
        }
    }

    private static bool TryParseToken(string text, out int token)
    {
        token = 0;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out token);
    }
}

/// <summary>启动计划（build/无 build 统一产物）；Error 非空=校验失败（中文，直接回给 agent）。</summary>
internal sealed class LaunchPlan
{
    public string? Error { get; init; }
    public string? LaunchCommand { get; init; }

    /// <summary>断点成员级定位的目标模块文件（console 工程=管理 dll；纯库=自身）。</summary>
    public string? ManagedModulePath { get; init; }

    public static LaunchPlan Ok(string launchCommand, string managedModulePath)
        => new() { LaunchCommand = launchCommand, ManagedModulePath = managedModulePath };
    public static LaunchPlan Fail(string error) => new() { Error = error };
}

/// <summary>成员级断点定位结果（模块短名 + 方法 token；ErrorMessage 为中文失败原因）。</summary>
internal readonly record struct MemberTarget(string ModuleName, int Token, string? Display, string? ErrorMessage)
{
    public static MemberTarget Ok(string module, int token, string display) => new(module, token, display, null);
    public static MemberTarget Error(string error) => new("", 0, null, error);
}

/// <summary>断言判定的上下文（纯内存快照 + 闭包；单测可整桩构造，真实会话由 VerifyService 组装）。</summary>
internal sealed class VerifyAssertContext
{
    public required DebugSessionState State { get; init; }
    public StopContext? LastStop { get; init; }
    public required int ExceptionHitCount { get; init; }

    /// <summary>场景内 breakpoint 步骤序（0-based）→ 实际断点 id；未设置返回 null。</summary>
    public required Func<int, int?> BreakpointIdForIndex { get; init; }

    /// <summary>表达式路径 → 求值结果（失败抛中文异常）。</summary>
    public required Func<string, DebugEvalResult> EvaluatePath { get; init; }

    /// <summary>目标输出捕获是否可用（launch 会话 true；attach 会话 false）。</summary>
    public required bool OutputAvailable { get; init; }

    /// <summary>输出缓冲是否含子串（忽略大小写；stream 空=全部流，否则只查 out/err）。</summary>
    public required Func<string, string, bool> OutputContains { get; init; }
}

/// <summary>断言原语判定（纯函数：输入上下文、返回是否通过 + 失败中文理由）。</summary>
internal static class VerifyAssertions
{
    public static (bool Passed, string Reason) Evaluate(VerifyStep step, VerifyAssertContext ctx)
    {
        switch (step.AssertKind)
        {
            case VerifyAssertKind.BreakpointHit:
            {
                var bpId = ctx.BreakpointIdForIndex(step.BreakpointIndex);
                if (bpId is null)
                    return (false, $"引用的断点步骤未设置（场景内第 {step.BreakpointIndex} 个 breakpoint 未执行？）。");
                var stop = ctx.LastStop;
                if (stop is null)
                    return (false, $"断点 {bpId} 未命中：尚无停点（当前状态 {VerifyService.StateText(ctx.State)}）。");
                if (stop.Kind == DebugEventKind.BreakpointHit && stop.BreakpointId == bpId)
                    return (true, "");
                return (false, $"断点 {bpId} 未命中：最近停点为 {VerifyService.DescribeStop(stop)}。");
            }
            case VerifyAssertKind.Evaluate:
            {
                DebugEvalResult eval;
                try { eval = ctx.EvaluatePath(step.Path); }
                catch (Exception ex) { return (false, $"表达式 \"{step.Path}\" 求值失败：{ex.Message}"); }

                var raw = eval.ScalarValue is string str ? str : null;
                var display = eval.Display ?? eval.ScalarValue?.ToString() ?? "";
                if (step.EqualsText.Length > 0)
                {
                    var actual = raw ?? display;
                    if (string.Equals(actual, step.EqualsText, StringComparison.Ordinal))
                        return (true, "");
                    return (false, $"表达式 \"{step.Path}\" 实际值 {Quote(actual)}，期望 equals {Quote(step.EqualsText)}。");
                }
                // contains：对 evaluate 展示文本做子串（字符串值的展示带引号也能命中内容子串）
                var haystack = eval.Display ?? "";
                if (haystack.Contains(step.ContainsText, StringComparison.OrdinalIgnoreCase))
                    return (true, "");
                return (false, $"表达式 \"{step.Path}\" 求值展示 {Quote(haystack)} 不含 contains {Quote(step.ContainsText)}。");
            }
            case VerifyAssertKind.Output:
            {
                if (!ctx.OutputAvailable)
                    return (false, "output 断言不可用：当前会话无目标输出捕获（仅 launch 启动的会话有）。");
                if (ctx.OutputContains(step.ContainsText, step.Stream))
                    return (true, "");
                return (false, $"目标输出未包含 \"{step.ContainsText}\"{(step.Stream.Length > 0 ? $"（stream={step.Stream}）" : "")}。");
            }
            case VerifyAssertKind.State:
            {
                var expected = step.Expect.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var actualName = ctx.State.ToString();
                if (expected.Any(e => string.Equals(e, actualName, StringComparison.OrdinalIgnoreCase)))
                    return (true, "");
                return (false, $"当前会话状态 {VerifyService.StateText(ctx.State)} 不在期望集合 {step.Expect}。");
            }
            case VerifyAssertKind.NoException:
                if (ctx.ExceptionHitCount == 0)
                    return (true, "");
                return (false, $"场景期间发生 {ctx.ExceptionHitCount} 次异常停点（最近：{VerifyService.DescribeStop(ctx.LastStop)}）。");
            default:
                return (false, $"未知断言类型 {step.AssertKind}。");
        }
    }

    private static string Quote(string text) => $"\"{text}\"";
}

