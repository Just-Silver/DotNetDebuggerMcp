using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session.Models;
using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Tools.Debugger;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// DB1 出口挂接的内存级测试（不启真实进程）：RenderVariable 递归脱敏（children 逐字段名判定）+
/// RenderTraceBlock 轨迹段脱敏 + 段头「（含已脱敏值）」标注。顶层计数/单次提示见 e2e（DebugMcpToolsTests）。
/// </summary>
public sealed class DebugRedactionOutletTests
{
    private const string Ph = SensitiveValueRedactor.Placeholder;

    private static DebugVariable Bag(string password = "\"hunter2\"", string token = "\"Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc\"") => new("b", 0,
        DebugValue.Object("4 字段", new[]
        {
            new DebugVariable("A", -1, DebugValue.Scalar("7"), IsArgument: false),
            new DebugVariable("S", -1, DebugValue.Scalar("\"sx\""), IsArgument: false),
            new DebugVariable("Password", -1, DebugValue.Scalar(password), IsArgument: false),
            new DebugVariable("Token", -1, DebugValue.Scalar(token), IsArgument: false),
        }), IsArgument: true);

    [Fact]
    public void RenderVariable_ChildrenRedactByOwnName_ParentKept()
    {
        var text = DebugInspectTool.RenderVariable(Bag(), depth: 1);

        Assert.Contains("b = 4 字段", text);
        Assert.Contains("A = 7", text);
        Assert.Contains("S = \"sx\"", text);
        // 子段按各自字段名脱敏（Password/Token 敏感名 → 占位符）
        Assert.Contains($"Password = {Ph}", text);
        Assert.Contains($"Token = {Ph}", text);
        Assert.DoesNotContain("hunter2", text);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", text);
    }

    [Fact]
    public void RenderVariable_ContentShapedValue_RedactedOnPlainName()
    {
        var v = new DebugVariable("x", -1, DebugValue.Scalar("\"Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc\""), IsArgument: false);
        var text = DebugInspectTool.RenderVariable(v, depth: 1);
        Assert.Contains($"x = {Ph}", text);
    }

    [Fact]
    public void RenderVariable_BenignField_Unchanged()
    {
        var v = new DebugVariable("tokenCount", -1, DebugValue.Scalar("\"3\""), IsArgument: false);
        var text = DebugInspectTool.RenderVariable(v, depth: 1);
        Assert.Contains("tokenCount = \"3\"", text);
        Assert.DoesNotContain(Ph, text);
    }

    [Fact]
    public void RenderTraceBlock_RedactsVariableLines_AndMarksHeader()
    {
        var traces = new[]
        {
            new TraceHitPayload(1, 1, DateTimeOffset.Now, null, new[]
            {
                new TraceVariable("arguments", "apiKey", 0, "\"sk-abcdefghijklmnop12345678\""),
                new TraceVariable("locals", "count", 1, "\"3\""),
            }),
        };
        var text = DebugSessionTool.RenderTraceBlock(traces, dropped: 0);

        Assert.Contains("trace 轨迹（1 条，旧→新）（含已脱敏值）:", text);
        Assert.Contains($"[arguments] apiKey = {Ph}", text);
        Assert.Contains("[locals] count = \"3\"", text); // 平凡/非敏感原样
        Assert.DoesNotContain("sk-abcdefghijklmnop12345678", text);
    }

    [Fact]
    public void RenderTraceBlock_NoSensitive_NoHeaderMarker()
    {
        var traces = new[]
        {
            new TraceHitPayload(2, 1, DateTimeOffset.Now, null, new[]
            {
                new TraceVariable("arguments", "n", 0, "5"),
                new TraceVariable("locals", "i", 1, "2"),
            }),
        };
        var text = DebugSessionTool.RenderTraceBlock(traces, dropped: 1);

        Assert.Contains("trace 轨迹（1 条，旧→新；因环形上限已丢弃最早 1 条）:", text);
        Assert.DoesNotContain("（含已脱敏值）", text);
        Assert.Contains("[arguments] n = 5", text);
    }

    // ---- P0-1：停点 message 按内容形态脱敏（name=null，仅内容判定）----

    [Fact]
    public void StopText_ContentShapedMessage_Redacted()
    {
        var stop = new StopContext(DateTimeOffset.Now, DebugEventKind.ExceptionHit, 7, null,
            "exception SqlException", null, "Login failed: Password=hunter2;Server=db");
        var text = DebugSessionTool.StopText(stop);

        Assert.Contains($"message=\"{Ph}\"", text);
        Assert.DoesNotContain("hunter2", text);
    }

    [Fact]
    public void StopText_BearerMessage_Redacted()
    {
        var stop = new StopContext(DateTimeOffset.Now, DebugEventKind.ExceptionHit, 7, null,
            "exception HttpRequestException", null, "Request failed with Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc");
        var text = DebugSessionTool.StopText(stop);

        Assert.Contains($"message=\"{Ph}\"", text);
        Assert.DoesNotContain("eyJhbGciOiJIUzI1NiJ9", text);
    }

    [Fact]
    public void StopText_BenignMessage_Unchanged()
    {
        var stop = new StopContext(DateTimeOffset.Now, DebugEventKind.ExceptionHit, 7, null,
            "exception NullReferenceException", null, "Object reference not set to an instance of an object.");
        var text = DebugSessionTool.StopText(stop);

        Assert.Contains("message=\"Object reference not set to an instance of an object.\"", text);
        Assert.DoesNotContain(Ph, text);
    }

    [Fact]
    public void StopText_Null_Placeholder()
    {
        Assert.Equal("（无）", DebugSessionTool.StopText(null));
    }

    // ---- P3-1：单次递归后计数与实际占位符数恒一致（全量 + 白名单两模式）----

    [Fact]
    public void BuildVariablesLines_RedactedCount_EqualsPlaceholderOccurrences()
    {
        var vars = new Dictionary<string, IReadOnlyList<DebugVariable>>
        {
            ["locals"] = new[]
            {
                Bag(), // Password + Token 命中；A/S 不命中
                new DebugVariable("x", -1, DebugValue.Scalar("\"Bearer eyJhbGciOiJIUzI1NiJ9.e30.abc\""), IsArgument: false),
                new DebugVariable("n", -1, DebugValue.Scalar("5"), IsArgument: false),
            },
        };

        var (lines, hits, redacted) = DebugInspectTool.BuildVariablesLines(vars, requested: null);
        Assert.Equal(0, hits);
        Assert.Equal(3, redacted); // b 的 Password + Token，x（内容形态）
        Assert.Equal(redacted, lines.Sum(l => CountOccurrences(l, Ph)));

        var (wlLines, wlHits, wlRedacted) = DebugInspectTool.BuildVariablesLines(vars, new List<string> { "b" });
        Assert.Equal(1, wlHits);
        Assert.Equal(2, wlRedacted); // 只渲染可见命中：b 的 Password + Token
        Assert.Equal(wlRedacted, wlLines.Sum(l => CountOccurrences(l, Ph)));
    }

    private static int CountOccurrences(string text, string needle)
    {
        var count = 0;
        for (var i = text.IndexOf(needle, StringComparison.Ordinal); i >= 0;
             i = text.IndexOf(needle, i + needle.Length, StringComparison.Ordinal))
            count++;
        return count;
    }
}

