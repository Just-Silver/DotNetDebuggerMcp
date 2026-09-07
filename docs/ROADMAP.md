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

## v2 候选（reverse-skill 启发，2026-09-07）

> 来源：对照 [zhaoxuya520/reverse-skill](https://github.com/zhaoxuya520/reverse-skill)（34.9k★，MIT，安全逆向技能路由器）的 `skills/dotnet-reverse/` 模块与 README_AI agent 引导架构，在「**agent 主导编程、人类配合**」前提下提炼的候选能力项。全为远期评估、**做不做待后续拍板**；若要提升为近期，须按项目规矩先立方案/spec。其中三项（IL 视图/语料回归/握手流程）近期价值其实不低，列此仅为遵循「全部先进 ROADMAP、再判断」的决策。

- **IL 视图输出**（`decompile_member` 加 `language="il"` 或新工具 `decompile_il`）——reverse-skill「IL 优先于 C#」核心原则：C# 反编译对编译器生成代码（async 状态机/yield/闭包）与异常驱动控制流**失真**，关键判断要切 IL。我们断点定位本就是 IL 层概念（token+IL offset），补 IL 面可与既有 token 断点闭环、并暴露真实 IL offset 供更精确设断。成本可控（ICSharpCode.Decompiler 自带 IL 反汇编面）。约束：缓存签名（`CacheSignatures`）扩展、输出约定分节、根 README 同步；与 ROADMAP「debug_stack 帧名真名化」可配套（IL 视图本身读 token 也受益）。
- **「agent 意图 → 引导文案/行为」语料级回归护栏**——把 R1-R8 实战经验固化为断言测试（如 step 返回文案含「debug_wait」、launch 返回含 pid、Attaching 提示含「冻结在 Main 前」、`list_types` 报错提示 `categories`），防未来改文案/工具悄悄退回旧坑。reverse-skill 用 175 个 `(提示语 → 期望动作)` 基准用例保证路由经验不随改动退化、CI 失败即拒。成本低、近期价值高。约束：挂 AppServices collection 串行；文案断言要与常量类（`AppText`/`ToolParameterText`）同源避免脆断。
- **握手 ServerInstructions 增加「动态调试操作序列 + 结束自检」**——reverse-skill 每个 skill 是可执行操作规范（触发场景 → 六阶段 → 自检清单）；我们现 `HandshakeFeatureIntro` 是**能力描述**而非操作流程。agent 主导下可补一小段推荐序列（launch 后先设断点再 `debug_continue`；停点后 `debug_state` 确认 Stopped → `debug_stack`/`debug_variables`；`debug_step` 后需 `debug_wait`；结束前 `debug_breakpoint_list` 核对绑定状态），并可加「任务自检」（是否确认 Stopped、是否核对了绑定状态）。约束：`HandshakeFeatureIntro` 明言「保持简短、常驻 agent 上下文、不逐条列工具」——只写关键顺序与易错点，控制篇幅。
- **调试-修复-重验证闭环**（中-大）——reverse-skill 工作流每阶段有「落盘产物 + 回到主流程验证」的交接；我们覆盖静态+动态但**缺 agent 改完 bug 后确认修复的最后一跳**：改码 → 重编译 → 用 `debug_launch` 重启目标 → 重跑场景 → 确认行为变化。当前 agent 得自己拿 shell 编译 + 靠 `debug_output` 猜结果。若做，方向是把「重编译 + 重启 + 复跑」也纳入工具面（或至少给可复现的启动参数/环境快照导出）。触发：收到 agent「改完无法确认是否修好」类反馈。
- **经验自进化 field-journal**（中-大，远期）——reverse-skill Auto-Evolution：agent 在任务中发现「新场景/工具坑/失败原因」时**自动写回经验库**，下次先查经验再动手。我们坑与教训散落各 TODO/AGENTS/踩坑实录，**无执行期可查询 + 自动沉淀通道**。契合「agent 主导+人类配合」分工（agent 执行沉淀、人类审阅），但工程量大。触发：多 agent 会话跨任务复用同一调试能力库后出现重复踩坑。

> **已评估不列入**：脱壳/patch/de4dot 生态与运行时 dump（定位不符——面向正常 .NET 程序调试非恶意样本，且破坏「引擎内置零外部依赖」卖点）；44 条路由规则 + 多 skill frontmatter 架构（单 MCP server 单入口，过度设计）；agent 自动装工具/自举（无此需求）。动态调试优先于死磕静态的方向与 reverse-skill 一致，P1-P9 已覆盖并更强（agent 可编程工具面 vs 其 dnSpy GUI 手点）。

## WebUI 后续（P4 收尾后的体验项）

> 近期待办已分散到各项目目录 `TODO.md`（当前仅 Web 剩 watch 表达式输入，已注记暂不做）。P4-2 全部待办（断点红点、刷新保持、树/编辑器双向联动、agent 时间线、零轮询化、`web_open` 幂等工具 + 默认去 `--web`）已完成（2026-09-06），不再列。

## 其它远期
- 全功能表达式求值 / 动态 EnC 排除项 / Mono 目标
