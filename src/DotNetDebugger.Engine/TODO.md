# TODO（DotNetDebugger.Engine 近期待办）

> 近期待办，完成一项删一项；远期想法见 `docs/ROADMAP.md`（v2 候选：pending 断点/PDB 行断点/表达式求值/多会话并行等）；开发指南见同目录 `AGENTS.md`。

- [ ] **单步步进位置不推进（高优）**：停点处 `StepOver/StepInto` 后 `StepCompleted` 落回**同一 IL offset**（实测 DebugTarget `Work+0x0` 反复原地），位置从未前进——裸 `thread.CreateStepper().Step(b)` 语义不对。参考 sharpdbg（`ManagedDebugger.cs`）：**帧级 `frame.CreateStepper()`** + `SetInterceptMask`（排除 SECURITY/CLASS_INIT）+ `SetUnmappedStopMask(STOP_NONE)` + **`StepRange`（当前语句的 IL [start,end) 区间，来自序列点）**，区间外即停。注意：Engine 不依赖 Decompiler，语句 IL 区间需由调用方（Session/宿主/Web，从 DocumentService 映射）传入或引擎自带轻量映射；`StepOut` 可保持裸调用。顺带核对 ClrDebug `CorDebugStepper.Step` 是否需显式 `Activate(true)`（断点有此坑，research/06 A.2）。`StepTests` 只断言事件数量不断言位置推进，修复时补「offset 前进」断言。**MCP `debug_step` 与 Web 单步/步入同受影响**（事件能收到但位置不动的表象是「单步无渲染效果」）。
