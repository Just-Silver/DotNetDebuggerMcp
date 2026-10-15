<!-- 职责：agent 使用反馈修复（P1–P7 + 能力开关）的实施计划（逐任务可执行步骤）；不写：设计与决策依据（见 spec）。 -->

# agent 使用反馈修复 + 能力开关 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: 用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐任务实施。步骤用 `- [ ]` 复选框跟踪。

**Goal:** 把 `docs/planning/2026-10-11-agent-usage-feedback.md` 的 P1–P7 修掉，并新增「能力族开关」（默认只启用反编译）。

**Architecture:** 分层落地——Engine（泵内 resume 兜底）、Session（attach 前置拒绝 + 三处生命周期 bug）、宿主 `Tools/Debugger`（冻结态可读栈、`debug_reset`、`clickPoint`、`scope`/`depth`、按族注册与握手拼装）、`SharpSight.UiAutomation`（UIA 客户端释放 + 真实点击）。

**Tech Stack:** .NET 10（`net10.0-windows10.0.22621.0`）、ClrDebug/ICorDebug（Engine）、FlaUI 5.0 UIA3（UiAutomation）、ModelContextProtocol 2.2.0（宿主）、xunit.v3 + MTP。

**Spec:** `docs/planning/specs/2026-10-15-agent-usage-feedback-fixes.md`

## Global Constraints

- **MCP 工具参数必须带默认值**（不声明可空）；`[Description]` 中文、面向 agent、注明默认值、必填标「（必填）」。
- **每个工具方法带 `CancellationToken cancellationToken = default`**（SDK 注入、不暴露为参数、不写 `[Description]`）。
- **工具返回 `Task<string>`，一切错误返回中文提示、不抛异常**（`screenshot` 图片类例外，本计划不改）。
- **改 MCP 工具（新增/改名/加参/改默认值/改行为）必须同 commit 同步**：工具 `[Description]` + 握手 `ServerInstructions`（`AppText.HandshakeFeatureIntro` 族）。握手回归断言 `DotNetDebuggerMcpCmdTests.HandshakeFeatureIntro_覆盖全部能力族触发条件` 必须同步。
- **stdout 只承载 MCP 协议帧；日志走 stderr**（`builder.Logging.ClearProviders()` + `AddConsole(LogToStandardErrorThreshold = Trace)`）——**严禁改动**。
- **跨层重复字面量定义成常量**（`Configuration/AppText.cs`、`AppConfig.cs`）。
- **单方法 ≤ 60 行为宜、> 80 行必须拆**。
- **注释只解释「为什么」**，不写来源/迁移/任务背景。
- **测试只写业务行为**；测试名读起来像一条业务规则。
- **能力开关只作用于 MCP 面**：CLI 执行路径（`-a`/`-l`/`-dbg`/`-c`）不受开关影响。
- 前置：`powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`（`tests/TestData/*.dll` git 忽略）。

## Review Focus

1. **未启用族的残留**——关掉 `debug`/`ui`/`screenshot` 后，握手文本、工具目录、`cache_stats` 计数里**不得**残留该族痕迹。期望：`--features=decompile` 时 `ServerInstructions` 不含 `debug_`/`ui_`/`screenshot`/`web_open` 任一 token，工具目录不含对应工具。
2. **冻结态读栈的实际可用性**——attach 后**不** `debug_continue` 直接 `debug_stack`。期望：返回全部托管线程的栈，而非「无停点线程可读」。
3. **默认路径的假成功**——对仅暴露 `legacy` 的控件 `verb=select`（`clickPoint=false`）。期望：文案明确标注「可能不生效」并给出 `clickPoint=true` 出路，不再是无条件「已 select」。
4. **重复 attach 的错误可操作性**——对已有会话再 `debug_attach`、或对已被占用的进程 attach。期望：**中文可操作提示**（先 disconnect / 可能被其它调试器占用 / 需重启目标），不出现裸 `ERROR_INVALID_PARAMETER`/HRESULT。
5. **既有测试的启动面**——能力开关落地后，凡经 MCP 起 server 的测试与 `ClientRunner` 是否都显式开启了所需族。期望：全绿；漏一处即该族测试全部找不到工具。

---

## File Structure

**新建**
- `src/DotNetDebuggerMcp/Configuration/FeatureFlags.cs` — 能力族枚举 + 解析（CLI/环境变量）+ 门控查询。
- `src/DotNetDebuggerMcp/Configuration/ToolFeatureRegistry.cs` — `[McpServerToolType]` 类型 → 能力族 的单一注册表。
- `src/DotNetDebuggerMcp/Configuration/HandshakeText.cs` — 按启用族拼装 `ServerInstructions` 各段（从 `AppText.HandshakeFeatureIntro` 拆出）。
- `tests/DotNetDebuggerMcp.Tests/FeatureFlagsTests.cs` — 开关解析纯逻辑用例。
- `tests/DotNetDebuggerMcp.Tests/ToolFeatureRegistryTests.cs` — 注册覆盖率 + 按族过滤用例。

**修改（按任务）**
- 宿主：`DotNetDebuggerMcpCmd.cs`（`--features` 选项 + 注册过滤 + 握手拼装）、`Tools/Debugger/DebugSessionTool.cs`、`DebugInspectTool.cs`、`DebugControlTool.cs`、`DebugEvaluateTool.cs`、`DebugObjectTool.cs`、`DebugSetTool.cs`、`Tools/Debugger/UiTools.cs`、`Configuration/AppText.cs`。
- Session：`DebugSessionManager.cs`。
- Engine：`Session/DebugSession.cs`、`Engine/DebugEngineCore.cs`。
- UiAutomation：`UiAutomationService.cs`、`UiPatternDispatcher.cs`、`UiModels.cs`。
- Web：`Services/DebugViewService.cs`（P1 语义同步）。
- 测试：`DebugMcpToolsTests.cs`（ConnectAsync 补 `--features`）、`McpSessionConcurrencyTests.cs`、`McpWebCoexistTests.cs`、`NoPhysicalInputGuardTests.cs`、`AgentCopyGuardTests.cs`、`DotNetDebuggerMcpCmdTests.cs`、`UiElementLocatorPureTests.cs`、`DebugUiToolsTests.cs`。
- 配置/文档：`opencode.json`（仓库根）、`src/DotNetDebuggerMcp/.mcp/server.json`、`README.md`、`CHANGELOG.md`、`docs/planning/specs/README.md`、`docs/planning/README.md`、各 `src/*/TODO.md`。
- 端到端：`src/DotNetDebuggerMcp.Client/ClientRunner.cs`。

