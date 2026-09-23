# 2026-09-24 握手 ServerInstructions 触发条件覆盖度审查报告

> **审查对象**：MCP 握手注入的 `ServerInstructions` 文本——`src/DotNetDebuggerMcp/Configuration/AppText.cs` 的 `HandshakeFeatureIntro`（`AppText.cs:38-48`），经 `DotNetDebuggerMcpCmd.BuildServerInstructions`（`DotNetDebuggerMcpCmd.cs:304-386`）注入；`EnvironmentChecker.BuildHandshakeText` 在其后追加「## 更新状态」段。
> **对照基线**：服务器实际暴露的 MCP 工具面（50 个工具，按语义分为 4 族）。
> **范围声明**：本报告**只陈述现状问题，不含修复方案 / 改动建议**。审查范围限于「握手是否让 agent 知道何时该用本服务器」，不含各工具内部行为。
> **状态**：**已修复（2026-09-24）**——握手正文、设计注释与回归断言按本报告 P0/P1/P2 全量修复，决策记录见 `docs/planning/decisions.md` D27，纪律见根 `AGENTS.md`「关键约束」；下文保留审查当时的现状描述。**审查当时未改动任何代码 / 文案**。
> **总体结论**：握手对「反编译」与「调试核心」覆盖够用；但**一个完整能力族（UI 自动化）与一个闭环工作流（场景复验）没有任何触发条件**，且握手自身的能力描述、设计注释、兜底发现机制、回归护栏均停留在"两类能力"的旧假设上。

---

## 0. 结论摘要

| 编号 | 严重度 | 问题 | 位置 |
|---|---|---|---|
| P0-1 | 高 | 握手简介写死"两类能力"，与实际 4 族工具面不符 | `AppText.cs:40` |
| P0-2 | 高 | UI 自动化族（`ui_*` 5 工具）在握手中**无任何触发条件** | `AppText.cs:41-46` / `Tools/Debugger/UiTools.cs` |
| P0-3 | 高 | `debug_verify`（场景化一键复验）在握手中**无任何触发条件** | `Tools/Debugger/DebugVerifyTool.cs` |
| P1-1 | 中 | 调试扩展工具（`debug_set`/`debug_evaluate`/`debug_object`/`debug_run_to`/`debug_timeline` 等）无触发条件 | `AppText.cs:43` |
| P1-2 | 中 | 握手设计说明与代码注释只承认"反编译 vs 动态调试"两类场景 | `AppText.cs:34-36`、`decisions.md:91` |
| P2-1 | 低 | 兜底发现机制（前缀清单）不完整，匹配不到 `ui_*` 及多个静态工具 | `AppText.cs:46` |
| P2-2 | 低 | 无回归护栏校验"能力族触发条件"是否齐全；现有测试反而固化"两类" | `DotNetDebuggerMcpCmdTests.cs:117-125` |

---

## 1. 背景事实（现状）

1. **握手是唯一可靠的"何时用"通道**。`CHANGELOG.md:147` 原文记载：opencode 2 等客户端握手期**只注入 `ServerInstructions` 且工具目录仅部分常驻**——正因如此才引入握手简介（"此前 agent 对服务器能力与触发时机一无所知"）。工具清单被截断时，agent 只能靠握手判断触发时机。

2. **握手曾用「工具一览」全量列工具，后被移除**。`CHANGELOG.md:147` 记载早期「## 工具一览」列全部 16 个工具，并要求"新增工具必须同步 `AppText.HandshakeFeatureIntro` 的工具一览"；`decisions.md:91`（D12，2026-09-05）拍板**改为触发条件导向——去掉「工具一览」，只留「## 何时使用」（反编译/调试两类场景触发条件），agent 经 MCP 工具目录发现工具**。P0/P1 类缺口正是该移除动作留下的覆盖风险。

3. **当前握手「何时使用」仅 4 条 bullet**（`AppText.cs:41-46`），对应触发：静态分析、动态调试、`web_open`、`screenshot`。

4. **实际工具面为 50 个工具 / 4 族**（宿主 `Tools/` 目录实测）：
   | 族 | 数量 | 代表工具 |
   |---|---|---|
   | 静态分析（反编译/类型/关系/字符串） | 17 | `decompile` `decompile_member` `signature` `list_types` `call_graph` `call_chain` `hierarchy` `interface_usage` `dependencies` `field_access` `generic_instantiations` `search_string` `assembly_info` `cache_stats` … |
   | 动态调试 | 26 | `debug_launch` `debug_attach` `debug_breakpoint_*` `debug_continue` `debug_step` `debug_run_to` `debug_wait` `debug_state` `debug_stack` `debug_variables` `debug_evaluate` `debug_object` `debug_set` `debug_exceptions` `debug_output` `debug_processes` `debug_modules` `debug_threads` `debug_timeline` `debug_verify` `debug_terminate` `debug_disconnect` … |
   | UI 自动化 | 5 | `ui_find` `ui_get` `ui_action` `ui_input` `ui_wait`（`Tools/Debugger/UiTools.cs`） |
   | 视觉 / 监视 | 2 | `screenshot`（`Tools/Debugger/DebugScreenshotTool.cs`）、`web_open`（`Tools/Web/WebOpenTool.cs`） |

