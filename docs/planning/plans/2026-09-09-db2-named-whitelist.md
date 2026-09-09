# 实施计划 · DB2 debug_variables 按名白名单读取

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施。Step 用 `- [ ]` 追踪。

**Goal:** `debug_variables` 加 `names` 白名单参数（逗号分隔；空=全量），按名读取省 token 且缩小暴露面；未知名零值反馈不静默。宿主渲染层过滤，Engine/Session 零改动。

**Architecture:** 纯宿主改动——`DebugInspectTool.DebugVariables` 解析 `names`（split/trim/去重/≤50）→ 读全量 `GetVariablesAsync` 后在渲染前按名过滤（匹配 locals/arguments 的展示名与 `$exception`，忽略大小写），命中项复用 RenderVariable；白名单里的未知名在返回前单独列出（不含值）。先白名单命中，再对命中值走 DB1 脱敏（DB1 落地后自然叠加）。

**Tech Stack:** C# net10.0、ModelContextProtocol.Server。

**Spec:** `docs/planning/specs/2026-09-08-db2-named-whitelist.md`（2026-09-09 拍板）。

## Global Constraints（根 AGENTS.md + 宿主 AGENTS.md 铁律）

- MCP 工具参数带默认值、`[Description]` 中文注明默认值、`CancellationToken cancellationToken = default`、返回 `Task<string>`、错误中文不抛异常。
- 改 MCP 工具（加参/改行为）同 commit 改根 `README.md`；`CHANGELOG.md` `[Unreleased]` 记使用者可见变更。
- 不动 Engine/Session 只读模型；过滤属展示策略（与 DB1 同层）。

---

### Task 0: 工作区/分支准备

- [ ] **Step 1**: 与用户确认实施分支策略。
- [ ] **Step 2**: `generate-testdata.ps1` 就绪 + Release build 基线绿。

---

### Task 1: debug_variables 加 names 白名单

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugInspectTool.cs`（`DebugVariables:95`）
- Modify: `README.md`、`CHANGELOG.md`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

- [ ] **Step 1: 参数面 + 失败测试**

`DebugVariables` 增参：
```csharp
[Description("按名白名单（逗号分隔，空=全量）。精确忽略大小写匹配 局部/参数名（无符号名用 slotN）与 $exception；最多 50 项；未知名会在返回中列出可用名。")]
string names = "",
```
宿主 e2e 失败测试：停点（WorkBag 入口）→ `debug_variables names="b,n"` 返回只含 `[arguments] b`/`n`（不含其它）；`names="b,NoSuch"` 返回未知名反馈列出可用名且**不含值**；`names` 超 50 项拒绝提示；空 names 回归全量。Run 定向 Expected FAIL（参数尚不支持）。

- [ ] **Step 2: 实现过滤**

`DebugVariables` 内 `GetVariablesAsync` 之后：
```csharp
var requested = SplitNames(names);                 // null=全量；其余 trim/去重/忽略空
if (requested is { Count: > 50 })
    return $"names 项数 {requested.Count} 超上限 50——请缩小白名单或用空 names 取全量。";
...
foreach (var (scope, list) in vars)
{
    if (requested is null) { /* 现状全量渲染 */ }
    else
    {
        var wanted = list.Where(v => MatchName(v, requested, scope)).ToList();
        if (wanted.Count == 0) continue;
        lines.Add($"[{scope}]");
        foreach (var v in wanted) lines.Add(RenderVariable(v, depth: 1));
        hits += wanted.Count;
    }
}
```
辅助：
```csharp
private static List<string>? SplitNames(string names) // null when empty/blank
private static bool MatchName(DebugVariable v, List<string> requested, string scope)
{
    var name = v.Name ?? $"slot{v.Slot}";                       // 无符号名按展示 slotN 匹配
    if (scope == "exception" && name == "$exception") name = "$exception";
    return requested.Any(r => string.Equals(r, name, StringComparison.OrdinalIgnoreCase));
}
```
未知名反馈：把 `requested` 里未出现在任何 scope 展示名的项收集，返回尾段：
`未找到：{joined}（当前帧可用名：{可用名清单，不含值}——白名单需精确匹配，忽略大小写）。` 同名跨作用域（locals+arguments 同有 `n`）两节都渲染。返回头：`局部变量/参数（thread={tid}，白名单 {requested.Count} 项 → 命中 {hits} 项）`（全量模式头不动）。

- [ ] **Step 3: README + CHANGELOG + 验证 + 提交**

README `debug_variables` 参数/描述同步 names；CHANGELOG `[Unreleased]` 记「debug_variables 支持 names 按名白名单读取」。Run 定向 + 宿主全量 + Client（DB1 若已落地，命中的敏感名仍被脱敏——加一条交叉断言）。`git commit -m "feat: debug_variables 增 names 按名白名单（省 token/缩小暴露面，DB2，同步 README/CHANGELOG）"`

---

## 收尾

- [ ] Release build + 宿主全量单测 + Client 端到端。
- [ ] 核对 `src/DotNetDebuggerMcp/TODO.md` DB2 状态；`docs/planning/specs/README.md` 收录 DB2 spec 行。

## Self-Review

- **Spec 覆盖**：names 参数/空=全量/忽略大小写/slotN/$exception/>50 拒绝/未知名零值反馈/同名跨作用域（Task1）；与 DB1 脱敏叠加交叉断言（Task1 Step3）。
- **占位符**：无 TBD。
- **类型一致**：`SplitNames`/`MatchName` 私有助手 Task1 定义 Task1 用；`GetVariablesAsync` 返回 `IReadOnlyDictionary<string, IReadOnlyList<DebugVariable>>`（scope=locals/arguments/exception）。
- **风险点已标**：无符号名按 slotN 匹配（展示名与引擎一致）；白名单不含值泄露（未知名仅列名）。