---

## Task 1: 能力开关解析（`FeatureFlags`）

**Files:**
- Create: `src/DotNetDebuggerMcp/Configuration/FeatureFlags.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/FeatureFlagsTests.cs`

**Interfaces:**
- Produces:
  - `[Flags] internal enum FeatureFamily { None = 0, Decompile = 1, Debug = 2, Ui = 4, Screenshot = 8, All = Decompile|Debug|Ui|Screenshot }`
  - `internal const string FeaturesEnvVar = "DOTNETDEBUGGERMCP_FEATURES";`
  - `internal static readonly string[] FeatureNames = ["decompile", "debug", "ui", "screenshot"];`
  - `internal static (FeatureFamily Family, string? Error) TryParse(string value)` — `""`（空白）→ `(None, null)`；逗号分隔忽略大小写/空白；未知 token → `(None, 中文错误)`（列全 `FeatureNames`）。
  - `internal static FeatureFamily Parse(string? cliValue, string? envValue)` — **CLI 非 null 即权威**（含 `""`→`None`）；CLI 为 `null` 时用 `envValue`（非空白才解析）；两者皆空 → 默认 `FeatureFamily.Decompile`。内部对未知值抛 `InvalidOperationException(Error)`（供启动路径 try/catch 转退出）。
  - `internal static (FeatureFamily Family, string? Error) Resolve(string? cliValue, string? envValue)` — 启动路径用：返回错误而非抛（便于 `OnExecuteAsync` 显式报错退出）。

  > **语义要点（McMaster 5.1.0 实测）**：不给参数 → `null`；`--features=` → `""`。故 `null`=「未指定」（回落默认/env），`""`=「显式零能力」。

- [ ] **Step 1: 写失败测试**

```csharp
// FeatureFlagsTests.cs
[Fact]
public void Parse_未指定_默认仅反编译()
{
    Assert.Equal(FeatureFamily.Decompile, FeatureFlags.Parse(null, null));
    Assert.Equal(FeatureFamily.Decompile, FeatureFlags.Parse(null, "   ")); // env 空白视为未设
}

[Fact]
public void Parse_显式空串_零能力()   // --features= （McMaster 落成 ""）
    => Assert.Equal(FeatureFamily.None, FeatureFlags.Parse("", null));

[Fact]
public void Parse_环境变量_生效()
    => Assert.Equal(FeatureFamily.Debug, FeatureFlags.Parse(null, "debug"));

[Fact]
public void Parse_CLI优先于环境变量()
{
    Assert.Equal(FeatureFamily.Screenshot, FeatureFlags.Parse("screenshot", "debug"));
    Assert.Equal(FeatureFamily.None, FeatureFlags.Parse("", "debug")); // CLI 显式空压过 env
}

[Theory]
[InlineData("decompile,debug,ui,screenshot", FeatureFamily.All)]   // FeatureFamily.All
[InlineData(" DEBUG , Ui ", FeatureFamily.Debug | FeatureFamily.Ui)]
[InlineData("decompile,decompile", FeatureFamily.Decompile)]
public void Parse_组合(string value, FeatureFamily expected)
    => Assert.Equal(expected, FeatureFlags.Parse(value, null));

[Theory]
[InlineData("debugg")]
[InlineData("decompile,nope")]
public void TryParse_未知取值_返回中文错误并列出合法值(string value)
{
    var (family, error) = FeatureFlags.TryParse(value);
    Assert.Equal(FeatureFamily.None, family);
    Assert.Contains("decompile", error);
    Assert.Contains("screenshot", error);
}

[Fact]
public void Resolve_未知取值_带出错误供启动路径退出()
{
    var (family, error) = FeatureFlags.Resolve("debbug", null);
    Assert.Equal(FeatureFamily.None, family);
    Assert.False(string.IsNullOrEmpty(error));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "DotNetDebuggerMcp.Tests.FeatureFlagsTests"`
Expected: 编译失败（`FeatureFlags` 不存在）。

- [ ] **Step 3: 实现 `FeatureFlags`**

要点：`[Flags]` 枚举；`TryParse` 按 token `switch` 置位、未知 token 累积中文错误（列出 `FeatureNames`）；`Parse` 按上面语义组合（CLI 非 null 权威）；`Resolve` 包装 `TryParse` 并对 CLI/env 分别判定返回错误。

