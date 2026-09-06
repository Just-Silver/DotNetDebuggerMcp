# DotNetDebugger.Session 开发指南

动态调试的**会话/状态层**（`DotNetDebugger.Session`）：在 Engine `DebugSession` 之上叠加单活动会话管理、停点事件缓冲快照、agent 轨迹环形日志，是宿主 `debug_*` MCP 工具与 Web 调试页面的**共享会话中枢**。

## 边界纪律

- net10.0，ProjectReference 仅 **Engine + Decompiler**（Decompiler 目前实际未用，仅保留依赖面）。无第三方包、无宿主反向依赖。
- 本层是「会话语义」而非「引擎细节」：不直接碰 ICorDebug/ClrDebug，一切经 Engine `DebugSession`。Engine 的模型类型（`DebugSessionState` 等）会被宿主工具直接引用作输出格式来源——改 Engine 枚举时留意宿主 `Tools/Debugger/`。
- 宿主与 Web 共享**同一个** `DebugSessionManager` 单例：宿主侧包装在 `DotNetDebuggerMcp.Services.DebugSessionService.Manager`，经 `WebHostBootstrap.Configure` 注入 Web。**勿在各处 new 独立 Manager**。

## 结构（文件即职责，均在根命名空间 `DotNetDebugger.Session`）

- `DebugSessionManager.cs` — **v1 单活动会话管理器**：
  - `Active`（lock 读）/ `Actions`（agent 轨迹日志，跨会话累积）/ `GetInfo()` → `DebugSessionInfo`
  - `LaunchAndAttachAsync(commandLine, timeoutSeconds=30)`：**绕开 Engine launch 路径**（dbgshim CreateProcessForLaunch 不支持输出重定向，会污染 MCP stdio）——先自起进程（CreateNoWindow + 重定向捕获到 ProcessOutputCapture），再 `RegisterForRuntimeStartup` 蹲守 CLR 启动（P9，回调时机=Main 前），到达即 `AttachAsync`——目标无需自带启动延迟。Engine launch 停在初始同步点但模块未加载——这是 `debug_launch` 与 `-dbg` 都走 attach 路径的原因。
  - `Activate` 替换旧会话时不阻塞：旧会话后台 `Task.Run` 断开+释放。即换会话自动后台回收旧会话。
- `SessionEventBuffer.cs` — 后台任务消费 `DebugSession.Events`，折叠成**最新状态快照** `CurrentState` + `LastStop`（`StopContext`），线程安全。**设计目的：让 debug_state/debug_stack 等查询「不等停点、立即返回」**。只认 SessionStateChanged/BreakpointHit/StepCompleted/ExceptionHit 四类事件。
- `Evaluation/` — **P6 表达式读值子集**：`ExpressionParser`（自研递归下降，不引 Roslyn——文法=规格，能解析即已支持；段数上限 `PathSegment.MaxSegments`）+ `ExpressionEvaluator`（AST 求值调度：PathNode 经 Engine `EvaluatePathAsync` 逐段直读；`!`/比较在本层标量化——数值统一 decimal、字符串 Ordinal、布尔与 null 仅 ==/!=）。`ExpressionEvaluationException.Message` 即面向 agent 的中文提示（宿主直接展示）。**P7 条件断点复用此组件**（求值失败/`ScalarValue is not bool` = 不命中）。
- `AgentActionLog.cs` — agent 轨迹环形日志（MaxEntries=1000，超限逐最旧），`Log`/`Snapshot`/`Clear`；P4 Web 回放源。**数据由宿主工具层喂**（每个 `debug_*` 工具成功/失败都写一条）。
- `Models/SessionModels.cs` — `StopContext`（断点=「breakpoint {id}」、step=step reason、异常=异常类型）+ `DebugSessionInfo`。

## 验证（tests/DotNetDebugger.Session.Tests）

```bash
dotnet test --project tests/DotNetDebugger.Session.Tests/DotNetDebugger.Session.Tests.csproj
```

- 同样真实 attach DebugTarget 子进程（TestTarget 包装，排空 stdout/stderr），**必须串行**（`AssemblyInfo.cs` 已 `Parallelization(ParallelMode.None)`，ICorDebug 会话相互干扰）。DebugTarget 由 `tests/TestData/generate-testdata.ps1` 生成且 git 忽略——先跑脚本再测。
- `AgentActionLogTests` 纯内存无进程，可快速跑；其余含真实会话。
- 改 `LaunchAndAttachAsync` 的固定 1s 等待/attach 逻辑会影响宿主 `debug_launch` 端到端（`DebugMcpToolsTests`）——同步验证。
