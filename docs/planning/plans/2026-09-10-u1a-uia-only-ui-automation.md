# 实施计划 · U1A UI 自动化全 UIA 化（弃用物理输入）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施（或 subagent-driven-development 逐 Task 派发）。Step 用 `[ ]` 追踪。

**Goal:** 把 UI 驱动 100% 收敛到 UIA 语义 pattern（不动光标/不注入输入/不抢前台/不抢前台仅必要时还原窗口）：重构 `UiAutomationService` 为聚焦组件，新增语义动词工具 `ui_action` / `ui_input` / `ui_get`，增强 `ui_find`（能力清单）/`ui_wait`（事件化），**删除** `ui_invoke`/`ui_scroll`，并让 `debug_verify` 支持 `uiAction`/`uiAssert` 步骤。

**Architecture:** 纯宿主层（Engine/Session 零改动，FlaUI 引用沿用 U1）。`Services/Ui/` 拆分：`UiAutomationService`（facade：gate+5s 双层超时+编排）、`UiElementLocator`（进程/窗口解析、条件缓存、动作前重解析/重试、虚拟化实体化）、`UiPatternDispatcher`（verb→pattern 分派 §6）、`UiStateReader`（what→pattern 属性 §6）、`UiEventWaiter`（StructureChanged/PropertyChanged 事件订阅+超时+轮询兜底）；`UiSemanticResolver` 保留。三工具薄包装 + verify 复用同核心。

**Tech Stack:** C# net10.0、FlaUI.Core/UIA3 5.0.0（PackageDownload+HintPath，宿主 TFM net10.0）、ModelContextProtocol.Server。
**Spec:** `docs/planning/specs/2026-09-10-u1a-uia-only-ui-automation.md`（已冻结；三项拍板：语义动词化 `ui_action` / 含 verify 集成 / 不抢前台仅还原窗口）。
**参考源码（动手前必读，本地已克隆）:** `../../Externals/DebuggerExternals/FlaUI`（`src/FlaUI.Core/AutomationElements/AutomationElement.cs`、`Input/Mouse.cs`（**基线用于对照删除**）、`Patterns/*`、`AutomationPattern.cs`）。
**权威依据:** spec §4（UIA 事实与 API 签名）、§6（verb/pattern 矩阵与 ui_input/ui_get 分派）、§7（无入口动作处置）、§8-§11（身份/前台/事件/线程）、§12（verify 集成）、§13（内部结构）。

## Global Constraints（根 AGENTS.md + 宿主 AGENTS.md 铁律）

- 所有 MCP 工具参数**语法上带默认值**（`string verb = ""`、`int index = -1`…），"必填"靠方法体内中文校验（不用 SDK 缺参 Tool Error）；`[Description]` 中文、面向 agent、注明默认值与"无右键/双击"边界；每方法 `CancellationToken cancellationToken = default`；返回 `Task<string>`、错误中文不抛异常。
- **禁止任何物理输入**：移除 `Mouse.*`/`SetCursorPos`/`SendInput`/`SetForegroundWindow`/`ActivateWindow`/`PerformPhysicalMouse` 调用；UIA-only（`WindowPattern.SetWindowVisualState(Normal)` 仅还原最小化）。
- **禁止静默提前台**：provider 因后台/不可见拒绝 → 捕获返中文（附 `ui_get what=offscreen` 提示）。
- 线程：单 `UIA3Automation` + `SemaphoreSlim(1,1)` 串行；跨进程 5s 双层超时（沿用 `UiaBoundAsync`）；`Invoke` 可能阻塞 → 独立线程+超时，超时按「放弃等待」。
- UI 读值/断言：facade `GetAsync` 返回**未脱敏原始值**供 verify 比对；**输出层**（`ui_get` 工具、verify 失败文案）用 DB1 `SensitiveValueRedactor.Redact(控件Name/AutoId, text)` 脱敏后再展示。写操作打 `AgentActionLog`。
- 破坏性工具面变更（删 `ui_invoke`/`ui_scroll`、加三工具）必须同 commit 改根 `README.md` + `CHANGELOG.md` `[Unreleased]`（记 Breaking/Changed）+ 同步 `AgentCopyGuardTests` ui_* 契约片段。
- 本地分支 `w3-data-breakpoint` 提交、**不合并/不 push**（用户 2026-09-10 决定）；中文提交。

