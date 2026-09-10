using ModelContextProtocol.Client;
using ModelContextProtocol.Protocol;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// V1 debug_verify 端到端（真实 MCP server + 本进程 Session）：DebugTarget 场景纯断点+evaluate+output+state
/// 断言 PASS；evaluate 期望错值 FAIL（fail-fast 含失败步骤）；断点永不命中+进程退出 → FAIL 报状态；
/// build 自动拿产物冒烟 PASS（临时 console 工程，commandLine 只写 exe 文件名）；坏工程 FAIL 含编译摘要；
/// commandLine 文件名与产物不一致 → 中文；产物路径含空格 → v1 边界中文提示；工具参数缺失/文件不存在提示。
/// 每测试自起真实 server（与 DebugMcpToolsTests 同源）。真实 attach/ICorDebug——不加并行集合会互相干扰吗？
/// debug 测试经独立 server 进程隔离（Manager 在 server 内），与其它 MCP-client 测试可并行（同 DebugMcpToolsTests 无 Collection）。
/// 但 U1A 的 uiAction/uiAssert e2e 驱动 UiSampleApp——与 DebugUiToolsTests 共用进程名，故并入 UiTools 集合串行，避免互相杀进程。
/// </summary>
[Collection("UiTools")]
public sealed class DebugVerifyToolTests
{
    [Fact]
    public async Task DebugVerify_BreakpointEvaluateOutputStateNoException_Passes()
    {
        var exe = DebugMcpToolsTests.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");

        // bag 模式（无 delay）：Main → WorkBag(Bag{A=7,S=sx},5) → WorkScores → return（无 done，输出 bag 行）
        var scenario = WriteScenario(
            """
            {
              "name": "bag 复验 PASS",
              "target": { "commandLine": "<EXE> bag" },
              "steps": [
                { "breakpoint": { "typeName": "DebugTarget.Program", "memberName": "WorkBag" } },
                { "continue": { "waitSeconds": 60 } },
                { "assert": { "kind": "breakpointHit", "breakpointIndex": 0 } },
                { "assert": { "kind": "state", "expect": "Stopped" } },
                { "assert": { "kind": "evaluate", "path": "b.A", "equals": "7" } },
                { "assert": { "kind": "evaluate", "path": "n", "equals": "5" } },
                { "assert": { "kind": "evaluate", "path": "b.S", "equals": "sx" } },
                { "assert": { "kind": "output", "contains": "[DebugTarget] start" } },
                { "assert": { "kind": "noException" } },
                { "continue": { "waitSeconds": 60 } },
                { "assert": { "kind": "state", "expect": "Exited" } },
                { "assert": { "kind": "output", "contains": "[DebugTarget] bag 0: 7 sx" } }
              ]
            }
            """.Replace("<EXE>", EscapeJson(exe)));

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var r = await VerifyCallAsync(mcp, scenario);
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("PASS", r.Text());
        Assert.Contains("断言 9/9 通过", r.Text());
        Assert.Contains("步骤 12/12 完成", r.Text());
        Assert.DoesNotContain("FAIL", r.Text());
    }

    [Fact]
    public async Task DebugVerify_WrongEvaluateExpectation_FailsFastWithStepContext()
    {
        var exe = DebugMcpToolsTests.DebugTargetExe;
        Assert.True(File.Exists(exe));
        var scenario = WriteScenario(
            """
            {
              "name": "bag 复验 FAIL（期望错值）",
              "target": { "commandLine": "<EXE> bag" },
              "steps": [
                { "breakpoint": { "typeName": "DebugTarget.Program", "memberName": "WorkBag" } },
                { "continue": { "waitSeconds": 60 } },
                { "assert": { "kind": "breakpointHit", "breakpointIndex": 0 } },
                { "assert": { "kind": "state", "expect": "Stopped" } },
                { "assert": { "kind": "evaluate", "path": "b.A", "equals": "99" } }
              ]
            }
            """.Replace("<EXE>", EscapeJson(exe)));

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var r = await VerifyCallAsync(mcp, scenario);
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("FAIL", r.Text());
        Assert.Contains("第 5 步 assert evaluate", r.Text());   // fail-fast：失败步骤定位
        Assert.Contains("实际值", r.Text());
        Assert.DoesNotContain("PASS", r.Text());               // fail-fast 不产出 PASS
    }

