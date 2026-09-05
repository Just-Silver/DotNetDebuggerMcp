# ROADMAP（远期待办）

> 记录「暂不做、以后再评估」的功能想法，防止丢失。当前迭代范围见 `CHANGELOG.md` 的 `[Unreleased]` 段；**近期待办见各项目目录 `TODO.md`**（Engine/Web/宿主各自独立），进行中的 P4-2 WebUI 待办见 `docs/planning/open-questions.md` #7。

## v2 候选（vision §4.3「后做」，不阻塞当前）
- **表达式求值安全子集**（调试器 watch 表达式，参考 debug-mcp AST 求值路线，research/01 §4）
- **PDB 行断点**（有 PDB 程序集按源行断点；当前反编译无 PDB 用 IL offset 断点）
- **模块延迟绑定断点**（模块未加载时设 pending 断点，Engine v2）
- **EventPipe / ClrMD 旁路**（轻量运行期观察，不走 ICorDebug 全 attach）
- **多调试会话并行**（当前 v1 单活动会话）

## WebUI 后续（P4 收尾后的体验项）

> 近期待办已分散到各项目目录 `TODO.md`（当前仅 Web 剩 watch 表达式输入，已注记暂不做）。P4-2 全部待办（断点红点、刷新保持、树/编辑器双向联动、agent 时间线、零轮询化、`web_open` 幂等工具 + 默认去 `--web`）已完成（2026-09-06），不再列。

## 其它远期
- 全功能表达式求值 / 动态 EnC 排除项 / Mono 目标
