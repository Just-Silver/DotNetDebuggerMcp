# TODO（DotNetDebuggerMcp 宿主近期待办）

> 近期待办，完成一项删一项；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

> **2026-09-08 清理归档**：已完成历史已删除（细节见 git log 与 `docs/planning/specs/`）：P1-P9 动态调试体验升级、R1-R8 agent 反馈修复、本机 DebugMcpToolsTests 竞态排查、A-O CoreMes 实证改进。以下仅保留**进行中/立项前**条目。

## 执行推进顺序总览（2026-09-08 排定，按批推进）

> 每条 TODO 含完整实现所需关键信息 + spec 路径；**中-大项先按 spec 拍板待办项再动码**；新工具落地须同步根 README 并新开 CHANGELOG `[Unreleased]` 段（1.7.0 已发布，当前无该段，下一批工具落地时新开）；小型项先补方案段。**2026-09-09：全部待办（W1/V3/DB1/D1/D2/DB2/V4/U1/V1/W3）已逐项拍板转正 spec + 实施计划就绪**（`docs/planning/plans/2026-09-09-*.md`），待统一审查后实施；W2/V2 已转 ROADMAP 不在待办。

| 批次 | 项 | spec | 状态 | 依赖 |
|---|---|---|---|---|
| **P1**（独立/低成本，先做） | **W1 现场改写** | `2026-09-08-w1-set-value.md` | **已完成**（2026-09-09 实施：DebugTarget WriteProbe + spike 支持矩阵定案 spec §7 → Engine `SetPathValueAsync`/readonly 拒绝 → Session `WriteValueParser` → 宿主 `debug_set` 工具 + README/CHANGELOG；本地 commit 1a80a09/1c206f5/3c956bd/3775627） | — |
| | **V3 统一时间线** | `2026-09-08-v3-timeline.md` | **已完成**（2026-09-09 实施：事件历史环形 500 + DebugTimeline 三源归并 + debug_timeline 工具 + 退出码/attach 顺手项；本地 commit 12456d8/d10ade5/eff9c29/a695658） | P1 时间戳✅ |
| | **DB1 敏感脱敏层** | `2026-09-08-db1-sensitive-redaction.md` | **已拍板+计划就绪**（2026-09-09：读值出口+表达式级、宿主层；计划 `plans/2026-09-09-db1-sensitive-redaction.md`） | — |
| | **D1 对象深读** | `2026-09-08-d1-object-drill.md` | **已完成**（2026-09-09 实施：DebugTarget DrillNode/drill 样本（链+环+null）+ Engine `ReadObjectAtPathAsync` 受控递归（depth/limit/沿路径防环 `<cyclic>`）+ 宿主 `debug_object` 工具（`$exception` 前缀特判）+ README/CHANGELOG；本地 commit 34dfa59/cfea258） | P6 求值链✅ |
| | **D2 子进程跟随** | `2026-09-08-d2-child-process.md` | **已完成**（2026-09-09 实施：DebugTarget spawn/sleep 样本 + 宿主 Toolhelp 父子快照助手（kernel32 P/Invoke，零新包）→ `debug_processes` 标注当前会话目标的 .NET 子孙进程链 + 切换引导 + 修正无 CLR 进程误列；Engine 零改动；本地 commit 87a3488/6fc2e69） | — |
| **P2** | **DB2 按名白名单** | `2026-09-08-db2-named-whitelist.md` | **已拍板+计划就绪**（2026-09-09；计划 `plans/2026-09-09-db2-named-whitelist.md`） | debug_variables 面 |
| | **V4 语料断言** | `2026-09-08-v4-copy-guard.md` | **已拍板+计划就绪**（2026-09-09；计划 `plans/2026-09-09-v4-copy-guard.md`） | 随新工具同批补 |
| **P3**（中-大，等前置） | **U1 UI 自动化** | `2026-09-08-u1-ui-automation.md` | **已拍板+计划就绪**（2026-09-09：不需会话/AgentActionLog 护栏/务实成员反查标注 v1/U1 先行；FlaUI 引用姿势线上核实；计划 `plans/2026-09-09-u1-ui-automation.md`） | — |
| | **V1 复验闭环** | `2026-09-08-v1-verify-loop.md` | **已拍板+计划就绪**（2026-09-09：可选 build+产物自动拿取/文件路径/fail-fast/纯断点版先行；计划 `plans/2026-09-09-v1-verify-loop.md`） | 执行按依赖排期 |
| **P4**（spike 前置） | **W3 数据断点** | `2026-09-08-w3-data-breakpoint.md` | **已拍板+计划就绪**（2026-09-09：spike Task0 三分支写死/breakpoint_set dataPath/降级=说明+ROADMAP；计划 `plans/2026-09-09-w3-data-breakpoint.md`） | spike 在 Task0 |
| **远期** | **W2 SetIP** | `2026-09-08-w2-set-ip.md` | **已转 ROADMAP（2026-09-08）** | — |
| | **V2 崩溃 dump** | `2026-09-08-v2-crash-dump.md` | **转远期（2026-09-08 决策，见 ROADMAP）**；退出码增量①随 V3 | V3 |

