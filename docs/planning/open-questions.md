# 开放问题（OPEN QUESTIONS）

> 最新在上。澄清后把「问题+结论」移入 decisions.md。

## #6 WebUI 代码视图与推送通道（已解决 2026-09-05）
- 状态：**已解决** → 见 decisions D4 定稿：Monaco 作 Blazor 互操作组件；推送走 Blazor Server SignalR 电路；无 React/Vite/SSE。
- 残留：面板具体 BB 组件分工与事件→Blazor 刷新机制 → P4 细化 spec 时定。

## #4 WebUI 技术栈方向（已定）
- 状态：**已定稿**（2026-09-05，decisions D4）→ **Blazor Server + BootstrapBlazor + Monaco 互操作**。

## #5 子项目 A/B 库名与项目拆分（已解决 2026-09-05）
- 状态：**已解决** → 采纳建议值 + **5 项目拆分**（decisions D6/D8）：Decompiler / Engine / Session / Web / McpHost(exe)；MCP 与 Web 不拆进程（共享会话）。
- 残留待实施确认：MCP server 注册名（建议 `dotnetdebugger`）、Client 端到端项目归属、GitHub rename 与 NuGet 旧包 ilspymcp 弃用策略。

## #2 命名决策（已拍板 2026-09-05）
- 状态：**已解决** → 主项目/仓库名 **DotNet-Debugger-MCP**（decisions.md D6，01 §7 已更新）。
- 残留待实施确认：MCP server 注册名（建议 `dotnetdebugger`）、子项目 A/B 库名与命名空间（建议见 D6）、GitHub rename 与 NuGet 旧包 ilspymcp 弃用策略。

## #1 动态调试引擎实现路线确认
- 状态：**方向已确认**，实施细节待设计（见 decisions D3/D5）
- 结论摘要：技术路线 = ClrDebug(MIT) + Microsoft.Diagnostics.DbgShim(MIT) + 自研引擎（clean-room 参考 dnSpy dndbg/Impl 协议）；dnSpyEx(GPL)/debug-mcp(AGPL) 只读不链不抄；v1 = 最小闭环（decisions D5）；包清单 research/05。
- 待澄清残留：无重大项。spike（ClrDebug 最小验证）建议在正式开工前或作为 M1 第一步执行以降低风险。

## #0（已解决）大重构先建分支 + 计划持久化
- 状态：**已解决**（2026-09-05）→ 分支 `plan/dynamic-debugging-and-rename`；规划文档 `docs/planning/` 多文件拆分 + git 提交。