---

### Task 0: 工作区准备

- [ ] **Step 1**: 确认在 `w3-data-breakpoint` 分支、工作树干净；`git log --oneline -1` 记录 BASE。
- [ ] **Step 2**: `powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1` 就绪；`dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` 基线绿。读 FlaUI 本地源码确认 §4 API 与当前 U1 实现（`Services/UiAutomationService.cs`）的物理输入调用点清单（待删）。

---

### Task 1: 组件拆分 + facade 重写（去物理输入）

**Files:**
- Create: `src/DotNetDebuggerMcp/Services/Ui/UiElementLocator.cs`
- Create: `src/DotNetDebuggerMcp/Services/Ui/UiPatternDispatcher.cs`
- Create: `src/DotNetDebuggerMcp/Services/Ui/UiStateReader.cs`
- Create: `src/DotNetDebuggerMcp/Services/Ui/UiEventWaiter.cs`
- Create: `src/DotNetDebuggerMcp/Services/Ui/UiModels.cs`（`UiElementInfo`/`UiActionResult`/`UiStateResult`/`UiWaitResult`/`UiPatternCapabilities`/`UiException` 集中定义，命名空间 `DotNetDebuggerMcp.Services.Ui`；**UiException 不单独建文件**）
- Modify: `src/DotNetDebuggerMcp/Services/UiAutomationService.cs`（**移动到** `src/DotNetDebuggerMcp/Services/Ui/UiAutomationService.cs`，命名空间改 `DotNetDebuggerMcp.Services.Ui`，重写为 facade；工具/verify 的 using 同步）
- Modify: `src/DotNetDebuggerMcp/Services/UiSemanticResolver.cs`（**保留原位/原命名空间** `DotNetDebuggerMcp.Services`；UiElementLocator 引用之）
- Delete（物理实现）: `ActivateWindow` / `PerformPhysicalMouse` / 所有 `FlaUI.Core.Input.Mouse`/`SetForegroundWindow` 调用。
- Test: `tests/DotNetDebuggerMcp.Tests/UiAutomationServiceTests.cs`（扩纯单测：dispatcher/stateReader 的纯 Choose 决策与失败文案；locator 纯逻辑若有；**不引入 WinForms**——条件缓存/失效重试/虚拟化走 Task3 跨进程 e2e）

**Interfaces（facade 对外，供工具与 verify 复用）:**
- `Task<IReadOnlyList<UiElementInfo>> FindAsync(string process, string title, string text, string type, string automationId, int limit, CancellationToken ct)`
- `Task<UiActionResult> ActionAsync(string process, string verb, int index, string name, string type, string direction, int lines, string windowstate, CancellationToken ct)`
- `Task<UiActionResult> InputAsync(string process, string value, int index, string name, string type, CancellationToken ct)`
- `Task<UiStateResult> GetAsync(string process, string what, int index, string name, string type, CancellationToken ct)`
- `Task<UiWaitResult> WaitAsync(string process, string text, string type, string textChangedFrom, string textChangedTo, int timeoutSeconds, CancellationToken ct)`
- 模型：`UiElementInfo(int Index, string Name, string Type, string AutoId, string Rect, string Patterns, string? Semantic)`（`Patterns` = 能力清单逗号串，替换 U1 `CanInvoke`）；`UiActionResult(bool Ok, string Message)`；`UiStateResult(string Value, string? Raw)`（**Value=原始显示值、Raw=原始值兜底；均不脱敏**——脱敏由工具/verify 输出层做）；`UiWaitResult(string Outcome, string Message)`；`UiPatternCapabilities`（bool 集合：Invoke/Toggle/SelectionItem/ExpandCollapse/Value/RangeValue/Scroll/ScrollItem/Window/Legacy）。
- `UiElementLocator`：按 process（pid/名）→窗口（title 可选）→条件（text/type/automationId）FindAllDescendants；**缓存定位条件**（pid + AutomationId/Name/ControlType + 相对路径），`index` 映射到条件；`ResolveForActionAsync` 每动作前重解析 + 1-2 次重试（stale/ElementNotAvailable→中文「目标已变化，请重新 ui_find」）；虚拟化：`ItemContainerPattern.FindItemByProperty`→`VirtualizedItemPattern.Realize()`→`ScrollItemPattern.ScrollIntoView()`。
- `UiPatternDispatcher.Dispatch(element, verb, direction, lines, windowstate)` → 命中 pattern 名或抛 internal `UiException`（中文 `Message`；定义在 `Services/Ui/UiModels.cs`，工具 catch 转返回文本）。
- `UiStateReader.Read(element, what)` → §6 ui_get 映射。
- `UiEventWaiter.WaitAsync(window, predicate, timeout, ct)`：`RegisterStructureChangedEvent(TreeScope.Subtree)` + `RegisterPropertyChangedEvent(TreeScope.Subtree, …, Name/Value)`；命中 `TaskCompletionSource.TrySetResult`；X 秒无事件 → 回退 200ms 轮询；回调只判定+set，不重入大操作；退订纳入 gate。

