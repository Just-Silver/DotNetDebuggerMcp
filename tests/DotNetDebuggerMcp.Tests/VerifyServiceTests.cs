using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session.Models;
using DotNetDebuggerMcp.Services;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// V1 VerifyService 断言原语纯内存单测（各 assert kind pass/fail + 失败中文理由；不碰真实会话）+
/// ResolveMemberTarget 元数据成员定位（TestSamples.dll 磁盘文件）。真实会话闭环走 Task4 e2e。
/// </summary>
public sealed class VerifyServiceTests
{
    private static readonly DateTimeOffset Now = DateTimeOffset.UtcNow;

    // ---------- breakpointHit ----------

    [Fact]
    public void BreakpointHit_HitMatches_True()
    {
        var step = HitStep(0);
        var ctx = Ctx(b => b.LastStop(BreakpointStop(7)).Bp(0, 7));
        Assert.True(VerifyAssertions.Evaluate(step, ctx).Passed);
    }

    [Fact]
    public void BreakpointHit_WrongStopOrNoStop_FalseWithReason()
    {
        var step = HitStep(0);
        // 最近停点是异常（不是预期断点 7）
        var wrong = Ctx(b => b.LastStop(ExceptionStop("System.InvalidOperationException")).Bp(0, 7));
        var (p1, r1) = VerifyAssertions.Evaluate(step, wrong);
        Assert.False(p1);
        Assert.Contains("断点 7 未命中", r1);

        // 无停点（进程 Running）
        var none = Ctx(b => b.State(DebugSessionState.Running).Bp(0, 7));
        var (p2, r2) = VerifyAssertions.Evaluate(step, none);
        Assert.False(p2);
        Assert.Contains("尚无停点", r2);

        // 引用的断点未设置（场景序越界/未执行）
        var noBp = Ctx(b => b.LastStop(BreakpointStop(7)));
        var (p3, r3) = VerifyAssertions.Evaluate(step, noBp);
        Assert.False(p3);
        Assert.Contains("未设置", r3);
    }

    // ---------- evaluate ----------

    [Fact]
    public void Evaluate_EqualsNumericAndStringRawCompare()
    {
        var ok = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.Evaluate, Path = "n", EqualsText = "5" };
        var ctx = Ctx(b => b.Eval(_ => new DebugEvalResult("5", "System.Int32", DebugEvalKind.Scalar, null, 5)));
        Assert.True(VerifyAssertions.Evaluate(ok, ctx).Passed);

