using DotNetDebuggerMcp.Services;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// V1 debug_verify 场景 JSON 模型与解析（VerifyScenario.Parse）：
/// 合法场景（含 build、全部步骤类型、assert 引用断点 index、evaluate equals/contains 互斥、ui/set 预留标记）
/// 字段断言；非法输入（文件不存在/JSON 语法错/未知步骤 kind/未知 assert kind/evaluate 缺或双给 equals·contains/
/// output 缺 contains/断点 step 缺 memberName+typeName/breakpointIndex 越界）中文错误含具体字段。
/// 纯内存解析（JsonDocument），不触碰静态单例——不挂 AppServices 串行集合。
/// </summary>
public sealed class VerifyScenarioTests
{
    [Fact]
    public void Parse_ValidFullScenario_AllFieldsAndRequiresReservedSteps()
    {
        var path = WriteScenario(
            """
            {
              "name": "切换手自动复验",
              "target": {
                "commandLine": "D:\\proj\\CoreMes\\CoreMes.exe 5",
                "workingDirectory": "D:\\proj\\CoreMes\\bin",
                "environment": "ASPNETCORE_ENVIRONMENT=Development;A=B"
              },
              "build": { "project": "D:\\proj\\CoreMes\\CoreMes.csproj", "configuration": "Release", "timeoutSeconds": 180 },
              "steps": [
                { "breakpoint": { "typeName": "CoreMes.Core.ApplicationContext", "memberName": "SwitchState", "hit": 2 } },
                { "continue": { "waitSeconds": 15 } },
                { "assert": { "kind": "breakpointHit", "breakpointIndex": 0 } },
                { "assert": { "kind": "evaluate", "path": "ApplicationContext.State", "equals": "自动" } },
                { "assert": { "kind": "evaluate", "path": "ApplicationContext.Reason", "contains": "manual" } },
                { "assert": { "kind": "output", "contains": "切换完成", "stream": "err" } },
                { "assert": { "kind": "state", "expect": "Stopped" } },
                { "assert": { "kind": "noException" } },
                { "uiAction": { "process": "CoreMes", "verb": "invoke", "index": 5, "name": "手动", "type": "Button" } },
                { "uiAssert": { "process": "CoreMes", "what": "name", "name": "自动", "contains": "自动" } },
                { "ui": { "tool": "ui_invoke", "args": { "index": 5 } } },
                { "set": { "path": "x", "value": "1" } }
              ]
            }
            """);

        var scenario = VerifyScenario.Parse(path);

        Assert.Equal("切换手自动复验", scenario.Name);
        Assert.Equal("D:\\proj\\CoreMes\\CoreMes.exe 5", scenario.Target.CommandLine);
        Assert.Equal("D:\\proj\\CoreMes\\bin", scenario.Target.WorkingDirectory);
        Assert.Equal("ASPNETCORE_ENVIRONMENT=Development;A=B", scenario.Target.Environment);
        Assert.NotNull(scenario.Build);
        Assert.Equal("D:\\proj\\CoreMes\\CoreMes.csproj", scenario.Build!.Project);
        Assert.Equal("Release", scenario.Build.Configuration);
        Assert.Equal(180, scenario.Build.TimeoutSeconds);
        Assert.Equal(12, scenario.Steps.Count);

        var bp = scenario.Steps[0];
        Assert.Equal(VerifyStepKind.Breakpoint, bp.Kind);
        Assert.Equal("CoreMes.Core.ApplicationContext", bp.TypeName);
        Assert.Equal("SwitchState", bp.MemberName);
        Assert.Equal(2, bp.Hit);

        var cont = scenario.Steps[1];
        Assert.Equal(VerifyStepKind.Continue, cont.Kind);
        Assert.Equal(15, cont.WaitSeconds);

        var hit = scenario.Steps[2];
        Assert.Equal(VerifyStepKind.Assert, hit.Kind);
        Assert.Equal(VerifyAssertKind.BreakpointHit, hit.AssertKind);
        Assert.Equal(0, hit.BreakpointIndex);

        var eq = scenario.Steps[3];
        Assert.Equal(VerifyAssertKind.Evaluate, eq.AssertKind);
        Assert.Equal("ApplicationContext.State", eq.Path);
        Assert.Equal("自动", eq.EqualsText);
        Assert.Equal("", eq.ContainsText);

        var ct = scenario.Steps[4];
        Assert.Equal(VerifyAssertKind.Evaluate, ct.AssertKind);
        Assert.Equal("manual", ct.ContainsText);
        Assert.Equal("", ct.EqualsText);

        var outStep = scenario.Steps[5];
        Assert.Equal(VerifyAssertKind.Output, outStep.AssertKind);
        Assert.Equal("切换完成", outStep.ContainsText);
        Assert.Equal("err", outStep.Stream);

        Assert.Equal(VerifyAssertKind.State, scenario.Steps[6].AssertKind);
        Assert.Equal("Stopped", scenario.Steps[6].Expect);
        Assert.Equal(VerifyAssertKind.NoException, scenario.Steps[7].AssertKind);

        var action = scenario.Steps[8];
        Assert.Equal(VerifyStepKind.UiAction, action.Kind);
        Assert.Equal("CoreMes", action.Process);
        Assert.Equal("invoke", action.Verb);
        Assert.Equal(5, action.UiIndex);
        Assert.Equal("手动", action.UiName);
        Assert.Equal("Button", action.UiType);

        var uiAssert = scenario.Steps[9];
        Assert.Equal(VerifyStepKind.UiAssert, uiAssert.Kind);
        Assert.Equal("CoreMes", uiAssert.Process);
        Assert.Equal("name", uiAssert.What);
        Assert.Equal("自动", uiAssert.ContainsText);
        Assert.Equal("", uiAssert.EqualsText);

        Assert.Equal(VerifyStepKind.Ui, scenario.Steps[10].Kind);
        Assert.Equal("U1", scenario.Steps[10].Requires);
        Assert.Equal(VerifyStepKind.Set, scenario.Steps[11].Kind);
        Assert.Equal("W1", scenario.Steps[11].Requires);
    }

