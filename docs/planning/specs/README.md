# Specs（设计文档）目录

> 总览 spec + 分阶段实施计划（decisions D7）。本目录存**设计文档（spec）**；实施计划存 `docs/planning/plans/`。
> Spec 是冻结后供实施计划引用的依据；被替代时加 Superseded 标注，不重写正文。

| 文档 | 内容 | 状态 |
|---|---|---|
| `2026-09-05-overview-design.md` | **总览 spec**：五项目全局架构、命名布局、各层设计原则、线程/事件模型、阶段边界 P1-P5、风险与合规 | **已确认**（用户 2026-09-05 review OK） |
| `2026-09-05-p4-webui.md` | **P4 WebUI 细化 spec**：运行形态双模式、页面布局 v1 核心面、文档模型/行映射、技术集成定稿（BB 10.10.0 + 自研 Monaco 互操作 + 宿主 --web 接线） | **已冻结**（用户 2026-09-05 review OK） |

## 阶段对应关系（decisions D7）

| 阶段 | 主题 | 对应实施计划 |
|---|---|---|
| P1 | 仓库改名与拆分（5 项目骨架 + 反编译代码迁入） | `archive/plans/2026-09-05-p1-rename-and-split.md`（**已完成** ✅ 2026-09-05，已归档） |
| P2 | 动态调试引擎 v1（Engine） | `archive/plans/2026-09-05-p2-engine-v1.md`（**已完成** ✅ 2026-09-05，已归档） |
| P3 | 会话层 + MCP 调试工具面（Session + McpHost） | `archive/plans/2026-09-05-p3-mcp-tools.md`（**已完成** ✅ 2026-09-05，已归档） |
| P4 | WebUI（Web，细节在 P4 前单独细化） | `archive/plans/2026-09-05-p4-1-documentservice.md`（**已完成** ✅，已归档）+ `plans/2026-09-05-p4-2-webui.md`（**进行中**） |
| P5 | 打磨与发布 | `plans/...-p5-release.md`（未写） |
