# Specs（设计文档）目录

> 总览 spec + 分阶段实施计划（decisions D7）。本目录存**设计文档（spec）**；实施计划存 `docs/planning/plans/`。
> Spec 是冻结后供实施计划引用的依据；被替代时加 Superseded 标注，不重写正文。

| 文档 | 内容 | 状态 |
|---|---|---|
| `2026-09-05-overview-design.md` | **总览 spec**：五项目全局架构、命名布局、各层设计原则、线程/事件模型、阶段边界 P1-P5、风险与合规 | **已确认**（用户 2026-09-05 review OK） |

## 阶段对应关系（decisions D7）

| 阶段 | 主题 | 对应实施计划 |
|---|---|---|
| P1 | 仓库改名与拆分（5 项目骨架 + 反编译代码迁入） | `plans/2026-09-05-p1-rename-and-split.md`（**已完成** ✅ 2026-09-05） |
| P2 | 动态调试引擎 v1（Engine） | `plans/2026-09-05-p2-engine-v1.md`（**已完成** ✅ 2026-09-05） |
| P3 | 会话层 + MCP 调试工具面（Session + McpHost） | `plans/...-p3-mcp-tools.md`（未写） |
| P4 | WebUI（Web，细节在 P4 前单独细化） | `plans/...-p4-webui.md`（未写） |
| P5 | 打磨与发布 | `plans/...-p5-release.md`（未写） |