    [Fact]
    public void Parse_MinimalScenario_DefaultsApplied()
    {
        // 无 name/build；breakpoint 缺省 hit=1；continue 缺省 waitSeconds；assert.output 缺省 stream
        var path = WriteScenario(
            """
            {
              "target": { "commandLine": "DebugTarget.exe 3" },
              "steps": [
                { "breakpoint": { "typeName": "DebugTarget.Program", "memberName": "Work" } },
                { "continue": {} },
                { "assert": { "kind": "breakpointHit", "breakpointIndex": 0 } },
                { "assert": { "kind": "output", "contains": "[DebugTarget] done" } }
              ]
            }
            """);

        var s = VerifyScenario.Parse(path);

        Assert.Equal("", s.Name);
        Assert.Null(s.Build);
        Assert.Equal("DebugTarget.exe 3", s.Target.CommandLine);
        Assert.Equal(1, s.Steps[0].Hit);
        Assert.Equal(10, s.Steps[1].WaitSeconds);
        Assert.Equal("", s.Steps[3].Stream);
        Assert.Equal(1, s.BreakpointCount);
    }

    [Fact]
    public void Parse_TwoBreakpoints_AssertReferencesSecondByIndexOne()
    {
        var path = WriteScenario(
            """
            {
              "name": "双断点引用",
              "target": { "commandLine": "App.exe" },
              "steps": [
                { "breakpoint": { "typeName": "App.Program", "memberName": "Work" } },
                { "breakpoint": { "typeName": "App.Program", "memberName": "WorkBag" } },
                { "assert": { "kind": "breakpointHit", "breakpointIndex": 1 } }
              ]
            }
            """);
        var s = VerifyScenario.Parse(path);
        Assert.Equal(2, s.BreakpointCount);
        Assert.Equal(1, s.Steps[2].BreakpointIndex);
    }

