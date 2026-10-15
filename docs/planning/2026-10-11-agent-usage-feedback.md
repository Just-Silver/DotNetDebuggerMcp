<!-- 职责：DotNetDebuggerMcp 的「agent 实际使用」反馈（问题清单 + 建议），来自一次真实 WPF 卡死排查；不写：修复方案的实施细节（落各项目 TODO / 计划）。 -->

# DotNetDebuggerMcp 使用反馈（一次 WPF「卡死」排查的实录）

> **用途**：记录 agent 在真实排障中使用 `DotNetDebugger` MCP（动态调试 / UI 自动化 / 截图 / 静态分析）时**踩到的问题与建议**，作为工具面打磨的输入。
> 日期：2026-10-11。环境：Windows；会话内 MCP（`DotNetDebuggerMcp` 2.2.0）；目标 = 一个 WPF 冒烟 App（`Common.Wpf.SmokeApp`，可反复启停）。
> 场景：排查「日志压测导致 UI 卡死」。**最终根因不是靠本 MCP 定位的**，而是靠外部 `dotnet-dump collect + analyze` 抓栈——本报告正是记录「为什么本 MCP 没能定位」。

## 0. 复现用的外部探针（非本 MCP）

| 探针 | 用途 |
|---|---|
| `SendMessageTimeout(hwnd, WM_NULL, timeout)` | 量目标 UI 线程真实响应延迟（不经 UIA，不受 UI 树大小影响） |
| `IsHungAppWindow(hwnd)` | 窗口是否被标记为 hung |
| `Get-Process ... TotalProcessorTime` 差值 | 目标是否在烧 CPU（忙）还是被阻塞（0%） |
| `dotnet-dump collect/analyze`（外部工具） | 抓托管栈（本 MCP 在「无停点」时给不出，见 P1） |

## 1. 问题清单（按影响排序）

### P1（高）附加后无法读栈；且没有「暂停 / break-all」

- **现象**：`debug_attach` 成功后 `debug_state` 报「已附加（Attaching，进程已冻结待放行…）」，但 `debug_stack` 直接回「**进程未停在断点/异常（当前非 Stopped 状态）**」；`debug_threads` 能列出全部托管线程（本次 8 个），却读不到**任何一个**线程的栈。
- **复现**：`debug_attach({processId})` → `debug_stack({})`（本次实测原文见 §4）。
- **影响**：排查「进程卡住 / 死锁 / 不响应」这一**最典型**场景时无能为力——恰恰是最需要它的时刻。没有「暂停全部线程」的工具，无法把「运行中 / 阻塞」的进程停到可读栈的状态；attach 时的「冻结」**不可用于观察**。最终只能外部 `dotnet-dump`（并为此额外安装工具）。
- **建议**：① attach 后进入**可读栈的 Stopped**（把 attach 冻结当停点），或新增 `debug_pause`（暂停全部线程）；② `debug_stack` 增 `all=true`（一次列出所有线程栈，死锁排查刚需）；③ 在工具 `[Description]` / 握手里写明「读栈前必须先有停点」这一前置（现在只在返回文案里说）。

### P2（高）`debug_attach` 在已有会话时报无解释的 `ERROR_INVALID_PARAMETER`，且无法恢复

- **现象**：会话已存在时再次 `debug_attach` → `附加调试失败：Error HRESULT ERROR_INVALID_PARAMETER has been returned from a call to a COM component.`（无「已附加」提示、无恢复路径）。
- **复现**：`debug_attach(pid)` → `debug_attach(pid)`（第二次**必现**，本次实测原文见 §4）。
- **影响**：错误不可操作；且本会话中一次出现「`debug_disconnect` 报已断开、`debug_state` 报无活动会话，但再次 attach 仍报同一 COM 错误」——**会话书签与引擎状态不一致且无法重置**，直接把排障卡死。
- **建议**：① attach 前检查已有会话 → 明确提示「该进程已有调试会话，请先 `debug_disconnect`」；② 该 COM 错误映射为中文可操作提示；③ 提供 `debug_reset`（强制释放引擎 / 会话）。

### P3（高）`ui_action` 驱不动 WPF-UI `NavigationViewItem`（报成功但不导航）