- [ ] **Step 4: 跑测试确认通过**。Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/DotNetDebuggerMcp/Configuration/FeatureFlags.cs tests/DotNetDebuggerMcp.Tests/FeatureFlagsTests.cs
git commit -m "feat(mcp): 新增能力族开关解析（--features / DOTNETDEBUGGERMCP_FEATURES）"
```

---

## Task 2: 能力族注册表 + 按族注册工具

**Files:**
- Create: `src/DotNetDebuggerMcp/Configuration/ToolFeatureRegistry.cs`
- Modify: `src/DotNetDebuggerMcp/DotNetDebuggerMcpCmd.cs`（`.WithToolsFromAssembly()` → `.WithTools(types)`）
- Test: `tests/DotNetDebuggerMcp.Tests/ToolFeatureRegistryTests.cs`

**Interfaces:**
- Consumes: `FeatureFamily`（Task 1）。
- Produces:
  - `internal static FeatureFamily FamilyOf(Type toolType)`
  - `internal static IReadOnlyList<Type> EnabledToolTypes(FeatureFamily enabled)` — 程序集内所有 `[McpServerToolType]` 类型中族 ∈ `enabled` 的。
  - `internal static IReadOnlyList<Type> AllToolTypes()` — 仅测试用（覆盖率断言）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void 每个工具类型_都在注册表内()
{
    // 新增工具漏登记 → 此测试失败（防「默默不可发现」）
    foreach (var t in ToolFeatureRegistry.AllToolTypes())
        Assert.True(Enum.IsDefined(ToolFeatureRegistry.FamilyOf(t)), $"{t.Name} 未登记能力族");
}

[Fact]
public void 默认族_只含反编译与元数据()
{
    var names = ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Decompile).Select(t => t.Name).ToHashSet();
    Assert.Contains("DecompileTool", names);
    Assert.Contains("ListTypesTool", names);
    Assert.Contains("CacheStatsTool", names);
    Assert.DoesNotContain("DebugSessionTool", names);
    Assert.DoesNotContain("UiTools", names);
    Assert.DoesNotContain("ScreenshotTool", names);
}

[Fact]
public void 各族过滤_互斥且完整()
{
    Assert.Contains("DebugSessionTool", ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Debug).Select(t => t.Name));
    Assert.Contains("UiTools", ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Ui).Select(t => t.Name));
    Assert.Contains("ScreenshotTool", ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Screenshot).Select(t => t.Name));
    // 各族并集 == 全部（无未归属）
    Assert.Equal(ToolFeatureRegistry.AllToolTypes().Count,
        new[] { FeatureFamily.Decompile, FeatureFamily.Debug, FeatureFamily.Ui, FeatureFamily.Screenshot }
            .Sum(f => ToolFeatureRegistry.EnabledToolTypes(f).Count));
}

[Fact]
public void web_open_随调试族()
    => Assert.Contains("WebOpenTool", ToolFeatureRegistry.EnabledToolTypes(FeatureFamily.Debug).Select(t => t.Name));
```

- [ ] **Step 2: 跑测试确认失败**（类型不存在）

Run: `--filter-class "DotNetDebuggerMcp.Tests.ToolFeatureRegistryTests"`。Expected: 编译失败。

- [ ] **Step 3: 实现注册表**

要点：单一 `Dictionary<Type, FeatureFamily>`（按 spec §8.3 映射：`Decompile`+`Metadata` 目录下类型 + `CacheStatsTool` → Decompile；`Debugger/Debug*.cs` 类型 + `WebOpenTool` → Debug；`UiTools` → Ui；`Screenshot/*` → Screenshot）。`AllToolTypes()` 用 `typeof(UiTools).Assembly.GetTypes()` 过滤 `[McpServerToolType]`（并跳过抽象/非 public）。

- [ ] **Step 4: 改 `DotNetDebuggerMcpCmd.OnExecuteAsync` 注册 + 未知取值退出**

在 MCP 分支：`var (flags, err) = FeatureFlags.Resolve(Features, Environment.GetEnvironmentVariable(FeatureFlags.FeaturesEnvVar)); if (err is not null) { Console.Error.WriteLine(err); return 1; }`。把 `.WithToolsFromAssembly()` 换成 `.WithTools(ToolFeatureRegistry.EnabledToolTypes(flags))`。`--features` 需在 `DotNetDebuggerMcpCmd` 上加 `[Option("--features <list>", "...", CommandOptionType.SingleValue)] public string Features { get; } = null!;`（无则 `null`，与 `Parse` 的「未指定」语义对齐）。

- [ ] **Step 5: 跑测试确认通过**

Run: 同 Step 2。Expected: PASS。

- [ ] **Step 6: 提交**

```bash
git add src/DotNetDebuggerMcp/Configuration/ToolFeatureRegistry.cs src/DotNetDebuggerMcp/DotNetDebuggerMcpCmd.cs tests/DotNetDebuggerMcp.Tests/ToolFeatureRegistryTests.cs
git commit -m "feat(mcp): 按能力族注册工具（未启用族不进 MCP 工具目录）+ 未知取值报错退出"
```

> 注：本任务后，凡经 MCP 起 server 的既有测试会因默认只注册反编译而失败——Task 4 统一补 `--features`。临时跑单测时按需带参数验证即可。

---

## Task 3: 握手按启用族拼装

**Files:**
- Create: `src/DotNetDebuggerMcp/Configuration/HandshakeText.cs`
- Modify: `src/DotNetDebuggerMcp/Configuration/AppText.cs`（`HandshakeFeatureIntro` 拆为分段常量并迁入 `HandshakeText`）、`src/DotNetDebuggerMcp/DotNetDebuggerMcpCmd.cs:312`（`BuildServerInstructions` 改签名）
- Test: `tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcpCmdTests.cs`

**Interfaces:**
- Consumes: `FeatureFamily`（Task 1）。
- Produces:
  - `internal static string HandshakeText.Build(FeatureFamily enabled, string? updateReport)` — **新的拼装入口**（迁自 `AppText` 的分段常量）。
  - `DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily enabled, string? updateReport)` — 保留在 `DotNetDebuggerMcpCmd`（既有测试与调用点的落点），内部 `=> HandshakeText.Build(enabled, updateReport)`。

  > **落点定案**：分段文案常量与新拼装逻辑都在**新文件 `HandshakeText`**（`AppText` 保持「纯常量转发/集中」，不塞方法）；`DotNetDebuggerMcpCmd.BuildServerInstructions` 保留为薄转发（既有 4 处调用点/断言只改签名，不改落点）。原 `AppText.HandshakeFeatureIntro` 整段删除（避免「关掉还残留」）。

