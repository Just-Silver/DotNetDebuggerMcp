# ROADMAP（远期待办）

> 记录「暂不做、以后再评估」的功能想法，防止丢失。当前迭代范围见 `CHANGELOG.md` 的 `[Unreleased]` 段；**近期待办见各项目目录 `TODO.md`**（Engine/Web/宿主各自独立），进行中的 P4-2 WebUI 待办见 `docs/planning/open-questions.md` #7。

## v2 候选（vision §4.3「后做」，不阻塞当前）

> 2026-09-06 盘点：面向「agent 动态调试替代加日志」定位的**调试体验升级已立项为近期待办**，主清单见 `src/DotNetDebuggerMcp/TODO.md`（P1-P9：stdout 转发/异常现场/行断点/停点上下文/命中计数+trace/表达式读值子集/条件断点/进程发现/launch 早期断点）；原候选中的 PDB 行断点、表达式求值安全子集、launch 原生路径早期断点、异常类型过滤均已移入该清单排定优先级。此处仅留仍属远期评估的项：

- **EventPipe / ClrMD 旁路**（轻量运行期观察，不走 ICorDebug 全 attach）
- **多调试会话并行**（当前 v1 单活动会话；Engine 测试实测并行 attach 多目标相互干扰，需先解决会话隔离）

## v2 候选（agent 反馈 R5 转来，2026-09-07 决策）

> 原近期待办 R5（async 状态机单步，spec `docs/planning/specs/2026-09-07-r5-async-step.md` 保留作调研记录）经评审**跳过**：agent 主导下状态机不可读非问题（agent 消化 token/MoveNext，工作流为断点直达 + trace/条件断点，非逐步走查）。以下为评估后仍值得远期观察的碎片，触发条件出现再立项：

- **async 单步语义（跨挂起点 continuation 追踪）**——若未来 agent 工作流演进到依赖 step 语义（而非断点直达），需 dnSpy DbgEngineStepperImpl 级实现（隐藏临时断点 + 对象 id 关联 Task + 反编译 async debug info 三方底座，1-2 周+）。触发：收到 agent「step 落点不可预测致流程失败」类反馈。
- **.NET 11 runtime-async（Async V2）观察**——dotnet/runtime#109632：V2 async 方法不再生成 `<Method>d__N.MoveNext` 状态机（栈天然干净），配套 ICorDebug 公开 API（`ICorDebugProcess12::GetAsyncStack`，PR #123101）+ JIT async 调试信息（PR #120303）已合入 main。**局限**：需目标代码 net11+`runtime-async=on` 重编译才生效；对 V1 状态机（存量代码）无任何运行时级可读性改进，V1 帧过滤永远是调试器侧自己的活。观察点：V2 正式发布后 BCL/生态普及度、`debug_stack` 帧特征变化（若 V2 普及，我们的 V1 帧名识别不误伤真方法帧即可）。
- **debug_stack 帧名真名化（低优先级顺手项）**——`TryGetTypeName`/`TryGetMethodName` 现只返回 token 文本；TypeNameResolver/SymbolNameResolver 已具备能力，接上即得真名（顺带可读性）。非 R5 前置，任何时候可单独做。

## WebUI 后续（P4 收尾后的体验项）

> 近期待办已分散到各项目目录 `TODO.md`（当前仅 Web 剩 watch 表达式输入，已注记暂不做）。P4-2 全部待办（断点红点、刷新保持、树/编辑器双向联动、agent 时间线、零轮询化、`web_open` 幂等工具 + 默认去 `--web`）已完成（2026-09-06），不再列。

## 其它远期
- 全功能表达式求值 / 动态 EnC 排除项 / Mono 目标
