<!-- 职责：MCP 工具面重构（命名统一/改名/合并/缺陷修复/描述瘦身）的逐任务实施步骤；不写：设计依据与取舍（见 specs/2026-10-20-mcp-tool-surface-design.md）。 -->

# MCP 工具面重构 实施计划（已执行）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 `superpowers:subagent-driven-development`（推荐）或 `superpowers:executing-plans` 逐任务实施。步骤用 `- [ ]` 复选框跟踪。

**Goal:** 把 53 个 MCP 工具的命名收敛到"agent 学一次即可用到底"，必填参数归零，合并到 42 个，并修掉 7 个实测缺陷。

**Architecture:** 工具面（工具名/参数名/描述/合并）只动宿主；缺陷按所在层动（D1 → Engine+宿主兜底、D3 → Session、D2 → 宿主文案 + Decompiler 只读探测）。合并一律"新方法入口 + `action`/`mode` 分派 + 内部调原实现"，不重写能力。

**Tech Stack:** .NET 10（`net10.0-windows10.0.22621.0`）、ModelContextProtocol 2.2.0（宿主）、ClrDebug/ICorDebug（Engine）、xunit.v3 + MTP。

**Spec:** `docs/planning/specs/2026-10-20-mcp-tool-surface-design.md`

## Global Constraints

- **所有 MCP 工具参数必须带默认值**（不声明可空）；缺参进入方法体由校验返回中文提示，不得在绑定阶段抛英文 `Missing key`。
- **每个工具方法带 `CancellationToken cancellationToken = default`**（SDK 注入、不暴露为参数）。
- **工具返回 `Task<string>`，一切错误返回中文提示、不抛异常**；`screenshot` 图片类例外（`CallToolResult`）。
- **改工具面必须同 commit 同步四处**：工具 `[Description]`、握手 `HandshakeText`、`README.md` 工具表、`CHANGELOG.md`。
- **能力族注册表 `ToolFeatureRegistry` 必须覆盖新工具类型**；改名/合并不新增类型时可不动，但 `ToolFeatureRegistryTests` 必须仍绿。
- **CLI 执行路径不受影响**：`DispatchCliAsync` 调的是 C# 方法（`DebugSessionTool.Disconnect` 等），合并时保留这些方法，只把 MCP 入口换成 `action` 分派。
- **stdout 只承载 MCP 协议帧；日志走 stderr**——严禁改动。
- **单方法 ≤ 60 行为宜、> 80 行必须拆**；`action`/`mode` 分派天然是 `switch`，允许保留 `switch`、内部按分支提小函数。
- **测试只写业务行为**；不为"参数名是否存在"单独写测试，改用 §Task 0 的 schema 快照（它断言的是"agent 看到的契约"）。
- 前置：`powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`。

## Review Focus

1. **零英文错误**：任何缺参/非法值都必须是中文提示。验收方式：对每个工具空调用一遍，扫是不是 `Missing key` / `Missing required` / HRESULT。
2. **改名后的死角**：`grep -r "call_chain\|call_graph\|search_string\|debug_state\|debug_breakpoint_set\|debug_breakpoint_list\|debug_breakpoint_remove\|debug_breakpoint_clear\|debug_exceptions_clear\|debug_disconnect\|debug_terminate\|debug_reset\|debug_variables\|debug_stack\|decompile_to_project\|screenshot_displays\|screenshot_windows"` 在 `src/` 与 `README.md` 必须只剩历史文档与 CHANGELOG 的迁移说明。
3. **合并工具的参数污染**：`debug_breakpoint(action=list)` 传 `token` 应被忽略或明确拒绝，不能静默按 set 处理。
4. **`debug_frame` 的实际信息量**：一次调用必须同时给出调用栈与该帧变量，否则合并无意义。
5. **async 样本**：D2 的回归必须有"async 方法体内调用具名静态方法"的样本，否则标注逻辑永远测不到。

---

## File Structure

**新建**
- `tests/DotNetDebuggerMcp.Tests/ToolSurfaceContractTests.cs` — 工具面契约快照（工具名集合、每工具参数名集合、`required` 全空）。
- `src/DotNetDebuggerMcp/Configuration/ToolSurfaceText.cs` — 三段式描述模板与 `action`/`mode` 合法值常量（避免描述里散落字面量）。

