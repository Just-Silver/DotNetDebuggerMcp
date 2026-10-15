# Spec · agent 使用反馈修复（P1–P7）

> 状态：**已拍板/待实施**（2026-10-15 用户 review + builder check 复核后定案，见 §3 拍板记录；§7 待定项已全部落定）。
> 来源：`docs/planning/2026-10-11-agent-usage-feedback.md`（一次真实 WPF「卡死」排查实录，含逐条复现原文）。
> 关联：`docs/planning/specs/2026-09-10-u1a-uia-only-ui-automation.md`（U1A「全 UIA 语义、零物理输入」定位——本 spec **局部解冻**，见 §3/P3）；`docs/planning/specs/2026-09-05-overview-design.md`（总览）。
> 影响面：Engine / Session / 宿主 `Tools/Debugger` / `SharpSight.UiAutomation` 四层；含 1 个新工具参数族、2 个新参数、1 处产品边界调整。

## 1. 背景

2026-10-11 的一次真实排障（日志压测导致 WPF UI 卡死）中，agent 用本 MCP 的**动态调试 / UI 自动化 / 截图 / 静态分析**能力排查，最终**未能靠本 MCP 定位根因**（改用外部 `dotnet-dump collect + analyze`）。反馈文档逐条记录踩到的问题（P1–P7）与建议（§3 建议汇总表）。

本 spec 的目标：把 P1–P7 落成可实施的设计，明确每条的**落点、做法、边界与回归护栏**。

### 已核验的现状（2026-10-15 逐条对代码核实）

| 项 | 现状证据 | 结论 |
|---|---|---|
| P1 | `CallbackHandler._initialHold` 默认 `true`（`CallbackHandler.cs:20`），attach/launch 后**除 ExitProcess 外一切事件不 Continue**（`:77`）→ 进程**确已冻结**；但 `DebugEngineCore.DoBootstrap` 只发布 `Attaching`（`DebugEngineCore.cs:126`），**从不发布 `Stopped`**；`TryRequireStopped`（`DebugInspectTool.cs:251`）要求 `State == Stopped` → 冻结态被当成不可读。`debug_stack` 无 `all`，缺省取 `Buffer.StoppedThreadId`（attach 时为 -1） | **成立**（纯状态口径问题，非能力缺失） |
| P2 | `DebugSessionManager.AttachAsync`（`:111`）无「已有会话」前置检查，直接 `DebugActiveProcess`；宿主 `DebugAttach`（`DebugSessionTool.cs:80`）只 `catch (ex) => 附加调试失败：{ex.Message}`；`ERROR_INVALID_PARAMETER` 全仓库无映射 | **成立** |
| P3 | `UiPatternDispatcher` `select` 分支（`:43-47`）在无 `SelectionItem`/`Invoke` 时退 `LegacyIAccessiblePattern.Select(0)`（`:169-175`）——对仅暴露 legacy 的 WPF-UI 导航项是**静默 no-op**，仍报「已 select」；全库无 `ClickablePoint`/真实点击路径 | **成立** |
| P4 | `UiElementLocator._lastFind` 每次 `FindCore` 末尾整体 `Clear()+AddRange()`（`:174-178`）→ 换词即冲掉；误导文案根因是 `GetCachedEntry`（`:350-358`）把「pid 不匹配」与「缓存为空/被覆盖」合并成同一句 | **成立** |
| P5 | `FindCore` 用 `window.FindAllDescendants()`（`:98`）一次性全树枚举；`ui_find` 无 `scope`/`depth` 参数 | **成立** |
| P6 | `UiAutomationService.GetAutomation()`（`:377`）`_automation ??= new UIA3Automation()` 创建后**从不 Dispose** | **成立** |
| P7 | `DebugEngineCore.DisposeAsync`（`:1701-1710`）只有 `Detach()` + `CorDebug.Terminate()`（终止 ICorDebug 对象，非进程），无「确保 resume」自检 | **防御性**（文档自述未能定因，干净路径不复现） |

## 2. 目标与非目标

