# 调研 · 动态调试依赖库技术选型

> 主题：在 net10.0、Windows 优先、MIT 许可底线前提下，实现 dnSpyEx 式**活动调试**（启动/附加、断点、单步、locals/栈/线程、C# 表达式求值、异常断点）的技术路线。
> 调研日期：2026-09-05。来源：dnSpyEx / dotnet-diagnostics / ClrDebug / debug-mcp 官方文档与源码结构、DeepWiki 等（URL 见文末）。

## TL;DR 一句话结论

.NET Core/.NET 5+ 上所有「活动调试」（断点/步进/求值）能力**都落在同一条底层通道：ICorDebug COM 接口（经 DbgShim + mscordbi.dll）**。问题不是「选哪条调试路线」，而是「选哪个 ICorDebug 封装层 + 自己写多少」。

**推荐组合**：`ClrDebug (MIT)` + `Microsoft.Diagnostics.DbgShim (MIT)` + 自研 ICorDebug 事件循环/断点/栈帧/值树 + 复用现有 PEReader 元数据层（token/IL 定位）做断点；表达式求值 v1 走 Roslyn 静态分析 + AST 安全求值子集。dnSpyEx / debug-mcp 仅 clean-room 参考（GPL/AGPL 不可链不可抄）。

## 1. 路线一：dnSpyEx 调试器栈（GPL-3.0，否决直引）

### 结构
| 层 | 位置 | 定位 |
|---|---|---|
| `dndbg` | `Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet.CorDebug/dndbg/`（95 cs，源码零 WPF/零 MEF） | ICorDebug COM 手写封装 + CoreCLRHelper（dbgshim 引导） |
| `dnSpy.Contracts.Debugger` | `dnSpy/dnSpy.Contracts.Debugger*/` | 公共调试对象模型（DbgManager/DbgProcess/DbgThread/DbgStackFrame/DbgBoundBreakpoint/DbgModule/DbgEngine…） |
| `dnSpy.Debugger.DotNet.CorDebug` | `Extensions/.../dnSpy.Debugger.DotNet.CorDebug/`（Impl/ 59 cs，0 UI using） | DbgEngineImpl：事件翻译 + 断点/步进/locals/**Roslyn 表达式求值**/异常 |

### 能力（全绿，最强参考）
启动早期断点（CREATE_SUSPENDED + GetStartupNotificationEvent）、附加/分离、token/IL/源码行断点、step into/over/out、locals/参数/字段（VariableHome 寄存器定位）、栈/线程、Roslyn→IL 解释器 + ICorDebugEval 双模求值、first/second-chance 异常断点；运行时覆盖 .NET Framework 4.x + .NET Core 2.1+（.NET 5–10）。EnC 不支持（运行时限制，所有路线皆无）。

### 否决理由（可复用性）
1. **许可传染（否决性）**：全仓 GPL-3.0。作为 NuGet 库分发并链接 = 全项目被迫 GPL。现 ILSpyMcp 走 MIT（ICSharpCode.Decompiler），产品性质改变，不可接受。
2. 无 NuGet，需源码构建；依赖自维护 Roslyn fork（Roslyn.ExpressionCompiler，子模块）+ dnSpy.Metadata.Dmd + dnSpy.Contracts.* 全家。
3. DbgManager 装配需大量服务 + 调试线程/dispatcher，headless 化文档缺失、成本高。
4. csproj 链 UseWPF / WindowsDesktop SDK / Microsoft.VisualStudio.Text.UI.Wpf（见 research/02）。

**合规变通**：读源码当「协议说明/API 用法参考」（clean-room，书面隔离），MIT 底座自研。

## 2. 路线二：ICorDebug COM + DbgShim 托管封装（ClrDebug / debug-mcp）★推荐主通道

### ClrDebug（lordmilko，MIT，NuGet 0.4.x 活跃）
- ICorDebug*/IMetaData*/ISym*/DAC 等 .NET 非托管诊断 API 的**全量 1:1 托管封装**（COM 薄封装，每个方法都有）；`CorDebugManagedCallback` 事件、Process/Thread/Frame/Function/Code/Value/Eval/Stepper/Breakpoint 全有包装。
- **不给**高层能力：断点管理器、源码↔IL 映射、求值编译器、状态机 → 自研。
- 官方 `Samples/NetCore` 有 attach/断点/单步最小骨架。

### debug-mcp（jkolo，AGPL-3.0，可行性样本）
- **MCP 服务器 + ClrDebug + Microsoft.Diagnostics.DbgShim.win-x64 + System.Reflection.Metadata(PDB)**，34 个调试 MCP 工具：launch/attach/disconnect/state、continue/pause/step、断点 CRUD/wait、异常断点、threads/stacktrace/variables/evaluate/object_inspect/memory/layout、模块/类型、进程 stdin/stdout 转发；MCP push 通知（breakpointHit）、`evaluate_safe`（AST 副作用护栏）、server_instruction 会话引导。
- 一句话：约「4000 行 ProcessDebugger + 800 行 ExpressionEvaluator + 断点/源映射」≈ 单人可维护规模 → **agent 主导 ICorDebug MCP 调试器可行性实证**。但 AGPL + Linux 优先（Windows 成熟度未经验证）→ 只借鉴产品设计不抄码。