5. **握手的四条触发与工具的对应关系**：
   | 握手 bullet | 位置 | 覆盖的工具族 |
   |---|---|---|
   | 静态分析 | `AppText.cs:42` | 静态分析族 ✅ |
   | 动态调试核心 | `AppText.cs:43` | 调试族的 launch/attach/断点/单步/栈/变量 ✅ |
   | `web_open` | `AppText.cs:44` | 视觉/监视（1/2）✅ |
   | `screenshot` | `AppText.cs:45` | 视觉/监视（2/2）✅ |
   | —— | —— | **UI 自动化族 ❌、`debug_verify` ❌、调试扩展工具 ❌** |

---

## 2. 问题详述

### P0-1 握手简介写死"两类能力"，与实际 4 族工具面不符（高）

- **位置**：`AppText.cs:40`。
- **现状**：原文为"本服务器是 .NET 程序集分析工具，提供**两类能力**：反编译/静态分析 …… 与动态调试 ……"，且第一句把服务器定位为"**.NET 程序集分析工具**"。
- **问题**：工具面已扩含 UI 自动化（5 工具）与视觉/监视（2 工具）。定位句与"两类能力"的表述在**简介层就排除了 UI 自动化与截图/监视**——UI 自动化与"程序集分析"在语义上本无关联，agent 从简介句不会把本服务器与"驱动 GUI 应用"联系起来。
- **后果（现状）**：即使 agent 认真读了握手，也不会认为"UI 交互"属于本服务器职责范围。

### P0-2 UI 自动化族（`ui_*` 5 工具）在握手中无任何触发条件（高）

- **位置**：`AppText.cs:41-46`（缺）对 `Tools/Debugger/UiTools.cs`（存在）。
- **现状**：握手中唯一涉及 GUI 的表述是 `AppText.cs:45`"当需要**观察** GUI 窗口/屏幕画面（看控件状态、布局冒烟）时，调用 screenshot 截图"——**只覆盖"观察"，不覆盖"交互"**。而 `ui_*` 五工具提供的是：列控件树（`ui_find`）、读控件状态（`ui_get`）、执行语义动作 invoke/toggle/select/expand/collapse/focus/scroll（`ui_action`）、写输入值（`ui_input`）、等待界面变化（`ui_wait`）；并明确"全 UIA 语义，不移动光标、不注入输入、不抢前台"。
- **后果（现状）**：仅凭握手，agent 遇到"点那个按钮 / 勾选复选框 / 把下拉切到手动 / 往输入框填值"这类任务时，**不知道存在 UIA 语义动作工具**，只会退化为"截图 + 猜坐标"或干脆放弃——而这恰是 `ui_*` 相对物理输入的优势场景。
- **补充**：该族在握手兜底前缀句（`AppText.cs:46`）里同样不可发现（见 P2-1）。

### P0-3 `debug_verify`（场景化一键复验）在握手中无任何触发条件（高）

- **位置**：`Tools/Debugger/DebugVerifyTool.cs`（存在）；`AppText.cs:41-46`（缺）。
- **现状**：`debug_verify` 读场景 JSON（target 启动快照 + 可选 build 重编译 + steps 断言序列）执行到 PASS/FAIL，断言失败即停（fail-fast）。工具自述用途为"**改完 bug 后用它自证修复**"——是"实现 → 进程级复验"的闭环入口。
- **后果（现状）**：握手对调试场景只描述"下断点 → 运行至命中 → 观察变量 → 单步"这一**交互式**路径，agent 修完 bug 后不会想起有"一键复验"可用，只能退回逐断点人工观察或跑与调试无关的 `dotnet test`。

### P1-1 调试扩展工具无触发条件（中）