> 注：UI 自动化条目（上方独立 section）对应总览 U1，两者同源；执行以本总览批次为准。

## UI 自动化主动触发业务操作（2026-09-08 调研，立项前）

> 来源：CoreMes（WPF 产线软件）实证 + 跨框架通用性探讨（agent 主导触发 UI 业务流，对标截图工具/Snipaste「圈选内部元素」能力）。**状态：调研完成；实现技术已定（FlaUI 引用包）；spec 草案已立 `docs/planning/specs/2026-09-08-u1-ui-automation.md`（含 FlaUI API 实查 + UIInspect.MCP 蓝本实读 + 语义闭环设计 + 6 项待拍板）**——立项时按 spec 拍板取舍。

- [ ] **UI 自动化触发（UIA 通用层）**（宿主新组件｜中-大）——**完整技术方案见 spec**（`docs/planning/specs/2026-09-08-u1-ui-automation.md`，工具面 §4.1 已定案）。要点：FlaUI 5.0.0（NuGet stable，net8.0-windows7.0 二进制，net10 向上兼容；net10.0-windows 目标在 main 未发 release——包分发照抄 UIInspect.MCP 的 PackageDownload+HintPath）；目标=整个 .NET 生态 UI 技术栈（.NET Framework 4.x→10 的 WPF/WinForms/WinUI/MAUI、Avalonia 11+），UIA 与被控目标 .NET 版本无关。**工具面（2026-09-08 定案：5 个，v1 做 3 个）**：`ui_find`（窗口/进程+text/type/autoId 条件 → 控件清单，无视觉"眼睛"）+ `ui_invoke`（语义点击，Invoke 优先坐标兜底）+ `ui_wait`（等控件出现/文本变化，点击后状态确认）为 **v1 最小闭环**；`ui_input`（文本框输入）/`ui_pick`（人类 hover 指认兜底）列 v1.5。**语义闭环（差异化）**：反编译读 Command 绑定 → UIA 元素标注语义 → run_to/断点设好 → ui_invoke 点击 → 命中观察 → ui_wait 确认状态变更。**待拍板**（见 spec §6）：ui_* 是否需活动 debug 会话（倾向不需要）、副作用护栏形态、自动语义标注 v1 做不做（倾向不做）。**V4 衔接**：ui_* 落地同批补语料断言。**README 同步**：ui_* 属新增 MCP 工具，落地同 commit 改根 README。

## agent 自动化调试闭环缺口清单（2026-09-08 盘点，按环节立项评估）

> 方法：把 agent 调试当作闭环「观察 → 假设 → 验证 → 修复 → 复验」逐环节对照现有 38 工具找缺口。共同主线：**agent 调试不应止步于"看/停"，要能"改/试/复验/复盘"**。每条含 能力/技术信息/难度/方案，难度标注基于现有代码结构与本地 Externals（dnSpy 等）可查证性；**无把握处已标注「先 spike/查证」**，不臆断。立项顺序按编号；中-大项立项先 spec，小型项先补方案段。**与既有项关系**：UI 自动化（上文条目）= 本清单缺口 U1。

### 观察/假设/验证 环节

