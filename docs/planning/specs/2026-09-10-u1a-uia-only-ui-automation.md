# Spec · U1A UI 自动化全 UIA 化（弃用一切物理输入）

> 状态：**已立项/冻结**（2026-09-10 用户拍板）。
> 替代关系：**取代** `2026-09-08-u1-ui-automation.md` 中「`ui_invoke(action=click/rightClick/doubleClick)` + `ui_scroll`（物理滚轮）四件套」的工具面部分；U1 的连接/语义标注/无视觉清单等设计继续有效。U1 spec 正文保留，加 Superseded 标注（见 §14）。
> 关联：宿主 TODO U1 条目；`debug_verify`（V1）步骤模型扩展；FlaUI 5.0 引用姿势沿用 U1（`PackageDownload`+`HintPath`，宿主保持 net10.0）。
> 参考实现（已实读源码）：`../../Externals/DebuggerExternals/FlaUI`（`FlaUI.Core/Input/Mouse.cs`、`AutomationElements/AutomationElement.cs`、`Patterns/*`、`AutomationPattern.cs`）。

## 1. 背景与目标

U1 v1 的 `ui_invoke` 在 `InvokePattern` 不可用时**物理左键兜底**、`rightClick`/`doubleClick` **一律物理鼠标**、`ui_scroll` **一律物理滚轮**，且物理动作前 `ActivateWindow`→`SetForegroundWindow`。这些走 `FlaUI.Core.Input.Mouse`（`SetCursorPos` + `SendInput`，已实读 `Mouse.cs:81-94/214-235/356-386`），会**瞬移真实光标、注入真实点击、抢前台**——人类无法同时使用这台机器；`SendInput` 还会落到当时前台窗口上，副作用不可控。

**目标**：把 UI 驱动 **100% 收敛到 UIA 语义 pattern**——不移动光标、不注入输入、不抢前台。观察走 UIA 属性/事件，内部状态仍由 `debug_*` 提供；最终与 `debug_verify` 编排出「驱动 UI → 调试断言」闭环。

**核心原则**：从「鼠标输入模型」转为「控件能力模型」——按**语义动词**（invoke/toggle/select/expand/…）分派到控件支持的 pattern，而不是按鼠标键。

## 2. 非目标

- **不**尝试用 UIA 复刻输入手势（右键菜单/真双击/悬停/拖拽/纯键盘快捷键）——UIA 无此类入口（§4、§7），直接移除。
- **不**引入独立 Windows 桌面/VM 隔离（彻底不打扰的终极方案，另立 spec）。
- **不**做 Consent/Audit 完整版（沿用 U1 §4.4：AgentActionLog + Description 明示）。
- **不**改 Engine/Session（纯宿主层 UIA）。

## 3. 拍板记录（2026-09-10）

1. **工具面形态**：语义动词化——新增 `ui_action` + `ui_get` + `ui_input`，**删除** `ui_invoke`/`ui_scroll`。
2. **范围**：含 **debug_verify 集成**（UIA 动作步骤 + UI 断言步骤）。
3. **前台策略**：默认**不** `SetForegroundWindow`；仅窗口最小化时用 `WindowPattern.SetWindowVisualState(Normal)` 还原；因后台被 provider 拒绝则报中文，不静默提前台。

## 4. UIA 事实（已核实 FlaUI 5.0 源码）

