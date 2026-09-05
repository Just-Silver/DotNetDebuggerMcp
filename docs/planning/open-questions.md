# 开放问题（OPEN QUESTIONS）

> 最新在上。澄清后把「问题+结论」移入 decisions.md。

## #6 WebUI 代码视图与推送通道落地（最新，待澄清）
- 状态：**待澄清**（2026-09-05）
- 背景：Web 前端定 BootstrapBlazor（Blazor Server，decisions D4 更新）。BB **无代码编辑器/语法高亮组件**（bb-llms 已查证：editor→仅 EditorForm；code/highlight→空；textarea→Textarea 纯文本）。
- 子问题：
  1. **代码视图**落地方式：(a) Monaco 作为 Blazor JS 互操作组件（观感最好，带 JS 构建）；(b) BB Textarea/自绘只读高亮（纯 C#，高亮/行装饰弱）；(c) BlazorMonaco 之类现成封装。
  2. **推送通道**：Blazor Server 的 SignalR 电路能否承载调试事件流？Session 的 Channel 事件如何驱动 Blazor 刷新（`IAsyncEnumerable`/订阅式 vs 轮询快照）。
  3. **构建链**：接受仅 Blazor Server（无静态 SPA）以完全避开 Node 构建链？
- 背景：research/04 的 SSE+快照建议在 Blazor 语境可能简化为电路内推送。

## #4 WebUI 技术栈方向（已定）
- 状态：**方向已定**（2026-09-05，decisions D4 更新）→ **BootstrapBlazor（Blazor Server）**，替代 React+Monaco 组合 A。
- 残留：代码视图/推送通道/构建链细节见 #6。

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