- [ ] **Step 1: 写失败测试**（改造既有 `HandshakeFeatureIntro_覆盖全部能力族触发条件`，并新增按族断言）

```csharp
[Fact]
public void 握手_启用族出现_未启用族不出现()
{
    var all = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.All, null);
    Assert.Contains("动态调试", all);
    Assert.Contains("ui_find", all);
    Assert.Contains("screenshot", all);
    Assert.Contains("debug_verify", all);

    var minimal = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.Decompile, null);
    Assert.Contains("反编译", minimal);
    Assert.DoesNotContain("debug_", minimal);
    Assert.DoesNotContain("ui_find", minimal);
    Assert.DoesNotContain("ui_action", minimal);
    Assert.DoesNotContain("screenshot", minimal);
    Assert.DoesNotContain("web_open", minimal);
}

[Fact]
public void 握手_全关_不注入能力简介()
{
    var none = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.None, null);
    Assert.DoesNotContain("## 何时使用", none);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `--filter-class "DotNetDebuggerMcp.Tests.DotNetDebuggerMcpCmdTests"`。Expected: 编译失败（`BuildServerInstructions` 无 `FeatureFamily` 重载）。

- [ ] **Step 3: 建 `HandshakeText`、拆常量、改签名**

要点：把现 `AppText.HandshakeFeatureIntro` 整段迁入 `HandshakeText` 并拆为「简介段模板 + 每族触发条件行（各自 const）+ 通用使用约定 + 族专属约定（`ui`：5s 护栏/`clickPoint`/观测扰动；`debug`：异步返回约定）」。`Build` 按 `enabled` 选择；`None` 不输出「## 何时使用」。`DotNetDebuggerMcpCmd.BuildServerInstructions` 改签名薄转发（调用点传 `flags`）。**更新既有三条断言**：`DotNetDebuggerMcpCmdTests.cs:97-105`（含更新报告）、`:112-113`（报告为空）、`:117`（能力族）——全部改为带 `FeatureFamily.All` 调用，第 117 条改造为 Step 1 的按族断言。

- [ ] **Step 4: 跑测试确认通过**。Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add src/DotNetDebuggerMcp/Configuration/HandshakeText.cs src/DotNetDebuggerMcp/Configuration/AppText.cs src/DotNetDebuggerMcp/DotNetDebuggerMcpCmd.cs tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcpCmdTests.cs
git commit -m "feat(mcp): 握手简介按启用能力族拼装（未启用族不注入）"
```

---

## Task 4: 测试启动面与配置/文档同步

**Files:**
- Modify: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs:1627`、`tests/DotNetDebuggerMcp.Tests/McpSessionConcurrencyTests.cs:161`、`tests/DotNetDebuggerMcp.Tests/McpWebCoexistTests.cs:42`、`src/DotNetDebuggerMcp.Client/ClientRunner.cs:31`、`opencode.json`、`src/DotNetDebuggerMcp/.mcp/server.json`、`README.md`、`CHANGELOG.md`、`docs/planning/specs/README.md`、`docs/planning/README.md`、`src/DotNetDebuggerMcp/TODO.md`

**Interfaces:** 无新代码接口（纯接线与文档）。

- [ ] **Step 1: 三个测试启动点补 `--features`**

`Arguments = [serverDll, "--features=decompile,debug,ui,screenshot"]`（`McpWebCoexistTests` 拼在 `extraArgs` 前）。

- [ ] **Step 2: `ClientRunner` 补 `--features`**

`Arguments = ["run","--project",serverProject,"-c","Release","--","--features=decompile,debug,ui,screenshot"]`（按 `dotnet run` 传参语法核实）。

- [ ] **Step 3: 交互式验证（全量）**

Run: `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj`
Expected: 全绿（对比改动前基线）。

- [ ] **Step 4: 配置与文档**

`opencode.json` 把 `--features=...` **追加进 `command` 数组**（opencode 的 local MCP 配置**没有** `args` 字段，schema `additionalProperties:false`——写成 `args` 会被拒/忽略）：
```json
"command": ["./src/DotNetDebuggerMcp/bin/Debug/net10.0-windows10.0.22621.0/DotNetDebuggerMcp.exe", "--features=decompile,debug,ui,screenshot"]
```
`.mcp/server.json` 的 `environmentVariables` 登记 `DOTNETDEBUGGERMCP_FEATURES`；`README.md` 新增「能力开关」一节（语法/默认/`mcp.json` 示例）；`CHANGELOG.md` `[Unreleased]` 记 `Added` + `Changed`（默认行为变化显著标注）；specs/planning 文档地图登记；宿主 TODO 登记。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "test(mcp): MCP 测试启动面启用全族 + 能力开关文档与配置同步"
```

---

