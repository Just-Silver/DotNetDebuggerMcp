# DotNet-Debugger-MCP 总览设计（Overview Spec）

> **日期**：2026-09-05　**状态**：草稿（待用户 review）　**分支**：plan/dynamic-debugging-and-rename
> 本 spec 是 P1-P5 各实施计划的**唯一依据**（plan 从 spec 论证）。配套：`docs/planning/` 下 decisions D1-D8、research/01-05、01-vision-and-scope。

## 0. 一句话

把现 `ILSpyMcp`（反编译 MCP，MIT）重构/扩建为 **DotNet-Debugger-MCP**：一个 .NET tool（宿主 exe）按需提供「静态反编译分析」与「dnSpyEx 式动态调试」两类能力，面向 **agent（MCP 控制面）** 与 **Web 可视化（展示面）**，两者共享同一调试会话（进程合一）。

## 1. 目标与非目标

### 目标
1. 保留并改名现有全部反编译/静态分析能力（工具行为、输出格式、并发护栏不变）。
2. 新增动态调试引擎 v1 = **最小 agent 调试闭环**（decisions D5）。
3. 新增 MCP 调试工具面（P3），agent 经 stdio 可完整走通「定位→断点→运行→命中→观察→单步」。
4. 新增 WebUI（P4）实时渲染调试过程与 agent 决策轨迹（回放）。
5. 模块化：5 程序集，各层可独立开发/测试/替换。

### 非目标（v1/本期明确不做）
- 表达式求值（Roslyn 编译求值 / AST 子集）→ v2。
- PDB 行号断点、模块未加载延迟绑定 → v2。
- Edit & Continue（.NET 运行时不支持）。
- Mono/Unity 调试（GPL 栈）→ 不做或另议。
- 多进程多会话并发调试（v1 单会话）。
- EventPipe/ClrMD dump 旁路（v1 不引）。
- 大前端（选区内联编辑/源码编辑等 IDE 功能）——Web 以展示/回放为主。

## 2. 命名布局（decisions D6/D8，定稿）

| 对象 | 值 |
|---|---|
| GitHub 仓库 | `DotNet-Debugger-MCP`（rename，保留跳转） |
| 解决方案 | `DotNetDebuggerMcp.slnx`（根） |
| 子项目 1 反编译库 | `src/DotNetDebugger.Decompiler/` → `DotNetDebugger.Decompiler` |
| 子项目 2 调试引擎库 | `src/DotNetDebugger.Engine/` → `DotNetDebugger.Engine` |
| 子项目 3 会话库 | `src/DotNetDebugger.Session/` → `DotNetDebugger.Session` |
| 子项目 4 Web 库 | `src/DotNetDebugger.Web/` → `DotNetDebugger.Web` |
| 子项目 5 宿主 | `src/DotNetDebuggerMcp/` → `DotNetDebuggerMcp`（PackAsTool、net10.0） |
| NuGet 包 / CLI | `dotnet-debugger-mcp` |
| MCP server 注册名 | `dotnetdebugger`（工具前缀 `dotnetdebugger_*`）— 实施时定 |
| 测试 | `tests/DotNetDebugger.Decompiler.Tests/`、`tests/DotNetDebugger.Engine.Tests/`、`tests/DotNetDebugger.Session.Tests/`、`tests/DotNetDebuggerMcp.Tests/`（Web 测试随 P4） |

## 3. 项目依赖与分层规则

```
McpHost(exe) ──> Web ──> Session ──> Engine ──> ClrDebug/DbgShim(nuget)
     │              └─────> Session
     └───> Session ──> Decompiler（静态能力经宿主直接暴露，不经过 Session 的调试队列）
```
- **只许向下依赖**：Session 依赖 Engine + Decompiler；Web 依赖 Session + Decompiler；宿主依赖全部。禁止反向/循环。
- Decompiler 无内部依赖（保持现有独立性）。
- Engine 保持「纯底层」：不感知 MCP/Web/轨迹；只暴露调试动作 + 事件流。
- Session 是 MCP 与 Web 的**公共会话 API**（命令串行化 + 事件总线 + 快照 + 轨迹 + IL→行映射）。
- 宿主 exe 是唯一可执行/装配根：CLI 分发、MCP 注册、握手 ServerInstructions、拉起 Session 与（可选）Web。

## 4. 核心抽象（跨层协议，先行冻结）

> 以下类型/签名是 P2/P3/P4 的**契约**，实施计划按此展开；允许实施期微调但需回写本 spec。

### 4.1 Engine 层（`DotNetDebugger.Engine`）
- `DebugSession`：一个被调试目标的会话（v1 单活动会话）。
  - 会话生命周期：`Create/Launch(commandLine, options)`、`Attach(pid)`、`Disconnect()`、`Dispose()`。
  - 状态机：`None → Launching/Attaching → Running → Stopped(Break/Exception/Step/Exited) → Detaching/None`。
