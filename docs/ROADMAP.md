# ROADMAP（远期待办）

> 记录「暂不做、以后再评估」的功能想法，防止丢失。当前迭代范围见 `CHANGELOG.md` 的 `[Unreleased]` 段；**近期待办见各项目目录 `TODO.md`**（Engine/Web/宿主各自独立），进行中的 P4-2 WebUI 待办见 `docs/planning/open-questions.md` #7。

## v2 候选（vision §4.3「后做」，不阻塞当前）
- **表达式求值安全子集**（调试器 watch 表达式，参考 debug-mcp AST 求值路线，research/01 §4）
- **PDB 行断点**（有 PDB 程序集按源行断点；当前反编译无 PDB 用 IL offset 断点）
- **launch 原生路径早期断点**（launch 停初始点时目标模块未必加载、直接下断点依赖 pending 重绑时序；Session/宿主现以「先起进程等稳定区再 Attach」绕开，目标需自带启动延迟）。~~模块延迟绑定断点~~——已随 1.5.0 落地（`BreakpointManager` pending 登记 + `TrackModule` 自动重绑），不再列
- **EventPipe / ClrMD 旁路**（轻量运行期观察，不走 ICorDebug 全 attach）
- **多调试会话并行**（当前 v1 单活动会话；Engine 测试实测并行 attach 多目标相互干扰，需先解决会话隔离）
- **异常断点类型精确过滤**（`ExceptionBreakpointFilter.Matches` 已有骨架未启用；v1 设了过滤器即停全部 first-chance）

## WebUI 后续（P4 收尾后的体验项）

> 近期待办已分散到各项目目录 `TODO.md`（当前仅 Web 剩 watch 表达式输入，已注记暂不做）。P4-2 全部待办（断点红点、刷新保持、树/编辑器双向联动、agent 时间线、零轮询化、`web_open` 幂等工具 + 默认去 `--web`）已完成（2026-09-06），不再列。

## 其它远期
- 全功能表达式求值 / 动态 EnC 排除项 / Mono 目标
