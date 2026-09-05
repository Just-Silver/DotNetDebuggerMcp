# 大重构规划目录（ILSpyMcp → DotNet 静态/动态分析 MCP 套件）

> **本目录用途**：持久化「ILSpyMcp 大重构」的调研资料、愿景、决策与计划，防止会话上下文丢失。
> 所有文档用简体中文，随进展持续更新并提交 git。规划分支：`plan/dynamic-debugging-and-rename`。
> 日期基准：2026-09-05 启动。

## 文档地图

| 文件 | 内容 |
|---|---|
| `01-vision-and-scope.md` | 愿景：5 项目模块化拆分（Decompiler/Engine/Session/Web/McpHost）、范围、约束、里程碑 P1-P5、命名定稿 |
| `research/01-debugger-tech-landscape.md` | **动态调试依赖库调研**：四路线能力/许可/工作量对比 + 推荐组合 |
| `research/02-dnspy-source-structure.md` | 本地 `E:\Code\Projects\Externals\dnSpy` 源码摸底：调试栈能否作为库嵌入 |
| `research/03-ilspy-source-structure.md` | 本地 `E:\Code\Projects\Externals\ILSpy` 源码摸底：反编译库/调试映射能力 |
| `research/04-webui-realtime-stack.md` | **Web 实时渲染技术调研**（含 2026-09-05 Superseded：React/SSE → Blazor Server + BootstrapBlazor） |
| `research/05-dependency-packages.md` | 依赖包清单（ClrDebug/DbgShim/Roslyn/ICorDebug/ClrMD 概念澄清） |
| `specs/2026-09-05-overview-design.md` | **总览设计 spec**（已确认） |
| `plans/2026-09-05-p1-rename-and-split.md` | **P1 实施计划**（改名+拆分，已写待执行） |
| `decisions.md` | 决策记录 D1-D9（最新在上） |
| `open-questions.md` | 开放问题清单（最新在上），回答后移入 decisions.md |

## 当前状态

- **阶段**：**P1（仓库改名与拆分）已完成** ✅（feature/p1-rename-split 分支，425 测试全绿 + Client 端到端全过）。
- **已拍板决策**（decisions D1-D10）：
  1. 现有反编译改名保留 → Decompiler 库；新增动态调试引擎（Engine）；主 MCP+Web 宿主（McpHost）。先引擎/MCP 后 Web。
  2. 5 项目拆分：`DotNetDebugger.Decompiler` / `.Engine` / `.Session` / `.Web` / `DotNetDebuggerMcp`(exe)，进程合一。
  3. 命名 **DotNet-Debugger-MCP**（包 id `dotnet-debugger-mcp`）；MCP 注册名建议 `dotnetdebugger`。
  4. 调试技术路线：ClrDebug + DbgShim + 自研引擎（MIT）；dnSpyEx(GPL)/debug-mcp(AGPL) 只 clean-room 参考。
  5. v1 引擎 = 最小 agent 调试闭环（不含表达式求值）；表达式求值/PDB 行断点列 v2。
  6. Web 栈 = **Blazor Server + BootstrapBlazor + Monaco 互操作**（SignalR 电路推送）。
  7. P1 执行策略 = **同仓重建**（先建 5 项目再迁源码，验证绿后删旧）。
- **待办**：P1 计划执行（从 master 开实现分支）→ P2-P5 计划依次产出。残留开放项见 `open-questions.md`。