**目标**：让「进程卡住/无响应」这一最典型场景**能被本 MCP 排障**；把两类**静默失效**（重复 attach 的裸 COM 错误、legacy select 的假成功）变成**可操作的中文反馈**；让 UI 自动化在大树/长列表下可用且不污染性能观测。

**非目标**：
- **不**新增 `debug_pause`（用户 2026-10-15 拍板：冻结态即可读的最小方案已覆盖场景）。
- **不**改 `Capture`/`Decompiler` 两库。
- **不**做多会话并行（仍是 v1 单活动会话；P2 只把它变成**明确提示**）。
- **不**为 P3 引入独立桌面/VM 隔离。

## 3. 拍板记录（2026-10-15）

1. **范围**：P1–P7 全做。
2. **P1 取向**：**冻结态即可读**（把 attach/launch 后的初始冻结视为可读态 + `debug_stack all=true`），**不新增** `debug_pause`。
3. **P3 取向**：**显式 opt-in 真实点击**——`ui_action` 新增 `clickPoint`（默认 `false`）。默认行为**完全不变**（零物理输入）；仅当 agent 显式 `clickPoint=true` 时才按 `ClickablePoint` 执行真实点击。**这是对 U1A「零物理输入」定位的局部解冻**：护栏放宽为「仅允许该显式分支」，握手/工具说明必须**明确宣告**「会移动光标、注入点击、可能抢前台」。**并且**默认路径（`clickPoint=false`）走 legacy 的「假成功」文案**一并修**（标注「该控件仅暴露 legacy，可能不生效；可试 `clickPoint=true`」）。

### 3.1 builder check 复核后的补充定案（2026-10-15）

| # | 问题 | 定案 |
|---|---|---|
| ① | 冻结态如何表征（无停点事件 → `LastStop==null`、`StoppedThreadId==-1`） | **冻结 = 全部线程可读**：`debug_stack` 缺省即列**全部**托管线程栈（等同隐式 `all=true`）；`debug_wait` 在冻结态直接返回「已冻结、可读栈」；`debug_state` 标注冻结态。**不合成 `StopContext`**（不污染「最近停点」语义与 `debug_run_to` 的陈旧停点引用比较）。 |
| ② | 放宽判定是共享还是分工具；`debug_set` 是否也放行 | **仅读类开放**：`debug_stack`/`debug_variables`/`debug_evaluate`/`debug_object` 在冻结态放行；**`debug_set`（写目标内存）仍要求真停点**——新增只读专用判定，不改 `TryRequireStopped` 的 `debug_set` 调用点。 |
| ③ | `debug_reset` 与 `debug_disconnect` 的实质差异；「残留附加」能否修 | **只对本工具建立的会话负责**。P2 = ①attach 前置拒绝 ②COM 错误中文化 ③**修三处真 bug**：失败 attach 漏释放 `DebugEngineCore`（`DebugSession.CreateAsync`）、detach 失败被静默吞（`DebugEngineCore.DisconnectAsync`）、`CloseAsync` 先置空后断开（改「断开成功后才置空」）④`debug_reset` = 强制最终化（Terminate ICorDebug + 释放引擎 + 丢引用，失败如实报）⑤文档诚实说明：**外部/残留附加（别的调试器占着目标）无法从会话层解除，需重启目标**。**不加引擎实例账本**（三处修好后不再产生孤儿）。 |
| ④ | P7 兜底 resume 的落点与 API | 落点改到**泵内** `DisconnectAsync`（泵外 `DisposeAsync` 直接 `Detach()` 属既有 off-pump 调用，可能是偶发「目标不响应」的来源）；resume 用循环 `Continue`（`SetAllThreadsDebugState(ThreadRunning)` 语义是撤销自身 suspend，不是通用「恢复运行」）；`Detach` 官方语义本即「进程继续正常运行」。 |
| ⑤ | P3：`clickPoint=true` 与其它 verb 组合；护栏形态 | `clickPoint=true` 只在 `verb ∈ {invoke, select}` 生效；**其它 verb + `clickPoint=true` → 明确中文报错**（不静默忽略）。护栏：因真实点击绕不开 `FlaUI.Core.Input.Mouse`（已核实）且旧护栏抓不到 `element.Click(false)`，需把 **`AutomationElement::Click` 的 MemberRef 纳入 IL 黑名单**，并把源码 `Mouse.`/`FlaUI.Core.Input` 字面量黑名单改为「允许 `clickPoint` 专用文件 + 断言调用点唯一」。 |
| ⑥ | P6：释放 UIA 客户端能否消除扰动 | **做释放 + 声明**：`ui_*` **整次调用结束**后释放（不能在每次 `RunGateAsync` 后释放——`WaitAsync` 的订阅跨多次取放 gate）；加可注入 seam 做单测（释放后再次调用能重建）。**不声称能完全消除扰动**（扰动源可能是 WPF peer 树/事件广播）。 |
| ⑦ | P1：`Launching` 是否纳入 | 纳入（与 `Attaching` 并列）。注：`Launching` 只由引擎原生 launch 触发，而 `debug_launch`/`-dbg` 都走 Session 的 `LaunchAndAttachAsync`（状态是 `Attaching`），故 `Launching` 实际不出现；一并纳入只为对称，无需专门话术。 |


