# DotNetDebugger.Engine 开发指南

进程内 .NET 动态调试**引擎**（`DotNetDebugger.Engine`，v1）：ICorDebug 通道（ClrDebug + dbgshim），支持启动/附加目标进程、按 模块名+方法 token+IL offset 下断点、continue、单步、读线程/调用栈/局部变量（标量）、first-chance 异常断点，统一对外暴露 `DebugEvent` 事件流。设计来源见 `docs/planning/specs/2026-09-05-overview-design.md` §4-5 与 `docs/planning/research/06-clrdebug-api-reference.md` §7（文件头注释均引 spec/research 段落）。

## 边界纪律

- net10.0，**只引 NuGet**：`ClrDebug 0.4.2` + `Microsoft.Diagnostics.DbgShim.win-x64 10.0.731102`。无 ProjectReference、无 MCP/DI/日志/Decompiler 依赖。
- **dbgshim 必引 RID 子包 `win-x64`**：主包 `Microsoft.Diagnostics.DbgShim` 是空壳内部元包（官方注明 not meant for direct consumption，勿直引）；子包只负责把 `runtimes/win-x64/native/dbgshim.dll` 带到输出目录，ClrDebug 的 `DbgShim` 类封装其调用。
- 引擎是纯能力层：**不反编译、不解析类型名**（栈帧/断点定位全用 token），供 Session 库 / 宿主 / Web 在之上叠加。不得反向引用它们。

## 结构（命名空间即目录）

```
Engine/    DebugEngineCore / CorDebugBootstrap / DbgShimLoader /
           CallbackHandler / DebugCommandQueue / BreakpointManager /
           SymbolNameResolver
Session/   DebugSession(根命名空间 DotNetDebugger.Engine!) / DebugBreakpoint / ExceptionBreakpointFilter
Stepping/  StepperManager
Models/    DebugEvent / DebugSessionState / DebugStackFrame / DebugThreadInfo /
           DebugValue / DebugVariable / FrameLocation / BreakpointSnapshot   （纯数据 record）
```

- `Session/DebugSession.cs` — **对外门面**（v1 单活动会话，spec §4.1）：静态工厂 `LaunchAsync(commandLine, timeoutMs)` / `AttachAsync(processId)`；`Events` 暴露 `IAsyncEnumerable<DebugEvent>`（无界 Channel，attach 后立刻订阅也能追到缓冲历史）；`SetBreakpointAsync(moduleName, token, ilOffset)` / `GetBreakpointsAsync` / `GetModulePathAsync(moduleName)`（模块短名→全路径，停点无条件跟随用）/ `ContinueAsync` / `StepInto/Over/OutAsync` / `GetThreads/StackFrames/VariablesAsync` / 异常断点 / `DisconnectAsync`。
- 注意 `Session/` 目录三个文件声明在**根命名空间** `DotNetDebugger.Engine`（不是 `.Session`），勿按目录臆测。

## 线程模型（核心纪律，勿破坏）

- **全部 ICorDebug 调用在一条专用 MTA 后台线程**（"DebugEngineMTA"，`SetApartmentState(MTA)`——ClrDebug 硬性要求）。引导成功后同一线程转命令泵（`RunCommandPump`）。
- **回调线程只入队，绝不在回调线程调 Continue**（与命令并发会导致 `CORDBG_E_SUPERFLOUS_CONTINUE`/卡死）。`CallbackHandler` 把原始事件写入 `_eventChannel`。
- 命令泵单线程串行：先排空事件（停点事件停住进程并发布、其余 Continue）→ 取一条命令同步执行 → 空闲 `Sleep(5)`。即**事件处理与命令执行同线程，避免并发 Continue**。
- 对外命令经 `PostAsync`/`PostAsyncResult` 投递等 TCS（带 CancellationToken + WaitAsync 超时）；事件发布双写（无界 Channel 可回放 + sink 同步回调），`AttachEventSink` 在 sink 建立后回放缓冲事件。
- `_stoppedThreadId`（volatile）记录最近停住线程，单步/读栈取它。

## 关键坑知识

