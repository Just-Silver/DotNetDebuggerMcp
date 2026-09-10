# TODO（DotNetDebuggerMcp 宿主近期待办）

> 近期待办，完成一项删一项；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

> **2026-09-08 清理归档**：已完成历史已删除（细节见 git log 与 `docs/planning/specs/`）：P1-P9 动态调试体验升级、R1-R8 agent 反馈修复、本机 DebugMcpToolsTests 竞态排查、A-O CoreMes 实证改进。以下仅保留**进行中/立项前**条目。

## 执行推进顺序总览（2026-09-08 排定，按批推进）

> 每条 TODO 含完整实现所需关键信息 + spec 路径；**中-大项先按 spec 拍板待办项再动码**；新工具落地须同步根 README 并补 CHANGELOG `[Unreleased]` 段（下一批工具落地时同步）。**2026-09-09：全部待办（W1/V3/DB1/D1/D2/DB2/V4/U1/V1/W3）已逐项拍板转正 spec + 实施计划就绪**（`docs/planning/plans/2026-09-09-*.md`），按批次审查后实施；W2/V2 已转 ROADMAP 不在待办。**2026-09-10：W1/V3/DB1/D1/D2/DB2/V4/U1/V1 已实施**（见下表状态）；**W3 数据断点 spike 实测定案不可行（A `CreateBreakpoint` 恒 E_NOTIMPL / B 无创建端）→ 按计划降级转 ROADMAP（2026-09-10，零 Engine 代码，见下表 P4 行）**。

