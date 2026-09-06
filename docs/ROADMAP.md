# ROADMAP（远期待办）

> 记录「暂不做、以后再评估」的功能想法，防止丢失。当前迭代范围见 `CHANGELOG.md` 的 `[Unreleased]` 段；**近期待办见各项目目录 `TODO.md`**（Engine/Web/宿主各自独立），进行中的 P4-2 WebUI 待办见 `docs/planning/open-questions.md` #7。

## v2 候选（vision §4.3「后做」，不阻塞当前）

> 2026-09-06 盘点：面向「agent 动态调试替代加日志」定位的**调试体验升级已立项为近期待办**，主清单见 `src/DotNetDebuggerMcp/TODO.md`（P1-P9：stdout 转发/异常现场/行断点/停点上下文/命中计数+trace/表达式读值子集/条件断点/进程发现/launch 早期断点）；原候选中的 PDB 行断点、表达式求值安全子集、launch 原生路径早期断点、异常类型过滤均已移入该清单排定优先级。此处仅留仍属远期评估的项：

- **EventPipe / ClrMD 旁路**（轻量运行期观察，不走 ICorDebug 全 attach）
- **多调试会话并行**（当前 v1 单活动会话；Engine 测试实测并行 attach 多目标相互干扰，需先解决会话隔离）

## WebUI 后续（P4 收尾后的体验项）

> 近期待办已分散到各项目目录 `TODO.md`（当前仅 Web 剩 watch 表达式输入，已注记暂不做）。P4-2 全部待办（断点红点、刷新保持、树/编辑器双向联动、agent 时间线、零轮询化、`web_open` 幂等工具 + 默认去 `--web`）已完成（2026-09-06），不再列。

## 其它远期
- 全功能表达式求值 / 动态 EnC 排除项 / Mono 目标