- **Continue 语义**：ICorDebug stop-counter 每次回调 +1、每次 Continue -1、且每次 Continue 只派发一个排队回调，故必须**循环 Continue 直到 `IsRunning=true`**（上限 100 防死循环）；容忍 `CORDBG_E_SUPERFLOUS_CONTINUE`（微软拼写少一 U）。
- **断点匹配不能按 RuntimeBreakpoint 引用比较**：ClrDebug 每次事件都新建 wrapper 实例，命中事件是新 wrapper，须按「模块 + 函数 token + IL offset」内容匹配（`BreakpointManager.Match`/`MatchContent`；模块名文件名级忽略大小写）。
- **pending 断点（模块未加载）**：`Add` 遇未登记模块不再抛错，登记为未绑定断点（`IsBound=false`），`TrackModule`（LoadModule 事件 / attach 枚举）时自动 `Bind` 重绑并回传重绑数（CallbackHandler 据此补发 BreakpointsChanged）；重绑失败（token 无效/无 IL）保持未绑定不阻塞进程。全部调用在 MTA 单线程，无并发。模块已加载时 token 错/无 IL 仍同步抛错（agent 立即拿到原因）；`CreateBreakpoint` 后**必须 `Activate(true)` 才生效**。
- **停点变量名解析（`SymbolNameResolver`）**：参数名来自 DLL 元数据 Param 表（无需 PDB），局部名来自**模块旁 portable PDB** 的 LocalScopes（`mr.GetLocalScopes(MethodDefinitionHandle)`——注意 API 在 Reader 上而非 MethodDebugInformation 上，槽位属性名是 `Index` 不是 Slot）。测试目标需 `generate-testdata.ps1` 拷出 `DebugTarget.pdb` 才有名；按 (模块路径, token) 缓存，解析失败静默回退 slot 展示。
- **对象/数组展开（`ReadValue(value, expand)`）**：顶层变量 expand=true 展开一级 children——对象按模块元数据字段清单（静态字段跳过）+ `GetFieldValue(cls.Raw, mdFieldDef)` 取值；数组按 `GetElementAtPosition` 线性取前 `MaxChildren`(32) 条；children 内不再递归（天然防环），引用浅读仅解字符串内容。字段 token 需模块 DLL 元数据（`CorDebugClass.Token`/`Module.Name`）。
- **单步坑（已修，勿回退）**：线程级裸 `thread.CreateStepper().Step()` 会**立即完成且 IP 不动**（StepCompleted 原地 +0x0，表象是「单步无效果」）——必须**帧级 `ilf.CreateStepper()`** + `SetInterceptMask(INTERCEPT_ALL & ~(SECURITY|CLASS_INIT))` + `SetUnmappedStopMask(STOP_NONE)` + **`StepRange`（当前语句 IL 区间 [序列点, 下一序列点)，`SymbolNameResolver.GetStatementIlRange`，PDB 提供）**。**无 PDB 回退也不能用裸 Step**（无序列点同样原地完成）——用 `StepRange([ip, ip+1))` 单条 IL 指令步进（dnSpy 无符号时同款）。循环回边停回循环头 offset 变小是合法的（`StepTests` 断言：首步离开入口、后续不原地）。Engine 测试套件在有/无 `DebugTarget.pdb` 两种状态下都应全绿（局部名断言仅在 PDB 在位时生效）。
- **launch 时序**：Engine launch 停在初始同步点但目标模块未必加载、直接 `LaunchAsync` 下断点会登记为 pending（重绑机制已实现）——Session 库的 `LaunchAndAttachAsync` 与宿主 `-dbg`/`debug_launch` 实际都走「先起进程等稳定区再 Attach」路径，不直接用 Engine `LaunchAsync`。新代码留意这个分工。
- `StepperManager`（Stepping/）目前是**静态薄封装**，真正命令路径在 DebugEngineCore 直接 `thread.CreateStepper()`，未走它——改单步逻辑先确认实际执行路径。
- 异常断点 v1 = 设了过滤器即停全部 first-chance；`ExceptionBreakpointFilter.Matches` 类型精确过滤列 v2（当前实际未用）。

## 验证（tests/DotNetDebugger.Engine.Tests）

```bash
powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1   # 先决：生成 DebugTarget.exe/dll（git 忽略）
dotnet test --project tests/DotNetDebugger.Engine.Tests/DotNetDebugger.Engine.Tests.csproj
```

- **真实 attach 子进程**（DebugTarget.exe，自带 delay 参数提供 attach 窗口），非 mock。**必须串行**：`AssemblyInfo.cs` 已 `[assembly: Xunit.v3.Parallelization(ParallelMode.None)]`——并行 attach 多个目标会使 ICorDebug/dbgshim 会话相互干扰（实测集体超时）。勿删该特性。
- 覆盖：attach→退出事件流、断点（token 用 BCL System.Reflection.Metadata 读 DebugTarget.dll，Engine 测试不引 Decompiler）、单步 ×3、栈/线程/变量读取、first-chance 异常断点（throw 模式）。每测试含真实进程 attach + 秒级 delay，**相对慢，不宜频繁全量跑**。
- 测试自身起目标进程必须**排空 stdout/stderr**（`DebugTargetProcess.cs` 已做，否则子进程写满管道会卡死——与宿主日志纪律同源教训）。
- 改 DebugTarget 需重跑脚本；token 随源码变化，测试断言会漂移——不 rename/remove 既有类型。