## 4. 逐项设计

### P1 attach 后即可读栈（含 `debug_stack all`）

**问题**：attach 后进程已冻结（`_initialHold=true`），但查询类工具以 `State == Stopped` 为前置，导致「能读却不给读」。

**关键约束（builder check 发现）**：冻结态**没有停点事件**——`Buffer.LastStop == null`、`StoppedThreadId == -1`。故仅放宽判定**不够**：`debug_stack` 缺省分支走 `StoppedThreadId`，会仍返回「无停点线程可读」。必须同时定义冻结态的线程选择语义。

**设计**：
1. **冻结态判定**：新增只读专用 `TryRequireReadable(...)`（`State ∈ {Stopped, Attaching, Launching}`）。**仅** `debug_stack`/`debug_variables`/`debug_evaluate`/`debug_object` 使用；**`debug_set` 不换**（仍 `TryRequireStopped`，冻结态写目标内存风险高）。
2. **`debug_stack` 冻结态缺省 = 全线程**：冻结时全部托管线程已停、本就不存在「单一停点线程」——`all=true` 或**冻结态缺省**均列出**全部**托管线程栈，按线程分组（非停点线程读不到给「（该线程无 IL 帧/不可读）」占位）。非冻结（真停点）时缺省保持现状（`threadId` 或最近停点线程）。
3. **`debug_wait` 认冻结态**：attach 后立即 `debug_wait` 时，「已在冻结态」作为**可读停点**返回（无 `StopContext`，直接报「进程已冻结（attach 冻结时刻）、可直接读栈；debug_continue 后运行至断点」）。
4. **`debug_state`**：冻结态额外给一行「进程已冻结（可读栈）」，`StateText` 的 `Attaching` 文案已含「进程已冻结待放行」，对齐即可。
5. **`[Description]` 与握手**：`debug_stack`/`debug_attach`/`debug_launch` 说明写明「**attach/launch 后进程即冻结，可直接读栈**」；把「读栈前必须先有停点」改为「冻结态或停点态均可读」。
6. **Web 展示面**：`DebugViewService` 的 `CurrentState != Stopped` 判定**同步**放宽为可读态（否则同一会话 MCP 与 Web 语义不一致）。

**边界**：本项只保证**栈可读**；`debug_variables`/`debug_evaluate`/`debug_object` 在冻结态依场景可能读不到值（attach 冻结在任意执行点），沿用既有提示。


### P2 重复 attach 的可操作反馈

**设计**（五层，只对本工具建立的会话负责）：

