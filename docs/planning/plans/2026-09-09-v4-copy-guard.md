# 实施计划 · V4 修复回归护栏（语料断言）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施。Step 用 `- [ ]` 追踪。

**Goal:** 把关键文案/行为契约固化为断言测试（只测关键片段、低脆），防 R1-R8 修复与新工具的提示语悄悄退化；**新工具落地强制同批补 V4 断言**。

**Architecture:** 宿主新增 `AgentCopyGuardTests`：① 反射读各 debug_* 工具 `[Description]` 断言含关键引导片段（Description 是 agent 唯一看到的契约面，不需会话/进程——纯内存）；② 真实调试返回文案的关键片段断言补进既有 `DebugMcpToolsTests` e2e 路径（不新增进程）。范围只宿主渲染层；不抽 Engine 错误常量（V4 spec 拍板）。

**Tech Stack:** C# net10.0、xunit.v3（`[Theory]`+反射）、MCP 工具 `[McpServerTool]` 元数据。

**Spec:** `docs/planning/specs/2026-09-08-v4-copy-guard.md`（2026-09-09 拍板：片段 Contains / 强制随新工具 / 只宿主层）。

## Global Constraints（根 AGENTS.md + 宿主 AGENTS.md 铁律）

- 测试放宿主测试项目并挂 `[Collection("AppServices")]`（若涉及静态单例则串行；纯反射 Description 测试可不挂——以是否触碰静态状态为准）。
- 只断言**关键片段**，不断言全文精确匹配（高脆）。断言语料优先引 `AppText`/`ToolParameterText` 常量，做不到时引字面量但加注释「语料契约，改文案须同步改此处」。
- 新工具计划（W1/V3/DB1/D1/D2 的 e2e 已含关键文案断言）在实施时须在收尾核对中标注 V4 语义；后续 debug_verify/ui_* 计划同样强制。

---

### Task 0: 工作区/分支准备

- [ ] **Step 1**: 与用户确认实施分支策略。
- [ ] **Step 2**: `generate-testdata.ps1` 就绪 + Release build 基线绿。

---

### Task 1: AgentCopyGuardTests — Description 契约 + e2e 文案补充

**Files:**
- Create: `tests/DotNetDebuggerMcp.Tests/AgentCopyGuardTests.cs`
- Modify: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`（e2e 返回文案断言补充，仅补不新建进程）
- Modify（可选项）: `README.md`、`CHANGELOG.md`（本次为测试内部护栏，无使用者可见变更——若顺带调整任一工具 Description 文案则必须同步 README/CHANGELOG）

- [ ] **Step 1: 写失败测试**

`AgentCopyGuardTests.cs`（纯内存，反射读 Description）：
```csharp
// 每对 = (工具方法, Description 必含关键片段) —— 提示语→期望动作的契约
public static TheoryData<string, string> ContractData => new()
{
    { nameof(DebugControlTool.DebugStep), "debug_wait" },        // step 提交后引导怎么等停点
    { nameof(DebugRunToTool.DebugRunTo), "临时断点" },            // run_to 一次性断点语义
    { nameof(DebugSessionTool.DebugLaunch), "冻结在 Main 前" },   // launch 早期断点语义
    { nameof(DebugSessionTool.DebugState), "Stopped" },           // 状态确认后再读栈/变量
    { nameof(DebugInspectTool.DebugVariables), "$exception" },    // 异常停点观察口
    { nameof(DebugBreakpointTool.DebugBreakpointList), "绑定" },  // list 含绑定状态
    { nameof(DebugExceptionTool.DebugExceptions), "first-chance" },
    // W1/V3 等落地后追加：debug_set 含「原值」/「风险」；debug_timeline 含「时间线」；debug_object 含「depth」
};

[Theory]
[MemberData(nameof(ContractData))]
public void ToolDescription_KeepsCriticalCopy(string methodName, string fragment)
{
    var method = typeof(...).Assembly.GetTypes()
        .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
        .Single(m => m.Name == methodName && m.GetCustomAttribute<McpServerToolAttribute>() is not null);
    var desc = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
    Assert.NotNull(desc);
    Assert.Contains(fragment, desc, StringComparison.Ordinal);
}
```
Run，Expected: 失败于缺失/变更项——**若个别既有 Description 现不含片段，属「语料与契约不符」，应先修 Description（并同步 README 对应处）再让测试过**。

- [ ] **Step 2: 让测试过（补齐既有缺口文案）**

按失败清单逐条核对：属「描述缺失契约片段」→ 改对应工具 `[Description]`（改 MCP 工具描述 = 使用者可见，同步根 README 工具表描述；若只增词不改行为，CHANGELOG 可并入下次工具变更）。跑定向测试全绿。

- [ ] **Step 3: e2e 文案断言补充（复用既有进程路径）**

在 `DebugMcpToolsTests` 既有 launch→step→wait→state 用例上补返回文本断言（用 `Assert.Contains`）：launch 返回含 `工作目录`、step 返回含 `debug_wait` 引导、wait 返回含 `最近停点`、异常断点 stop 后 debug_variables 含 `$exception`。**不新增真实进程用例**。

- [ ] **Step 4: 验证 + 提交**

宿主全量单测 + Client。`git commit -m "test: V4 语料回归护栏（AgentCopyGuardTests Description/e2e 文案契约）"`

---

## 收尾

- [ ] 把「V4 强制随新工具」落到 W1/V3/DB1/D1/D2 计划实施时的收尾核对（它们 e2e 已带关键文案断言；实施时对照 D21 政策再自查一遍）。
- [ ] 核对 `src/DotNetDebuggerMcp/TODO.md` V4 状态；`docs/planning/specs/README.md` 收录 V4 spec 行。

## Self-Review

- **Spec 覆盖**：关键片段 Contains（Task1）、宿主层范围（Task1 反射 Description + e2e 文案）、随新工具强制（收尾核对项 + spec/决策引用）。
- **占位符**：无 TBD；ContractData 为明示清单，落地后按失败逐条补齐。
- **风险点已标**：改动既有 Description = 使用者可见 → 同步 README；契约数据保持「关键片段」粒度避免脆断。