- **前台不是 UIA 通用必要条件**：语义 pattern 是跨进程 COM 请求 provider 执行动作，不做命中测试、与前台/光标无关。只有物理输入、`AutomationElement.Focus()`（对窗口，内部 `SetForegroundWindow`）、少数自绘 provider 需要前台。
- **「在树里」≠「可见」**：被遮挡/`IsOffscreen=true` 的控件通常仍在树里，语义动作可用；虚拟化列表里未实体化的项**不在树里**，需语义滚动/实体化。
- **API 签名（实读）**：
  - 能力探测：`element.Patterns.<X>.IsSupported` / `.PatternOrDefault`（`AutomationPattern.cs:74-96`）。
  - `InvokePattern.Invoke()`；`TogglePattern.Toggle()`；`SelectionItemPattern.Select()`；`ExpandCollapsePattern.Expand()/Collapse()`（`ExpandCollapseState`）；`ValuePattern.SetValue(string)`/`.Value`；`RangeValuePattern.SetValue(double)`/`.Value`/`.Minimum/Maximum`；`ScrollPattern.Scroll(ScrollAmount, ScrollAmount)`/`.SetScrollPercent(double,double)`/`.VerticallyScrollable`/`.VerticalScrollPercent`（`ScrollAmount` = LargeDecrement/SmallDecrement/NoAmount/SmallIncrement/LargeIncrement）；`ScrollItemPattern.ScrollIntoView()`；`WindowPattern.SetWindowVisualState(...)`；`LegacyIAccessiblePattern.DoDefaultAction()/Select(int)/SetValue(string)/.Value`。
  - `AutomationElement.Focus()`（窗口会抢前台，见 §9）。
  - 事件：`RegisterStructureChangedEvent(TreeScope, Action<AutomationElement, StructureChangeType, int[]>)`、`RegisterPropertyChangedEvent(TreeScope, Action<AutomationElement, PropertyId, object>, params PropertyId[])`（`AutomationElement.cs:352-358`）。
  - 虚拟化：`ItemContainerPattern.FindItemByProperty(AutomationElement?, PropertyId?, object?)`、`VirtualizedItemPattern.Realize()`。
- **UIA 无输入手势入口**（刻意设计，非 FlaUI 缺失）：右键/双击/悬停/拖拽/裸滚轮手势没有 pattern；滚动有**语义** `ScrollPattern`/`ScrollItemPattern`。

## 5. 工具面契约

| 工具 | 状态 | 参数 | 返回 |
|---|---|---|---|
| `ui_find` | 保留（增强） | `process`(必填) `title` `text` `type` `automationId` `limit` | 行格式在 U1 基础上把 `Invoke✓` 换为**能力清单** `patterns=invoke,toggle,selectionitem,expandcollapse,value,rangevalue,scroll,scrollitem,window`（只列 `IsSupported` 的）；其余（index/Name/Type/AutoId/Rect/语义候选）不变 |
| `ui_action` | **新** | `process`(必填) `verb`(必填) `index`(-1) `name` `type` `direction` `lines` `windowstate` | 动作结果 + 实际命中的 pattern（如「已 select（SelectionItemPattern）」）；写入 AgentActionLog |
| `ui_input` | **新** | `process`(必填) `value`(必填) `index`(-1) `name` `type` | 写入结果 + 实际命中的 pattern |
| `ui_get` | **新** | `process`(必填) `what`(必填) `index`(-1) `name` `type` | `what=value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext` 的当前值 |
| `ui_wait` | 保留（事件化） | 同 U1（`text` 或 `textChangedFrom`+`textChangedTo`，`type`，`timeoutSeconds`） | 命中（出现/已变化）或超时当前状态 |
| ~~`ui_invoke`~~ | **删除** | — | 由 `ui_action` 取代 |
| ~~`ui_scroll`~~ | **删除** | — | 由 `ui_action verb=scroll/scrollintoview` 取代 |

- 参数契约对齐根 `AGENTS.md`：**所有参数语法上都带默认值**（如 `string verb = ""`、`int index = -1`），「必填」是**方法体内中文校验**的语义必填——不靠 SDK 的缺参 Tool Error。每个方法带 `CancellationToken`、返回 `Task<string>`、错误返回中文。
- `[Description]` 中文、面向 agent、注明默认值；`ui_action` 的 verb 清单与「无右键/双击」边界要写进描述。

## 6. verb → pattern 分派矩阵

`UiPatternDispatcher` 按下列优先级选 pattern；全部不支持 → 抛 `UiException`（中文提示，见 §7）。

| verb | 主 | 兜底 | 校验/失败 |
|---|---|---|---|
| `invoke` | `InvokePattern.Invoke` | `LegacyIAccessible.DoDefaultAction` | 都无 → 「该控件不支持无鼠标触发；请用其菜单/命令入口 invoke」 |
| `toggle` | `TogglePattern.Toggle` | `InvokePattern` | 都无 → 同上 |
| `select` | `SelectionItemPattern.Select` | `InvokePattern` / `LegacyIAccessible.Select(0)` | 都无 → 「该控件不支持选中」 |
| `expand` | `ExpandCollapsePattern.Expand` | — | 无 → 「该控件不可展开」 |
| `collapse` | `ExpandCollapsePattern.Collapse` | — | 无 → 「该控件不可折叠」 |
| `focus` | `AutomationElement.Focus()`（**仅控件**） | — | 目标是窗口 → 改走 `windowstate=normal`（Focus 窗口会抢前台） |
| `scroll` | `ScrollPattern.Scroll`（`up`→`SmallDecrement`/`down`→`SmallIncrement`，`lines` 为调用次数；>5 用 `Large`） | `SetScrollPercent`（按 `direction` 步进百分比） | 无 Scroll → 「改用 scrollintoview 或换目标」 |
| `scrollintoview` | `ScrollItemPattern.ScrollIntoView` | — | 无 → 「该控件不可滚入视野」 |
| `windowstate` | `WindowPattern.SetWindowVisualState`（`normal/maximized/minimized`） | — | 无 → 「该窗口无 WindowPattern」 |