1. **Session 层前置检查**：`DebugSessionManager.AttachAsync` 在建立会话**之前**检查 `Active`——非 null 则抛中文 `InvalidOperationException`：`「已有活动调试会话（pid=X）。同一时刻仅支持一个调试会话，请先 debug_disconnect 或 debug_terminate 后再附加。」`（**不改**自动替换语义：`Activate` 的替换路径保留给 launch；仅 attach 走前置拒绝）。
2. **COM 错误中文化**：宿主 `DebugSessionTool` 增 `DescribeAttachFailure(Exception)`，把 `ERROR_INVALID_PARAMETER`/`0x80070057` 等映射为可操作中文（「目标可能已被本 / 其它调试器附加，或权限不足（管理员进程需同权限）；先 `debug_disconnect`，或确认无其它调试器」），未识别错误保留原文。
3. **修三处真 bug**（「我们造成的残留」根因）：
   - `DebugSession.CreateAsync`：`await start(core)` 抛异常时**漏释放** `DebugEngineCore`（泄漏 MTA 线程 + 已 Initialize 的 ICorDebug）→ 失败路径 `DisposeAsync` 兜底。
   - `DebugEngineCore.DisconnectAsync`：`try { _process?.Detach(); } catch { }` **静默吞 detach 失败** → 改为失败**如实上报**（不再假装已断开）。
   - `DebugSessionManager.CloseAsync`：**先置空 `_active` 再断开** → 改「断开成功后才置空」，避免「报已断开/无会话但引擎仍挂着」。
4. **`debug_reset`（新增工具）**：强制最终化——Terminate ICorDebug + 释放引擎 + 丢引用（失败如实报），返回「已强制释放调试会话；目标进程继续独立运行」。
5. **诚实说明**：文档（工具 `[Description]` + README）写明——**外部调试器附加 / 上次会话崩溃 / 系统挂起**造成的「残留附加」**无法从会话层解除，需重启目标进程**。

**不做**：引擎实例账本（跟踪每个 `DebugEngineCore` 防孤儿）——三处 bug 修好后不再产生孤儿，账本成本（Session 层跨实例生命周期 + 新竞态面）大于收益。


### P3 无 Invoke/SelectionItem 控件的真实点击（显式 opt-in）

**已核实事实**（本地 FlaUI v5.0.0 克隆 + NuGet 缓存）：`element.Click(false)` 内部**先取 `ClickablePoint` 再真点**，`GetClickablePoint()`/`TryGetClickablePoint()` 只取点；真实点击**绕不开 `FlaUI.Core.Input.Mouse`**（`Click` → `Mouse.Position`(`SetCursorPos`) → `Mouse.LeftClick`(`SendInput`)），即便写 `element.Click(false)` 也只是把它藏在 `FlaUI.Core.dll` 内部。UIA3 取点有**三级回退**（原生 → `Properties.ClickablePoint` → `BoundingRectangle.Center()`），取点失败**极为罕见**。

**设计**：
1. **新参数**：`ui_action` 增 `clickPoint`（`bool`，默认 `false`），`[Description]` 注明「**会移动真实光标、注入真实点击、可能抢前台**；仅在目标无 `Invoke`/`SelectionItemPattern`（如 WPF-UI 导航项只暴露 `legacy`）且语义动作被证实无效时显式开启」。
2. **分派**：`clickPoint=true` 且 `verb ∈ {invoke, select}` → 真实点击；**其它 verb + `clickPoint=true` → 明确中文报错**（不静默忽略）。`clickPoint=false` 时行为与现在一致。
3. **默认路径文案修复**：`clickPoint=false` 且命中 `LegacySelect`/`LegacyDefaultAction` 时，返回文案标注**不确定性**（如「已 select（LegacyIAccessiblePattern）——该控件仅暴露 legacy，**可能不生效**；无效时可试 `clickPoint=true`（会真实点击）」）——直击「报成功但没效果」的原痛点，默认行为仍零物理输入。
4. **护栏调整**：`NoPhysicalInputGuardTests` 需①把 `AutomationElement::Click` 的 MemberRef 纳入 IL 黑名单（旧护栏抓不到 `Click(false)`）；②源码 `Mouse.`/`FlaUI.Core.Input` 字面量黑名单改为「允许 `clickPoint` 专用文件、断言其调用点唯一且被 `clickPoint` 守卫」，另一处仍禁。**不得**整条删除护栏。
5. **握手 + 工具说明**：握手「UI 自动化」段补「目标无 `Invoke`/`SelectionItem`（如部分 WPF-UI 导航项）时可显式 `ui_action clickPoint=true` 走真实点击——会移动光标/注入点击/可能抢前台，性能类排查勿用」。
6. **U1A spec**：加 Superseded（部分）标注（「零物理输入」在 `clickPoint=true` 显式分支下例外）。
7. **测试目标**：优先纯逻辑单测（分派 + 文案契约）+ e2e 真点击（环境可用时），`UiSampleApp` 是否新增「仅 legacy 控件」按实现可行性定。


