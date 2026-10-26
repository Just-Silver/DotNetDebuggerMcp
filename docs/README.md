<!-- 职责：docs/ 树的顶层「文档 → 负责 / 不写」地图（唯一索引，一行一篇，只登记顶层入口）；不写具体设计内容、不写实施细节。 -->

# docs 文档地图

维护者与 agent 的建档入口。新增文档必须在同一次改动里登记到本表。

| 路径 | 负责 | 不写 |
|---|---|---|
| `../AGENTS.md` | 仓库级总览：项目地图、开发铁律、跨项目共享约束、命令 | 单个项目的结构/踩坑细节 |
| `planning/README.md` | **规划目录权威入口**：调研 / specs / plans 导航与当前状态 | 具体设计正文 |
| `planning/specs/` | 设计文档（已拍板的功能设计、边界、回归护栏） | 实施步骤 |
| `planning/plans/` | 实施计划（逐任务 RED→GREEN） | 设计决策理由 |
| `planning/research/` | 技术调研（选型对比、实测记录） | 最终决策 |
| `reports/` | **真实使用现场记录与复核**：agent/维护者的误诊、踩坑、实测证据 | 修复方案（由维护者决定） |
| `ROADMAP.md` | 远期待办 | 近期任务（见各项目 `TODO.md`） |

## reports/ 清单

| 文件 | 负责 |
|---|---|
| `reports/2026-10-26-disabled-control-misdiagnosis.md` | 一次 `ui_*` 使用侧误诊的原始记录（禁用控件语义动作失败、错误文案归因、`ui_find` 缺 `enabled`） |
| `reports/2026-10-26-disabled-control-misdiagnosis-verification.md` | 对上一篇逐条结论的独立复核：重跑对照实验 + 读 WPF/FlaUI 源码，给出成立/推翻/未复现判定 |