- 断点：`DebugBreakpoint`（按 `ModuleName + MethodToken + IlOffset` 定位）；`SetBreakpoint/RemoveBreakpoint/ClearBreakpoints`。
- 执行控制：`ContinueAsync()`、`StepIntoAsync/StepOverAsync/StepOutAsync`、`PauseAsync()`。
- 状态读取（停顿时有效）：`GetThreads()`、`GetStackFrames(threadId)`、`GetVariables(frameId)`（标量+简单对象首层字段）。
- 异常断点：`SetExceptionBreakpoints(filter)`（first-chance 类型过滤）。
- 事件：`DebugEvent`（见下）经**有界 Channel** 对外发布；命令全部**串行化**到专用调试线程执行（见 §6）。

### 4.2 DebugEvent 规范（P3/P4 共用的事件语言）
事件统一承载：`SessionId`、`Sequence`（单调）、`UtcTimestamp`、`Kind`、`Payload`。
- `SessionStateChanged`（Launching/Running/Stopped/Exited/Detached + reason）
- `BreakpointHit`（bpId + threadId + 栈顶 frame：module/methodToken/ilOffset）
- `StepCompleted`（threadId + 栈顶 frame）
- `ExceptionHit`（firstChance，类型名 + 栈顶 frame + 消息）
- `ThreadsChanged` / `StateSnapshot`（连接/重连时全量；见 §7 SSE）
- `AgentAction`（MCP 每步指令的轨迹记录：工具名+参数摘要+结果摘要+关联事件序列）→ Web 回放源
- `EngineLog`（engine 自身日志/错误）

统一字段值语义：
- **位置三元组** `FrameLocation = { moduleName, methodToken(0x060…), ilOffset }` —— 全局唯一标识一个执行点（v1 断点定位与事件都用它，天然接现有元数据层）。
- 行映射（`FrameLocation → 反编译行`）由 **Session** 负责解析后随事件附 `ResolvedLine`（docId+line 由 Web 层消费；MCP 层也可要文本行）。

### 4.3 Session 层（`DotNetDebugger.Session`）
- `DebugSessionService`：管理活动会话、命令队列（`PostCommand<T>`）、事件订阅、轨迹日志、状态快照缓存。
- `DocumentService`（复用 Decompiler）：反编译文档模型（docId → 行列表 + 行→IL 映射表），供断点定位与行解析。
- 命令执行策略：调试命令在调试线程串行排队；**MCP 并发请求**经队列串行化（沿用历史 stdio 防撕帧纪律的同类教训）。
- 轨迹：`AgentAction` 追加写只读日志（内存环形 v1），Web 时间线 = 日志只读播放器。

### 4.4 Web 层（`DotNetDebugger.Web`）——**提案，技术栈待确认（open-questions #4）**
> 以下为基于调研（research/04 组合 A）的**提案设计**，在技术栈确认前不视为已定架构；确认或改选后回写本段。
- Kestrel 内嵌：`/`（静态 SPA）、`GET /api/state`（快照）、`GET /api/docs/{id}`、`POST /api/control/{action}`、`GET /api/events`（SSE）。
- 事件模型：连接先拉快照（`StateSnapshot`），再 SSE 增量；`EventSource` 自动重连后重拉快照（幂等）。
- 详细面板协议/数据形状 → **P4 前单独细化 spec**（本总览只定边界）。

### 4.5 MCP 工具面（宿主层，P3）
工具命名空间前缀 `dotnetdebugger_`；建议工具集（P3 定稿）：
- `debug_session`（launch/attach/disconnect/status 子命令或独立工具，实施定）
- `debug_breakpoint` / `debug_continue` / `debug_pause`
- `debug_step`（into/over/out）
- `debug_stack` / `debug_threads` / `debug_variables`
- `debug_exceptions`（异常断点过滤）
- 静态类既有工具并入后以反编译/元数据语义保留（工具名可能改名，见 P1）。
- 全部调试工具带默认参数值（空串）、CancellationToken、中文提示；行为沿用 stdout/stderr 纪律。

## 5. 线程与并发模型（关键纪律）