**可测性接缝（不可造 `AutomationElement` 桩，2026-09-10 审查修正）**：把「分派决策」与「pattern 执行」分离——
- `UiPatternDispatcher.Choose(string verb, UiPatternCapabilities caps, string direction, int lines, string windowstate)` **纯函数**（`UiPatternCapabilities` = 各 pattern `IsSupported` 布尔集合的快照），返回命中项或抛 `UiException`（中文）；单测直接喂 caps。
- `UiPatternDispatcher.Execute(AutomationElement element, ChosenAction action)` 做真实 pattern 调用（e2e 覆盖）。
- `UiStateReader.Read` 同样拆 `Choose(what, caps)` 纯 + `Execute(element, choice)`；纯决策单测无需 COM。
- `UiElementLocator` 的测试策略（2026-09-10 round2 修正）：**不在宿主测试工程引入 WinForms**（该工程为 `net10.0` 且无 `UseWindowsForms`，宿主须保 PackAsTool 的 net10.0）；其**纯逻辑**（条件构造/缓存键/重试计数判定，若可抽为不依赖 COM 的纯函数）走单测，**条件缓存/失效重试/虚拟化实体化**改由**跨进程 e2e**（UiSampleApp 真实控件）覆盖。
- `facade.GetAsync` 返回**未脱敏原始值**（见 Global Constraints/模型说明）；脱敏发生在调用方（工具/verify 输出层）。

- [ ] **Step 1: 失败单测（组件行为，纯内存/桩；不引入 WinForms）**：dispatcher 各 verb 对「支持/不支持」caps 快照的 `Choose` 分派与失败文案（§6 表逐行）；stateReader what 的纯 `Choose` 映射；locator 纯逻辑（条件构造等）若有；**条件缓存/失效重试/虚拟化由 Task3 跨进程 e2e 覆盖**；facade 不再含 `Mouse.` 引用（`git grep "FlaUI.Core.Input" src/DotNetDebuggerMcp` 为 0）。
- [ ] **Step 2: 实现拆分与 facade**：按上述接口；`UiActionResult.Message` = 「已 {verb}（{pattern}）」；`ui_input` 按 §6（ValuePattern→RangeValuePattern→LegacyIAccessible；`IsReadOnly` 报只读）；前台策略 §9；超时/串行沿用。
- [ ] **Step 3: 跑定向单测 + Release build + 提交**（`feat: U1A 组件化 UiAutomationService（UIA-only 去物理输入）`）。

---

