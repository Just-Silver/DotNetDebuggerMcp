# 实施计划 · W3 数据断点（值变化即停）——spike 驱动三分支

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施。Step 用 `- [ ]` 追踪。
> **本计划为「spike 前置」型**：Task 0 是实测 spike，结论（A 可行 / B 可行 / 降级）决定走哪个分支 Task——分支块均已写死，实施按结论执行对应块并跳过其余（降级块为文档收尾）。

**Goal:** `debug_breakpoint_set` 支持 `dataPath`——字段/局部变量值被改时停下（值变化断点），复现「某字段被莫名改错（多线程/回调/副作用）」场景。技术路线由 spike 实测三问定夺。

**Architecture:** Engine 复用 BreakpointManager/命令泵/CallbackHandler 停点体系 + P6 `ReadPathValue` 定位数据目标；入口并入 `debug_breakpoint_set`（dataPath 参数）。A/B 分支实现路径不同但对外契约一致（`dataPath` 定位 + 命中停 + 复用计数/条件/清单/移除）；降级分支零 Engine 代码。

**Tech Stack:** C# net10.0、ClrDebug 0.4.2（`CorDebugValue.CreateBreakpoint` / `ICorDebugManagedCallback4.OnDataBreakpoint`）、DbgShim。

**Spec:** `docs/planning/specs/2026-09-08-w3-data-breakpoint.md`（2026-09-09 拍板：spike Task0 + breakpoint_set.dataPath 入口 + 降级=说明+ROADMAP 零代码）。

## Global Constraints（根 AGENTS.md + Engine/宿主 AGENTS.md 铁律）

- MCP 工具加参（dataPath）同 commit 改根 `README.md`；`CHANGELOG.md` `[Unreleased]` 记使用者可见变更；**V4 语料断言同批补**（Description 含「dataPath」片段）。
- Engine 纯能力层纪律：全部 ICorDebug 调用在命令泵 MTA 线程；回调线程只入队；值对象定位复用 P6 `ReadPathValue`（泵内同步）。
- 测试真实 attach DebugTarget、串行；改 DebugTarget 只追加不 rename（token 稳定）。
- spike 测试文件为临时物，结论落地后删除/并入正式测试；**spike 失败分支同样要跑通降级收尾再算计划完成**。

---

### Task 0: 工作区准备 + spike 实测（三分支决策门）

- [ ] **Step 1**: 与用户确认实施分支策略 + 说明本计划执行会先跑 spike（真实 attach DebugTarget，耗时秒级）。
- [ ] **Step 2**: `generate-testdata.ps1` 就绪；给 DebugTarget 追加写场景锚点（append 不改既有）：`ValueProbe.Run(ValueHolder h, int[] arr)`——线程/回调里 `h.Counter++`、`arr[2]=999`，Main 加 `probe-value` 分支。

- [ ] **Step 3: 写 spike 测试（临时 `tests/DotNetDebugger.Engine.Tests/DataBreakpointSpikeTests.cs`）**

骨架复刻 `EvaluatePathTests`（attach → 断点 `ValueProbe.Run` 入口 → 停）。spike 三问各自一测（不 assert 硬结论，打印 `[spike]` 行收集）：
```csharp
// 问1 A-局部：停点 GetLocalVariable 拿 int 局部 → CreateBreakpoint() → continue → 改值 → 是否触发/触发条件是什么
// 问2 A-字段：停点 ReadPathValue 定位 h.Counter → CreateBreakpoint() → continue → 回调改值 → 是否触发（字段值对象是否活引用）
// 问3 B-创建端：查 ICorDebugProcess/ICorDebugProcess5/新接口是否暴露数据断点创建（ClrDebug 封装 grep）；回调节点是否只在 A 触发后出现
```
Run `dotnet test --project tests/DotNetDebugger.Engine.Tests/... --filter DataBreakpointSpike -- --output Detailed`。

- [ ] **Step 4: 记录决策，走对应分支**

按实测输出更新本计划/`spec` 决策注：**A 可行** → Task A；**B 可行且创建端找到** → Task B；**A/B 均不可行** → Task D（降级收尾）。删除临时 spike 测试（结论断言并入正式测试）。提交 spike 结论 + DebugTarget 样本（若走 A/B）。

---

### Task A（A 分支：ValueBreakpoint 版）

