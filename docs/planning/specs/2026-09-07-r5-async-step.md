# Spec · R5 async 状态机单步体验

> 状态：**草案待评审**（2026-09-07 起草）。
> 关联：宿主 TODO R5；agent 实战反馈清单 R5（2026-09-06 收到，中-大项）；引擎动态调试 StepAsync。
> 参考：本地克隆 dnSpy `Extensions/dnSpy.Debugger/dnSpy.Debugger.DotNet/Steppers/Engine/DbgEngineStepperImpl.cs`（async 步进完整实现，本 spec 取其思路、缩窄到务实子集）。

## 1. 背景与目标

agent 调试 async/await 代码时单步体验差。现状（已核实 `DebugEngineCore.StepAsync`）：

- **step-over**：有 PDB 按语句 IL 区间 `StepRange`（一次跨整条 C# 语句）；无 PDB 回退**单条 IL 指令区间** `[ip, ip+1)`——框架代码（无 PDB）一条 C# 语句要停多次。
- **step-into** `await FooAsync()`：async 方法体被编译器改写为状态机（`<Foo>d__N.MoveNext`），`await` 处方法**立即返回**，`MoveNext` 的续延由线程池/同步上下文后续调度——ICorDebug 原生 stepper 对 await 语义无感知，step-into 表现为「步进后停在奇怪位置/原地完成」，agent 看不到真正的方法体执行。
- **step-out**：状态机内部 `MoveNext` 里 step-out 会退回调度点（如 `AsyncTaskMethodBuilder` 内部/线程池），而非用户代码的调用处——「步出 async 方法」语义错乱。

目标：让 agent 在 async 代码上的 step-over/step-out 不落入状态机内部机械帧；step-into 经 `await` 时**诚实停住**而非假装跨过（跨异步边界的完整「返回 awaiter」是 dnSpy 重量级路线，v1 不做）。

非目标：完整 async step-into（dnSpy `AsyncDebugging` + FuncEval 断点回 awaiter 机制——依赖 dnlib/语言服务/异步求值，远超本项目 v1）；迭代器（yield）状态机同类处理（`AreIteratorsDecompiled` 参考，v1 仅 async）；Web/CLI 步进面板改动。

## 2. 现状锚点（起草时已核实）

| 设施 | 位置 | 关系 |
|---|---|---|
| `DebugEngineCore.StepAsync(bool? stepIn)` | `Engine/DebugEngineCore.cs:300` | 单步命令：stepOut=null→`StepOut()`；否则 `TryGetStatementRange`→有 PDB `StepRange(语句区间)`、无 PDB `StepRange([ip, ip+1))`。**本次改动主战场** |
| `TryGetStatementRange(ilf)` | 同文件 :340 | 模块旁 PDB 序列点取当前语句 IL 区间；无 PDB/失败回退单条 IL |
| `TryGetTypeName` / `TryGetMethodName` | 同文件 :1197/:1207 | **当前只返回 token 十六进制**（`0x...`），未接名字解析——async 帧识别需补方法名/类型名 |
| `TypeNameResolver.Resolve(modulePath, classToken)` | `Engine/TypeNameResolver.cs` | 类 token → `命名空间.类型名`（嵌套走 enclosing 链；async 状态机类 `<Foo>d__N` 可解析）。P2 已用 |
| `SymbolNameResolver.Resolve` / `ReadArgNames` | `Engine/SymbolNameResolver.cs` | 已按 methodToken 读 MethodDef（Param 表）；**加个 `ReadMethodName` 即可取 `md.Name`**（如 `MoveNext`），无需新设施 |
| `DebugStackFrame.TypeName/MethodName` | `Models/DebugStackFrame.cs` | 已有字段，接上 resolver 即得真名 |
| `DebugTarget` `WithAsync.Go()` | `tests/TestData/generate-testdata.ps1` | `public async Task Go() => await Task.Yield();` 编译器生成 `<Go>d__N`——**测试锚点** |

## 3. 分层设计与关键决策

### 3.1 识别 async 状态机帧（决策①：类名 + 方法名双特征）

async 状态机特征（编译器生成、IL 层稳定）：

- **类型**：`<方法名>d__N` 嵌套 struct/class（`N` 为序号），实现 `IAsyncStateMachine`（`System.Runtime.CompilerServices`）。
- **方法**：`MoveNext()`（`void MoveNext()`，状态机主执行体）+ `SetStateMachine(...)`。

识别（命令泵内、按帧判断）：帧的 `(modulePath, classToken)` → `TypeNameResolver.Resolve` 得类名；类名匹配 `^<.+>d__\d+`（C#/VB 编译器统一形态）→ 判定为 async 状态机帧。方法名（`MoveNext`/`SetStateMachine`）作辅助证据，不单独作为判定（避免误伤用户自写的 `MoveNext`）。判定失败（元数据不可读等）→ 不视为状态机帧（保守，行为同现状）。

不做：反编译/IL 扫描确认是否实现 `IAsyncStateMachine`（开销大）；dnSpy 式完整 async debug info（`AsyncMethodDebugInfo`——需反编译器产出状态机→原方法的语句映射，属 Decompiler 大工程）。

### 3.2 step-over/step-out：过滤状态机内部帧（决策②）

现状 step-over 的「单条 IL 区间」在状态机内会逐 IL 停；step-out 会退出到调度器。改为**遇状态机内部帧时自动连步**：