| 批次 | 项 | spec | 状态 | 依赖 |
|---|---|---|---|---|
| **P1**（独立/低成本，先做） | **W1 现场改写** | `2026-09-08-w1-set-value.md` | **已完成**（2026-09-09 实施：DebugTarget WriteProbe + spike 支持矩阵定案 spec §7 → Engine `SetPathValueAsync`/readonly 拒绝 → Session `WriteValueParser` → 宿主 `debug_set` 工具 + README/CHANGELOG；本地 commit 1a80a09/1c206f5/3c956bd/3775627） | — |
| | **V3 统一时间线** | `2026-09-08-v3-timeline.md` | **已完成**（2026-09-09 实施：事件历史环形 500 + DebugTimeline 三源归并 + debug_timeline 工具 + 退出码/attach 顺手项；本地 commit 12456d8/d10ade5/eff9c29/a695658） | P1 时间戳✅ |
| | **DB1 敏感脱敏层** | `2026-09-08-db1-sensitive-redaction.md` | **已完成**（2026-09-10 实施：宿主 `SensitiveValueRedactor`（DebugMCP 规则双模式全集，名归一化 exact-match + 内容正则）+ 读值出口挂接（RenderVariable 递归/debug_evaluate 标量+children/trace 变量行）+ 占位符+计数/单次提示 + DebugTarget Bag 增 Password/Token e2e 样本；Engine/Session 零改动；本地 commit 8485695/ac455bf） | — |
| | **D1 对象深读** | `2026-09-08-d1-object-drill.md` | **已完成**（2026-09-09 实施：DebugTarget DrillNode/drill 样本（链+环+null）+ Engine `ReadObjectAtPathAsync` 受控递归（depth/limit/沿路径防环 `<cyclic>`）+ 宿主 `debug_object` 工具（`$exception` 前缀特判）+ README/CHANGELOG；本地 commit 34dfa59/cfea258） | P6 求值链✅ |
| | **D2 子进程跟随** | `2026-09-08-d2-child-process.md` | **已完成**（2026-09-09 实施：DebugTarget spawn/sleep 样本 + 宿主 Toolhelp 父子快照助手（kernel32 P/Invoke，零新包）→ `debug_processes` 标注当前会话目标的 .NET 子孙进程链 + 切换引导 + 修正无 CLR 进程误列；Engine 零改动；本地 commit 87a3488/6fc2e69） | — |
| **P2** | **DB2 按名白名单** | `2026-09-08-db2-named-whitelist.md` | **已完成**（2026-09-10 实施：`debug_variables` 增 `names` 白名单参数——宿主渲染层过滤（SplitNames/MatchName/BuildVariablesLines），空=全量/≤50 拒绝/未知名零值反馈/同名跨作用域；与 DB1 脱敏叠加命中对象仍逐字段脱敏；Engine/Session 零改动；本地 commit da3df05） | — |
| | **V4 语料断言** | `2026-09-08-v4-copy-guard.md` | **已完成**（2026-09-10 实施：宿主测试 `AgentCopyGuardTests` 反射读 debug_* 工具 `[Description]` 断言关键引导片段（7 计划对 + W1/V3/D1 已落地工具补录 4 行），只断片段 Contains 不挂 AppServices 串行；`DebugMcpToolsTests` 既有 e2e 补 wait「最近停点」/step「debug_wait」返回文案断言（launch「工作目录」/异常 `$exception` 断言先前批次已带）；负向验证：人为删 DebugStep 描述「debug_wait」→ 断言即红。Engine/Session/宿主零代码改动，纯测试护栏） | 随新工具同批补 |
| **P3**（中-大，等前置） | **U1 UI 自动化** | `2026-09-08-u1-ui-automation.md` | **已完成**（2026-09-10 实施：宿主 `UiAutomationService`（FlaUI UIA3 单实例 + 5s 双层超时护栏/串行锁/index 缓存/Invoke 优先+物理左键兜底/rightClick·doubleClick·Scroll 物理鼠标）+ `UiSemanticResolver`（PEReader 成员反查，apphost 同名 dll 兜底）+ `ui_find`/`ui_invoke(action)`/`ui_wait`/`ui_scroll` 四工具 + WinForms `UiSampleApp` 测试目标（generate-testdata.ps1 产出，源码入库）+ README/CHANGELOG/V4 语料补录；Engine/Session 零改动；本地 commit 539a3fb/99e0b41/7a5bf0d/3289fb6） | — |
| | **V1 复验闭环** | `2026-09-08-v1-verify-loop.md` | **已完成**（2026-09-10 实施：宿主 `VerifyScenario`（JSON 模型+中文解析）+ `VerifyBuildRunner`（dotnet build 默认输出 + `-getProperty:TargetPath` 产物自动拿取）+ `VerifyService`（步骤翻译=同进程直调 Session、断言原语、fail-fast、AgentActionLog）+ `debug_verify` 工具；e2e：DebugTarget 纯断点+evaluate+output+state PASS / 期望错值·断点永不命中 FAIL / build 自动拿产物 PASS·坏工程编译摘要 FAIL·文件名不一致中文；README/CHANGELOG/V4 语料补录；SensitiveValueRedactor 移入 Services 共享层；Engine/Session 零改动；本地 commit 72f9a0a/0e9b3e5/e281a4e 等） | — |
| **P4**（spike 前置） | **W3 数据断点** | `2026-09-08-w3-data-breakpoint.md` | **已评估转 ROADMAP**（2026-09-10 spike 实测定案：A `CreateBreakpoint()` 恒 E_NOTIMPL（运行时未实现）、B 无创建端 → A/B 均不可行，按计划 TaskD 降级零 Engine 代码；spec §2 结论 + 根 README 指引 + ROADMAP 条目；计划 `plans/2026-09-09-w3-data-breakpoint.md` Task0/TaskD） | spike 已实测 |
| **远期** | **W2 SetIP** | `2026-09-08-w2-set-ip.md` | **已转 ROADMAP（2026-09-08）** | — |
| | **V2 崩溃 dump** | `2026-09-08-v2-crash-dump.md` | **转远期（2026-09-08 决策，见 ROADMAP）**；退出码增量①随 V3 | V3 |

> 注：UI 自动化条目（上方独立 section）对应总览 U1，两者同源；执行以本总览批次为准。

## UI 自动化主动触发业务操作（U1，**已完成** 2026-09-10，移总览批次历史）

> 来源：CoreMes（WPF 产线软件）实证 + 跨框架通用性探讨（agent 主导触发 UI 业务流，对标截图工具/Snipaste「圈选内部元素」能力）。实现已完成（spec `2026-09-08-u1-ui-automation.md` + 计划 `plans/2026-09-09-u1-ui-automation.md`）：四工具落地 + UiSampleApp 测试目标 + README/CHANGELOG/V4 补录，提交见总览表 P3/U1 行；`ui_input`/`ui_pick`/横向与自动滚动列 v1.5。**2026-09-10：工具面（`ui_invoke`/`ui_scroll`）已被 U1A 全 UIA 化取代（见下方 follow-up U1A 条目）——U1 连接/语义标注设计保留。**

- [x] **UI 自动化触发（UIA 通用层）**（宿主新组件｜中-大）——实施于 2026-09-10（详总览 P3/U1 行）。遗留（spec §4.1 排期）：`ui_input`（ValuePattern 输入）/`ui_pick`（人类指认）/横向滚动/ScrollPattern 自动滚动列 **v1.5**。