`ui_input`：
1. `ValuePattern` → `SetValue(value)`（文本/可写值）。
2. 否则 `RangeValuePattern` → 解析数字后 `SetValue(double)`（滑块/数值框）。
3. 否则 `LegacyIAccessiblePattern` → `SetValue`。
4. 都无 → 「该控件不支持写值（无 Value/RangeValue/LegacyIAccessible）」。
5. `IsReadOnly`（Value/RangeValue）为真 → 报「只读」。

`ui_get`：对应 pattern 属性读取；`value` 优先 `ValuePattern.Value`，无则 `LegacyIAccessible.Value`；`toggle`=`ToggleState`；`selected`=`SelectionItem.IsSelected`；`expandstate`=`ExpandCollapseState`；`rangevalue`=`RangeValue.Value`；其余为通用属性 `Name/IsEnabled/IsOffscreen/BoundingRectangle/HelpText`。

**语义差异提示**（写进 `ui_action` 描述）：`invoke` 是该控件的**默认动作**；复选框应 `toggle`、列表/树/页签项应 `select`、下拉/树节点应 `expand`——工具按控件能力分派，而非固定「点一下」。

## 7. UIA 无入口动作的处置（一律移除）

| 动作 | 处置 |
|---|---|
| 右键 / 上下文菜单 | 不提供。`ui_action verb` 若传 `rightclick`/`doubleclick` → 中文：「UIA 无该语义入口，物理输入已移除；请改用等价菜单/命令的 `ui_action verb=invoke`」 |
| 双击 | 不提供；引导用 `select`/`expand`/`ui_input` 的语义等价 |
| 悬停 / 拖拽 | 不提供 |
| 纯键盘快捷键 | 不提供（UIA 无 key pattern）；有菜单/工具栏入口则 `invoke` |

`ui_find` 命中元素后，沿用 U1 的**同名成员语义反查**（`UiSemanticResolver`）帮 agent 找等价命令/处理器。

## 8. 元素身份 / 生命周期 / 虚拟化

- `ui_find` 缓存**定位条件**（pid + AutomationId/Name/ControlType + 相对路径），**不再**长期持有 live `AutomationElement`（U1 的 `_lastFind` 持元素是失效隐患）。
- 每次动作前按条件**重解析 + 有限重试**（`ElementNotAvailable`/stale → 重试 1-2 次后报「目标已变化，请重新 ui_find」）。
- 虚拟化：`ItemContainerPattern.FindItemByProperty` 找项 → `VirtualizedItemPattern.Realize()` 实体化 → `ScrollItemPattern.ScrollIntoView()`；用于 `ui_find` 定位屏外项/`ui_action` 作用于屏外项。
- `index` 语义保持「最近一次 ui_find 的序号」，但解析退化为「用该序号对应的条件重新定位」。

## 9. 窗口与前台策略

- **默认不调 `SetForegroundWindow`**；仅当目标窗口 `WindowVisualState.Minimized` 时 `WindowPattern.SetWindowVisualState(Normal)`（提升 provider 成功率；还原是否激活取决于目标应用，不主动抢前台）。
- `AutomationElement.Focus()` 只用于**非窗口控件**；目标是窗口时改用 `windowstate=normal`。
- provider 因后台/不可见拒绝语义动作 → 捕获并返回中文（附 `ui_get what=offscreen` 提示），**绝不静默提前台**。

## 10. 等待事件化（`ui_wait`）