## Task 5: P1 冻结态可读栈

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugInspectTool.cs`、`DebugEvaluateTool.cs:34`、`DebugObjectTool.cs:39`、`DebugControlTool.cs`（`debug_wait`）、`DebugSessionTool.cs`（`debug_attach`/`debug_launch`/`debug_state` 文案）、`src/DotNetDebugger.Web/Services/DebugViewService.cs:126/146/157`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

**Interfaces:**
- Produces:
  - `internal static bool TryRequireReadable([NotNullWhen(true)] out ActiveDebugSession? active, [NotNullWhen(false)] out string? error)` — `State ∈ {Stopped, None, Attaching, Launching}` 放行（含 `None`：会话刚建、事件缓冲尚未追上——进程同样处于冻结态，见 Step 5「竞态」）；`TryRequireStopped` 保持不变（`debug_set` 仍用它）。
  - `internal static bool IsFrozenReadableState(DebugSessionState state)` — `None`/`Attaching`/`Launching`（纯函数，供 Web/工具共享）。

- [ ] **Step 1: 写失败测试（e2e，经 MCP）**

```csharp
// 真实 attach 一个自起的目标（delay 模式）后，不 continue，直接读栈
[Fact]
public async Task Attach_冻结态_可直接读全部线程栈()
{
    await using var mcp = await ConnectAsync();
    using var target = DebugTargetProcess.Start(delaySec: 60);  // 自起 + 排空 stdout/stderr
    var attach = await CallAsync(mcp, "debug_attach", new() { ["processId"] = target.Id });
    Assert.Contains("已附加", attach);
    // attach 返回后事件缓冲可能仍是 None——先轮询 debug_state 到冻结态（防竞态 flaky）
    await WaitUntilAsync(mcp, "debug_state", t => t.Contains("冻结") || t.Contains("已附加"));
    var stack = await CallAsync(mcp, "debug_stack", new());  // 不带 continue
    Assert.Contains("调用栈", stack);
    Assert.DoesNotContain("无停点线程可读", stack);
}
```

- [ ] **Step 2: 确认失败**（当前返回「无停点线程可读」）

Run: `--filter-method "*Attach_冻结态_可直接读全部线程栈*"`。Expected: FAIL。

- [ ] **Step 3: 实现**

要点：① 加 `TryRequireReadable`（带 `[NotNullWhen]`）+ `IsFrozenReadableState`；`debug_stack`/`debug_variables`/`debug_evaluate`/`debug_object` 换用 `TryRequireReadable`，`debug_set` 不动。② `debug_stack`：新增 `all`（默认 `false`）参数；**冻结态或缺省 `threadId<=0` 时**遍历 `GetThreadsAsync()` 全部线程读栈、按线程分组，非可读线程给占位。③ `debug_wait`：`IsFrozenReadableState(CurrentState)` 时直接返回「已冻结（可读栈）」而非等超时。④ `debug_state`/`StateText`：冻结态补「可读栈」措辞。⑤ Web `DebugViewService` **只放宽 `GetStackAsync`/`GetVariablesAsync`（`:146/:157`）**——**不要**改 `StepAsync`（`:126`，单步前置，spec 未授权冻结态单步）。⑥ 相关 `[Description]` 与握手同步。

> **测试辅助**：`DebugMcpToolsTests` 无裸进程启动 helper，需新增 `DebugTargetProcess.Start(delaySec)`（**必须持续排空 stdout/stderr**，否则子进程管道写满卡死——根 `AGENTS.md` 强制）；`WaitUntilAsync(mcp, tool, predicate)` 轮询辅助。

- [ ] **Step 4: 确认通过** + 断言语义（`Assert.Contains("注", ...)` 保持不动）

Run: 同 Step 2。Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "fix(debug): attach/launch 冻结态即可读栈（debug_stack 缺省列全部线程）"
```

---

## Task 6: P2 重复 attach 反馈 + 生命周期 bug + `debug_reset`

**Files:**
- Modify: `src/DotNetDebugger.Session/DebugSessionManager.cs`（`AttachAsync` 前置拒绝、`CloseAsync` 顺位、新增 `ForceResetAsync`）、`src/DotNetDebugger.Engine/Session/DebugSession.cs:30-35`（`CreateAsync` 失败释放）、`src/DotNetDebugger.Engine/Engine/DebugEngineCore.cs:247-255`（`DisconnectAsync` 失败不静默）、`src/DotNetDebuggerMcp/Tools/Debugger/DebugSessionTool.cs`（`DescribeAttachFailure` + `debug_reset` 工具）
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`、`tests/DotNetDebugger.Session.Tests/DebugSessionManagerTests.cs`

**Interfaces:**
- Produces:
  - `Task<bool> DebugSessionManager.ForceResetAsync(CancellationToken ct = default)`
  - `internal static string DebugSessionTool.DescribeAttachFailure(Exception ex)`（纯函数，可单测）

- [ ] **Step 1: 写失败测试**

```csharp
// 纯函数：COM 错误中文化
[Theory]
[InlineData("Error HRESULT ERROR_INVALID_PARAMETER has been returned from a call to a COM component.", "已被")]
[InlineData("0x80070057", "已被")]
public void DescribeAttachFailure_已知错误_给可操作中文(string raw, string mustContain)
{
    var text = DebugSessionTool.DescribeAttachFailure(new InvalidOperationException(raw));
    Assert.Contains(mustContain, text);
    Assert.Contains("debug_disconnect", text);   // 给出恢复路径
    Assert.DoesNotContain("ERROR_INVALID_PARAMETER", text); // 原文不再裸露
}

[Fact]
public void DescribeAttachFailure_未识别错误_保留原文()
{
    var text = DebugSessionTool.DescribeAttachFailure(new InvalidOperationException("某种未知失败"));
    Assert.Contains("某种未知失败", text);
}