    [Fact]
    public void Parse_UiActionAndUiAssert_DefaultsAndValidation()
    {
        // 缺省：index=-1、lines=0、其余空
        var ok = VerifyScenario.Parse(WriteScenario(
            """{ "target": { "commandLine": "x" }, "steps": [ { "uiAction": { "process": "App", "verb": "invoke", "name": "B" } }, { "uiAssert": { "process": "App", "what": "value", "name": "T", "equals": "v" } } ] }"""));
        var action = ok.Steps[0];
        Assert.Equal(VerifyStepKind.UiAction, action.Kind);
        Assert.Equal(-1, action.UiIndex);
        Assert.Equal(0, action.Lines);
        var assert = ok.Steps[1];
        Assert.Equal(VerifyStepKind.UiAssert, assert.Kind);
        Assert.Equal("value", assert.What);
        Assert.Equal("v", assert.EqualsText);

        // 缺 process / verb
        var noProcess = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario(
            """{ "target": { "commandLine": "x" }, "steps": [ { "uiAction": { "verb": "invoke" } } ] }""")));
        Assert.Contains("process", noProcess.Message);
        var noVerb = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario(
            """{ "target": { "commandLine": "x" }, "steps": [ { "uiAction": { "process": "App" } } ] }""")));
        Assert.Contains("verb", noVerb.Message);

        // uiAssert 缺 what / equals+contains 互斥 / 二者都缺
        var noWhat = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario(
            """{ "target": { "commandLine": "x" }, "steps": [ { "uiAssert": { "process": "App", "equals": "v" } } ] }""")));
        Assert.Contains("what", noWhat.Message);
        var both = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario(
            """{ "target": { "commandLine": "x" }, "steps": [ { "uiAssert": { "process": "App", "what": "value", "equals": "v", "contains": "v" } } ] }""")));
        Assert.Contains("互斥", both.Message);
        var neither = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario(
            """{ "target": { "commandLine": "x" }, "steps": [ { "uiAssert": { "process": "App", "what": "value" } } ] }""")));
        Assert.Contains("二选一", neither.Message);

        // continue.waitSeconds=0 合法（放行不等停点）
        var zero = VerifyScenario.Parse(WriteScenario(
            """{ "target": { "commandLine": "x" }, "steps": [ { "continue": { "waitSeconds": 0 } } ] }"""));
        Assert.Equal(0, zero.Steps[0].WaitSeconds);
    }

    [Fact]
    public void Parse_MissingFile_ChineseError()
    {
        var missing = Path.Combine(Path.GetTempPath(), "no-such-scenario-" + Guid.NewGuid() + ".json");
        var ex = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(missing));
        Assert.Contains("不存在", ex.Message);
    }

    [Fact]
    public void Parse_BrokenJson_SyntaxErrorWithPosition()
    {
        var path = WriteScenario("""{ "name": "x", "steps": [ { "continue":  } ] }""");
        var ex = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(path));
        Assert.Contains("JSON 解析失败", ex.Message);
        Assert.Contains("场景文件", ex.Message);
    }

    [Fact]
    public void Parse_UnknownStepKind_ChineseErrorWithStepNumberAndSupportedList()
    {
        var path = WriteScenario("""{ "target": { "commandLine": "x" }, "steps": [ { "hop": {} } ] }""");
        var ex = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(path));
        Assert.Contains("第 1 步", ex.Message);
        Assert.Contains("未知步骤类型", ex.Message);
        Assert.Contains("breakpoint/continue/assert/uiAction/uiAssert/ui/set", ex.Message);
    }

    [Fact]
    public void Parse_UnknownAssertKind_ChineseError()
    {
        var path = WriteScenario("""{ "target": { "commandLine": "x" }, "steps": [ { "assert": { "kind": "mystery" } } ] }""");
        var ex = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(path));
        Assert.Contains("第 1 步", ex.Message);
        Assert.Contains("mystery", ex.Message);
        Assert.Contains("breakpointHit/evaluate/output/state/noException", ex.Message);
    }

    [Fact]
    public void Parse_BreakpointMissingTypeAndMember_ChineseErrors()
    {
        var noType = WriteScenario("""{ "target": { "commandLine": "x" }, "steps": [ { "breakpoint": { "memberName": "Work" } } ] }""");
        var ex1 = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(noType));
        Assert.Contains("typeName", ex1.Message);

        var noMember = WriteScenario("""{ "target": { "commandLine": "x" }, "steps": [ { "breakpoint": { "typeName": "App.Program" } } ] }""");
        var ex2 = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(noMember));
        Assert.Contains("memberName", ex2.Message);
    }

    [Theory]
    [InlineData("""{ "target": { "commandLine": "x" }, "steps": [ { "assert": { "kind": "evaluate", "path": "n" } } ] }""", "equals")]
    [InlineData("""{ "target": { "commandLine": "x" }, "steps": [ { "assert": { "kind": "evaluate", "path": "n", "equals": "5", "contains": "5" } } ] }""", "互斥")]
    [InlineData("""{ "target": { "commandLine": "x" }, "steps": [ { "assert": { "kind": "output" } } ] }""", "contains")]
    [InlineData("""{ "target": { "commandLine": "x" }, "steps": [ { "assert": { "kind": "breakpointHit" } } ] }""", "breakpointIndex")]
    [InlineData("""{ "target": { "commandLine": "x" }, "steps": [ { "assert": { "kind": "state" } } ] }""", "expect")]
    public void Parse_InvalidAssertParams_ChineseErrorWithField(string json, string fragment)
    {
        var ex = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario(json)));
        Assert.Contains(fragment, ex.Message);
    }

    [Theory]
    [InlineData(3, "3 越界")]
    [InlineData(-1, "-1 越界")]
    public void Parse_BreakpointIndexOutOfRange_ChineseError(int index, string fragment)
    {
        var json = $$"""{ "target": { "commandLine": "x" }, "steps": [ { "breakpoint": { "typeName": "A.B", "memberName": "M" } }, { "assert": { "kind": "breakpointHit", "breakpointIndex": {{index}} } } ] }""";
        var ex = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario(json)));
        Assert.Contains("breakpointIndex", ex.Message);
        Assert.Contains(fragment, ex.Message);
        Assert.Contains("1 个 breakpoint 步骤", ex.Message);
    }

    [Fact]
    public void Parse_EmptyOrWrongJsonShape_ChineseErrors()
    {
        var notObject = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario("[1,2]")));
        Assert.Contains("必须是 JSON 对象", notObject.Message);

        var noTarget = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario("""{ "steps": [] }""")));
        Assert.Contains("target", noTarget.Message);

        var noCommandLine = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario("""{ "target": { }, "steps": [] }""")));
        Assert.Contains("commandLine", noCommandLine.Message);

        var noSteps = Assert.Throws<VerifyFormatException>(() => VerifyScenario.Parse(WriteScenario("""{ "target": { "commandLine": "x" } }""")));
        Assert.Contains("steps", noSteps.Message);
    }

    /// <summary>把场景 JSON 写入唯一临时文件并返回路径（测试不清理，系统临时目录）。</summary>
    private static string WriteScenario(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "verify-scenario-tests");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "scenario-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }
}