### P4 `index` 语义与文案

**设计**：
1. **修文案**（`GetCachedEntry`）：拆分两种情形——
   - `_lastFindPid != pid` → 「index={index} 引用的不是进程 pid={pid} 的 ui_find 结果（当前缓存属于 pid={current}）。请先对该进程执行 ui_find。」
   - `_lastFind.Count == 0`（pid 一致）→ 「ui_find 结果缓存已被后续查询覆盖或为空，index 无法解析（index 只在**紧邻上一次 ui_find** 内有效）。请重新 ui_find 后立即用其 index。」
2. **input 的 index 解析（P4 建议 ②「name+type 唯一化」）**：`ResolveByName` 已支持 `name`+`type` 组合过滤（`FindUniqueByName` 先按 type 过滤再按 name 唯一化）；本项**在 `[Description]` 明确**「name+type 组合可唯一化，避免 index 缓存脆弱」，并把「命中多个」的报错文案改为**优先引导 name+type**（现在是「请先 ui_find 定位后按 index 操作」）。
3. **index 稳定化（P4 建议 ② 原文）**：**不做**（按元素编号而非「上次结果」需要长期持有 live 元素或全量重扫索引，与 U1A §8「不长期持有 live 元素」及 P5 的性能目标冲突）——改为**文档化现有契约** + 文案引导。

### P5 `ui_find` 的 `scope` / `depth`

**设计**：
1. **新参数**：`ui_find` 增 `scope`（`int`，默认 `-1` = 不限；>=0 时以**上一次 `ui_find`** 的该 index 元素为根，仅枚举其子树）与 `depth`（`int`，默认 `0` = 不限；>0 时限制相对根的最大深度）。
2. **实现**：`UiElementLocator.FindCore` 增加 `scopeIndex`/`maxDepth`——scope 由 `_lastFind` 缓存条件重解析为 `AutomationElement` 作根（复用 `ResolveByDescriptor`），再遍历；depth 在遍历时按 `TreeWalker` 层级计数（`FindAllDescendants` 无深度参数，需用 `TreeWalker` 或按祖先链判定）。
3. **说明**：`[Description]` 注明「大表/长列表（如 999 行 DataGrid）建议先 `scope` 到容器 + `depth` 限层，否则单次枚举可能超 5s 护栏」。
4. **超时反馈**：`UiaBoundAsync` 超时文案已说明可调大 `timeoutSeconds`；本项在 `ui_find` 超时提示里**加一句**「大 UI 树请用 scope/depth 限定范围」。

### P6 `ui_*` 的 UIA 客户端释放与观测扰动告知

**设计**：
1. **整次调用结束即释放**：`UiAutomationService` 在**每次 `ui_*` 工具调用结束**后释放所持 `UIA3Automation`（不能按「每次 `RunGateAsync` 后」释放——`WaitAsync` 的订阅跨多次取放 gate）。释放后再次调用按需重建。
2. **可注入 seam**：加内部 seam（如 `Func<UIA3Automation>` / `Dispose` 钩子）供单测断言「调用结束后已释放、再次调用能重建」。
3. **文档声明**：握手 + `ui_find`/`ui_action` 的 `[Description]` 明示「`ui_*` 会向目标进程注入 UI Automation 客户端、**可能改变其性能特征**（WPF 会维护/广播自动化树）；**性能/卡顿类排查请优先用 `debug_*` + 截图**」。
4. **不声称完全消除扰动**：反馈中 UI 线程停在 `fireAutomationEvents → AutomationPeer.UpdateSubtree` 说明扰动源可能是 WPF peer 树/事件广播；本项只保证「用完不常驻客户端」，并在文档如实表述。


