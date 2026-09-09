# 实施计划 · V1 一键复验闭环（debug_verify）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施。Step 用 `- [ ]` 追踪。

**Goal:** 新增 `debug_verify(scenarioPath)`：读结构化 JSON 场景（target 启动快照 + 可选 build + steps 断言序列），一条命令跑成 PASS/FAIL——重编译(可选，产物自动拿取) → `debug_launch` → 步骤执行 → fail-fast 断言 → 汇总，给 agent「改完 bug 自证修复」的最后一跳。**执行前置**：W1/U1/V3 未落地时只走纯断点+output+evaluate 断言版（evaluate 用 P6 读值即可）。

**Architecture:** 宿主新增 `Services/VerifyScenario.cs`（JSON 模型+解析）、`Services/VerifyBuildRunner.cs`（dotnet build + `-getProperty:TargetPath` 产物自动定位：排空、超时、错误摘要、文件名匹配校验）、`Services/VerifyService.cs`（步骤翻译=同进程直调 DebugSessionService/Session，不走 MCP 往返；断言原语；fail-fast；AgentActionLog 轨迹）；`debug_verify` 工具薄包装。Engine/Session 零改动（复用其门面）。

**Tech Stack:** C# net10.0、System.Text.Json、ModelContextProtocol.Server。

**Spec:** `docs/planning/specs/2026-09-08-v1-verify-loop.md`（2026-09-09 拍板：可选 build+项目默认输出 / 文件路径 / fail-fast / 类型预留+纯断点版先行）。

## Global Constraints（根 AGENTS.md + 宿主 AGENTS.md 铁律）

- MCP 工具参数带默认值、`[Description]` 中文注明默认值、`CancellationToken`、返回 `Task<string>`、错误中文不抛异常。
- 新工具落地同 commit 改根 `README.md` + `CHANGELOG.md` `[Unreleased]` + **V4 语料断言同批补**。
- 自起子进程（dotnet build / debug_launch 目标）必须**持续排空 stdout/stderr**（防管道阻塞卡死——仓库教训）。
- build 只重编场景指定工程，**不设置 OutputPath**；产物路径 = 项目默认生成路径，`target.commandLine` 写默认输出 exe。build 失败即停、绝不启动旧产物；build 成功但 commandLine 的 exe 不存在 → 轻校验提示。
- 编排直调 Session（`active.Session.SetBreakpointAsync/ContinueAsync/…`、`Buffer.WaitForStopAsync`、`active.Output.Tail`、`ExpressionEvaluator.EvaluateAsync`），不依赖 MCP 工具往返；每步写 AgentActionLog。
- 依赖未就绪步骤（`ui`/`set`）明确报「步骤类型依赖未就绪（U1/W1）」不静默跳过。

---

### Task 0: 工作区/分支准备

- [ ] **Step 1**: 与用户确认实施分支策略。
- [ ] **Step 2**: `generate-testdata.ps1` 就绪 + Release build 基线绿。

---

### Task 1: 场景模型 + JSON 解析

**Files:**
- Create: `src/DotNetDebuggerMcp/Services/VerifyScenario.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/VerifyScenarioTests.cs`

**Interfaces:**
- Produces: `VerifyScenario`（`Name`/`Target(CommandLine, WorkingDirectory, Environment)`/`Build?(Project, Configuration, TimeoutSeconds?)`/`Steps: IReadOnlyList<VerifyStep>`）；`VerifyStep` = `BreakpointStep(TypeName, MemberName, Hit=1)` / `ContinueStep(WaitSeconds)` / `AssertStep(AssertKind, 参数)` / `UiStep`(预留) / `SetStep`(预留)；`AssertKind ∈ breakpointHit/evaluate/output/state/noException`。解析失败抛中文 `VerifyFormatException`（含路径/字段名/行号线索）。

- [ ] **Step 1: 写失败测试**

合法场景解析（含 build、全部步骤类型、evaluate 参数 path/equals 或 output 参数 contains/stream、assert 引用断点 index）→ 字段断言；非法：文件不存在/JSON 语法错/未知步骤 kind/未知 assert kind/evaluate 缺 equals 或 contains/output 同/断点 step 缺 memberName+typeName → 中文错误含具体字段。Run，Expected: FAIL（类型不存在）。

- [ ] **Step 2: 实现**

`VerifyScenario.Parse(string path)`（System.Text.Json，`JsonDocument` 手解析以给中文错误/避免多态反序列化复杂度）；校验互斥：`AssertStep.evaluate` 的 `equals` 与 `contains` 二选一（都空或都有报错）；`assert.breakpointHit` 的 `breakpointIndex` 引用场景内断点步骤序号（越界报错）。`UiStep/SetStep` 解析通过但标记 `Requires = "U1"/"W1"`。

- [ ] **Step 3: 运行测试 + 提交**。`git commit -m "feat: VerifyScenario 场景 JSON 模型与解析（V1）"`