    [Fact]
    public async Task DebugVerify_BreakpointNeverHit_ProcessExited_ReportsNoStopReason()
    {
        var exe = DebugMcpToolsTests.DebugTargetExe;
        Assert.True(File.Exists(exe));
        // 默认模式不调用 ThrowIfZero → 断点永不命中；进程跑完退出 → breakpointHit 断言报「尚无停点/进程已退出」
        var scenario = WriteScenario(
            """
            {
              "name": "断点永不命中 FAIL",
              "target": { "commandLine": "<EXE> 1" },
              "steps": [
                { "breakpoint": { "typeName": "DebugTarget.Program", "memberName": "ThrowIfZero" } },
                { "continue": { "waitSeconds": 60 } },
                { "assert": { "kind": "breakpointHit", "breakpointIndex": 0 } }
              ]
            }
            """.Replace("<EXE>", EscapeJson(exe)));

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var r = await VerifyCallAsync(mcp, scenario);
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("FAIL", r.Text());
        Assert.Contains("未命中", r.Text());
    }

    [Fact]
    public async Task DebugVerify_BuildAutoRetrieve_PassesWithBareExeName()
    {
        var projectDir = BuildSmokeProject("SmokeVerify");
        try
        {
            var scenario = WriteScenario(
                """
                {
                  "name": "build 自动拿产物 PASS",
                  "target": { "commandLine": "SmokeVerify.exe" },
                  "build": { "project": "<PROJECT>", "configuration": "Debug" },
                  "steps": [
                    { "continue": { "waitSeconds": 60 } },
                    { "assert": { "kind": "state", "expect": "Exited" } },
                    { "assert": { "kind": "output", "contains": "[SmokeVerify] ready" } },
                    { "assert": { "kind": "output", "contains": "[SmokeVerify] done", "stream": "out" } }
                  ]
                }
                """.Replace("<PROJECT>", EscapeJson(Path.Combine(projectDir, "SmokeVerify.csproj"))));

            await using var mcp = await DebugMcpToolsTests.ConnectAsync();
            var r = await VerifyCallAsync(mcp, scenario);
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("PASS", r.Text());
            Assert.Contains("断言 3/3 通过", r.Text());
        }
        finally { TryDelete(projectDir); }
    }

    [Fact]
    public async Task DebugVerify_BuildBrokenSource_FailsWithCompileSummary()
    {
        var projectDir = CreateProject("BrokenVerify", "class P { void M() { int x = ; } }");
        try
        {
            var scenario = WriteScenario(
                """
                {
                  "name": "坏工程 FAIL",
                  "target": { "commandLine": "BrokenVerify.exe" },
                  "build": { "project": "<PROJECT>", "configuration": "Debug" },
                  "steps": [ { "continue": { "waitSeconds": 30 } } ]
                }
                """.Replace("<PROJECT>", EscapeJson(Path.Combine(projectDir, "BrokenVerify.csproj"))));

            await using var mcp = await DebugMcpToolsTests.ConnectAsync();
            var r = await VerifyCallAsync(mcp, scenario);
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("FAIL", r.Text());
            Assert.Contains("重编译失败", r.Text());
            Assert.Contains("编译失败", r.Text());
            Assert.Contains("CS", r.Text()); // 编译错误行进摘要
        }
        finally { TryDelete(projectDir); }
    }

    [Fact]
    public async Task DebugVerify_BuildFileNameMismatch_FailsChinese()
    {
        var projectDir = BuildSmokeProject("MismatchApp");
        try
        {
            var scenario = WriteScenario(
                """
                {
                  "name": "文件名不一致 FAIL",
                  "target": { "commandLine": "WrongName.exe" },
                  "build": { "project": "<PROJECT>", "configuration": "Debug" },
                  "steps": []
                }
                """.Replace("<PROJECT>", EscapeJson(Path.Combine(projectDir, "MismatchApp.csproj"))));

            await using var mcp = await DebugMcpToolsTests.ConnectAsync();
            var r = await VerifyCallAsync(mcp, scenario);
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("FAIL", r.Text());
            Assert.Contains("不一致", r.Text());
            Assert.Contains("WrongName.exe", r.Text());
            Assert.Contains("MismatchApp", r.Text());
        }
        finally { TryDelete(projectDir); }
    }