- 主路径：对目标窗口订阅 `RegisterStructureChangedEvent(TreeScope.Subtree, ...)`（控件出现/消失）+ `RegisterPropertyChangedEvent(TreeScope.Subtree, ..., Element.Name/Value ...)`（文本变化），命中条件即 `TaskCompletionSource.TrySetResult`；超时 `TrySetResult(超时)`。
- 兜底：若事件 X 秒内无任何触发（provider 不支持），回退 200ms 轮询（只读，不抢鼠标）。
- 订阅/退订/回调线程纳入 gate；回调里只做条件判定 + `TrySetResult`，不重入 UIA 大操作。

## 11. 线程 / 超时（沿用 U1 护栏）

- 单 `UIA3Automation` 实例 + `SemaphoreSlim(1,1)` 串行（MCP 并发调用）。
- 跨进程调用 5s 双层超时（`ConnectionTimeout`/`TransactionTimeout` + 外层 `Task.WhenAny`，现有 `UiaBoundAsync`）。
- **`Invoke` 可能阻塞（模态对话框）**：语义动作一律在独立线程 + 超时内调用；超时按「放弃等待」返回中文。

## 12. debug_verify 集成（V1 扩展）

在 `VerifyScenario` 步骤模型新增两类步骤，`VerifyService` 复用 gate/超时与 `AgentActionLog`：

- **动作步骤 `uiAction`**：`process` + `verb` + 定位（`index`/`name`/`type`）+ 可选 `value`/`direction`/`lines`/`windowstate` → 调 `UiAutomationService` 的 `ui_action`/`ui_input` 同款核心。
- **断言步骤 `uiAssert`**：`process` + 定位 + `what` + `equals`|`contains` → 调 `ui_get` 核心比对；失败理由附**期望 vs 实际**（实际值若命中 DB1 敏感规则照常脱敏）。
- fail-fast、PASS/FAIL 文案与现有步骤一致；步骤顺序里 UI 动作与断点/continue/断言可自由混排，构成「驱动 UI → 调试断言」闭环。

## 13. 内部结构（方案 B：聚焦组件）

```
Services/Ui/
  UiAutomationService.cs     facade：gate + 5s 超时 + Find/Action/Input/Get/Wait 对外 API
  UiElementLocator.cs        进程/窗口解析、FindAllDescendants、条件缓存、动作前重解析/重试、虚拟化实体化
  UiPatternDispatcher.cs     verb → pattern 分派（§6）+ LegacyIAccessible 兜底
  UiStateReader.cs           what → pattern 属性读取（§6 ui_get）
  UiEventWaiter.cs           事件订阅 + 超时（§10），轮询兜底
  UiSemanticResolver.cs      （保留，U1）同名成员反查
```

- 每个单元单一职责、可独立单测；`UiAutomationService` 只做编排与护栏。
- 删除 U1 的 `ActivateWindow`/`PerformPhysicalMouse`/`Mouse.*`；`UiActionResult` 文案改为「已 {verb}（{pattern}）」。

## 14. 破坏性影响与同步清单

- **删工具**：`UiTools.UiInvoke`、`UiTools.UiScroll`；新增 `UiAction`/`UiInput`/`UiGet`。
- **README**：§「UI 自动化」工具表、参数表、用法示例全部改写为 ui_action/ui_input/ui_get；删除物理右键/双击/滚轮措辞。
- **CHANGELOG `[Unreleased]`**：记录破坏性变更（U1 尚未发布 → 无迁移负担；旧四件套从未进入任何版本）。
- **V4 语料**：`AgentCopyGuardTests` 的 ui_* 断言改为新工具关键片段。
- **U1 spec**：`docs/planning/specs/README.md` 的 U1 行加「工具面已被 U1A 取代」标注；U1 spec 正文顶部加 Superseded 说明（保留正文）。
- **测试目标**：`tests/TestData/UiSampleApp` 扩展控件覆盖各 pattern（CheckBox/ComboBox/ListView/Slider/TextBox/Menu）；`generate-testdata.ps1` 不需改（整目录构建拷贝）。
- **旧测试**：`DebugUiToolsTests` 中物理右键/双击/滚轮三例删除，改写为 UIA-only。

## 15. 测试与验收