---

### Task 2: VerifyBuildRunner（默认输出、排空、超时、轻校验）

**Files:**
- Create: `src/DotNetDebuggerMcp/Services/VerifyBuildRunner.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/VerifyBuildRunnerTests.cs`

**Interfaces:**
- Produces: `static Task<BuildOutcome> RunAsync(string project, string configuration, int timeoutSeconds, CancellationToken ct)`；`BuildOutcome(bool Ok, string Summary, int ExitCode)`（Summary 失败含 stderr 尾部，成功含「编译成功」）。`static Task<string?> GetTargetPathAsync(string project, string configuration, CancellationToken ct)`（`dotnet msbuild <project> -p:Configuration=<cfg> -getProperty:TargetPath` → 产物绝对路径；失败返回 null）。

- [ ] **Step 1: 写失败测试**

对一份已知临时 csproj（测试内 `dotnet new console` 到临时目录）RunAsync：成功路径 ExitCode 0 + Summary 含「编译成功」；坏工程（故意语法错源文件）→ Ok=false + Summary 含错误；`GetTargetPathAsync` 成功后返回**以 .exe/.dll 结尾的绝对路径**且 `File.Exists` true；不存在的 csproj → RunAsync Ok=false 中文提示「工程文件不存在」、GetTargetPathAsync 返回 null。Run，Expected: FAIL（类型不存在）。

- [ ] **Step 2: 实现**

`ProcessStartInfo`（`dotnet build <project> -c <config> --nologo -v m`，WorkingDirectory=工程目录，重定向输出）→ 持续排空到 StringBuilder（保留尾部 max 40 行）+ `WaitForExitAsync(timeout)`；超时 Kill + Ok=false「编译超时」；ExitCode!=0 → 尾部行拼接摘要。`GetTargetPathAsync` = 同款子进程 `dotnet msbuild <project> -p:Configuration=<config> -getProperty:TargetPath` 捕获 stdout 首行非空 trimmed。错误统一中文。

- [ ] **Step 3: 运行测试 + 提交**。`git commit -m "feat: VerifyBuildRunner（默认输出 dotnet build + 排空/超时/轻校验，V1）"`

---

### Task 3: VerifyService 执行器（步骤翻译 + 断言 + fail-fast）

**Files:**
- Create: `src/DotNetDebuggerMcp/Services/VerifyService.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/VerifyServiceTests.cs`（桩数据纯内存：断言判定各 kind pass/fail；真实会话走 Task 4 e2e）

**Interfaces:**
- Consumes: `DebugSessionService.Manager`（`LaunchAndAttachAsync`、`Active`）；`ExpressionEvaluator.EvaluateAsync`（Session，evaluate 断言）；`DebugBreakpointTool` 的成员级定位解析（`ResolveBreakpointTargetAsync`，若 internal 可复用；否则 VerifyService 内自含同款 typeName+memberName → token 解析，走 `active.Session.SetBreakpointAsync`）。
- Produces: `static Task<string> VerifyAsync(string scenarioPath, CancellationToken ct)`（结果文本）；内部逐步记录进 `Manager.Actions.Log("debug_verify", step, outcome)`。

- [ ] **Step 1: 断言判定（纯内存先测）**

每断言 kind 的判定函数（输入 = 场景上下文：命中断点 id 集/`Buffer.LastStop`/evaluate 结果/`Output.Tail`/当前 state/异常计数）返回 bool + 失败理由中文：
- `breakpointHit(index)`：对应断点步骤在最近停点命中（`LastStop.BreakpointId == bp.Id`）
- `evaluate(path, equals|contains)`：`ExpressionEvaluator.EvaluateAsync(active.Session, tid, path)` → `Display`/`ScalarValue` 比较（equals=精确；contains 用于 Display 含子串）
- `output(contains, stream)`：`Output.Tail(50, filter)` 命中
- `state(expect)`：`Buffer.CurrentState` 在预期集合（Stopped/Exited）
- `noException`：场景期间无 `ExceptionHit` 事件（执行器累计）
单测用桩上下文直接调判定函数（不碰真实会话）。

- [ ] **Step 2: 执行器（编排序列）**