### 用裸 ICorDebug 实现各项功能的工作量与要点
| 功能 | ICorDebug 对应 | 工作量/要点 |
|---|---|---|
| 启动 | `CreateProcess` / dbgshim 启动序列（CREATE_SUSPENDED+GetStartupNotificationEvent+EnumerateCLRs+CreateDebuggingInterfaceFromVersionEx） | 中（复刻 dnSpy CoreCLRHelper 引导协议） |
| 附加 | `DebugActiveProcess(pid, win32Attach:false)` 软附加 | 小 |
| 断点（token） | `module.GetFunctionFromToken(token)`→`func.GetILCode()`→`code.CreateBreakpoint(ilOffset)` | **小；token 断点与现有元数据层天然契合** |
| 断点（源码行） | 自读 Portable PDB（源文件/行 ↔ token+IL offset） | 中（System.Reflection.Metadata） |
| 挂起断点 | ModuleLoad 回调重绑 | 中 |
| step | `thread.CreateStepper()` + Step/StepOut + 拦截掩码 | 小-中 |
| locals/参数 | ILFrame.EnumerateArguments/LocalVariables + `ICorDebugCode4.EnumerateVariableHomes` + Value 值树 | 中-大（纯代码活） |
| 栈/线程 | ICorDebugStackWalk / Chain / Thread 枚举 | 中 |
| C# 表达式求值 | ICorDebugEval2（CallParameterizedFunction/NewString/…） | **大**（见 §4） |
| 异常断点 | ManagedCallback2.Exception/Exception2 + 类型过滤 | 小-中 |

**平台约束（写进架构）**：ICorDebug 回调串行化、需专用调试线程（COM STA/消息线程）；进程停 = synchronized，全部托管线程冻结；**每进程只能一个 ICorDebug 调试器**（与 VS/生产内置互斥）；x86 调试器进程↔x86 目标位数匹配；纯托管调试（无 mixed-mode）。

### 工作量估计
- 骨架（启动/附加/continue/token 断点/step into）：约 1–2 周/单人。
- 到 debug-mcp 完整度（行断点+PDB、locals 对象树、栈线程、异常断点、简化求值、MCP 封装）：约 2–4 人月。
- 全功能 C# 求值另加（见 §4）。

## 3. 路线三：dotnet/diagnostics 系列（MIT，只读，不作活动调试主通道）

| 包 | 能力 | 活动调试？ |
|---|---|---|
| `Microsoft.Diagnostics.NETCore.Client` | diagnostic port IPC：EventPipe 实时事件、写 dump、Attach/Startup Profiler、环境变量读写、ResumeRuntime | **否**（无断点/步进/求值）——做启动早期配置与事件监控旁路 |
| `Microsoft.Diagnostics.Runtime`（ClrMD） | DAC 读托管堆/对象/栈/类型；dump 与 live attach（需挂起或快照） | **否**（官方 FAQ：不是 debugging api，无断点/step）——做 dump 事后分析旁路 |
| `Microsoft.Diagnostics.DebugServices(+Implementation)` | dotnet-dump/dotnet-debug 分析宿主抽象（IHost/ITarget/IMemory/IModule/IThread/IRuntime/SOSHost） | 只读分析（clrstack/pe/threads…），无执行控制与求值；live attach 走 dbgshim→ICorDebug→data reader，证明纯托管 host 可行性 |
| `Microsoft.Diagnostics.DbgShim`（+ RID 子包 win-x64 等） | 原生 dbgshim 引导 DLL 的 NuGet 分发 | 是路线二的装载件；**坑**：PlatformManifest 冲突致 dll 不复制（dotnet/runtime#90187），需 MSBuild 手动 Copy 或运行时从目标 runtime 目录 LoadLibraryEx |

## 4. 专项：C# 表达式求值

- **无独立开箱即用的调试器 C# 表达式求值 NuGet**。VS/dnSpy 的 `Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator` 是私有 fork，非官方 NuGet 分发。
- dnSpyEx：Roslyn 编译 → IL → 调试器进程内自研 IL 解释器（纯计算本地算；读 locals/字段/方法调用转 ICorDebug 值操作/Eval）。支持 lambda/LINQ，复杂度数千行。
- debug-mcp：Roslyn 仅语法+语义分析 → AST 求值器；能力上限：算术/比较/位/三元/强转/locals/属性链/索引器/基本方法调用/字符串插值；**不支持 lambda/LINQ**。`evaluate_safe` AST 副作用静态护栏（agent 必配）。
- **ICorDebugEval 硬约束**（dotnet/runtime#72190）：函数求值要求目标线程在 GC safe point；进程停于 P/Invoke 等位置得 `CORDBG_E_ILLEGAL_AT_GC_UNSAFE_POINT`。策略：优先非侵入求值（不执行代码）→ 需执行才 func eval → 失败降级提示。
- **建议 v1 路线**：照 debug-mcp AST 求值子集（600–1500 行 + 值树渲染）；Roslyn 全量编译求值列远期（3000+ 行），接口保持可替换。