### P7 `debug_disconnect` 兜底 resume 与自检

**设计**：
1. **Engine**：兜底 resume 落在**泵内** `DisconnectAsync`（**不是**泵外 `DisposeAsync`——那里直接 `Detach()` 属既有 off-pump 调用，可能正是偶发「目标不响应」的来源）。detach 前若进程仍处停止态（`TryIsRunning == false`），循环 `Continue` 放行后再 `Detach()`。resume 用**循环 `Continue`**（`SetAllThreadsDebugState(ThreadRunning)` 的语义是撤销自身此前的 suspend，不是通用「恢复运行」）。detach 失败保底 `CorDebug.Terminate()`（终止 ICorDebug 对象，**不是**进程——**严禁**误用 `CorDebugController.Terminate`，那会杀掉目标）。
2. **宿主自检**：`debug_disconnect` 返回后做轻量自检（launch 会话用 `Process.HasExited`/`Responding`；attach 会话只能报「无法判定」），结果写进返回文案。
3. **文档**：`debug_disconnect` 说明补「断开后目标继续独立运行；若目标随后不响应，可能是外部因素（外部调试器残留附加 / `NtSuspendProcess` / 系统挂起），可重启目标恢复」。


## 5. 工具面变更汇总（agent 可见契约）

| 工具 | 变更 | 默认值/兼容 |
|---|---|---|
| `debug_stack` | 新增 `all`（bool，默认 `false`）；**冻结态缺省即全线程** | 兼容（非冻结默认行为不变） |
| `debug_attach` / `debug_launch` / `debug_state` / `debug_wait` | 文案改：**冻结态即可读** | 纯文案 + P1 行为 |
| `debug_reset` | **新增工具**（强制最终化 / 释放引擎） | — |
| `debug_disconnect` | 文案改（自检 + 不响应指引）；Engine 兜底 resume + 失败不再静默 | 兼容 |
| `ui_action` | 新增 `clickPoint`（bool，默认 `false`）；legacy 命中时文案标注「可能无效」 | 兼容（默认零物理输入） |
| `ui_find` | 新增 `scope`（int，默认 `-1`）、`depth`（int，默认 `0`） | 兼容 |
| **能力开关** | 新增 `--features` / `DOTNETDEBUGGERMCP_FEATURES`（四族门控，默认仅 `decompile`；未开启完全不注册且握手不提） | **默认行为变化（破坏性）**——见 §8 |
| 握手 `HandshakeFeatureIntro` | **改为按启用族拼装**；补：冻结态可读栈 / `clickPoint` 物理输入声明 / `ui_*` 观测扰动 / `scope`·`depth`；`HandshakeFeatureIntro_覆盖全部能力族触发条件` 改为**按开关断言** | — |

> 所有新增参数带默认值、方法带 `CancellationToken`、错误返回中文——遵循根 `AGENTS.md` 铁律。

## 6. 回归与验证

> **前置**：能力开关（§8）落地后，所有**经 MCP 起 server** 的测试必须显式传 `--features=decompile,debug,ui,screenshot`（`DebugMcpToolsTests.ConnectAsync`、`McpSessionConcurrencyTests`、`McpWebCoexistTests` 三个启动点），端到端 `ClientRunner` 同理；否则默认只开 `decompile`，调试/UI/截图测试全部找不到工具。

