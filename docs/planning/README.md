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
- **P5（打磨与发布）已完成** ✅（2026-09-06）：版本三处同步（1.5.0）+ CHANGELOG 转正 + 发布前回归全通过；v1.5.0 已发布（GitHub Release + NuGet 包，OIDC 受信任发布打通；过程修复宿主 IsPackable=false 导致 pack 静默空跑、nuget.org 受信任发布策略仓库改名失配两处发布阻塞）。
- **已拍板决策**：见 `decisions.md`（D1-D19，最新在上）。
- **P1 批次（宿主 TODO「agent 自动化调试闭环缺口」）已立项（2026-09-09）**：W1 现场改写 / V3 统一时间线 / DB1 敏感脱敏 / D1 对象深读 / D2 子进程跟随——五项逐项审阅拍板转正 spec + 5 份实施计划就绪（`plans/2026-09-09-*.md`），**待实施**（执行用 superpowers:executing-plans）。
- **P2 批次（DB2/V4）已立项（2026-09-09）**：DB2 按名白名单 + V4 语料护栏——拍板转正 spec + 2 份实施计划就绪，待实施（随新工具批次执行）。
- **P3-U1 已立项（2026-09-09）**：UI 自动化（FlaUI 5.0 + ui_find/ui_invoke(action)/ui_scroll 四件套）拍板转正 + 实施计划就绪；FlaUI 引用姿势已线上核实（nuspec 三档 lib / UIInspect.MCP csproj）。
- **V1 复验闭环 + W3 数据断点已立项（2026-09-09）**：按用户「先继续计划、都计划完成后再审查」指令补齐最后两份计划——V1（debug_verify，产物自动拿取）+ W3（spike Task0 三分支）。**全部 TODO 待办（除已转 ROADMAP 的 W2/V2）均已完成 spec 拍板 + 实施计划，待统一审查与实施。**

## 文档地图

| 文件 | 内容 |
|---|---|
| `01-vision-and-scope.md` | 愿景与范围（P1-P4 大部分落地，保留作历史背景）：5 项目模块化拆分、范围、约束、里程碑 P1-P5、命名定稿 |
| `decisions.md` | 决策记录 D1-D19（最新在上） |
| `open-questions.md` | 开放问题清单（#0-#7 全部已解决折叠） |
| `plans/2026-09-06-p4-closeout-web-open.md` | **P4-2 收尾计划**（web_open 幂等工具 + 默认去 --web，已完成） |
| `plans/2026-09-09-w1-set-value.md` / `-v3-timeline.md` / `-db1-sensitive-redaction.md` / `-d1-object-drill.md` / `-d2-child-process.md` / `-db2-named-whitelist.md` / `-v4-copy-guard.md` / `-u1-ui-automation.md` / `-v1-verify-loop.md` / `-w3-data-breakpoint.md` | **实施计划 ×10（全部待办批次）**：W1 debug_set / V3 debug_timeline / DB1 脱敏 / D1 debug_object / D2 子进程标注 / DB2 白名单 / V4 语料护栏 / U1 ui_* / V1 debug_verify / W3 数据断点(spike)；**待统一审查与实施** |
| `archive/plans/` | 已完成计划归档：P1 改名拆分 / P2 引擎 v1 / P3 MCP 工具面 / P4-1 DocumentService / P4-2 WebUI（实际工作在 master 分支，规划所写 feature/p4-monitor 系笔误） |
| `research/01-debugger-tech-landscape.md` | 动态调试依赖库调研：四路线能力/许可/工作量对比 + 推荐组合 |
| `research/04-webui-realtime-stack.md` | Web 实时渲染技术调研（含 2026-09-05 Superseded：React/SSE → Blazor Server + BootstrapBlazor） |
| `research/05-dependency-packages.md` | 依赖包清单（ClrDebug/DbgShim/Roslyn/ICorDebug/ClrMD 概念澄清；WebUI 侧包清单已 Superseded，见 D4） |
| `research/06-clrdebug-api-reference.md` | ClrDebug 0.4.2 最小调试器 API 参考（源码核对版） |
| `research/archive/` | 已归档调研：dnSpy 源码结构摸底 / ILSpy 源码结构摸底 |
| `specs/README.md` | 设计文档目录（specs/ 导航） |
| `specs/2026-09-05-overview-design.md` | **总览设计 spec**（已确认） |
| `specs/2026-09-05-p4-webui.md` | **P4 WebUI 细化 spec**（已冻结） |
| `specs/2026-09-08-w1-set-value.md` 等 10 份 | **全部待办批次 spec**（已冻结）：W1/V3/DB1/D1/D2/DB2/V4/U1/V1/W3（导航与状态见 specs/README；W2/V2 已转 ROADMAP） |

> 规划文档配套 Git 历史与已完成计划见 `archive/plans/`；归档材料移出主目录以免导航误读为进行中。