## 5. 被调试进程侧（诊断配置/环境变量）
- 普通附加/启动调试**无需配置**。阻断项：`DOTNET_EnableDiagnostics=0`（.NET 8+ 连关四通道）或 `DOTNET_EnableDiagnostics_Debugger=0` → 无法 attach。
- 早期启动调试（托管代码第一行前断下）：dbgshim 启动序列即可，不需要环境变量。
- diagnostic port 通道：`DOTNET_DefaultDiagnosticPortSuspend=1` / `DOTNET_DiagnosticPorts=`，只能 EventPipe/profiler/env/startup hook，**不能下 ICorDebug 断点**。
- Release/JIT 优化目标：行级断点漂移、locals 丢失普遍限制；对新加载模块 `SetJITCompilerFlags` 可保后续模块质量。

## 6. 备选对照
- **Samsung/netcoredbg**（MIT，C++/C#）：完整 ICorDebug 活动调试器，GDB/MI + DAP + CLI，仅 CoreCLR；不是库，可作子进程被 MCP/DAP 驱动（进程内需求若松动是低工作量替代）。
- **sharpdbg**（MattParkerDev，MIT，纯 C#，基于 ClrDebug，net10.0）：DAP 实现，含 ExpressionEvaluator——「ClrDebug 之上长出完整调试器」的 MIT 活证据，最贴近的 MIT 参考。

## 7. 推荐组合（采纳）

```
活动调试主通道（自研，clean-room 参考 dnSpyEx/debug-mcp/sharpdbg 的 API 用法）
├─ ClrDebug (MIT)                    ICorDebug COM 全量托管封装
├─ Microsoft.Diagnostics.DbgShim    (MIT) 或目标 runtime 目录 LoadLibraryEx
├─ 自研 DbgEngineCore：专用调试线程 + CorDebugManagedCallback 状态机
├─ 断点：token/IL（复用现有 PEReader 元数据层）+ PDB 行号 + 模块加载延迟绑定
├─ step：Stepper 封装；locals/args：ILFrame + VariableHome + 值树
├─ 栈/线程：StackWalk + Chain + Thread
├─ 异常断点：Exception(2) + 类型过滤（first/second chance）
└─ 表达式求值 v1：Roslyn 静态分析 + AST 安全子集 + ICorDebugEval2 + evaluate_safe 式护栏

只读内省/监控旁路（不争用 ICorDebug 会话）
├─ Microsoft.Diagnostics.NETCore.Client (MIT)  EventPipe / dump / env
└─ ClrMD (MIT)                                 dump 事后分析
```

要点：
1. ClrDebug 省掉几百处手写 COM 互操作，但要接受 1:1 镜像 API（易 breaking，锁版本）。
2. 自研事件循环是调试器心脏，无 MIT 库替你抽象 DbgManager 同级对象模型。
3. 断点定位白捡现有资产：token→`GetFunctionFromToken+CreateBreakpoint(ilOffset)`；行断点需 PDB↔IL 映射（现有元数据/反编译同源增量）。
4. **并发模型警告**：ICorDebug 单线程同步状态机，所有调试操作排队专用调试线程；MCP 并发请求要串行化，否则复现「并发撕帧」教训。blocking 语义（breakpoint wait）用 TCS + push 双通道显式设计。
5. agent 面向设计借鉴 debug-mcp 产品行为：异常 autopsy 一把抓、evaluate_safe 护栏、命中通知带 top-frame locals。

## 8. 关键参考 URL
- dnSpyEx 调试引擎：https://github.com/dnSpyEx/dnSpy/tree/master/Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet.CorDebug ；CoreCLRHelper.cs（dbgshim 引导协议）；DbgEngineImpl(.Evaluation).cs
- 架构：https://deepwiki.com/dnSpyEx/dnSpy/4.1-core-debugger-architecture
- ClrDebug：https://github.com/lordmilko/ClrDebug 、NuGet、Samples/NetCore；dbgshim 冲突 issue https://github.com/dotnet/runtime/issues/90187
- debug-mcp：https://github.com/jkolo/debug-mcp 、https://debug-mcp.net/docs/architecture
- netcoredbg https://github.com/Samsung/netcoredbg ；sharpdbg https://github.com/MattParkerDev/sharpdbg
- dotnet/diagnostics：DebugServices 设计 https://github.com/dotnet/diagnostics/blob/main/documentation/design-docs/dotnet-dump-extensibility.md ；DbgShim NuGet；dotnet-debug 文档
- ClrMD：https://github.com/microsoft/clrmd ；FAQ（非 debugging api）
- ICorDebug 官方：CreateDebuggingInterfaceFromVersionEx / ICorDebug / ICorDebugEval / ICorDebugManagedCallback（learn.microsoft.com/dotnet/core/unmanaged-api/debugging/...）
- func-eval safe point：https://github.com/dotnet/runtime/issues/72190

> 许可判断为一般性常识，落地前建议按公司合规复核。