// Session：已有会话再 attach → 中文提示，且不触引擎
[Fact]
public async Task Attach_已有会话_中文拒绝()
{
    // 先建立会话（沿用本文件既有真实 attach 辅助），再 AttachAsync 另一次
    var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => mgr.AttachAsync(pid2));
    Assert.Contains("已有活动调试会话", ex.Message);
    Assert.Contains("debug_disconnect", ex.Message);
}
```

- [ ] **Step 2: 确认失败**。Run 两个 filter。Expected: FAIL / 编译失败。

- [ ] **Step 3: 实现**

要点：
1. `AttachAsync` 入口：`if (Active is not null) throw new InvalidOperationException($"已有活动调试会话（pid={Active.ProcessId}）。…请先 debug_disconnect 或 debug_terminate…");`（**在** `DebugSession.AttachAsync` **之前**）。
2. `DebugSession.CreateAsync`：`try { await start(core); } catch { await core.DisposeAsync(); throw; }`。
3. `DebugEngineCore.DisconnectAsync`：detach 失败时**记录并上报**（不再 `catch {}` 静默）；保留「进程已退出则忽略」的合理分支。
4. `DebugSessionManager.CloseAsync`：**先** `await DisconnectAsync()` **后**置空 `_active`（失败时按既有约定处理）。
5. `ForceResetAsync`：`lock` 取旧会话 → **只** `await old.DisposeAsync()`（其内部即 `Detach + CorDebug.Terminate`）。**严禁**调用 `Session.TerminateAsync`/`DebugEngineCore.TerminateAsync`——那是**杀目标进程**（`DebugSessionManager.cs:228-250` → `DebugEngineCore.cs:280` `process.Terminate(exitCode)`），与「只对会话负责、目标继续运行」矛盾。
6. 宿主：`DebugAttach` 用 `DescribeAttachFailure` 包装；新增 `debug_reset` 工具（参数仅 `CancellationToken`）。
7. 同步 `[Description]`（`debug_attach` 补「已有会话会拒绝」；`debug_reset` 说明**不杀目标**；`debug_disconnect` 补残留说明）。

- [ ] **Step 4: 确认通过**。Expected: PASS。

- [ ] **Step 4b: Session 回归用例（spec §6 要求，防漏）**

在 `tests/DotNetDebugger.Session.Tests/DebugSessionManagerTests.cs` 补两条：
```csharp
[Fact]
public async Task Attach_引擎启动失败_不泄漏引擎实例() { /* attach 不存在/非 .NET 进程 → 失败；断言可立即再次正常 attach（不残留） */ }

[Fact]
public async Task CloseAsync_断开失败_不置空活动会话() { /* 构造断开失败场景 → 断言 Active 仍非 null（不谎报已断开） */ }
```
（第二条若难以构造失败注入，退化为断言「CloseAsync 成功后 Active 为 null 且再次 attach 成功」并注明局限。）

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "fix(debug): 重复 attach 中文拒绝/COM 错误中文化 + 修引擎生命周期泄漏 + debug_reset（不杀目标）"
```

---

## Task 7: P3 `clickPoint` opt-in + legacy 文案 + 护栏

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs`、`src/SharpSight.UiAutomation/UiAutomationService.cs`、`UiPatternDispatcher.cs`、`UiModels.cs`、`tests/DotNetDebuggerMcp.Tests/NoPhysicalInputGuardTests.cs`、`AgentCopyGuardTests.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/UiAutomationServiceTests.cs`、`NoPhysicalInputGuardTests.cs`

**Interfaces:**
- Produces:
  - `UiPatternDispatcher.Choose(string verb, UiPatternCapabilities caps, string direction, int lines, string windowstate, bool clickPoint = false)` — **默认 `false`**（既有 17 处 5 参 `Choose(...)` 调用与 `ActionCore` 不受影响）；`clickPoint=true` 且 verb ∉ {invoke, select} → 抛中文 `UiException`。
  - `ActionCore`/`ActionAsync` 的 `clickPoint` 形参同带 `= false`（透传链不破坏既有调用）。
  - `internal static void PerformClickPoint(AutomationElement element)` — **唯一**真实点击调用点（`element.Click(moveMouse: false)`），供护栏断言唯一性。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Choose_clickPoint_true_且verb非法_抛中文()
{
    var caps = new UiPatternCapabilities(
        Invoke: false, Toggle: false, SelectionItem: false, ExpandCollapse: false,
        Value: false, RangeValue: false, Scroll: false, ScrollItem: false, Window: false, Legacy: true);
    var ex = Assert.Throws<UiException>(() => UiPatternDispatcher.Choose("expand", caps, "", 0, "", clickPoint: true));
    Assert.Contains("clickPoint", ex.Message);
}
```

- [ ] **Step 2: 确认失败**。

- [ ] **Step 3: 实现**

要点：① `ui_action` 加 `clickPoint`（默认 `false`）参数与 `[Description]`（明示物理输入/抢前台）。② `Choose` 加 `bool clickPoint = false` 并做 verb 校验；`ActionCore`/`ActionAsync` 同加默认值形参并透传；命中 `LegacySelect`/`LegacyDefaultAction` 且 `clickPoint=true` 时改走 `PerformClickPoint`。③ `clickPoint=false` 且命中 legacy → 结果文案标注「该控件仅暴露 legacy，**可能不生效**；无效时可试 `clickPoint=true`（会真实点击）」。④ 握手 `ui` 段补 `clickPoint` 声明。

- [ ] **Step 3b: 护栏改造（`NoPhysicalInputGuardTests`）**

> **为什么不能只把 `AutomationElement::Click` 加进 IL 黑名单**：该测试遍历 `reader.MemberReferences` **整表**（`:115-120`，按程序集、不定位方法体），而 `PerformClickPoint` 落在被扫的 `SharpSight.UiAutomation` 程序集 → 只要真调 `Click`，该程序集必现 `AutomationElement::Click` MemberRef，IL 检查**恒违规**。且 FlaUI 真实入口不止 `Click`（`DoubleClick(bool)`/`RightClick(bool)`/`RightDoubleClick(bool)` 同经 `Mouse.Position/SendInput`）。

改为两类断言：
1. **源码调用点唯一**：扫描 `src/` 所有 `.cs`，`.Click(`/`.DoubleClick(`/`.RightClick(`/`.RightDoubleClick(` 的**调用点必须只在 `UiAutomationService.cs` 的 `PerformClickPoint` 方法体内**（其余文件/方法出现即失败）——真正防「绕过 `clickPoint` 直接真实点击」。
2. **IL 黑名单保留** `FlaUI.Core.Input` 命名空间 + `SetCursorPos`/`SendInput`/`SetForegroundWindow`（不变）；**不**加 `Click` MemberRef（会与合法调用共存导致恒红）。