- **单测**：`UiPatternDispatcher`（各 verb 对样例控件的分派与失败文案）、`UiStateReader`、`UiElementLocator`（失效重解析/虚拟化实体化）。
- **e2e（`DebugUiToolsTests` 重写）**：起 `UiSampleApp` → `ui_find` 能力清单 → `ui_action` 各 verb → `ui_get` 断言 → `ui_wait` 事件命中。
- **不抢鼠标强断言**：动作前后 `GetCursorPos()` 不变 + `GetForegroundWindow()` 不变（护栏，防回退到物理输入）。
- **非交互/最小化会话**：语义动作仍可用（天然去桌面依赖）。
- **debug_verify e2e**：场景文件含 `uiAction`+`uiAssert`，PASS；失败时实际值脱敏。
- **回归**：宿主全量单测 + `McpSessionConcurrencyTests`。

> **§15 勘误（2026-09-10 实施期修正；复核以本勘误为准）**：
> ① **不抢前台强断言口径收窄（verb 限定）**：仅对 `invoke`/`toggle`/`select`/`expand`/`collapse`/`scroll`/`scrollintoview`/`ui_input` 断言
> `GetCursorPos()`/`GetForegroundWindow()` 不变；**豁免 `focus`/`windowstate`**（`AutomationElement.Focus` 对带 HWND 控件
> 可能激活顶层窗口、`SetWindowVisualState(Normal)` 是否激活取决于目标应用——依据 §9，非物理输入）。
> ② **外部活动处理 = 「基线静默才强判」**：动作前对光标/前台**连续采样**确认基线静默（连续多帧一致）后才执行动作；
> 若动作前基线本就不稳定（检出外部活动）→ **跳过该断言并打印明确原因**（不做「强特征」启发式，也不静默降级为弱断言）；
> 基线静默时动作后光标/前台**任一变化即失败**（目标窗口被抢前台或光标落入目标窗口为额外失败触发）。CI 非交互桌面
> 应稳定取到静默基线，从而使该断言成为强断言。
> ③ **确定性防回归兜底**：新增 `NoPhysicalInputGuardTests`——扫描宿主源码树 `src/DotNetDebuggerMcp/**/*.cs` 与构建产物
> `DotNetDebuggerMcp.dll` 的 IL 元数据，禁止出现 `FlaUI.Core.Input`/`Mouse.`/`SetCursorPos`/`SendInput`/`SetForegroundWindow`/
> `ActivateWindow`/`PerformPhysicalMouse` 引用；与桌面是否交互无关、CI 必过/必败（e2e 因桌面噪声跳过时仍有信号）。
> ④ **`UiElementLocator` 测试策略（如实）**：宿主测试工程为 `net10.0` 且不引入 WinForms（须保 PackAsTool 的 net10.0）。
> locator 的**纯逻辑**（身份匹配谓词/ordinal 计数键/同名跨类型 ordinal 定位）**以纯逻辑单测覆盖**（`UiElementLocatorPureTests`，
> 不触 COM）；**index→条件重解析与 stale/失效重试**由跨进程 e2e（`DebugUiToolsTests` 使用 `index` 的用例）覆盖；
> **虚拟化实体化**受 UiSampleApp 非虚拟化列表限制**未覆盖**（不虚称 e2e 已覆盖）。
> ⑤ **`continue.waitSeconds=0`**：为让 `uiAction` 在 `debug_verify` 的 launch 路径下驱动运行中目标，`continue` 步骤新增
> `waitSeconds=0` = 放行不等停点（默认仍 10；`>=1` 行为不变，0 为新增语义）。

## 16. 风险

- **provider 拒后台/最小化**：报中文（不抢前台）；个别自绘控件只能明确「不支持」。
- **虚拟化实体化失败**：报「目标项不可达」并建议 `ui_find` 复查。
- **`Invoke` 阻塞/超时**：5s 兜底。
- **无 pattern 的自定义控件**：`LegacyIAccessible` 兜底，再不行明确不支持——这是 UIA-only 的硬边界。
- **能力探测的跨进程开销**：`ui_find` 已遍历树，补充 pattern 探测在同一次遍历内完成（`IsSupported` 逐项调用，注意总耗时，必要时限制 limit）。

## 17. 工作量

- 中：1 个 facade + 4 组件（部分从现有 `UiAutomationService` 拆/改）+ 3 新工具 + 删 2 工具 + `UiSampleApp` 扩展 + `DebugUiToolsTests` 重写 + `VerifyScenario/VerifyService` 扩展 + README/CHANGELOG/V4 同步。
- 无 Engine/Session 改动；无新 NuGet（FlaUI 引用沿用 U1）。
- 最大不确定性：虚拟化实体化与 provider 后台行为（靠 e2e 暴露，非阻塞）。