- **step-out**：当前帧是状态机 `MoveNext` → 退出的正确目标不是「上一个物理帧」（那是调度器/线程池），而是**用户代码调用处**。v1 务实做法：先在状态机内 `StepOut` 到状态机边界，若落点仍是状态机/调度帧则**继续自动 step-out/continue 至用户代码帧或停点**（次数上限防失控），到非状态机帧停。诚实标注：无 PDB 的框架调度帧无法精确定位「用户 await 调用行」，能保证的是「不再停在 `<Foo>d__N` 机械帧」。
- **step-over**：当前在用户代码非状态机帧 → 正常语句 StepRange；若 StepRange 完成落点进入状态机内部（step-over 一条含 await 的语句会这样）→ 自动连步至回到用户代码帧（或停点/上限）。

实现形态：不在 StepAsync 一次性搞定（StepRange 是异步回调链），而在**StepCompleted 事件处理**加「自动连步」判定：本次步进落点若是状态机内部帧（且不是用户明确 step-into 目标）→ 自动再发一次步进（over/out 语义延续），直到落点非状态机帧或到上限（默认如 20 次，防死循环）。回调线程投泵，与现有 StepCompleted→停 流程兼容：自动连步期间不 PublishState(Stopped)、不产生 agent 可见停点。

### 3.3 step-into：诚实停住 + 文档（决策③）

step-into `await FooAsync()` 时，物理上会进入 `<Foo>d__N.MoveNext` 或停在 await 返回处——**v1 不尝试跨异步边界**（dnSpy 那套 SetNotifyDebuggerOfWaitCompletionBreakpoint 需 FuncEval/对象 id/断点回 awaiter，见 :138-242，超出 v1）。v1 行为：

- step-into 照常进入（可能是状态机 MoveNext）——**但**状态机内部帧的机械执行不隐藏 step-into（agent 明确要进，让其看到真实执行），仅在 `debug_state`/返回文案标注「当前在 async 状态机内部帧 `<Foo>d__N.MoveNext`（编译器生成）」。
- 文档/`debug_step` Description 诚实标注：「step-into 经 await 只能进到状态机内部；要看 await 之后的方法体，在目标方法内直接下断点直达。」

### 3.4 无 PDB 的 step-over：跨调用边界启发式（决策④）

现状无 PDB 单条 IL 区间是「每条 IL 一停」的根源。v1 改进（不动 PDB 路线）：无 PDB 且**非状态机帧**时，step-over 用「到下一个 call/ret 边界」启发式聚合——StepRange 从当前 IP 到「本方法内下一个调用指令（含）后」或方法末尾，使无 PDB 的 step-over 从「逐 IL 停」提升到「每次调用/返回停一次」。取舍：比逐 IL 少停但不如 PDB 精确（可能跨过多个语句）。风险控制：仅 step-over 用，step-into 保持逐 IL（agent 明确要进时宁可细）；边界解析失败回退现状单条 IL。

（注：3.4 是无 PDB 独立改进，不依赖 async；若评审认为 v1 范围过大可单独砍掉，async 过滤仍是主体。）

### 3.5 帧名接上 resolver（前置小改）

`TryGetTypeName`/`TryGetMethodName` 从「token 文本」改为「resolver 真名，失败降级 token」：

```
TryGetTypeName  → TypeNameResolver.Resolve(modulePath, (int)cls.Token.Value) ?? token 文本
TryGetMethodName → SymbolNameResolver 新增 ReadMethodName(modulePath, methodToken) ?? token 文本
```

顺带收益：`debug_stack` 输出从 `0x...` 升级为真名（agent 可读性大提升，R 系反馈精神一致）。**行为变化点**：栈帧展示文本变化——需检查现有测试是否断言 token 文本（见测试计划回归）。

## 4. 工具面

```
debug_step / debug_state / debug_wait 行为与返回
  step-over/out 遇 async 状态机内部帧 → 自动连步至用户代码（附标注「已跳过 N 个编译器生成帧」）
  step-into 停在状态机内 → 停点附「当前在 async 状态机内部帧 <Foo>d__N.MoveNext」提示
  debug_stack 帧名 → 真名（TypeName.MethodName），token 仅当解析失败降级
CLI -dbg / Web → v1 不加步进参数（agent 是主消费者）
```

## 5. 测试计划

- **Engine 集成**（DebugTarget WithAsync 锚点）：step-over 含 `await Task.Yield()` 的语句 → 完成后不停在 `<Go>d__N.MoveNext` 内部（连步至用户帧）；step-out 从 MoveNext 内发起 → 退到用户代码帧而非线程池机械帧；step-into `Go()` → 停在状态机内部但返回带「async 状态机内部帧」标注。
- **单元**：状态机帧识别（类名 `^<.+>d__\d+`）正/反例（含用户自写 `MoveNext` 不误判）；无 PDB step-over 边界启发式（用无 PDB 的框架方法/或构造 IL 区间样本）。
- **宿主 e2e**：debug_launch DebugTarget async 场景 → step-over/out 序列 → 断言最终停点非状态机帧、返回含标注。
- **回归红线**：现有 5 个 DebugMcpToolsTests 的 step 场景（若有）零变化；`debug_stack` 真名化后全量断言同步（搜 `0x...` 断言的测试逐一核对——token 仍是位置标识，帧展示层变真名）。

## 6. 工作量与顺序

帧名接 resolver（0.5，前置）→ 状态机帧识别 + 自动连步（1）→ 无 PDB 启发式（0.5，可砍）→ 工具面标注/文案（0.5）→ 测试三层（1），≈ 3.5 人日。顺序即依赖序；无 PDB 启发式独立可延后。

## 7. 待评审取舍点

① 状态机帧识别用「类名特征」够不够（vs 加 IAsyncStateMachine 实现确认）；② step-out 自动连步的上限与「落点不可知时停在哪」（建议：到非状态机帧即停，上限 20）；③ 无 PDB step-over 启发式是否进 v1；④ `debug_stack` 真名化是否顺势做（建议做，与 R 系反馈一致）。