**Files:**
- Modify: `src/DotNetDebugger.Engine/Engine/BreakpointManager.cs`（数据断点登记表：dataPath→值对象、失效清理）
- Modify: `src/DotNetDebugger.Engine/Engine/DebugEngineCore.cs`（`SetDataBreakpointAsync`/定位/命中接线）
- Modify: `src/DotNetDebugger.Engine/Engine/CallbackHandler.cs`（数据断点命中事件识别 → BreakpointHit 停）
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugBreakpointTool.cs`（dataPath 参数）
- Test/修改 spec/README/CHANGELOG/V4

**Interfaces:**
- Produces（Engine）：`DebugSession.SetDataBreakpointAsync(threadId, rootName, segments, hitCount, condition, ct)` → `DebugBreakpoint`（Id 并入既有断点序号体系）；数据断点命中发 `DebugEvent(BreakpointHit)`（Payload 附 dataPath 标识/ThreadId）；值对象失效（GC/帧离开）→ 自动移除 + `EngineLog` 提示。
- Produces（宿主）：`debug_breakpoint_set` 加 `dataPath`（P6 路径文法），`debug_breakpoint_list`/`remove`/`clear`/`debug_exceptions` 语义不变（命中走断点清单）。

- [ ] **Step 1: 失败测试（Engine）**：probe-value 场景——attach → data 断点设 `h.Counter` → continue → 回调改值 → 命中断言（含 dataPath 标识）；`arr[2]` 数组元素数据断点；失效路径（帧离开后断点自动清理提示）。
- [ ] **Step 2: 实现**：`SetDataBreakpointAsync` 泵内定位（复用 `ReadPathValue`/值对象解析）→ 值对象 `CreateBreakpoint()`（ClrDebug 已封装，spike 确认调用姿势）→ 登记（dataPath/id）→ CallbackHandler 命中识别（值断点回调 kind）→ 停 + 发事件。失效：值对象不可用（访问抛/回调标识）→ 移除 + EngineLog。
- [ ] **Step 3: 宿主 dataPath + README/CHANGELOG/V4**：`debug_breakpoint_set` dataPath 参数（与 moduleName/token/typeName+memberName/line 互斥分支——dataPath 非空走数据断点）；描述示例 `dataPath="holder.Counter"`；命中/清单含「数据断点 @ 路径」标注；e2e（probe-value）+ V4 Description 片段。
- [ ] **Step 4: 验证 + 提交**（Engine + 宿主全量 + Client）。

---

### Task B（B 分支：现代 DataBreakpoint 创建端）

**Files:** 同 A（创建/登记路径换成 spike 找到的创建端 API）。

- [ ] **Step 1**: 按 spike 问3结论实现创建端封装（ClrDebug 有封装则用；无封装则 P/Invoke/COM 新接口手封装——以 spike 查证为准；**若最终确认无可用创建端 → 归入降级 Task D**）。
- [ ] **Step 2-4**: 同 A 分支（Engine 命中/宿主 dataPath/e2e/文档/V4）。

---

### Task D（降级分支：说明 + 转 ROADMAP，零 Engine 代码）

- [ ] **Step 1**: 把 spike 实测结论（A 为何不可行：值对象存活/触发语义局限；B 为何无创建端）写入 spec §2「结论」与根 README（数据断点暂不支持 + 局限 + 「已知写入点用条件断点比较（如 evaluate i==期望 → 停）」指引）。
- [ ] **Step 2**: `docs/ROADMAP.md` 记「W3 数据断点——不可行结论 + 触发条件（若未来 ICorDebug 暴露创建端再评估）」；宿主 TODO W3 状态 → 已评估转 ROADMAP。
- [ ] **Step 3**: 提交（文档），无代码。计划完成。

---

## 收尾（任意分支）

- [ ] 若 A/B：Engine 全量 + 宿主全量 + Client + README/CHANGELOG/V4 同步；若 D：文档收尾即可。
- [ ] 核对 `src/DotNetDebuggerMcp/TODO.md` W3 状态（实施完成 或 转 ROADMAP）；`docs/planning/specs/README.md` 收录 W3 spec 行。

## Self-Review

- **Spec 覆盖**：三问 spike（Task0）、A/B/D 三分支全写死、入口 breakpoint_set.dataPath（A/B Task3）、复用 hitCount/condition/list/remove（A/B Task2/3）、降级零代码+ROADMAP（TaskD）、V4/README/CHANGELOG（A/B）。
- **占位符**：无 TBD；B 分支「创建端以 spike 查证为准，确无则归降级」是计划内的决策门而非占位。
- **类型一致**：`SetDataBreakpointAsync(threadId, rootName, segments, …)` A/B 共用契约；`debug_breakpoint_set.dataPath` A/B 宿主侧一致。
- **风险点已标**：值对象生命周期（失效清理+提示）；B 创建端不确定性（决策门）；spike 为临时物结论后删除；DebugTarget 只追加样本保 token。