- [ ] **Step 4: 确认通过**。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(ui): ui_action 新增 clickPoint 显式真实点击 + legacy 命中文案标注可能无效"
```

---

## Task 8: P4 index 文案修复

**Files:**
- Modify: `src/SharpSight.UiAutomation/UiElementLocator.cs:350-362`（`GetCachedEntry`）、`:298-303`（歧义引导）
- Test: `tests/DotNetDebuggerMcp.Tests/UiElementLocatorPureTests.cs`

**Interfaces:**
- Produces: `internal static string DescribeIndexMiss(int pid, int currentPid, int cacheCount, int index)` — 抽出的**纯函数**（`GetCachedEntry` 的文案选择逻辑），供 `UiElementLocatorPureTests` 直接断言（`GetCachedEntry` 是 private 且构造缓存需真 UIA，不可纯测）。

- [ ] **Step 1: 写失败测试**

```csharp
[Theory]
[InlineData(100, 200, 5, 0, "不属于")]        // pid 不一致
[InlineData(100, 100, 0, 0, "已被后续查询覆盖")] // pid 一致但缓存空/被覆盖——不得说 pid 不匹配
public void DescribeIndexMiss_区分pid不符与缓存被覆盖(int pid, int cur, int count, int index, string mustContain)
{
    var text = UiElementLocator.DescribeIndexMiss(pid, cur, count, index);
    Assert.Contains(mustContain, text);
    Assert.Contains("ui_find", text); // 引导补救
}

[Fact]
public void DescribeIndexMiss_pid一致_不出现自相矛盾的pid文案()
{
    var text = UiElementLocator.DescribeIndexMiss(100, 100, 0, 3);
    Assert.DoesNotContain("当前缓存 pid=100", text); // 旧 bug：pid 一致却打印「不是 pid=100 的结果（当前缓存 pid=100）」
}
```

- [ ] **Step 2: 确认失败**。

- [ ] **Step 3: 实现**

抽出 `DescribeIndexMiss`（两支：`pid != currentPid` → 「…不属于进程 pid={pid}（当前缓存属于 pid={currentPid}）…」；否则 → 「ui_find 结果缓存已被后续查询覆盖或为空（index 只在紧邻上一次 ui_find 内有效），请重新 ui_find」）。`GetCachedEntry` 改调它。`FindUniqueByName` 的歧义文案改为**优先引导 name+type**。

- [ ] **Step 4: 确认通过**。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "fix(ui): 修正 index 缓存失效的误导文案并引导 name+type 定位"
```

---

## Task 9: P5 `ui_find` 的 `scope` / `depth`

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs`、`src/SharpSight.UiAutomation/UiElementLocator.cs`（`FindCore` 增 `scopeIndex`/`maxDepth`）、`UiAutomationService.cs`（透传）
- Test: `tests/DotNetDebuggerMcp.Tests/UiElementLocatorPureTests.cs`、`DebugUiToolsTests.cs`

**Interfaces:**
- Produces: `UiElementLocator.Find(..., int scopeIndex = -1, int maxDepth = 0)`（`FindForCapture` 同签名保持缺省）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void Find_depth限定_不含更深层()
{
    // 以窗口为根、depth=1：结果只含窗口直接子级
}

[Fact]
public void Find_scope限定_只含子树()
{
    // scope=容器 index：结果只含该容器后代
}
```

- [ ] **Step 2: 确认失败**。

- [ ] **Step 3: 实现**

`ui_find` 加 `scope`（默认 `-1`）、`depth`（默认 `0`）参数与 `[Description]`（大表建议 scope+depth）；`FindCore` 按 `scopeIndex` 从 `_lastFind` 重解析根（复用 `ResolveByDescriptor`），`maxDepth>0` 时按 `TreeWalker` 层数限层；index 仍相对本次结果 0 基。超时提示补「大 UI 树请用 scope/depth」。

> **index 口径**：带 `scope`/`depth` 时结果 index 只覆盖子集，**不可**与 `screenshot element`（走 `FindForCapture`，无 scope/depth）跨用——在两块 `[Description]` 写明「带 scope/depth 的 index 仅限本工具本次结果，截图请用无过滤 `ui_find` 的 index」（根 `AGENTS.md` 既有契约）。

- [ ] **Step 4: 确认通过**（含真实 UiSampleApp e2e，环境不可用则 Skip）。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(ui): ui_find 新增 scope/depth 限定子树与层数（大 UI 树可用）"
```

---

## Task 10: P6 UIA 客户端释放 + 观测扰动声明

**Files:**
- Modify: `src/SharpSight.UiAutomation/UiAutomationService.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/UiAutomationServiceTests.cs`

**Interfaces:**
- Produces: `internal void ReleaseAutomation()`；`internal bool HasAutomation`（测试可观察）；`internal static Func<UIA3Automation>? AutomationFactoryForTest`（注入 seam——测试注入一个可计数的假工厂，断言「释放后置空 + 再次调用走工厂重建」，不真建 UIA 客户端）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void 调用结束后_客户端已释放_再次调用可重建()
{
    var built = 0;
    UiAutomationService.AutomationFactoryForTest = () => { built++; return new UIA3Automation(); };
    try
    {
        var svc = UiAutomationService.Instance;
        // 触发一次（无法脱机的可跳过真实调用，只验证 Release 语义）
        svc.ReleaseAutomation();
        Assert.False(svc.HasAutomation);
    }
    finally { UiAutomationService.AutomationFactoryForTest = null; }
}
```

- [ ] **Step 2: 确认失败**。

- [ ] **Step 3: 实现**

要点：`GetAutomation` 优先用 `AutomationFactoryForTest?.Invoke()`；`ReleaseAutomation()` dispose + 置 null；`ui_*` **整次工具调用结束**（`UiTools` 各方法 `finally`）调用 `ReleaseAutomation()`（接线在工具层，避免破坏 `WaitAsync` 跨 gate 订阅）。`[Description]`（`ui_find`/`ui_action`）+ 握手补「会注入 UIA 客户端、可能影响性能观测，性能类排查优先用 `debug_*`+截图」。