- [ ] **W3 数据断点（值变化即停）**（Engine+Session｜大，spike 前置）——**能力**：字段/局部变量值变化时停下。**spec 草案**：`docs/planning/specs/2026-09-08-w3-data-breakpoint.md`。**已查证**：ClrDebug 有两条相关路径——A `CorDebugValue.CreateBreakpoint()`（ValueBreakpoint，挂值对象，受值对象存活/GC/帧约束）+ B `OnDataBreakpoint` 回调（Callback4 已封装）但**未搜到创建端 API**。**spike 必答**：A 对栈上局部是否可行/对对象字段是否有效；B 的创建入口是否在 ICorDebug 新接口或诊断口；两者是否 A 触发→B 通知。**结论三选一**：A 可行（ValueBreakpoint 版）/B 可行（现代版）/不可行→降级条件断点+转 ROADMAP。**立项前置 = spike**。

### 修复/复验 环节（agent「改完 bug 确认修好」的最后一跳）

- [ ] **V1 一键复验闭环**（宿主｜中-大，ROADMAP reverse-skill 闭环落地）——**能力**：agent 改完代码自证修复：重编译→重启（可复现快照）→重跑场景→断言 pass/fail。**spec 草案**：`docs/planning/specs/2026-09-08-v1-verify-loop.md`。**核心设计**：结构化手写场景 JSON（target 启动快照 + 可选 build + steps 数组）+ 断言原语（breakpointHit/evaluate/output/state/noException）+ 宿主 `debug_verify` 编排（同进程直调 Session 不走 MCP 往返）。**关键取舍**：不做录制回放（v1 手写场景，录制 v2 从 AgentActionLog 生成）；fail-fast；编译步建议 v1 含（闭环缺"改码"半环）。**依赖**：debug_launch 可复现 ✅；W1/U1/V3 为增强断言源（纯断点版可先行）。
- [ ] **V2 崩溃现场自动保留（dump + 轨迹）**（Engine+Session+宿主｜中）——**转远期（2026-09-08 决策）**：dump 自动抓取对 agent 代价大（依赖注入 `DOTNET_DbgEnableMiniDump` 环境变量改变目标运行环境；路径 A 抓取时机 spike 不确定），收益边际低（V3 时间线 + 退出码判定已覆盖大部分复盘）。保留事项：① **第一增量「退出码 + 崩溃判定」随 V3 顺手做（已写入 V3 条目顺手项）**（ExitProcess 只发 Exited 无退出码，补 code 到 Reason 成本极小）；② 完整 dump 转 `docs/ROADMAP.md` 远期。spec 草案保留：`docs/planning/specs/2026-09-08-v2-crash-dump.md`（dump 路径查证结论仍有效：.NET 崩溃默认不生成 dump；WER LocalDumps 对 .NET 无效已否决；正解 = 会话内异常停点抓 + `DOTNET_DbgEnableMiniDump=1` 注入）。
- [ ] **V4 修复回归护栏（语料断言）**（宿主测试｜小，ROADMAP reverse-skill 候选）——**能力**：把关键文案/行为契约固化为断言测试防回归。**spec 草案**：`docs/planning/specs/2026-09-08-v4-copy-guard.md`。**技术要点**：只测关键片段（非全匹配，防脆）；断言源直接引 `AppText`/`ToolParameterText` 常量（改文案不同步改测试即红，与常量纪律互补）；新工具落地强制同批补 V4。已有先例：McpSessionConcurrencyTests 等行为级护栏。**建议**：debug_set/debug_object/debug_verify/ui_* 落地时同批补断言；语料可吸收 DebugMCP 停点返回常驻「你找到的是症状还是根因」+ 下一步建议的文案形态（v4 spec 关联行已注，立项时拍板）。

### 现场纵深/环境 环节

- [ ] **D2 子进程/多进程跟随** = **已完成**（2026-09-09，见上方总览表 D2 行）——拍板落点：Toolhelp P/Invoke 落宿主 + 增强 `debug_processes` 全链标注与切换引导（不新增 debug_children、不主动提示）。

### 已评估关闭/远期（防重复立项，一行结论）

