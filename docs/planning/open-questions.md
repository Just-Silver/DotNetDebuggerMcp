# 开放问题（OPEN QUESTIONS）

> 最新在上。澄清后把「问题+结论」移入 decisions.md。

## #4 WebUI 技术栈确认（待澄清，勿删）
- 状态：**待澄清**（2026-09-05 恢复——曾被误标已解决）
- 问题：WebUI 技术栈是否采纳调研建议组合 A（research/04）：
  - 服务端：ASP.NET Core / Kestrel 单进程内嵌（静态资源 + REST + SSE 同端口）
  - 实时推送：SSE（连接拉 `/api/state` 快照，此后增量；EventSource 自动重连）
  - 前端框架：React + TS + Vite
  - 代码视图：Monaco Editor（read-only + deltaDecorations 断点/当前行高亮）
  - 图/时间线：时间线自绘 DOM 列表；时序/调用图 mermaid.js 按需动态加载
  - IL→反编译行映射：全部在服务端（Session 层）解析后推浏览器
  - 备选：CodeMirror 6（更轻、无 worker，但 C# 高亮弱）；vanilla TS（仅极简展示）
- 背景：decisions D4（建议）仍是「建议待确认」状态；实施时机已定（D7：P4 再上 Web）。

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