- **调试线程**：Engine 自持专用调试线程（ICorDebug 回调串行化要求；COM STA/消息泵）。所有 ICorDebug 调用只在此线程发生。
- **命令队列**：外部（MCP/Web/CLI）发命令 → 队列 → 调试线程执行 → 事件回 Channel。
- **事件分发**：Engine 写有界 Channel → Session 订阅转轨迹/快照 → MCP（轮询/notifications）与 Web（SSE）各自消费。
- **进程停 = 冻结**：ICorDebug 同步停时托管线程全冻结；读栈/变量只在 Stopped 态允许；求值类（v2）必须知悉 GC safe point 限制。
- **每目标进程仅一个 ICorDebug 调试器**：与 VS/生产调试器互斥，文档明示。
- **stdout 纪律**：宿主 stdout 只走 MCP 协议；Web/调试日志全走 stderr/文件（P4 不占 stdio）。

## 6. 关键技术选型（research/01、04、05 结论）

| 用途 | 选型 | 许可 | 备注 |
|---|---|---|---|
| ICorDebug COM 封装 | ClrDebug | MIT | 1:1 镜像 API，锁版本 |
| dbgshim 引导 | Microsoft.Diagnostics.DbgShim（win-x64）或目标 runtime 目录 LoadLibraryEx | MIT | 规避 #90187 复制坑 |
| 引擎状态机 | 自研（clean-room 参考 dnSpy dndbg/Impl 协议） | MIT | P2 |
| 反编译/元数据 | ICSharpCode.Decompiler（现依赖）+ System.Reflection.Metadata | MIT | Decompiler 库已有 |
| 表达式求值 | v2：官方 Roslyn 语义 + AST 安全子集（暂定） | MIT | v1 不做 |
| Web 服务端 | ASP.NET Core + TypedResults.ServerSentEvents（.NET 10） | MIT | **P4 细化前待确认**（open-questions #4） |
| Web 前端 | Monaco + React/TS/Vite + mermaid(按需) | MIT | **P4 细化前待确认**（open-questions #4）；备选 CodeMirror 6 |
| IL→行映射 | SequencePointBuilder 思路（Decompiler 内，同设置产映射） | MIT | Session 负责（独立于前端选型，已定） |

## 7. P1-P5 阶段边界（实施计划拆分依据）

### P1 仓库改名与拆分（不动调试）
- git：仓库重命名（GitHub rename 保跳转）；分支策略。
- 解决方案：5 csproj 骨架；现 ILSpyMcp 源码迁入 Decompiler 库；命名空间/PackageId/CLI/注册名/README/CHANGELOG/CI/测试项目全量同步（AGENTS.md 的三处版本同步纪律延续）。
- **验收**：改名后仓库构建通过；全部现有反编译测试（含 stdio 并发护栏）通过；行为零变化。
- 旧 NuGet 包 `ilspymcp` 弃用策略（open-questions 残留）。

### P2 动态调试引擎 v1（Engine，无 MCP/Web）
- ClrDebug/DbgShim 引入 + **技术 spike 前置**（附加→token 断点→continue→命中→读栈→step 最小闭环，风险最高项先证）。
- 实现 §4.1 全部；DebugEvent 通道；CLI 驱动验证（宿主 `-dbg` 调试子命令或临时控制台，实施定）。
- 引擎单测：进程内起目标进程做真调试断言（参考现有 Client 模式）。

### P3 会话 + MCP 工具面（Session + 宿主）
- Session 服务实现（§4.3）；调试工具注册（§4.5）；与静态工具并存。
- 并发串行化 + 回归护栏（stdout 纯净）；端到端验证扩展。
- 文档/握手 ServerInstructions 更新。

### P4 WebUI（Web）
- 单独细化 spec（面板布局/事件协议/文档模型/回放数据形状）；实现 §4.4；`--web` 拉起。

### P5 打磨与发布
- 版本 1.5.0（沿用三处同步）；README/示例/CI/发布；CHANGELOG。

## 8. 风险与缓解

| 风险 | 缓解 |
|---|---|
| ICorDebug 通道在本机 net10 不可用/行为差异（最高） | P2 前置 spike，最快暴露 |
| 自研引擎工作量超估 | 严格 v1 范围；clean-room 参考 dnSpy；不做求值 |
| Release/JIT 优化目标断点漂移、locals 缺失 | 文档明示 + 建议 debug 配置；对新模块 SetJITCompilerFlags |
| stdio 并发撕帧回归（历史教训） | P3 回归护栏测试延续；调试命令队列串行化 |
| 许可合规（GPL/AGPL 参考） | 只读不链不抄；代码独立书写 |
| 反编译改名引发大规模重构错误 | P1 机械搬迁+自动化测试护栏，行为零变化验收 |
| Web 体积/加载（Monaco） | 本机 localhost 可接受；按需加载 mermaid |
| IL→行映射缺 sequence point | 服务端降级策略（research/04 §5） |

## 9. 参考
- `docs/planning/decisions.md`（D1-D8）、`01-vision-and-scope.md`、`research/01-05`、`open-questions.md`、仓库 `AGENTS.md`（历史纪律）。