- **现象**：对 WPF-UI 导航项（UIA 只暴露 `legacy`）执行 `ui_action verb=select`（或 `invoke`）→ 返回「**已 select（LegacyIAccessiblePattern）**」，但**页面未切换**（切换后本应出现的「日志压测」按钮仍 `ui_find` 不到）。
- **复现**：`ui_find(text=导航项名)` → `ui_action(select|invoke, index=…)` → `ui_find(目标页独有控件)` = **0**（本次实测原文见 §4）。
- **影响**：WPF-UI 应用的**主菜单导航**用 UIA 驱不动；agent 只能改用外部真实鼠标点击（本会话即如此），但这违背本工具「不注入输入」的定位。
- **建议**：对无 `Invoke`/`SelectionItemPattern` 的控件支持「按 `ClickablePoint` 的真实点击」（可加显式开关）；否则在工具说明里明确此限制与替代路径。

### P4（中）`ui_action` / `ui_get` 的 `index` 语义脆弱 + 报错文案误导

- **现象**：
  1. `ui_action(name=…)` 命中多个控件即拒绝（本次「日志与异常」命中 `DataItem` + `Text` 两个）→ 必须 `ui_find` 拿 `index`；
  2. `index` 只对**紧邻的上一次** `ui_find` 有效，中间任何一次 `ui_find`（哪怕换了 `text`）都会**冲掉**缓存——本次我就被自己的一次 `ui_find(text="日志压测")` 冲掉，随后 `index` 失效；
  3. 失效报错文案误导：`index=0 引用的不是进程 pid=X 的 ui_find 结果（当前缓存 pid=X）`——pid 明明一致。
- **影响**：交互琐碎易错；文案让人误以为 pid 不匹配，浪费排查时间。
- **建议**：① `ui_action`/`ui_get` 允许 `name` + `type` 唯一化（内部优先取可交互控件，如 `Button`）；② 让 `index` 在**同一进程的多次 `ui_find` 之间稳定**（按元素编号而非「上次结果」）；③ 修文案为「缓存已被后续 `ui_find` 覆盖，请重新 `ui_find`」。

### P5（中）`ui_find` 在大 UI 树（999 行 DataGrid）上超时，且无「限定子树 / 浅层枚举」

- **现象**：目标日志表有 999 行时，`ui_find`（带 `text` 过滤）单次耗时 >4s，直接触发默认 5s 护栏超时；表为空时瞬间返回。
- **影响**：大表格 / 长列表的 UI 目标几乎无法用 `ui_find` 操作。
- **建议**：支持 `scope`（以某元素为根的子树）与 `depth`（最大深度）；或对 `Text`/`Button` 这类叶节点做轻量枚举；`[Description]` 注明「大表会慢」。

### P6（中）使用 `ui_*` 会向目标注入 UIA 客户端，扰动目标性能特征

- **现象**：一旦用过 `ui_*`，目标进程被挂上 UI Automation 客户端；WPF 随即维护 / 广播自动化树（本会话一次转储里 UI 线程整段停在 `ContextLayoutManager.fireAutomationEvents → AutomationPeer.UpdateSubtree`）。这会**改变目标的性能特征**——我据此一度把「UIA 开销」误判为卡死主因，直到改用「不碰 UIA 的探针」才纠正。
- **影响**：**观测扰动**——用本工具看性能 / 卡顿会得到失真结论。
- **建议**：工具用完**主动释放 UIA 客户端**（或提供释放开关）；在握手 / 工具说明里明示「`ui_*` 会向目标注入自动化客户端、可能影响性能观测；性能类排查请改用非 UIA 手段」。

### P7（观察，未定因）一次「目标不响应」无法归因；干净 attach→disconnect 不复现

- **现象**：本会话中一次目标 `IsHungAppWindow=True`、`WM_NULL` 数秒全超时、CPU≈0%，而转储显示 UI 线程停在**正常的 `GetMessage`**（即**进程级挂起**，不是死锁），杀进程才恢复。当时与一次 `debug_attach` 失败相邻出现。
- **复现（本轮）**：**未能定因**——干净 `attach → disconnect` 与 `attach →（失败 attach）→ disconnect` **两轮实测都正常**（`WM_NULL` 0–7ms、CPU 0%）；我用外部 `NtSuspendProcess` 才复现出类似「不响应」。故**不能归因于 MCP**。
- **建议**：给 `debug_disconnect` / 会话异常结束加「确保目标被 resume」的兜底与自检；文档补一节「若目标在会话后不响应，如何自证与恢复」。