## agent 自动化调试闭环缺口清单（2026-09-08 盘点，按环节立项评估）

> 方法：把 agent 调试当作闭环「观察 → 假设 → 验证 → 修复 → 复验」逐环节对照现有 38 工具找缺口。共同主线：**agent 调试不应止步于"看/停"，要能"改/试/复验/复盘"**。每条含 能力/技术信息/难度/方案，难度标注基于现有代码结构与本地 Externals（dnSpy 等）可查证性；**无把握处已标注「先 spike/查证」**，不臆断。立项顺序按编号；中-大项立项先 spec，小型项先补方案段。**与既有项关系**：UI 自动化（上文条目）= 本清单缺口 U1。

### 观察/假设/验证 环节

- [ ] **W3 数据断点（值变化即停）**（Engine+Session｜大，spike 前置）——**能力**：字段/局部变量值变化时停下。**spec 草案**：`docs/planning/specs/2026-09-08-w3-data-breakpoint.md`。**已查证**：ClrDebug 有两条相关路径——A `CorDebugValue.CreateBreakpoint()`（ValueBreakpoint，挂值对象）+ B `OnDataBreakpoint` 回调（Callback4 已封装）但**未搜到创建端 API**。**已评估转 ROADMAP（2026-09-10 spike 实测定案）**：真实 attach 停点对活局部/活字段值对象调 `CreateBreakpoint()` 恒抛 `E_NOTIMPL`（.NET 源码 `divalue.cpp CordbValue::CreateBreakpoint => return E_NOTIMPL`，非值对象存活问题）；B 无创建端、回调全程零触发（配套硬件寄存器机制，VS 自有非公开口）。**结论**：A/B 均不可行 → 按计划 TaskD 降级，**不写 Engine/宿主代码**；spec §2 结论 + 根 README 指引 + ROADMAP 条目；「字段被莫名改错」场景用已知写入点条件断点替代。**触发条件（若未来再评估）**：ICorDebug/诊断口暴露数据断点创建 API。

### 修复/复验 环节（agent「改完 bug 确认修好」的最后一跳）

- [x] **V1 一键复验闭环**（宿主｜中-大，ROADMAP reverse-skill 闭环落地）——**已完成**（2026-09-10，见上方总览表 V1 行）——**能力**：agent 改完代码自证修复：重编译（可选）→重启（可复现快照）→重跑场景→断言 pass/fail。**核心设计**：结构化手写场景 JSON（target 启动快照 + 可选 build + steps 数组）+ 断言原语（breakpointHit/evaluate/output/state/noException）+ 宿主 `debug_verify` 编排（同进程直调 Session 不走 MCP 往返）。**关键取舍**：不做录制回放（v1 手写场景，录制 v2 从 AgentActionLog 生成）；fail-fast；编译步 v1 含（闭环缺"改码"半环已补）。遗留：ui.*/set 步骤类型在 U1/W1 能力就绪后按预留契约补实现。
- [ ] **V2 崩溃现场自动保留（dump + 轨迹）**（Engine+Session+宿主｜中）——**转远期（2026-09-08 决策）**：dump 自动抓取对 agent 代价大（依赖注入 `DOTNET_DbgEnableMiniDump` 环境变量改变目标运行环境；路径 A 抓取时机 spike 不确定），收益边际低（V3 时间线 + 退出码判定已覆盖大部分复盘）。保留事项：① **第一增量「退出码 + 崩溃判定」随 V3 顺手做（已写入 V3 条目顺手项）**（ExitProcess 只发 Exited 无退出码，补 code 到 Reason 成本极小）；② 完整 dump 转 `docs/ROADMAP.md` 远期。spec 草案保留：`docs/planning/specs/2026-09-08-v2-crash-dump.md`（dump 路径查证结论仍有效：.NET 崩溃默认不生成 dump；WER LocalDumps 对 .NET 无效已否决；正解 = 会话内异常停点抓 + `DOTNET_DbgEnableMiniDump=1` 注入）。
- [x] **V4 修复回归护栏（语料断言）**（宿主测试｜小，ROADMAP reverse-skill 候选）——**已完成**（2026-09-10，见上方总览表 V4 行）——**能力**：把关键文案/行为契约固化为断言测试防回归。**spec 草案**：`docs/planning/specs/2026-09-08-v4-copy-guard.md`。**技术要点**：只测关键片段（非全匹配，防脆）；断言源直接引 `AppText`/`ToolParameterText` 常量（改文案不同步改测试即红，与常量纪律互补）；新工具落地强制同批补 V4。已有先例：McpSessionConcurrencyTests 等行为级护栏。**建议**：debug_set/debug_object/debug_verify/ui_* 落地时同批补断言；语料可吸收 DebugMCP 停点返回常驻「你找到的是症状还是根因」+ 下一步建议的文案形态（v4 spec 关联行已注，立项时拍板）。