- **Engine**：泵内 `DisconnectAsync` 兜底 resume（真实 attach 目标 → 停止态 detach → 断言目标仍在运行/未挂起）；detach 失败不再静默。
- **Session**：`AttachAsync` 已有会话 → 抛中文提示（不触引擎）；失败 attach 不漏释放 `DebugEngineCore`；`CloseAsync` 断开成功才置空。
- **宿主**：
  - P1：attach 后 `debug_state` 报可读、`debug_stack` 冻结态缺省列全部线程（`DebugMcpToolsTests` 扩展）。
  - P2：重复 attach 中文提示；`debug_reset` 后能重新 attach（e2e）。
  - P3：`clickPoint` 分派与「其它 verb 报错」纯逻辑单测；legacy 文案契约（`AgentCopyGuardTests`）。
  - P4：`GetCachedEntry` 两种文案（纯逻辑单测）。
  - P5：`scope`/`depth` 限定（`UiElementLocatorPureTests` + 真实 UiSampleApp e2e，环境不可用则 Skip）。
  - P6：`UiAutomationService` 释放 seam 单测（调用结束已释放、再次调用能重建）。
  - **能力开关**：注册按族过滤（单元）；握手按族拼装、未启用族**不出现**（`DotNetDebuggerMcpCmdTests`）；`--features=` 解析（默认/空/未知取值）。
  - `AgentCopyGuardTests`（V4）：P1/P2/P3/P6 的关键文案片段补断言。
- **护栏**：`NoPhysicalInputGuardTests` 改为「`AutomationElement::Click` MemberRef 入 IL 黑名单 + 源码字面量放行限定 `clickPoint` 专用文件」（不得整条删除）；新增「工具注册覆盖率」断言（每个 `[McpServerToolType]` 类型都在能力族注册表里）。
- **测试启动面**：`DebugMcpToolsTests.ConnectAsync` / `McpSessionConcurrencyTests` / `McpWebCoexistTests` / `ClientRunner` 补 `--features`；`opencode.json`（本仓库自举）补 `--features=decompile,debug,ui,screenshot`。
- **文档**：`CHANGELOG.md`（`[Unreleased]`，面向使用者，含默认行为变化的显著标注）、根 `README.md`（能力开关一节）、`docs/planning/specs/README.md`、`docs/planning/README.md` 文档地图、各 `TODO.md`、`src/DotNetDebuggerMcp/.mcp/server.json`。

## 7. 待定项（实施时定，不阻塞拍板）

> 已全部落定（见 §3.1）。其余纯实施细节（`scope`/`depth` 的 off-by-one 与 index 缓存交互、`depth` 用 TreeWalker 还是祖先链判定、超时提示加句位置、`DescribeAttachFailure` 的实现位置）实施中定，不影响方向。

## 8. 能力开关（能力族门控，2026-10-15 新增需求）

### 8.1 需求

**默认只启用反编译能力**；调试 / UI 自动化 / 截图三族默认关闭。未开启的族：

- **工具不注册**（不进 MCP 工具目录——agent 看不到、也调不到）；
- **握手不注入相关内容**（`ServerInstructions` 里不得出现该族的触发条件、工具名、使用约定）。

**开关只作用于 MCP 面**：CLI 执行路径（`-a` 反编译、`-l` 列类型、`-dbg` 一次性调试、`-c` 更新检查）**完全不受开关影响**——即使四族全关，CLI 照常工作。

### 8.2 拍板记录（2026-10-15）

1. **四个开关**：`decompile` / `debug` / `ui` / `screenshot`，**全部可关**（含 `decompile`，对称——不存在"不可关"的特例）。
2. **未开启 = 完全不注册**（不是"注册但返回未启用提示"）。
3. **配置来源**：进程启动参数 + 环境变量（MCP 客户端在 `mcp.json` 的 `args`/`env` 配置）——**不引入 JSON 配置文件**（避免隐式状态源）。CLI 业务参数不参与门控。

### 8.3 能力族 → 工具映射

| 开关 | 覆盖的工具类型 | 默认 |
|---|---|---|
| `decompile` | `Tools/Decompile/*`（Decompile/DecompileMember/DecompileIl/DecompileToDir/DecompileToProject）+ `Tools/Metadata/*`（11 个）+ `Tools/CacheStatsTool` | **开** |
| `debug` | `Tools/Debugger/Debug*.cs`（DebugSession/Breakpoint/Control/Inspect/Exception/Output/Process/RunTo/Timeline/Verify/Set/Evaluate/Object 共 13）+ `Tools/Web/WebOpenTool`（`web_open` 随调试走，不单列开关） | 关 |
| `ui` | `Tools/Debugger/UiTools.cs`（`ui_find`/`ui_action`/`ui_input`/`ui_get`/`ui_wait`） | 关 |
| `screenshot` | `Tools/Screenshot/*`（`screenshot`/`screenshot_displays`/`screenshot_windows`） | 关 |