**修改（按任务）**
- 工具：`Tools/Metadata/CallChainTool.cs`、`CallGraphTool.cs`、`SearchStringTool.cs`、`DecompileIlTool.cs`、`DecompileToDirTool.cs`、`DecompileToProjectTool.cs`、`Tools/Screenshot/*`、`Tools/Debugger/*`。
- 配置：`Configuration/HandshakeText.cs`、`Configuration/AppText.cs`、`Configuration/ToolParameterText.cs`。
- 格式化：`Formatting/FormatContext.cs`（头部路径显示）。
- 测试：`DebugMcpToolsTests.cs`、`CallGraphToolTests.cs`、`MetadataCacheTests.cs`、`AgentCopyGuardTests.cs`、`DotNetDebuggerMcpCmdTests.cs`、`McpSessionConcurrencyTests.cs`。
- 端到端：`src/DotNetDebuggerMcp.Client/*.cs`。
- 文档：`README.md`、`CHANGELOG.md`、`docs/planning/specs/README.md`、`docs/planning/README.md`。
- 测试数据：`tests/TestData/generate-testdata.ps1`（async 调用者样本）。

---

## Task 0: 工具面契约快照（护栏先行）

**Files:**
- Create: `tests/DotNetDebuggerMcp.Tests/ToolSurfaceContractTests.cs`
- Modify: `tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj`（如需引用宿主 internal）

**Interfaces:**
- 用 `ToolFeatureRegistry.AllToolTypes()` + 反射读 `[McpServerTool]` 方法与参数，产出快照：
  - `IReadOnlyList<(string ToolName, string[] Parameters, string[] Required)>`
- 断言：**`Required` 必须全部为空**（这是 §4.3 的机器护栏，后续任务靠它兜底）。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void 所有工具参数均为可选_缺参由方法体返回中文()
{
    var offenders = ToolSnapshot.All()
        .Where(t => t.Required.Length > 0)
        .Select(t => $"{t.Name}: {string.Join(",", t.Required)}")
        .ToList();
    Assert.Empty(offenders);
}