## 2. 做得好的部分（保留）

- **断点**：`debug_breakpoint_set` 支持「类型 + 成员子串」定位（本次一次命中 `LogBuffer.UpdateUI`），`debug_breakpoint_list` 显示绑定状态——实用。
- **等停点 / 现场**：`debug_wait` + `debug_state` +（命中后）`debug_stack` 给出清晰调用栈——本次正是它确认了「UI 线程停在布局尾部」。
- **UIA（常规控件）**：对普通 `Button` 用 `ui_action(index=…)` 有效；`ui_get` / `ui_wait` 语义清晰。
- **截图**：`screenshot`（WGC 整窗 / region / element）稳定好用，头部 `原点 / 帧` 元数据有用。
- **进程 / 模块**：`debug_processes` / `debug_modules` 便于确认目标与加载情况。

## 3. 建议汇总（可直接进各项目 TODO）

| # | 建议 | 建议归属 |
|---|---|---|
| 1 | attach 即可读栈（或新增 `debug_pause`）+ `debug_stack all=true` | Engine / Session + 宿主工具面 |
| 2 | attach 已有会话 → 明确提示；新增 `debug_reset` | Session / 宿主 |
| 3 | `ERROR_INVALID_PARAMETER` 等 COM 错误中文化、给可操作提示 | 宿主 |
| 4 | 无 `Invoke`/`SelectionItemPattern` 控件的真实点击（`ClickablePoint`） | SharpSight.UiAutomation + `ui_action` |
| 5 | `ui_action` 支持 `name`+`type` 唯一化；`index` 稳定化；修文案 | 宿主 `ui_*` |
| 6 | `ui_find` 支持 `scope` / `depth` | 宿主 `ui_find` |
| 7 | `ui_*` 用完释放 UIA 客户端 + 文档声明「观测扰动」 | SharpSight.UiAutomation |
| 8 | `debug_disconnect` 兜底 resume + 自检 | Engine / Session |

## 4. 附录：本次实测原文（复现步骤）

**P1（附加后读不到栈）**
```
debug_attach({processId: 36504}) → 已附加调试会话…（进程已冻结待放行）
debug_stack({})                  → 进程未停在断点/异常（当前非 Stopped 状态）。先 debug_continue 运行至断点停下，再读栈/变量。
debug_threads()                  → 托管线程（8）: thread 43952 / 47356 / 43592 / 51120 / 26160 / 49092 / 40364 / 48164
```

**P2（重复 attach → 无解释 COM 错误）**
```
debug_attach({processId: 24280}) → 已附加调试会话…
debug_attach({processId: 24280}) → 附加调试失败：Error HRESULT ERROR_INVALID_PARAMETER has been returned from a call to a COM component.
```

**P3（导航项 UIA 报成功但不导航）**
```
ui_find({text:"日志与异常"})                      → [0] DataItem 日志与异常 [1] Text 日志与异常
ui_action({verb:"select", index:0})              → 已 select（LegacyIAccessiblePattern）
ui_find({text:"日志压测"})                        → 共 0 个控件（页面未切换）
```

**P4（index 缓存被冲掉 + 文案误导）**
```
ui_find({text:"日志与异常"})   → frameId=1，[0]=DataItem
ui_find({text:"日志压测"})     → frameId=2，0 命中（缓存已被覆盖）
ui_action({verb:"select", index:0}) → index=0 引用的不是进程 pid=36504 的 ui_find 结果（当前缓存 pid=36504）。请先对该进程执行 ui_find。
```

**P7（干净路径不复现挂起）**
```
A：attach → disconnect 后：IsHung=False，WM_NULL 6/0/0 ms
B：attach → attach(失败) → disconnect 后：IsHung=False，WM_NULL 7/0/0/0 ms，CPU 3s 增量 0.00s
```