- **func-eval 主动调用业务方法** = **关闭**（I 项结论：async/UI/外设方法 func-eval 必死锁；纯函数触发需求未见）。**完整 SetIP/强制返回** = 远期（W2，已转 ROADMAP 2026-09-08，查证结论/触发条件随条目移入）。**崩溃自动 dump** = **远期（2026-09-08 决策）**：agent 代价大（注入 `DOTNET_DbgEnableMiniDump` 改环境）+ spike 不确定，保留退出码/崩溃判定小增量（随 V3），完整 dump 见 ROADMAP。**ClrMD live 内存分析** = ROADMAP 已有（dump 事后分析，live 会话内与 ICorDebug 冲突）。**多调试会话并行** = ROADMAP 已有（Engine 实测干扰）。

## DebugMCP 调研可借鉴点（2026-09-08，源码实读 microsoft/DebugMCP）

> 来源：微软官方 `microsoft/DebugMCP`（493★/MIT，TypeScript VS Code 扩展，把 VS Code 调试器/DAP 包成 MCP）源码实读（本地 `../../Externals/DebuggerExternals/DebugMCP/`）。其架构=借 VS Code 调试器（需源码+launch.json+扩展），与自研引擎路线**互补非替代**——不借鉴其"遥控 IDE"架构，只借鉴 7 项设计（归置见下：2 新待办 / 2 增强参考 / 3 印证）。

### 新独立待办

- [ ] **DB1 变量敏感脱敏层**（宿主/Session｜小-中）——**安全缺口**：agent 读到的 api_key/password/token/JWT/PEM 等敏感值会跨信任边界进 LLM 模型，现状无脱敏。参照 DebugMCP `secretRedaction.ts` 双模式（本地 `../../Externals/DebuggerExternals/DebugMCP/src/utils/secretRedaction.ts`）：按**变量名**（api_key/password/token/…）+ 按**内容模式**（JWT/PEM/AKIA…/ghp_…/Bearer …/Password=…）过滤，null 值不动（缺凭据 bug 仍可调）；`debug_evaluate` 一并覆盖（`os.environ` 是绕过变量级控制的平凡路径——DebugMCP 显式指出）。输出用占位符 + 提示「值已脱敏」。**方案**：Session/宿主渲染层统一走脱敏管线（引擎层不动，渲染时脱敏）。**难度**：小-中。**测试**：脱敏单测（名匹配/内容匹配/嵌套路径/求值绕过四类）。
- [ ] **DB2 变量按名白名单读取**（宿主｜小）——DebugMCP `get_variables_values` 要求显式 `variableNames`（非空/无通配/≤50），配 `list_variable_names`（只列名与类型、零值泄露，参考 `../../Externals/DebuggerExternals/DebugMCP/src/debuggingHandler.ts` `normalizeRequestedNames`/`handleListVariableNames`）。对照：你们 `debug_variables` 全量+分页——加按名过滤模式（省 token、不泄露无关值；未知名反馈不静默）。**方案**：`debug_variables` 加 `names` 参数（逗号分隔白名单；空=现状全量）。**难度**：小。**关联**：与 DB1 脱敏天然配合（按名白名单缩小暴露面）。

### 增强参考（并入已有项，不单独立项）

- **事件驱动等停点五态语义** → 并入 V3/debug_state 增强：DebugMCP `waitForDebugSessionReady` 区分 stopped/attached/terminated/no-session/timeout，且 **attach 到长活进程即成功态**（不会自己停，等栈帧会烧光超时）——对照 R2/R3 的 attach 语义反馈，可借鉴其五态枚举与提示措辞。
- **根因分析检查点（SYMPTOM vs ROOT CAUSE）** → 并入 V4 语料护栏：DebugMCP 在 stop 后返回里常驻提示「你找到的是症状还是根因」+ 建议下一步。你们 V4 语料可吸收该文案形态（放 debug_state 停点反馈或握手引导）。

### 印证（不立项，一行结论）

- **logpoint = 你们的 trace 模式**（P5 已完成）——DebugMCP `add_logpoint` 等价，验证 trace 方向正确。
- **递归展开限深/环检测/预算** → D1 设计已被同款验证（DebugMCP maxDepth=6/maxFields=100/cyclic-reference 占位）——D1 spec 环检测+预算方案可定稿。
- **MCP instructions + skill 分离**（工具 terse 行为、方法学进 SKILL.md 装 `~/.agents/skills/`）→ 印证 ROADMAP 语料/HandshakeFeatureIntro 思路与微软一致。