    [Fact]
    public async Task DebugVerify_BuildOutputPathWithSpace_RecordsV1BoundaryHint()
    {
        // 产物路径含空格 = v1 记录边界（启动器按空格切分）——verify 返回中文边界提示，不静默起错文件
        var baseDir = Path.Combine(Path.GetTempPath(), "verify-space tests");
        Directory.CreateDirectory(baseDir);
        var projectDir = BuildSmokeProject("SpaceApp", baseDir);
        try
        {
            var scenario = WriteScenario(
                """
                {
                  "name": "产物含空格边界",
                  "target": { "commandLine": "SpaceApp.exe" },
                  "build": { "project": "<PROJECT>", "configuration": "Debug" },
                  "steps": []
                }
                """.Replace("<PROJECT>", EscapeJson(Path.Combine(projectDir, "SpaceApp.csproj"))));

            await using var mcp = await DebugMcpToolsTests.ConnectAsync();
            var r = await VerifyCallAsync(mcp, scenario);
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("FAIL", r.Text());
            Assert.Contains("含空格", r.Text());
            Assert.Contains("v1 启动器按空格切分命令暂不支持", r.Text());
        }
        finally { TryDelete(projectDir); TryDelete(baseDir); }
    }

    [Fact]
    public async Task DebugVerify_TwoBreakpoints_SecondContinueWaitsForNewStopNotStaleSnapshot()
    {
        var exe = DebugMcpToolsTests.DebugTargetExe;
        Assert.True(File.Exists(exe), "DebugTarget.exe 不存在，请先运行 generate-testdata.ps1");
        // 双断点先后命中：bag 模式 WorkBag 入口先停 → continue（WorkBag 跑完 5 轮）→ WorkScores 入口再停。
        // 第二次 continue 的 breakpointHit(1) 必须对应新停点 WorkScores——陈旧停点快照（旧 WorkBag 停点）会被引用比较丢弃，防 assert 假 FAIL。
        var scenario = WriteScenario(
            """
            {
              "name": "双断点陈旧快照回归",
              "target": { "commandLine": "<EXE> bag" },
              "steps": [
                { "breakpoint": { "typeName": "DebugTarget.Program", "memberName": "WorkBag" } },
                { "breakpoint": { "typeName": "DebugTarget.Program", "memberName": "WorkScores" } },
                { "continue": { "waitSeconds": 60 } },
                { "assert": { "kind": "breakpointHit", "breakpointIndex": 0 } },
                { "assert": { "kind": "state", "expect": "Stopped" } },
                { "continue": { "waitSeconds": 60 } },
                { "assert": { "kind": "breakpointHit", "breakpointIndex": 1 } },
                { "assert": { "kind": "state", "expect": "Stopped" } }
              ]
            }
            """.Replace("<EXE>", EscapeJson(exe)));

        await using var mcp = await DebugMcpToolsTests.ConnectAsync();
        var r = await VerifyCallAsync(mcp, scenario);
        Assert.True(r.IsError != true, r.Text());
        Assert.Contains("PASS", r.Text());
        Assert.Contains("断言 4/4 通过", r.Text());
        Assert.Contains("步骤 8/8 完成", r.Text());
    }

    [Fact]
    public async Task DebugVerify_MissingOrEmptyScenarioPath_ChineseHints()
    {
        await using var mcp = await DebugMcpToolsTests.ConnectAsync();

        var empty = await DebugMcpToolsTests.CallAsync(mcp, "debug_verify", new Dictionary<string, object?>());
        Assert.Contains("scenarioPath", empty.Text());

        var missing = await DebugMcpToolsTests.CallAsync(mcp, "debug_verify",
            new Dictionary<string, object?> { ["scenarioPath"] = Path.Combine(Path.GetTempPath(), "no-such.json") });
        Assert.Contains("场景文件不存在", missing.Text());
    }

    [Fact]
    public async Task DebugVerify_UiActionAndUiAssert_DrivesAndAssertsUi()
    {
        var exe = DebugUiToolsTests.UiSampleAppExe;
        Assert.True(File.Exists(exe), "UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1");
        var scenario = WriteScenario(
            """
            {
              "name": "UiSampleApp UI 复验 PASS",
              "target": { "commandLine": "<EXE>" },
              "steps": [
                { "continue": { "waitSeconds": 0 } },
                { "uiAction": { "process": "UiSampleApp", "verb": "select", "name": "Item 90", "type": "ListItem" } },
                { "uiAssert": { "process": "UiSampleApp", "what": "selected", "name": "Item 90", "type": "ListItem", "equals": "True" } }
              ]
            }
            """.Replace("<EXE>", EscapeJson(exe)));

        try
        {
            await using var mcp = await DebugMcpToolsTests.ConnectAsync();
            var r = await VerifyCallAsync(mcp, scenario);
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("PASS", r.Text());
            Assert.Contains("断言 1/1 通过", r.Text());
            Assert.Contains("步骤 3/3 完成", r.Text());
        }
        finally { KillUiSampleApp(); }
    }