[Fact]
public void 工具面快照_与基线一致()
{
    // 首次运行生成基线文件 tests/DotNetDebuggerMcp.Tests/Snapshots/tool-surface.txt
    // 后续改名/合并必须显式更新基线，防止漏改
}
```

- [ ] **Step 2: 运行确认失败**（当前 `debug_set` / `debug_evaluate` / `debug_object` 有 required）
- [ ] **Step 3: 落地快照读取与基线文件写入**（缺失时生成并提示"基线已创建，重新运行"）
- [ ] **Step 4: 全量测试**：`dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj`

**验收:** 两条测试存在；第一条暂红（由 Task 1 转绿），第二条绿基线可生成。

---

## Task 1: P0 参数命名统一 + 必填归零

**Files:**
- Modify: `Tools/Debugger/DebugBreakpointTool.cs`、`DebugSetTool.cs`、`DebugEvaluateTool.cs`、`DebugObjectTool.cs`、`DebugControlTool.cs`、`Tools/Metadata/SearchStringTool.cs`
- Modify: `Configuration/ToolParameterText.cs`（补齐参数描述常量）

**Interfaces（改名）**

| 工具 | 参数 | 旧 | 新 |
|---|---|---|---|
| `debug_breakpoint_set` | 方法 token | `methodToken` | `token` |
| `debug_breakpoint_remove` | 断点 id | `breakpointId` | `id` |
| `debug_step` | 步进类型 | `stepType` | `kind` |
| `search_literals` | 子串 | `search` | `contains` |

**Interfaces（默认值）:** `debug_set(path="", value="")`、`debug_evaluate(expression="")`、`debug_object(path="")`，方法体首行校验并返回中文。

- [ ] **Step 1: 改 `debug_set` 默认值 + 中文校验**

```csharp
public static Task<string> Set(
    [Description("...默认空=必填校验")] string path = "",
    [Description("...")] string value = "",
    int threadId = 0,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(path)) return Task.FromResult("请指定要改写的路径（path 必填，如 h.N 或 cfg.Current）。");
    if (value is null) value = "";   // 允许显式空串写字符串
    ...
}
```

- [ ] **Step 2: 同样处理 `debug_evaluate` / `debug_object`**
- [ ] **Step 3: 四个参数改名**（同步 `[Description]` 里的示例文本，例如 `methodToken` → `token` 的所有出现）
- [ ] **Step 4: 运行 Task 0 的两条契约测试**（第一条转绿）
- [ ] **Step 5: 更新 Client 用例与既有测试中的参数名**

**验收:** `ToolSurfaceContractTests` 双绿；对 `debug_set`/`debug_evaluate`/`debug_object` 空调用返回中文提示。

---

## Task 2: 参数集补齐（D6 + run_to 定位方式）

**Files:**
- Modify: `Tools/Metadata/DecompileIlTool.cs`、`Tools/Debugger/DebugRunToTool.cs`

**Interfaces:**
- `decompile_il(assembly="", token="", memberName="", typeName="", lines="", timeoutSeconds=...)`：`token` 优先；无 token 时按 `memberName`（+可选 `typeName`）解析，多匹配时按 `decompile_member` 的既有清单格式返回并提示用 token。
- `debug_run_to(typeName="", line="", memberName="", moduleName="", token="", ilOffset=-1, timeoutSeconds=...)`：与 `debug_breakpoint_set` 四路定位对齐。

- [ ] **Step 1: 写失败测试**（`decompile_il` 传 `memberName` 反汇编成功；`debug_run_to` 传 `token` 命中）
- [ ] **Step 2: 实现 `decompile_il` 的成员解析**（复用 `MemberResolver`，不新写匹配逻辑）
- [ ] **Step 3: 实现 `debug_run_to` 的 token 分支**
- [ ] **Step 4: 全量测试**

**验收:** 两条新用例绿；空调用中文提示。

---

## Task 3: 工具改名（方向进名字）

**Files:**
- Modify: `Tools/Metadata/CallChainTool.cs`、`CallGraphTool.cs`、`SearchStringTool.cs`
- Modify: `Configuration/HandshakeText.cs`、`README.md`、`src/DotNetDebuggerMcp.Client/*`

**Interfaces:**

| 旧 | 新 | 首句必须写明 |
|---|---|---|
| `call_chain` | `call_sequence` | 正向（下游）展开调用序列并反编译被调成员体 |
| `call_graph` | `call_edges` | 默认**双向**；`token` 模式才是"调用它的成员" |
| `search_string` | `search_literals` | 只搜字符串字面量，不搜符号名 |

- [ ] **Step 1: 改 C# 方法名**（SDK 由方法名派生工具名；如需固定名用 `[McpServerTool(Name = "...")]`）
- [ ] **Step 2: 改三段式描述首句**（见 Task 9 模板）
- [ ] **Step 3: 全局搜索旧名并清理**：`grep -rn "call_chain\|call_graph\|search_string" src/ tests/ README.md`
- [ ] **Step 4: 更新握手 `HandshakeText`** 中的工具名与触发词
- [ ] **Step 5: 更新 Client 用例、测试、README 工具表**
- [ ] **Step 6: 全量测试 + Client 端到端**

**验收:** Review Focus 第 2 条的 grep 干净；`DotNetDebuggerMcpCmdTests` 握手断言绿。

---

## Task 4: 合并 A（断点 / 异常 / 会话）

**Files:**
- Modify: `Tools/Debugger/DebugBreakpointTool.cs`、`DebugExceptionTool.cs`、`DebugSessionTool.cs`
- Create: `src/DotNetDebuggerMcp/Configuration/ToolSurfaceText.cs`

**Interfaces:**

```
debug_breakpoint(assembly="", action="set", ...)   // action: set|list|remove|clear
debug_exceptions(action="list")                    // action: list|clear
debug_session(action="")                           // action: disconnect|terminate|reset
```

- 内部保留原静态方法（`DebugBreakpointSet/List/Remove/Clear`、`DebugDisconnect/Terminate/Reset`），CLI 继续调它们。
- `action` 非法值 → 中文提示并列出合法值（顺序与描述一致）。

- [ ] **Step 1: 写失败测试**（`action=list` 返回断点清单；`action=remove` 缺 `id` 给中文提示；`action=clear` 后清单为空）
- [ ] **Step 2: 加 `action` 分派入口**，各分支体量控制在 60 行内
- [ ] **Step 3: 删除旧工具方法上的 `[McpServerTool]`**（保留方法本身）
- [ ] **Step 4: 同步描述 / 握手 / README / Client**
- [ ] **Step 5: 全量测试**

**验收:** 工具数 −6（4→1、2→1、3→1）；生命周期 CLI 用例仍绿。

---

## Task 5: 合并 B（等待 / 现场 / 写盘 / 截图）

**Files:**
- Modify: `Tools/Debugger/DebugInspectTool.cs`、`DebugControlTool.cs`、`Tools/Decompile/DecompileToDirTool.cs`、`DecompileToProjectTool.cs`、`Tools/Screenshot/*`

**Interfaces:**

```
debug_wait(waitSeconds=0, outputLines=..., outputFilter="", contextLines=...)  // 0=立即返回当前状态
debug_frame(threadId=0, all=false, names="")                                    // 调用栈 + 当前帧变量
decompile_to_dir(assembly="", outputDir="", typeName="", project=false, nestedDirectories=false, timeoutSeconds=...)
screenshot(mode="...", ...)  // 新增 mode: list-displays | list-windows
```

- `debug_wait(0)` 输出 = 状态头 + （有停点时）停点上下文 + 输出尾部；`debug_wait(n>0)` = 现有等待语义。
- `debug_frame` 输出分节：`调用栈:` → `当前帧变量:`（段标题沿用中文约定）。
- `decompile_to_dir(project=true)` 等价原 `decompile_to_project`。

- [ ] **Step 1: 写失败测试**（`waitSeconds=0` 秒回且含会话状态；`debug_frame` 一次含栈与变量；`project=true` 产出可编译项目目录；`screenshot(mode=list-windows)` 返回窗口清单）
- [ ] **Step 2: `debug_state` 并入 `debug_wait`**（`DebugState` 方法保留供 CLI/内部）
- [ ] **Step 3: `debug_frame` 聚合**
- [ ] **Step 4: 写盘二合一**
- [ ] **Step 5: 截图三合一**（`screenshot_displays`/`screenshot_windows` 方法保留，作为 mode 分支）
- [ ] **Step 6: 更新描述 / 握手 / README / Client**
- [ ] **Step 7: 全量测试**

**验收:** 工具数 42（`ToolSurfaceContractTests` 快照更新）；`tools/list` JSON ≤ 45 KB。

---

## Task 6: D1 —— `debug_disconnect` 在 Attaching 下失败

**Files:**
- Modify: `src/DotNetDebugger.Engine/Session/DebugSession.cs`（断开前推进到可同步状态）
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugSessionTool.cs`（COM 错误包中文、保证 `debug_session(reset)` 可用）
- Test: `tests/DotNetDebugger.Engine.Tests/*`（真实 attach→disconnect）

- [ ] **Step 1: 写复现测试**：attach 到 `DebugTarget.exe sleep 8` → 立即 disconnect → 断言成功且进程继续运行
- [ ] **Step 2: 运行确认红**（当前 `CORDBG_E_PROCESS_NOT_SYNCHRONIZED`）
- [ ] **Step 3: 修复**：断开前如需同步则先 resume/等待同步，再 detach；失败路径包成中文
- [ ] **Step 4: 引擎测试 + 宿主测试**

**验收:** 复现用例绿；若根因修复受阻，降级为"中文兜底 + `reset` 可用"，并在 CHANGELOG 记降级。

---

## Task 7: D3 —— `debug_wait` 把运行中报成已退出

**Files:**
- Modify: `src/DotNetDebugger.Session/SessionEventBuffer.cs`（或实际停点判定位置）
- Test: `tests/DotNetDebugger.Session.Tests/*`（该套串行）

- [ ] **Step 1: 写复现测试**：设断点 → continue → 立即 wait，断言不会误报已退出（允许超时）
- [ ] **Step 2: 修复**：仅在读到退出码且无待处理停点事件时判退出；事件丢失回落状态查询并重试一次
- [ ] **Step 3: 跑 Session 测试（并行关闭）+ 宿主 e2e**

**验收:** 连续跑 20 次等待，0 次误报。

---

## Task 8: D2 —— `call_edges(token)` 漏 async 调用者

**Files:**
- Modify: `tests/TestData/generate-testdata.ps1`（新增 async 调用者样本）
- Modify: `Tools/Metadata/CallGraphTool.cs`（空结果 + 存在 async 状态机时追加提示）
- Modify: `src/DotNetDebugger.Decompiler/Metadata/*`（只读探测：程序集内是否存在状态机类型）

- [ ] **Step 1: 加样本**：`static async Task CallerAsync() { Callee(); await Task.Yield(); }`，`Main` 在 `async` 参数下调用
- [ ] **Step 2: 写失败测试**：`call_edges(token=Callee)` 结果含"async 状态机内的调用点未收录"提示
- [ ] **Step 3: 实现提示**（不改默认结果集）
- [ ] **Step 4: 全量测试**

**验收:** 提示出现；真收录列 ROADMAP，不在本计划。

---

## Task 9: D4 + D7 + 描述三段式 + 输出头瘦身

**Files:**
- Modify: `Services/*`（apphost 探测）、`Tools/Debugger/DebugSetTool.cs`（成功文案）、所有工具 `[Description]`
- Modify: `Formatting/FormatContext.cs`（头部路径）

**描述模板:**

```
<做什么>。何时选我：<触发意图>。不要选我：<相邻工具 + 正确去向>。
```

- [ ] **Step 1: D4** —— 目标无元数据时返回：`该文件是 apphost（无托管元数据），请改用同名 .dll：<推导路径>。`
- [ ] **Step 2: D7** —— `debug_set` 成功文案追加"若断点在方法入口，局部变量尚未初始化，step 后可能被覆盖。"
- [ ] **Step 3: 逐工具改写描述**（中位数 ≤ 160 字符、最长 ≤ 400）
- [ ] **Step 4: 去重 `lines` 分页模板**（正文删除，统一收进 `ToolParameterText.LinesParam`）
- [ ] **Step 5: 头部改文件名**（`程序集: Foo.dll`；完整路径只出现在写盘与错误里）
- [ ] **Step 6: 量化验收**：写个一次性脚本统计描述字符数分布

**验收:** §8.1 四项定量指标达标。

---

## Task 10: 文档、握手、发布面收尾

- [ ] **Step 1: `README.md` 工具表整体重写**（42 个、新名字、action 枚举）
- [ ] **Step 2: `CHANGELOG.md`** 加 `[Unreleased]` 破坏性变更段（旧名 → 新名映射表）
- [ ] **Step 3: 握手 `HandshakeText`** 全量核对（每个族的触发词都必须用新名字）
- [ ] **Step 4: `docs/planning/specs/2026-10-20-mcp-tool-surface-design.md` 状态改"已实施"**
- [ ] **Step 5: 版本三处同步**（csproj / .mcp/server.json / CHANGELOG）

**验收:** `DotNetDebuggerMcpCmdTests` 握手四族断言绿。

---

## Task 11: 验收（定量 + 零返工）

- [ ] **Step 1: 定量**：`tools/list` JSON ≤ 45 KB；描述正文 ≤ 8 000 字符；`required` 全空；工具数 42
- [ ] **Step 2: 零返工**：派未参与本设计的 subagent，按 spec §8.2 跑三组任务，统计"名字猜错导致的返工"
- [ ] **Step 3: 记录**：把返工明细写回本计划末尾

**验收线:** 返工 ≤ 2 次（基线 17 次）。

---

## 风险与回退

| 风险 | 处置 |
|---|---|
| 合并后单工具描述过长，agent 反而选不准 | 每个合并工具首句列全枚举值；描述超 400 字符即拆分支说明进参数描述 |
| `debug_frame` 输出过大 | 默认只给当前帧变量；`all=true` 才给全部帧 |
| D1 引擎改动引入不稳定 | 复现用例在 CI 串行跑；不绿则降级为中文兜底并在 CHANGELOG 标注 |
| 改名遗漏（测试/Client/文档） | Task 0 快照 + Review Focus 第 2 条 grep 双保险 |