- **位置**：`AppText.cs:43`。
- **现状**：握手调试 bullet 覆盖 launch/attach/断点/单步/栈/变量；**未提及**以下具独立触发语义的工具：
  - `debug_set`：**改写现场值**（局部/字段/数组元素，支持引用重定向）后继续——用于"强制走某分支 / 置空 / 换引用来验证假设"；
  - `debug_run_to`：运行到光标处（设一次性临时断点并继续）；
  - `debug_evaluate` / `debug_object`：表达式纯读求值 / 对象结构下钻；
  - `debug_timeline`：日志 + 调试事件 + agent 动作统一时间线复盘；
  - `debug_output` / `debug_processes` / `debug_modules` / `debug_threads`：支撑性（可不在握手）。
- **问题**：其中 `debug_set`（改写现场验证假设）与 `debug_run_to`（直奔目标方法，省去反复设断点）具有与既有 bullet 不同的**触发场景**，现状握手无法唤起。

### P1-2 握手设计说明与代码注释只承认"两类场景"（中）

- **位置**：`AppText.cs:34-36`、`decisions.md:91`。
- **现状**：`AppText.cs:34-36` 的 XML 注释写明该常量"只写「何时使用本服务器」的触发条件（**反编译 vs 动态调试场景**）"；`decisions.md:91`（D12）同样表述为"反编译/调试两类场景触发条件"。
- **问题**：工具面扩到 4 族后，这条**设计约束/说明未更新**，形成"文档自证只有两类"的闭环——后续维护者按此注释行事，会继续把 UI 自动化、复验等排除在握手之外。

### P2-1 兜底发现机制（前缀清单）不完整（低）

- **位置**：`AppText.cs:46`。
- **现状**：握手末句"具体工具清单见 MCP 工具目录（名称带 `decompile`/`debug`/`web` 等语义前缀）"是在工具目录被截断时的**兜底发现手段**。
- **问题**：该前缀清单**匹配不到 `ui_`**；也匹配不到 `signature` / `list_types` / `call_graph` / `call_chain` / `hierarchy` / `interface_usage` / `dependencies` / `field_access` / `generic_instantiations` / `search_string` / `assembly_info` / `screenshot`。以"前缀"推断能力时，agent 会漏掉这些工具及整个 UI 族。

### P2-2 无回归护栏校验"能力族触发条件"是否齐全（低）

- **位置**：`tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcpCmdTests.cs:117-125`。
- **现状**：唯一相关测试 `HandshakeFeatureIntro_含反编译与调试触发条件` 只断言文本含"反编译""动态调试""## 何时使用"，并**显式断言 `DoesNotContain("## 工具一览")`**。
- **问题**：该测试把"两类"固化为期望行为；新增能力族（UI 自动化 / `debug_verify` / `screenshot` 之后的新工具）**即使漏进握手，测试也不会失败**。现状：握手覆盖度无可执行校验，纯靠人工自觉（`CHANGELOG.md:147` 曾靠"新增工具必须同步工具一览"的纪律，该纪律随「工具一览」移除而失效）。

---

## 3. 相关已知项（避免重复立项，均不含本报告结论）

- `docs/ROADMAP.md:26`——web 引导措辞易诱导 agent 误以为需人类参与（**措辞**问题）。
- `docs/ROADMAP.md:30`——握手增加"动态调试操作序列 + 结束自检"（**流程**问题）。

上述两项针对**既有 bullet 的措辞/顺序**，均不覆盖本报告指出的"**整个能力族 / 工作流无触发条件**"。

---

## 4. 证据清单

| 事实 | 来源 |
|---|---|
| 握手文本本体（3 块标题 / 4 条 bullet） | `src/DotNetDebuggerMcp/Configuration/AppText.cs:38-48` |
| 握手设计注释（"只写……反编译 vs 动态调试场景"） | `AppText.cs:34-36` |
| 握手组装与注入点 | `DotNetDebuggerMcpCmd.cs:304-386`；`AppText.cs` 经 `BuildServerInstructions` 注入 `ServerInstructions` |
| 「工具一览」曾存在后被移除 + 客户端工具目录仅部分常驻 | `CHANGELOG.md:147`；`docs/planning/decisions.md:91`（D12） |
| UI 自动化工具已实现 | `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs` |
| `debug_verify` 已实现 | `src/DotNetDebuggerMcp/Tools/Debugger/DebugVerifyTool.cs` |
| 其余调试扩展工具已实现 | `Tools/Debugger/` 下 `DebugSetTool.cs` `DebugObjectTool.cs` `DebugRunToTool.cs` `DebugTimelineTool.cs` `DebugEvaluateTool.cs` `DebugScreenshotTool.cs` |
| 握手覆盖度相关测试 | `tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcpCmdTests.cs:117-125` |
| 实际暴露工具面（50 工具 4 族） | MCP 工具目录（`search` 枚举 `DotNetDebugger` 命名空间，50 项） |