- [ ] **Step 4: 确认通过**。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "fix(ui): ui_* 调用结束释放 UIA 客户端 + 声明观测扰动"
```

---

## Task 11: P7 断开兜底 resume + 自检

**Files:**
- Modify: `src/DotNetDebugger.Engine/Engine/DebugEngineCore.cs:247-255`（`DisconnectAsync`）、`src/DotNetDebuggerMcp/Tools/Debugger/DebugSessionTool.cs`（`debug_disconnect` 自检文案）
- Test: `tests/DotNetDebugger.Engine.Tests/BreakpointTests.cs`（新增 detach-resume 用例）

**Interfaces:** 无新公共接口。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public async Task Disconnect_进程处停止态_目标继续运行()
{
    // attach → 命中断点（Stopped）→ DisconnectAsync → 断言目标未挂起
}
```

> **可观测判据（DebugTarget 是控制台程序，无窗口 → `Process.Responding`/`SendMessageTimeout` 不可用）**：改用 **stdout 心跳**——DebugTarget 在 `delay` 秒后继续打印 `[DebugTarget] work N: …` / `[DebugTarget] done`。断言 detach 后 N 秒内**收到新的 stdout 行**即为「已恢复运行」；配合断言 `DisconnectAsync` 不抛异常。（若采用 `DebugTargetProcess` 包装，其 stdout 已持续排空，可直接读缓冲。）

- [ ] **Step 2: 确认失败**（现有实现 detach 前不确保 resume，目标停住不再输出 → 断言超时/失败）。

- [ ] **Step 3: 实现**

`DisconnectAsync`（泵内）：detach 前若 `TryIsRunning` 为 false，循环 `Continue` 放行（上限防死循环），再 `Detach()`；失败保底 `_corDebug?.Terminate()`（**不是**进程 Terminate）。宿主 `debug_disconnect` 文案补自检（launch 会话查 `HasExited`/`Responding`；attach 报「无法判定」）与「外部残留附加需重启目标」。

- [ ] **Step 4: 确认通过**。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "fix(debug): 断开前兜底 resume，避免 detach 后目标仍暂停"
```

---

## Task 12: 收尾——文案护栏、文档、版本与全量回归

**Files:**
- Modify: `tests/DotNetDebuggerMcp.Tests/AgentCopyGuardTests.cs`、`README.md`、`CHANGELOG.md`、`docs/planning/specs/README.md`、`docs/planning/README.md`、各 `src/*/TODO.md`、`src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`、`src/DotNetDebuggerMcp/.mcp/server.json`

- [ ] **Step 1: V4 文案护栏补录**（P1/P2/P3/P6 关键片段）
- [ ] **Step 1b: `debug_verify` 说明补「ui 步骤需 `ui` 族启用」（spec §8.8 待定 F）**——`debug_verify` 的 `[Description]` 注明：场景含 `uiAction`/`uiAssert` 步骤时需服务端已启用 `ui` 族，否则运行期报错。
- [ ] **Step 2: 版本三处同步**（csproj `<Version>` + `server.json` 两处 + CHANGELOG `[Unreleased]` 转正），按仓库发布流程。
- [ ] **Step 3: 全量回归**

Run: `dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` → 各测试项目 → `dotnet run -c Release --project src/DotNetDebuggerMcp.Client/...`。
Expected: 全绿 + 端到端 PASS。

- [ ] **Step 4: 本机 CLI 抽查**（不受开关影响的证明）：`-a` 反编译、`-dbg` 一次性调试照常。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "docs+chore(release): 反馈修复收尾（文案护栏/文档/版本）/ 任务收口"
```

---

## Self-Review 记录

- **Spec 覆盖**：P1→T5、P2→T6、P3→T7、P4→T8、P5→T9、P6→T10、P7→T11、§8 开关→T1/T2/T3/T4；§6 回归与验证 → 各任务 Step 1（Session 两项见 T6 Step 4b）；§8.4 未知取值退出→T2 Step 4；§8.8 待定 F→T12 Step 1b；§6 文档面 → T4/T12。无遗漏。
- **独立审查修订记录（2026-10-15，子代理）**：已修 8 个阻塞/高危项——① `Parse` 的「未指定 vs 显式空」自相矛盾（改为 CLI 非 null 即权威）② 握手落点三处不一致（定案 `HandshakeText` + `DotNetDebuggerMcpCmd.BuildServerInstructions` 薄转发）③ `opencode.json` 无 `args` 字段（改为追加进 `command`）④ 护栏方案自相矛盾（改为源码调用点唯一 + 不动 IL 黑名单的 `Click`）⑤ `debug_reset` 误用 `Terminate` 可能杀目标（明确只 `DisposeAsync`）⑥ `Choose` 增参无默认值打断 17 处调用（加 `= false`）⑦ P4 纯测不可执行（抽 `DescribeIndexMiss`）⑧ §8.4 退出无承接；另修 9–19：P7 可观测判据（stdout 心跳）、Session 回归补用例、Web 只放宽读栈、attach 竞态轮询、测试辅助排空、scope index 口径、`UiPatternCapabilities` 参数、seam 真验证、待定 F。
- **类型一致性**：`FeatureFamily`/`TryRequireReadable`/`DescribeAttachFailure`/`PerformClickPoint`/`DescribeIndexMiss`/`ReleaseAutomation`/`Find(scopeIndex,maxDepth)` 在「Interfaces」块定义一次，后续任务只引用。
- **Review Focus 对应**：RF1→T2/T3 测试；RF2→T5；RF3→T7；RF4→T6；RF5→T4。
