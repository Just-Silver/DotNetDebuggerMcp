# 大重构规划目录（ILSpyMcp → DotNetDebuggerMcp 静态/动态分析 MCP 套件）

> **本目录用途**：持久化「ILSpyMcp → DotNetDebuggerMcp 大重构」的调研资料、愿景、决策与计划，防止会话上下文丢失。
> 所有文档用简体中文，随进展持续更新并提交 git。规划分支：`plan/dynamic-debugging-and-rename`。
> 日期基准：2026-09-05 启动。

## 当前状态

- **P1（仓库改名与拆分）已完成** ✅：仓库改名 DotNetDebuggerMcp，5 项目骨架（Decompiler/Engine/Session/Web/McpHost exe），反编译代码迁入 Decompiler 库。
- **P2（动态调试引擎 v1，Engine）已完成** ✅：ClrDebug + DbgShim 会话管理/token+IL 断点/单步/栈/变量/异常 + 统一 DebugEvent 流。
- **P3（会话层 + MCP 调试工具面）已完成** ✅：Session 库 + 宿主 `debug_*` 工具 + 触发条件导向握手简介。
- **P4-1（DocumentService）已完成** ✅：无 PDB 语句级反编译行映射服务。
- **P4-2（WebUI 监视器）已完成** ✅（2026-09-06）：Blazor Server + BootstrapBlazor + Monaco 展示面；`web_open` 幂等工具落地、MCP server 默认去 `--web`（实施计划 `2026-09-06-p4-closeout-web-open.md`）。
- **P5（打磨与发布）进行中** 🔄：版本三处同步（1.5.0）+ CHANGELOG 转正 + 发布前回归（tag/Release/NuGet 发布待确认）。
- **已拍板决策**：见 `decisions.md`（D1-D13，最新在上）。

## 文档地图

| 文件 | 内容 |
|---|---|
| `01-vision-and-scope.md` | 愿景与范围（P1-P4 大部分落地，保留作历史背景）：5 项目模块化拆分、范围、约束、里程碑 P1-P5、命名定稿 |
| `decisions.md` | 决策记录 D1-D13（最新在上） |
| `open-questions.md` | 开放问题清单（#0-#7 全部已解决折叠） |
| `plans/2026-09-06-p4-closeout-web-open.md` | **P4-2 收尾计划**（web_open 幂等工具 + 默认去 --web，已完成） |
| `archive/plans/` | 已完成计划归档：P1 改名拆分 / P2 引擎 v1 / P3 MCP 工具面 / P4-1 DocumentService / P4-2 WebUI（实际工作在 master 分支，规划所写 feature/p4-monitor 系笔误） |
| `research/01-debugger-tech-landscape.md` | 动态调试依赖库调研：四路线能力/许可/工作量对比 + 推荐组合 |
| `research/04-webui-realtime-stack.md` | Web 实时渲染技术调研（含 2026-09-05 Superseded：React/SSE → Blazor Server + BootstrapBlazor） |
| `research/05-dependency-packages.md` | 依赖包清单（ClrDebug/DbgShim/Roslyn/ICorDebug/ClrMD 概念澄清；WebUI 侧包清单已 Superseded，见 D4） |
| `research/06-clrdebug-api-reference.md` | ClrDebug 0.4.2 最小调试器 API 参考（源码核对版） |
| `research/archive/` | 已归档调研：dnSpy 源码结构摸底 / ILSpy 源码结构摸底 |
| `specs/README.md` | 设计文档目录（specs/ 导航） |
| `specs/2026-09-05-overview-design.md` | **总览设计 spec**（已确认） |
| `specs/2026-09-05-p4-webui.md` | **P4 WebUI 细化 spec**（已冻结） |

> 规划文档配套 Git 历史与已完成计划见 `archive/plans/`；归档材料移出主目录以免导航误读为进行中。