### 现场纵深/环境 环节

- [ ] **D2 子进程/多进程跟随** = **已完成**（2026-09-09，见上方总览表 D2 行）——拍板落点：Toolhelp P/Invoke 落宿主 + 增强 `debug_processes` 全链标注与切换引导（不新增 debug_children、不主动提示）。

### 已评估关闭/远期（防重复立项，一行结论）

- **func-eval 主动调用业务方法** = **关闭**（I 项结论：async/UI/外设方法 func-eval 必死锁；纯函数触发需求未见）。**完整 SetIP/强制返回** = 远期（W2，已转 ROADMAP 2026-09-08，查证结论/触发条件随条目移入）。**崩溃自动 dump** = **远期（2026-09-08 决策）**：agent 代价大（注入 `DOTNET_DbgEnableMiniDump` 改环境）+ spike 不确定，保留退出码/崩溃判定小增量（随 V3），完整 dump 见 ROADMAP。**数据断点（W3）** = **远期（2026-09-10 spike 实测转 ROADMAP）**：A `CorDebugValue.CreateBreakpoint` 恒 E_NOTIMPL、B 无 ICorDebug 创建端——A/B 均不可行，用已知写入点条件断点替代，详见 ROADMAP「近期评估转远期」节。**ClrMD live 内存分析** = ROADMAP 已有（dump 事后分析，live 会话内与 ICorDebug 冲突）。**多调试会话并行** = ROADMAP 已有（Engine 实测干扰）。

## DebugMCP 调研可借鉴点（2026-09-08，源码实读 microsoft/DebugMCP）

> 来源：微软官方 `microsoft/DebugMCP`（493★/MIT，TypeScript VS Code 扩展，把 VS Code 调试器/DAP 包成 MCP）源码实读（本地 `../../Externals/DebuggerExternals/DebugMCP/`）。其架构=借 VS Code 调试器（需源码+launch.json+扩展），与自研引擎路线**互补非替代**——不借鉴其"遥控 IDE"架构，只借鉴 7 项设计（归置见下：2 新待办 / 2 增强参考 / 3 印证）。

### 新独立待办

- [x] **DB1 变量敏感脱敏层**（宿主｜小-中）——**已完成**（2026-09-10，见总览表 DB1 行）——**安全缺口**：agent 读到的 api_key/password/token/JWT/PEM 等敏感值会跨信任边界进 LLM 模型，现状无脱敏。参照 DebugMCP `secretRedaction.ts` 双模式（本地 `../../Externals/DebuggerExternals/DebugMCP/src/utils/secretRedaction.ts`）：按**变量名**（api_key/password/token/…）+ 按**内容模式**（JWT/PEM/AKIA…/ghp_…/Bearer …/Password=…）过滤，null 值不动（缺凭据 bug 仍可调）；`debug_evaluate` 一并覆盖（`os.environ` 是绕过变量级控制的平凡路径——DebugMCP 显式指出）。输出用占位符 + 提示「值已脱敏」。**方案**：Session/宿主渲染层统一走脱敏管线（引擎层不动，渲染时脱敏）。**难度**：小-中。**测试**：脱敏单测（名匹配/内容匹配/嵌套路径/求值绕过四类）。
- [x] **DB2 变量按名白名单读取**（宿主｜小）——**已完成**（2026-09-10，见总览表 DB2 行）——DebugMCP `get_variables_values` 要求显式 `variableNames`（非空/无通配/≤50），配 `list_variable_names`（只列名与类型、零值泄露，参考 `../../Externals/DebuggerExternals/DebugMCP/src/debuggingHandler.ts` `normalizeRequestedNames`/`handleListVariableNames`）。对照：你们 `debug_variables` 全量+分页——加按名过滤模式（省 token、不泄露无关值；未知名反馈不静默）。**方案**：`debug_variables` 加 `names` 参数（逗号分隔白名单；空=现状全量）。**难度**：小。**关联**：与 DB1 脱敏天然配合（按名白名单缩小暴露面）。