    [Fact]
    public async Task DebugVerify_UiAssertFailure_RedactsSensitiveActualValue()
    {
        var exe = DebugUiToolsTests.UiSampleAppExe;
        Assert.True(File.Exists(exe), "UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1");
        // password 控件（AutoId=password）经 DB1 敏感名规则：失败理由中的实际值应脱敏
        var scenario = WriteScenario(
            """
            {
              "name": "uiAssert 失败脱敏",
              "target": { "commandLine": "<EXE>" },
              "steps": [
                { "continue": { "waitSeconds": 0 } },
                { "uiAssert": { "process": "UiSampleApp", "what": "value", "name": "password", "equals": "wrong" } }
              ]
            }
            """.Replace("<EXE>", EscapeJson(exe)));

        try
        {
            await using var mcp = await DebugMcpToolsTests.ConnectAsync();
            var r = await VerifyCallAsync(mcp, scenario);
            Assert.True(r.IsError != true, r.Text());
            Assert.Contains("FAIL", r.Text());
            Assert.Contains("实际值", r.Text());
            Assert.Contains("[已脱敏:疑似凭据]", r.Text());
            Assert.Contains("equals \"wrong\"", r.Text());
        }
        finally { KillUiSampleApp(); }
    }

    // ---------- helpers ----------

    private static void KillUiSampleApp()
    {
        foreach (var p in System.Diagnostics.Process.GetProcessesByName("UiSampleApp"))
        {
            try { if (!p.HasExited) p.Kill(entireProcessTree: true); }
            catch { /* 权限/已退出忽略 */ }
        }
    }

    private static async Task<CallToolResult> VerifyCallAsync(McpClient mcp, string scenarioPath)
    {
        var task = mcp.CallToolAsync("debug_verify",
            new Dictionary<string, object?> { ["scenarioPath"] = scenarioPath },
            cancellationToken: TestContext.Current.CancellationToken);
        return await task.AsTask().WaitAsync(TimeSpan.FromSeconds(180), TestContext.Current.CancellationToken);
    }

    /// <summary>写场景 JSON 到唯一临时文件（返回路径；与 server 进程共享磁盘）。</summary>
    private static string WriteScenario(string json)
    {
        var dir = Path.Combine(Path.GetTempPath(), "verify-e2e-scenarios");
        Directory.CreateDirectory(dir);
        var path = Path.Combine(dir, "scenario-" + Guid.NewGuid().ToString("N") + ".json");
        File.WriteAllText(path, json);
        return path;
    }

    /// <summary>JSON 字符串值转义（绝对路径的 \\ 与引号）。</summary>
    private static string EscapeJson(string text) => text.Replace("\\", "\\\\").Replace("\"", "\\\"");

    /// <summary>建可编译 console 工程（csproj + Program.cs，输出 marker 行后稍停再退出——输出断言在退出后立即跑，
    /// 末尾停 800ms 让重定向输出在进程退出事件前全部送达捕获缓冲，防断言竞态）。</summary>
    private static string BuildSmokeProject(string name, string? baseDir = null)
        => CreateProject(name,
            """
            System.Console.WriteLine("[__NAME__] ready");
            for (int i = 0; i < 5; i++) { System.Console.WriteLine("[__NAME__] tick " + i); System.Threading.Thread.Sleep(10); }
            System.Console.WriteLine("[__NAME__] done");
            System.Console.Out.Flush();
            System.Threading.Thread.Sleep(800);
            """.Replace("__NAME__", name), baseDir);

    private static string CreateProject(string name, string source, string? baseDir = null)
    {
        var root = baseDir ?? Path.Combine(Path.GetTempPath(), "verify-build-e2e");
        var dir = Path.Combine(root, name + "-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        File.WriteAllText(Path.Combine(dir, name + ".csproj"),
            $"""
            <Project Sdk="Microsoft.NET.Sdk">
              <PropertyGroup>
                <OutputType>Exe</OutputType>
                <TargetFramework>net10.0</TargetFramework>
                <AssemblyName>{name}</AssemblyName>
                <ImplicitUsings>disable</ImplicitUsings>
                <Nullable>disable</Nullable>
              </PropertyGroup>
            </Project>
            """);
        File.WriteAllText(Path.Combine(dir, "Program.cs"), source);
        return dir;
    }

    private static void TryDelete(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch { /* 残留系统临时目录 */ }
    }
}
