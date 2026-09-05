# TODO（DotNetDebugger.Engine 近期待办）

> 近期待办，完成一项删一项；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

- [ ] **对象/数组成员展开（面板 Children）**：`ReadValue` 目前只有 标量/引用摘要，`DebugValue.Object`（Children）从未产出。需走 CorDebug 值链：`CorDebugReferenceValue.Dereference` → `CorDebugHeapValue`/`CorDebugClassValue` 取字段（数组走 `CorDebugArrayValue`），一级展开即可满足面板；防环（深度限制 + 已见引用集合）。