- **归属实现**：集中式**注册表**（单一文件，`类型 → 能力族` 表）+ **覆盖率回归测试**（断言每个 `[McpServerToolType]` 类型都在表里，新增工具漏登记即测试失败）。不逐个工具加特性（避免 30 文件散落、便于审查）。
- `ui` 独立于 `debug` 的依据：`ui_*` 不要求活动调试会话（可独立驱动 UI），且 `default` 场景下两者是不同用途。

### 8.4 配置语法

- **CLI**：`--features=<逗号分隔>`，取值 `decompile,debug,ui,screenshot`（忽略大小写/空白）；**未指定**该参数时默认 `decompile`；**显式传空**（`--features=`）表示**零能力**。
- **环境变量**：`DOTNETDEBUGGERMCP_FEATURES`（同语法）；**CLI 优先于环境变量**（CLI 显式给定时忽略环境变量）。
- **未知取值**：给出明确中文错误并退出（列出合法取值），不静默忽略——防拼错导致"以为开了其实没开"。

### 8.5 注册实现

- `DotNetDebuggerMcpCmd.OnExecuteAsync` 的 MCP 分支：把 `.WithToolsFromAssembly()` 改为「扫描程序集内 `[McpServerToolType]` 类型 → 按注册表过滤出**已启用族**的类型 → `.WithTools(IEnumerable<Type>)`」。已核实 SDK 2.2.0 提供该重载。
- 仓库无 `[McpServerPrompt]`/`[McpServerResource]`（无 prompts/resources 需门控），故只需处理 tools。

### 8.6 握手拆分（关键改动）

现状：`AppText.HandshakeFeatureIntro` 是**单个 const**，静态包含四族全部触发条件（`AppText.cs:40`），`BuildServerInstructions`（`DotNetDebuggerMcpCmd.cs:312`）只做「简介 + 更新报告」拼接。

改为：按**启用的能力族**拼装 `ServerInstructions`——

1. **服务器简介段**：按启用族动态生成「提供 N 族能力：…」列表（全关时给中性表述或不注入简介）。
2. **何时使用段**：**只列已启用族**的触发条件（disabled 族的工具名/触发词**完全不出现**）。
3. **使用约定段**：保留与族无关的通用约定；族专属约定（如 `ui_*` 的 5s 护栏、`clickPoint` 物理输入声明、`ui_*` 观测扰动）只在 `ui` 启用时出现。

**回归护栏调整**：`DotNetDebuggerMcpCmdTests.HandshakeFeatureIntro_覆盖全部能力族触发条件`（`:117`）从「全量断言」改为**按开关断言**——对每个族：启用时其触发条件存在、禁用时**不存在**（防"关了还注入"这一核心要求回归）。

### 8.7 文档与配置面同步

- `src/DotNetDebuggerMcp/.mcp/server.json`：`environmentVariables` 登记 `DOTNETDEBUGGERMCP_FEATURES`（含说明与默认值）。
- 根 `README.md`（面向包使用者）：新增「能力开关」一节（语法、默认值、示例 `mcp.json`）。
- `CHANGELOG.md` `[Unreleased]`：记 `Added`（能力开关）+ `Changed`（**默认行为变化：调试/UI/截图默认不再暴露**——属使用者可见的破坏性默认变化，必须显著标注并提供开启方式）。
- 各 `TODO.md` / 文档地图登记。

### 8.8 待定项

- **待定 E**：全关（零能力）时握手简介段如何表述——倾向「不注入简介段，仅保留更新状态段」；实施时定。
- **待定 F**：`debug` 关闭但 `ui` 开启时，`debug_verify` 的 `uiAction`/`uiAssert` 步骤（属 debug 族工具的能力）无冲突；反之 `ui` 关闭而 `debug` 开启时，verify 场景里的 ui 步骤会失败——按**运行期报错**处理（不在注册期联动），实施时在 `debug_verify` 说明里注明。