### 增强参考（并入已有项，不单独立项）

- **事件驱动等停点五态语义** → 并入 V3/debug_state 增强：DebugMCP `waitForDebugSessionReady` 区分 stopped/attached/terminated/no-session/timeout，且 **attach 到长活进程即成功态**（不会自己停，等栈帧会烧光超时）——对照 R2/R3 的 attach 语义反馈，可借鉴其五态枚举与提示措辞。
- **根因分析检查点（SYMPTOM vs ROOT CAUSE）** → 并入 V4 语料护栏：DebugMCP 在 stop 后返回里常驻提示「你找到的是症状还是根因」+ 建议下一步。你们 V4 语料可吸收该文案形态（放 debug_state 停点反馈或握手引导）。

### 印证（不立项，一行结论）

- **logpoint = 你们的 trace 模式**（P5 已完成）——DebugMCP `add_logpoint` 等价，验证 trace 方向正确。
- **递归展开限深/环检测/预算** → D1 设计已被同款验证（DebugMCP maxDepth=6/maxFields=100/cyclic-reference 占位）——D1 spec 环检测+预算方案可定稿。
- **MCP instructions + skill 分离**（工具 terse 行为、方法学进 SKILL.md 装 `~/.agents/skills/`）→ 印证 ROADMAP 语料/HandshakeFeatureIntro 思路与微软一致。

## 实施后 follow-up（2026-09-10，review 循环收集，低优先）

- [x] **U1A 全 UIA 化（2026-09-10 实现完成，远程 CI 已签收）**：`docs/planning/specs/2026-09-10-u1a-uia-only-ui-automation.md`（取代 U1 工具面）→ 计划 `docs/planning/plans/2026-09-10-u1a-uia-only-ui-automation.md` 已实施：删 `ui_invoke`/`ui_scroll`、增 `ui_action`/`ui_input`/`ui_get`、`Services/Ui/` 组件化、`ui_find` 能力清单、`ui_wait` 事件化、去一切物理输入、verify `uiAction`/`uiAssert`；Engine/Session 零改动（本地 commit，未合并/未 push）。修复波 + re-review Approved（R1 源码+IL 双扫描实证非恒真、R4 locator 身份匹配修复）。**远程 CI（GitHub Actions windows-latest）run 34442360727：675/675 通过、0 警告；期间据 CI 实证修出 `ui_input` 写后读回确认（ValuePattern 静默 no-op 自动退 RangeValue、只读即终止）产品缺陷**。
- [x] **U1A UI e2e 复验（2026-09-10 已完成）**：`DebugUiToolsTests`/`DebugVerifyToolTests` UI 例已在远程 CI（非交互桌面）整跑通过（675/675）。开发机 UIA 全局 5s 超时属本机环境问题，不影响 CI；测试自带 R2「基线静默才强判」（交互桌面检出外部活动时 Skip）。遗留观察项（低优先）：① locator 严格 Name 全等使「UIA Name 随内容变化」控件同 index 二次操作判 stale（既定契约）；② `ui_wait` 释放 gate 后 `window` 跨操作复用（降级轮询，无崩溃证据）。

- [ ] **D2 根因下沉 Engine**：dbgshim `EnumerateCLRs` 对无 CLR 进程返回 S_OK+空枚举，`ClrProcessFinder` 把本机全部非 .NET 进程记为 `CLR <unknown>`（Engine 注释语义与实测不符）；D2 在宿主按 `ClrVersion != "<unknown>"` 过滤绕过（review Ruling Accept + CHANGELOG Fixed）。**正确修法**：Engine `ClrProcessFinder.List` 改判「枚举 0 项跳过」而非返回 `<unknown>` 哨兵，补 Engine 单测；宿主过滤随之可去。
- [x] **W1 收口（review deferred minors）**：README/DebugSetTool Description「枚举给底层整数值」口径与 enum 对象字段 v1 实测降级不一致，统一为「枚举仅底层 GenericValue 形态可写整数值，enum 对象字段 v1 降级」；`WritePathTests` hex 注释校正（isHex 同时豁免后缀/科学计数误判、带符号 hex 工具面不可达）；引擎 `ParseCharBytes` 单引号路径注记经工具不可达（防御性保留）。**终审 fix wave 一并补齐 debug_set 回显值脱敏（DB1 出口）。**
- [ ] **D1 措辞小项**：合成标量/数组目标返回头「对象」、空 children 兜底文案与 Display 重复等（reviewer Minor）。