        // 期望错值 → 失败理由含实际值与期望
        var bad = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.Evaluate, Path = "n", EqualsText = "6" };
        var (p2, r2) = VerifyAssertions.Evaluate(bad, ctx);
        Assert.False(p2);
        Assert.Contains("实际值", r2);
        Assert.Contains("\"6\"", r2);
    }

    [Fact]
    public void Evaluate_StringEquals_UsesRawScalarValueNotQuotedDisplay()
    {
        // display 带引号，但字符串字段 equals 按字面值 sx 比较（场景不用写引号）
        var step = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.Evaluate, Path = "b.S", EqualsText = "sx" };
        var ctx = Ctx(b => b.Eval(_ => new DebugEvalResult("\"sx\"", "System.String", DebugEvalKind.Scalar, null, "sx")));
        Assert.True(VerifyAssertions.Evaluate(step, ctx).Passed);
    }

    [Fact]
    public void Evaluate_Contains_SubstringOfDisplayIgnoreCase()
    {
        var step = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.Evaluate, Path = "msg", ContainsText = "manual" };
        var ctx = Ctx(b => b.Eval(_ => new DebugEvalResult("\"switch to MANUAL mode\"", "System.String", DebugEvalKind.Scalar, null, "switch to MANUAL mode")));
        Assert.True(VerifyAssertions.Evaluate(step, ctx).Passed);

        var miss = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.Evaluate, Path = "msg", ContainsText = "NOPE" };
        var (p2, r2) = VerifyAssertions.Evaluate(miss, ctx);
        Assert.False(p2);
        Assert.Contains("不含 contains", r2);
    }

    [Fact]
    public void Evaluate_EvalThrows_FailWithChineseReason()
    {
        var step = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.Evaluate, Path = "zzz", EqualsText = "1" };
        var ctx = Ctx(b => b.Eval(_ => throw new InvalidOperationException("栈顶帧无变量 zzz")));
        var (p, r) = VerifyAssertions.Evaluate(step, ctx);
        Assert.False(p);
        Assert.Contains("求值失败", r);
        Assert.Contains("zzz", r);
    }

    // ---------- output ----------

    [Fact]
    public void Output_ContainsPassFailAndNoCapture()
    {
        var passStep = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.Output, ContainsText = "[DebugTarget] done" };
        var pass = Ctx(b => b.Output(true).OutHit("[DebugTarget] done"));
        Assert.True(VerifyAssertions.Evaluate(passStep, pass).Passed);

        var failStep = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.Output, ContainsText = "never-printed", Stream = "err" };
        var fail = Ctx(b => b.Output(true).OutMiss());
        var (p2, r2) = VerifyAssertions.Evaluate(failStep, fail);
        Assert.False(p2);
        Assert.Contains("目标输出未包含", r2);

        var attach = Ctx(b => b.Output(false));
        var (p3, r3) = VerifyAssertions.Evaluate(passStep, attach);
        Assert.False(p3);
        Assert.Contains("无目标输出捕获", r3);
    }

    // ---------- state / noException ----------

    [Fact]
    public void State_ExpectMatchesOneOf_IgnoreCase()
    {
        var step = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.State, Expect = "Stopped" };
        var ctx = Ctx(b => b.State(DebugSessionState.Stopped));
        Assert.True(VerifyAssertions.Evaluate(step, ctx).Passed);

        var multi = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.State, Expect = "Exited, Stopped" };
        Assert.True(VerifyAssertions.Evaluate(multi, ctx).Passed);

        var miss = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.State, Expect = "Exited" };
        var (p2, r2) = VerifyAssertions.Evaluate(miss, ctx);
        Assert.False(p2);
        Assert.Contains("不在期望集合 Exited", r2);
        Assert.Contains("Stopped", r2);
    }

    [Fact]
    public void NoException_ZeroPasses_HitsFailWithCount()
    {
        var step = new VerifyStep { Kind = VerifyStepKind.Assert, Index = 1, AssertKind = VerifyAssertKind.NoException };
        var clean = Ctx(b => b.ExceptionHits(0));
        Assert.True(VerifyAssertions.Evaluate(step, clean).Passed);

        var dirty = Ctx(b => b.ExceptionHits(2).LastStop(ExceptionStop("System.DivideByZeroException")));
        var (p, r) = VerifyAssertions.Evaluate(step, dirty);
        Assert.False(p);
        Assert.Contains("2 次异常停点", r);
        Assert.Contains("DivideByZeroException", r);
    }

    // ---------- ResolveMemberTarget（纯元数据：TestSamples.dll）----------

    [Fact]
    public void ResolveMemberTarget_SingleMethod_HitsUniqueToken()
    {
        var dll = TestDataPaths.TestSamplesDll;
        // Callee 含 Help() 与 StaticHelp()——"Help" 子串命中两个；"StaticHelp" 唯一命中
        var step = new VerifyStep { Kind = VerifyStepKind.Breakpoint, Index = 1, TypeName = "DotNetDebuggerMcp.Samples.Callee", MemberName = "StaticHelp" };
        var target = VerifyService.ResolveMemberTarget(dll, step);
        Assert.Null(target.ErrorMessage);
        Assert.Equal("DotNetDebuggerMcp.TestSamples.dll", target.ModuleName);
        Assert.True(target.Token > 0 && (target.Token & 0x06000000) == 0x06000000);
        Assert.Contains("Callee.StaticHelp", target.Display);
    }

    [Fact]
    public void ResolveMemberTarget_MultiMethodSubstring_ErrorListsCandidates()
    {
        var dll = TestDataPaths.TestSamplesDll;
        // ManyOverloads 21 个同名 Do 重载（decompile_member 超限素材）——子串命中多个方法须报错引导唯一名
        var step = new VerifyStep { Kind = VerifyStepKind.Breakpoint, Index = 1, TypeName = "DotNetDebuggerMcp.Samples.ManyOverloads", MemberName = "Do" };
        var target = VerifyService.ResolveMemberTarget(dll, step);
        Assert.NotNull(target.ErrorMessage);
        Assert.Contains("匹配 21 个方法", target.ErrorMessage!);
        Assert.Contains("唯一方法名", target.ErrorMessage!);
    }

    [Fact]
    public void ResolveMemberTarget_MissingTypeOrMember_ChineseError()
    {
        var dll = TestDataPaths.TestSamplesDll;
        var noType = new VerifyStep { Kind = VerifyStepKind.Breakpoint, Index = 1, TypeName = "No.Such.Type", MemberName = "M" };
        var r1 = VerifyService.ResolveMemberTarget(dll, noType);
        Assert.NotNull(r1.ErrorMessage);
        Assert.Contains("未找到类型", r1.ErrorMessage!);

        var noMember = new VerifyStep { Kind = VerifyStepKind.Breakpoint, Index = 1, TypeName = "DotNetDebuggerMcp.Samples.Callee", MemberName = "Nope" };
        var r2 = VerifyService.ResolveMemberTarget(dll, noMember);
        Assert.NotNull(r2.ErrorMessage);
        Assert.Contains("未找到名称含", r2.ErrorMessage!);
    }

    // ---------- helpers ----------

    private static VerifyStep HitStep(int index) => new()
    {
        Kind = VerifyStepKind.Assert,
        Index = 1,
        AssertKind = VerifyAssertKind.BreakpointHit,
        BreakpointIndex = index,
    };

    private static VerifyAssertContext Ctx(Action<AssertCtxBuilder> setup)
    {
        var b = new AssertCtxBuilder();
        setup(b);
        return b.Build();
    }

    private static StopContext BreakpointStop(int bpId) =>
        new(Now, DebugEventKind.BreakpointHit, 1, null, $"breakpoint {bpId}", bpId);

    private static StopContext ExceptionStop(string type) =>
        new(Now, DebugEventKind.ExceptionHit, 1, null, $"exception {type}", Message: type);

    /// <summary>断言上下文的可变构造器（对象初始化器无法改 init-only 属性，测试经它组装）。</summary>
    private sealed class AssertCtxBuilder
    {
        private DebugSessionState _state = DebugSessionState.Stopped;
        private StopContext? _lastStop;
        private int _exceptionHits;
        private readonly Dictionary<int, int> _bpIds = new();
        private Func<string, DebugEvalResult> _eval = _ => throw new InvalidOperationException("未配置 evaluate 桩");
        private bool _outputAvailable;
        private Func<string, string, bool> _outputContains = (_, _) => false;

        public AssertCtxBuilder State(DebugSessionState state) { _state = state; return this; }
        public AssertCtxBuilder LastStop(StopContext? stop) { _lastStop = stop; return this; }
        public AssertCtxBuilder ExceptionHits(int count) { _exceptionHits = count; return this; }
        public AssertCtxBuilder Bp(int scenarioIndex, int bpId) { _bpIds[scenarioIndex] = bpId; return this; }
        public AssertCtxBuilder Eval(Func<string, DebugEvalResult> eval) { _eval = eval; return this; }
        public AssertCtxBuilder Output(bool available) { _outputAvailable = available; return this; }
        public AssertCtxBuilder OutHit(string text) { _outputContains = (t, _) => t.Contains(text, StringComparison.OrdinalIgnoreCase); return this; }
        public AssertCtxBuilder OutMiss() { _outputContains = (_, _) => false; return this; }

        public VerifyAssertContext Build() => new()
        {
            State = _state,
            LastStop = _lastStop,
            ExceptionHitCount = _exceptionHits,
            BreakpointIdForIndex = i => _bpIds.TryGetValue(i, out var id) ? id : null,
            EvaluatePath = _eval,
            OutputAvailable = _outputAvailable,
            OutputContains = _outputContains,
        };
    }
}
