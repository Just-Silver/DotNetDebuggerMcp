# TODO（DotNetDebuggerMcp 宿主近期待办）

> 近期待办，完成一项删一项；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

> **2026-09-10 清理归档**：已完成历史已删除（细节见 git log 与 `docs/planning/specs/`）。已交付：W1 现场改写、V3 统一时间线、DB1 敏感脱敏、DB2 按名白名单、D1 对象深读、D2 子进程跟随、V4 语料断言、U1+U1A 全 UIA 化 UI 自动化、V1 一键复验。W3 数据断点、W2 SetIP、V2 崩溃 dump **已转 `docs/ROADMAP.md`**。以下仅保留**未完成近期待办**、**本轮 CoreMes 实证发现**与**防重复立项结论**。

## 一、CoreMes 动态调试实证发现（2026-09-10，47 工具全量实测）

> 来源：对 WPF 应用 CoreMes（离线跑）做 47 工具全量实测，端到端闭环（launch→断点→单步→异常→UI→verify PASS）跑通。按严重度排序；**改工具面须同步根 `README.md` 与 CHANGELOG `[Unreleased]`**。

- [ ] **【低】缺失工具候选**：帧选择 `frameIndex`（evaluate/variables 指定非栈顶帧）。（`debug_terminate`/`debug_modules` 已补，2026-09-10）

## 二、已评估关闭/远期（防重复立项，一行结论）

- **func-eval 主动调用业务方法** = **关闭**（async/UI/外设方法 func-eval 必死锁；纯函数触发需求未见）。
- **W2 完整 SetIP/强制返回** = 远期（已转 ROADMAP 2026-09-08）。
- **V2 崩溃自动 dump** = 远期（注入 `DOTNET_DbgEnableMiniDump` 改环境 + spike 不确定；退出码/崩溃判定小增量随 V3 已完成）。
- **W3 数据断点** = 远期（2026-09-10 spike 实测：A `CorDebugValue.CreateBreakpoint` 恒 E_NOTIMPL、B 无创建端，均不可行；用已知写入点条件断点替代）。
- **ClrMD live 内存分析** = ROADMAP（dump 事后分析，live 会话内与 ICorDebug 冲突）。
- **多调试会话并行** = ROADMAP（Engine 实测相互干扰）。

## 三、实施后遗留观察项（低优先）

- U1A：① locator 严格 Name 全等使「UIA Name 随内容变化」控件同 index 二次操作判 stale（既定契约）；② `ui_wait` 释放 gate 后 `window` 跨操作复用（降级轮询，无崩溃证据）。