```
解析场景 → 无会话则 debug_launch(target)；有会话则先 Close/断开重开（复现快照语义）
build 字段存在：
  1) VerifyBuildRunner.RunAsync(project, config) — Ok=false → FAIL（编译错误摘要，绝不启动旧产物）
  2) GetTargetPathAsync → null → FAIL「无法解析产物路径（工程/配置名错？）」
  3) target.commandLine 首段 exe 文件名 = Path.GetFileName(TargetPath)（忽略大小写）？
     不一致 → FAIL「commandLine 首段 {x} 与编译产物 {TargetPath 文件名} 不一致——请写工程入口文件名」
     一致 → launch = TargetPath + 剩余参数
build 字段不存在：launch 按 commandLine 原样（完整路径/PATH 命令；不存在 → 启动失败中文提示）
for step in steps:
  breakpoint → Resolve → active.Session.SetBreakpointAsync(...)（记 bpId 供 assert 引用）
  continue   → active.Session.ContinueAsync + Buffer.WaitForStopAsync(waitSeconds)（超时=步骤失败）
  assert     → Step1 判定；false → FAIL(fail-fast)，返回失败步骤+实际上下文（停点/输出尾部）
  ui/set     → 依赖未就绪 → FAIL「步骤类型依赖未就绪（U1/W1）」（预留，不实现）
结束：目标继续跑或 disconnect？——v1 结束即 Close（disconnect 目标独立运行），返回 PASS/FAIL 汇总
```

- [ ] **Step 3: 验证 + 提交**：Task1/2 单测绿 + 桩断言单测。`git commit -m "feat: VerifyService 执行器（步骤翻译+断言+ fail-fast，V1）"`

---

### Task 4: debug_verify 工具 + e2e + README/CHANGELOG

**Files:**
- Create: `src/DotNetDebuggerMcp/Tools/Debugger/DebugVerifyTool.cs`
- Modify: `README.md`、`CHANGELOG.md`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugVerifyToolTests.cs`

- [ ] **Step 1: 工具**

```csharp
[McpServerTool]
[Description("一键复验：读场景 JSON 文件（target 启动快照 + 可选 build 重编译 + steps 断言序列）并执行到 PASS/FAIL。启动/断点/output/evaluate 断言走本进程 Session；build 用项目默认输出路径（不设置 OutputPath）；断言失败即停（fail-fast）。场景格式示例见 README。改完源码后用它自证修复。")]
public static async Task<string> DebugVerify(
    [Description("场景 JSON 文件绝对路径（必填）。")] string scenarioPath,
    CancellationToken cancellationToken = default)
```
空/文件不存在/解析错 → 中文；执行结果原样返回 VerifyService。Actions.Log 在 VerifyService 内逐步打。

- [ ] **Step 2: e2e（真实闭环）**

`DebugVerifyToolTests`：用 DebugTarget 造可复现场景（不需要改 DebugTarget 源码也能跑纯断点+output+evaluate 版：场景 = breakpoint Work 入口 → continue → assert evaluate `n`==传入值 → output contains `[DebugTarget] done`）；场景文件写临时目录，`debug_verify` → PASS；再写一个必然 FAIL 场景（evaluate 期望错值/断点永不命中+超时）→ FAIL 文案含失败步骤。build 自动拿产物冒烟：临时 console 工程（`dotnet new console`）→ 场景含 build + commandLine 写 `MyApp.exe`（只文件名）→ verify build→自动拿 TargetPath 启动→ output 断言 PASS；错误源码 → FAIL 含编译摘要；commandLine 文件名与产物不一致 → 中文提示。Run 定向 + 宿主全量 + Client。
README：工具表 + 场景 JSON 示例（§3.1 样例简化版）+ 风险/边界（build 需本机 SDK、产物自动定位说明、无源码场景不写 build）。CHANGELOG 记 debug_verify。V4：ContractData 加 `debug_verify`（「场景」/「PASS」）片段。
`git commit -m "feat: 新增 debug_verify 一键复验工具（V1，产物自动拿取，同步 README/CHANGELOG/V4）"`

---

## 收尾

- [ ] Release build + 宿主全量 + Client。
- [ ] 核对 `src/DotNetDebuggerMcp/TODO.md` V1 状态；`docs/planning/specs/README.md` 收录 V1 spec 行。
- [ ] ui.*/set 步骤在 U1/W1 落地后按预留契约补充实现（后续批次）。

## Self-Review

- **Spec 覆盖**：可选 build+产物自动定位 GetTargetPathAsync+文件名匹配（Task2/3）、文件路径（Task1/4）、fail-fast（Task3）、断言原语集（Task1/3）、evaluate 走 P6 不依赖 W1（Task3 Step1）、类型预留 ui/set（Task3 Step2）、排空子进程（Task2）、V4/README/CHANGELOG（Task4）。
- **占位符**：无 TBD；步骤翻译所需内部定位解析若 `ResolveBreakpointTargetAsync` 不可复用则以自含解析补足（Task3 注明双路径）。
- **类型一致**：`VerifyScenario`/`VerifyStep` Task1 定义 Task3/4 用；`BuildOutcome`/`GetTargetPathAsync` Task2 定义 Task3 用；`VerifyAsync` Task3 定义 Task4 用。
- **风险点已标**：build/msbuild 与 debug_launch 子进程排空（防管道阻塞）；build 失败绝不启动旧产物；commandLine 文件名与产物不一致提示；场景 fail-fast 停止后目标断开（disconnect 语义）；多轮复验每次重开快照会话。