### Task 2: 工具面（ui_action/ui_input/ui_get + ui_find 能力清单 + ui_wait 事件化；删 ui_invoke/ui_scroll）

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs`（删 `UiInvoke`/`UiScroll`；加 `UiAction`/`UiInput`/`UiGet`；改 `UiFind` 输出 `patterns=` 能力清单；`UiWait` 走事件化）
- Modify: `README.md`（UI 自动化工具表/参数/用法全部改写；删物理右键/双击/滚轮措辞与风险段，替换为 UIA-only 边界说明）
- Modify: `CHANGELOG.md`（`[Unreleased]` Breaking/Changed：ui_invoke/ui_scroll → ui_action/ui_input/ui_get；ui_find 能力清单；不抢前台）
- Modify: `tests/DotNetDebuggerMcp.Tests/AgentCopyGuardTests.cs`（ui_* 契约片段改新工具：ui_action「verb」/「无右键」、ui_get「what」、ui_input「value」；删 ui_invoke/ui_scroll 旧片段）
- Test: `tests/DotNetDebuggerMcp.Tests/DebugUiToolsTests.cs`（Task 3 重写）

**Interfaces（工具；参数默认值见 spec §5；错误中文不抛）:**
- `ui_action(process="", verb="", index=-1, name="", type="", direction="", lines=0, windowstate="", ct)`：verb ∈ invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate；`rightclick`/`doubleclick` → 明确中文「UIA 无该语义入口，物理输入已移除；请改用等价菜单/命令的 ui_action verb=invoke」；返回「已 {verb}（{pattern}）」+ 写 AgentActionLog。**`verb=windowstate` 目标解析**：忽略 index/name/type，按 `process` 定位**顶层窗口**（`ui_action` 无 `title` 参数），取 `WindowPattern`（与元素定位规则显式区分，避免实现/测试各写一套）。
- `ui_input(process="", value="", index=-1, name="", type="", ct)`：value 语义必填；按 §6 写值 + 只读/无 pattern 中文。
- `ui_get(process="", what="", index=-1, name="", type="", ct)`：what 语义必填 ∈ `value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext`（对齐 spec §5）；读取**原始值**后，由**工具输出层**按控件 `Name`（无则 AutomationId）经 DB1 `SensitiveValueRedactor.Redact(name, text)` 脱敏再返回；facade `GetAsync` 本身不脱敏。
- `ui_find`：`patterns=` 能力清单（只列 IsSupported：invoke/toggle/selectionitem/expandcollapse/value/rangevalue/scroll/scrollitem/window/legacy）。
- `ui_wait`：事件化主路径 + 轮询兜底（§10）；行为/参数与 U1 兼容。

- [ ] **Step 1: 失败 e2e 断言（Task 3 目标）先定契约**；实现三工具 + 改 ui_find/ui_wait + 删旧两工具；`git grep -n "UiInvoke\|UiScroll" src/DotNetDebuggerMcp` 无残留（**仅 src**；测试内旧用例名在 Task3 重写，收尾再全仓核对）。
- [ ] **Step 2: README/CHANGELOG/V4** 同步；`AgentCopyGuardTests` 更新并跑绿。
- [ ] **Step 3: Release build + 定向 + 提交**（`feat: U1A 语义动词工具 ui_action/ui_input/ui_get（删 ui_invoke/ui_scroll，同步 README/CHANGELOG/V4）`）。

---

### Task 3: UiSampleApp 扩展 + DebugUiToolsTests 重写（UIA-only + 不抢鼠标强断言）

**Files:**
- Modify: `tests/TestData/UiSampleApp/Program.cs`（扩展控件覆盖各 pattern：CheckBox(toggle)、ComboBox 或 ListView(selectionitem/expandcollapse/scroll)、Slider(rangevalue)、TextBox(value + 只读一例)、Menu（invoke）、**一个确实暴露 `ScrollPattern` 的可滚动容器**（实施时在 RichTextBox/自绘容器中择一验证：`ScrollPattern.VerticalScrollPercent` 可读写）；另加首窗 `Load/Shown` 的 `ShowWindow(Handle, SW_SHOW)` P/Invoke（绕过 debug_launch 的隐藏启动，仅测试目标，见 Task4 Ruling）；保留 UiSample 标题；源码入库）
- Modify: `tests/DotNetDebuggerMcp.Tests/DebugUiToolsTests.cs`（重写：删物理右键/双击/滚轮三例；改 UIA-only）
- `generate-testdata.ps1` 不需改（整目录构建拷贝）

- [ ] **Step 1: 先重跑 `generate-testdata.ps1`**（Task3 改了 `UiSampleApp/Program.cs` 源码——e2e 读的是脚本拷入 `tests/TestData/UiSampleApp/` 的 Release 产物，**必须重生成**，否则 e2e 静默跑到旧 exe），再写 e2e（真实起 UiSampleApp）：`ui_find` 能力清单；`ui_action verb=toggle/select/expand/collapse/scroll/scrollintoview/windowstate/focus` 各命中对应 pattern 或明确失败文案；`ui_input` Value/RangeValue + 只读拒绝；`ui_get what=value/toggle/selected/expandstate/rangevalue/…` 断言；`ui_wait` 事件命中（控件出现/文本变化）；错误面（unknown verb、rightclick/doubleclick 拒绝、无 pattern、进程不存在、超时）。
- [ ] **Step 2: 不抢鼠标/前台强断言（限定 verb，2026-09-10 审查修正）**：对 **invoke/toggle/select/expand/collapse/scroll/scrollintoview/ui_input** 断言动作前后 `GetCursorPos()` 不变 + `GetForegroundWindow()` 不变；**豁免 `focus`/`windowstate`**（`AutomationElement.Focus` 对带 HWND 控件可能激活顶层窗口、`SetWindowVisualState(Normal)` 是否激活取决于目标应用——spec §9 已述，非物理输入）。
- [ ] **Step 3: 非交互桌面/最小化**：语义动作可用（天然去桌面依赖）；最小化时 `windowstate=normal` 还原。
- [ ] **Step 4: 保持 UIA×调试编排既有覆盖**：`DebugUiToolsTests` 里两条非物理用例（断点闭环 `…OnToggleState…HitsBreakpoint`、错误面 `…InvalidArgsAndMissingTargets…`）**改写为 `ui_action`**（不随物理三例一起删）；仅物理右键/双击/滚轮三例删除。
- [ ] **Step 5: Release build + 宿主全量 + 提交**（`test: U1A UiSampleApp 扩展 + DebugUiToolsTests UIA-only 重写（不抢鼠标断言）`）。

---

### Task 4: debug_verify 集成（uiAction / uiAssert 步骤）

**Files:**
- Modify: `src/DotNetDebuggerMcp/Services/VerifyScenario.cs`（步骤模型新增 `uiAction` / `uiAssert`）
- Modify: `src/DotNetDebuggerMcp/Services/VerifyService.cs`（执行 uiAction=调 facade ActionAsync/InputAsync；uiAssert=调 GetAsync 比对 equals/contains，失败理由附期望 vs 实际且实际值经 DB1 脱敏；fail-fast/汇总文案与现有步骤一致）
- Modify: `README.md` 场景 JSON 示例加 uiAction/uiAssert；`CHANGELOG` 记 verify 步骤扩展
- Test: `tests/DotNetDebuggerMcp.Tests/VerifyScenarioTests.cs`（解析）/ `DebugVerifyToolTests.cs`（e2e 场景含 uiAction+uiAssert PASS；失败脱敏）；`AgentCopyGuardTests` 若涉及片段同步

- [ ] **Step 1: 解析 + 执行失败测试 → 实现**：`uiAction`/`uiAssert` 可解析、错误中文、缺字段校验。
  - **e2e 前置决策（2026-09-10 审查修正）**：`debug_verify` 只会 `LaunchAndAttachAsync`（`CreateNoWindow/WindowStyle=Hidden`），实测 launch 的 WinForms 目标 `MainWindowHandle=0`、UIA 无顶层窗口 → 现计划下 `uiAction` e2e 无法 PASS。**决策（Ruling）**：不改 Engine/Session（spec §2/§3 边界）；改**测试目标** `UiSampleApp` 在首窗 `Load/Shown` 调 `ShowWindow(Handle, SW_SHOW)`（P/Invoke，仅测试目标）绕过隐藏启动，使 launch 后窗口可见，verify launch 路径可驱动。若该 P/Invoke 仍无效，则退路 = 场景改为「只解析 + 纯单测」并把 e2e 标注为依赖既有 attach 窗口（在 README/report 如实记录，不静默降级）。
  - **既有测试同步（必改）**：`VerifyScenarioTests.cs` 中断言步骤种类清单/未知步骤错误文案（现含 `ui` 预留 + `Kind=Ui`) 的两处断言随新 `uiAction`/`uiAssert` 更新；**旧 `ui`/`set` 预留步骤保留现状**（不在本 spec 范围删除，仍按其既有「依赖未就绪」语义），错误文案清单加入 `uiAction/uiAssert`。
  - e2e：场景驱动 UiSampleApp 控件（`uiAction`）+ `uiAssert` 断言其状态 → PASS；失败路径实际值经 DB1 脱敏。
- [ ] **Step 2: Release build + 宿主全量 + 提交**（`feat: debug_verify 支持 uiAction/uiAssert 步骤（U1A 闭环）`）。

---

## 收尾

- [ ] Release build 全解决方案 0 警告；Engine/Session/宿主全量测试；Client；`git grep` 确认无 `FlaUI.Core.Input`/`SetForegroundWindow`/`Mouse.` 残留与 `ui_invoke`/`ui_scroll` 残留（除历史 CHANGELOG/spec）。
- [ ] 核对 `TODO.md` U1A 条目、`specs/README.md`（U1 已标 superseded、U1A 行）；**U1 spec 正文顶部加 Superseded 说明**（`docs/planning/specs/2026-09-08-u1-ui-automation.md` 置顶：工具面被 U1A 取代、正文保留，spec §14 要求）；**spec U1A §15 勘误注**：① 不抢前台强断言口径收窄为「invoke/toggle/select/expand/collapse/scroll/scrollintoview/ui_input，豁免 focus/windowstate」（依据 §9）；② `UiElementLocator` 测试策略改为「纯逻辑（若有）单测 + 条件缓存/失效重试/虚拟化走跨进程 e2e」——宿主测试工程不引入 WinForms（spec §15 原文与计划不符处按此勘误）。在 spec 加勘误行，避免后续按原 §15 无条件口径复核；CHANGELOG `[Unreleased]` Breaking 记录齐全。
- [ ] 本地分支提交，不合并/不 push（用户 2026-09-10 决定）。

## Self-Review（writing-plans 内审）

- **Spec 覆盖**：组件拆分（Task1 §13）、verb/pattern 矩阵与 ui_input/ui_get（Task1+2 §6）、无入口动作处置（Task2 §7）、身份/虚拟化（Task1 §8）、前台策略（Task1 §9）、事件化 ui_wait（Task1+2 §10）、线程/超时（Task1 §11）、verify 集成（Task4 §12）、破坏性同步清单（Task2 §14、Task3 §14 旧测试）、测试与验收含不抢鼠标强断言（Task3 §15）。
- **占位符**：无 TBD；FlaUI API 细节以本地克隆 `../../Externals/DebuggerExternals/FlaUI` 为准（spec §4 已列签名，实施前可复核）。
- **类型一致**：facade `FindAsync/ActionAsync/InputAsync/GetAsync/WaitAsync` Task1 定义、Task2/4 消费；`UiElementInfo.Patterns`（能力清单）替换 U1 `CanInvoke` 贯穿 Task2；verify 步骤 `uiAction/uiAssert` Task4 定义并执行。
- **风险点已标**：provider 后台/最小化拒绝（报中文不抢前台）、虚拟化实体化失败、Invoke 阻塞（5s 兜底）、无 pattern 自绘控件（LegacyIAccessible 兜底后明确不支持）、能力探测跨进程开销（同次遍历）。
- **2026-09-10 plan-review 修正已落实**：verify launch 窗口不可见的显式 Ruling（UiSampleApp 测试目标 `ShowWindow` 绕隐藏 + 退路如实记录）、前台强断言限定 verb（豁免 focus/windowstate）、脱敏归属（facade 原始值/输出层脱敏）、可测性接缝（纯 Choose + Execute 分离；locator 纯逻辑若有 / 缓存与失效重试走跨进程 e2e，宿主测试工程不引入 WinForms）、U1 spec Superseded 标注入收尾、VerifyScenario 既有断言同步与旧 ui/set 保留决策、windowstate 目标解析、ScrollPattern 样例控件、Task3 重跑 generate-testdata。
