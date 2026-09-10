# Specs（设计文档）目录

> 总览 spec + 分阶段实施计划（decisions D7）。本目录存**设计文档（spec）**；实施计划存 `docs/planning/plans/`。
> Spec 是冻结后供实施计划引用的依据；被替代时加 Superseded 标注，不重写正文。

| 文档 | 内容 | 状态 |
|---|---|---|
| `2026-09-05-overview-design.md` | **总览 spec**：五项目全局架构、命名布局、各层设计原则、线程/事件模型、阶段边界 P1-P5、风险与合规 | **已确认**（用户 2026-09-05 review OK） |
| `2026-09-05-p4-webui.md` | **P4 WebUI 细化 spec**：运行形态双模式、页面布局 v1 核心面、文档模型/行映射、技术集成定稿（BB 10.10.0 + 自研 Monaco 互操作 + 宿主 --web 接线） | **已冻结**（用户 2026-09-05 review OK） |
| `2026-09-07-r5-async-step.md` | **R5 async 状态机单步体验 spec**（agent 反馈 R5）：状态机帧识别 + step-over/out 自动连步滤帧 + step-into 诚实停住 + `debug_stack` 帧名真名化；参考 dnSpy DbgEngineStepperImpl | **Superseded 不实施**（2026-09-07 评审跳过，决策见 ROADMAP v2 候选） |
| `2026-09-07-r6-line-breakpoint-deferred.md` | **R6 行断点延迟解析登记 spec**（agent 反馈 R6）：SourceLineResolver 迁引擎 + 源行延迟项表 + TrackModule 解析补设（sharpdbg 模型，引擎内闭环）；sourcePath 先行，typeName 排除 | **已完成**（2026-09-07 实施，方案 C；Engine 23/Session 90 全过） |
| `2026-09-08-w1-set-value.md` | **W1 现场改写 spec**（宿主 TODO P1）：`debug_set` 停点改值后继续——对象字段/栈上值类型局部/数组元素/引用置 null + 对象重定向；返回原值回显；ClrDebug 写 API 已查证 | **已实施**（2026-09-09：spike 支持矩阵实测落定 spec §7 → Engine `SetPathValueAsync` + Session `WriteValueParser` + 宿主 `debug_set` 全落地；实施计划 `plans/2026-09-09-w1-set-value.md`） |
| `2026-09-08-v3-timeline.md` | **V3 统一时间线 spec**（宿主 TODO P1）：`debug_timeline` 日志↔事件↔agent 动作按 UTC 归并；事件历史环形缓冲补齐 + 退出码/attach 提示顺手项 | **已立项/冻结**（2026-09-09 用户拍板：500 含 EngineLog+动作入+双轨+顺手项；实施计划 `plans/2026-09-09-v3-timeline.md`） |
| `2026-09-08-db1-sensitive-redaction.md` | **DB1 敏感脱敏 spec**（宿主 TODO P1，参照 DebugMCP secretRedaction）：读值出口按变量名+内容形态双模式脱敏（宿主渲染层，Engine/Session 零改动） | **已实施**（2026-09-10：宿主 `SensitiveValueRedactor` 规则全集 + RenderVariable 递归 / debug_evaluate 标量+children / trace 变量行三出口挂接 + 占位符与计数/单次提示 + DebugTarget Bag 敏感字段 e2e 样本；实施计划 `plans/2026-09-09-db1-sensitive-redaction.md`） |
| `2026-09-08-d1-object-drill.md` | **D1 对象深读 spec**（宿主 TODO P1）：`debug_object` 受控递归下钻对象树（depth/limit/`<cyclic>` 环占位）——原草案单层与 debug_evaluate 重叠，拍板改受控递归 | **已实施**（2026-09-09：拍板受控递归 v1 depth 默认 2 上限 6 → DebugTarget DrillNode/drill 样本 → Engine `ReadObjectAtPathAsync` 受控递归展开器 → 宿主 `debug_object` 工具全落地；实施计划 `plans/2026-09-09-d1-object-drill.md`） |
| `2026-09-08-d2-child-process.md` | **D2 子进程跟随 spec**（宿主 TODO P1）：debug_processes 标注当前会话目标的 .NET 子孙进程链 + 切换引导（Toolhelp 零依赖路径，Engine 零改动） | **已实施**（2026-09-09：Toolhelp 宿主快照助手 `ProcessTreeSnapshot`（kernel32 P/Invoke，单快照全表）+ `debug_processes` 全链标注（子/孙/曾孙父链 + 切换引导 + 单活动会话边界）+ DebugTarget spawn/sleep 样本；另修正 debug_processes 只列实际探测到 CLR 的进程；实施计划 `plans/2026-09-09-d2-child-process.md`） |
| `2026-09-08-db2-named-whitelist.md` | **DB2 按名白名单 spec**（宿主 TODO P2）：debug_variables 加 names 白名单（空=全量、≤50、未知名零值反馈） | **已实施**（2026-09-10：`debug_variables` 增 `names` 参数——宿主渲染层 SplitNames/MatchName 过滤（忽略大小写、slotN/`$exception` 展示名、同名跨作用域、≤50 拒绝、未知名附可用名零值反馈），命中项照常走 DB1 脱敏，Engine/Session 零改动；实施计划 `plans/2026-09-09-db2-named-whitelist.md`） |
| `2026-09-08-v4-copy-guard.md` | **V4 语料护栏 spec**（宿主 TODO P2）：关键文案/行为契约固化为片段断言；新工具强制同批补 V4 | **已实施**（2026-09-10：宿主测试 `AgentCopyGuardTests` 反射读 debug_* 工具 `[Description]` 断言关键引导片段（7 计划对 + W1/V3/D1 补录 4 行）+ `DebugMcpToolsTests` 既有 e2e 补 wait「最近停点」/step「debug_wait」文案断言；Engine/Session/宿主零代码改动，纯测试护栏；实施计划 `plans/2026-09-09-v4-copy-guard.md`） |
| `2026-09-08-u1-ui-automation.md` | **U1 UI 自动化 spec**（宿主 TODO P3）：FlaUI 5.0 + ui_find/ui_invoke(action)/ui_wait/ui_scroll 四件套，无视觉文本清单 + 务实成员反查语义标注，与 debug_* 编排 UI 业务流闭环 | **已实施**（2026-09-10：宿主 FlaUI 引用落地（PackageDownload+HintPath，host TFM 保持 net10.0）→ `UiAutomationService`（单 UIA3 实例 + 5s 双层超时护栏/串行锁/find 缓存 + click=Invoke 优先·物理左键兜底 + 物理右键/双击/滚轮）+ `UiSemanticResolver`（PEReader 成员反查，apphost→同名 dll 元数据）→ `ui_find`/`ui_invoke(action)`/`ui_wait`/`ui_scroll` 工具 + WinForms `UiSampleApp` 测试目标（generate-testdata.ps1 产出）+ README/CHANGELOG + V4 语料补录；Engine/Session 零改动；实施计划 `plans/2026-09-09-u1-ui-automation.md`）；**工具面（`ui_invoke`/`ui_scroll`）已被 `2026-09-10-u1a-uia-only-ui-automation.md` 取代** |
| `2026-09-10-u1a-uia-only-ui-automation.md` | **U1A UI 自动化全 UIA 化 spec**（宿主 U1 改版）：弃用一切物理输入（删 `ui_invoke`/`ui_scroll`），改语义动词 `ui_action`（invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate）+ `ui_input` + `ui_get` + `ui_wait`（事件化）；不抢前台、必要时仅还原窗口；元素条件缓存+重解析、虚拟化实体化；并入 `debug_verify`（uiAction/uiAssert 步骤） | **已立项/冻结**（2026-09-10 用户拍板三项：语义动词化 `ui_action` / 含 verify 集成 / 不抢前台仅还原窗口；取代 U1 工具面） |
| `2026-09-08-v1-verify-loop.md` | **V1 复验闭环 spec**（宿主 TODO P3）：debug_verify 场景文件→重编译(可选,产物自动拿取)→重跑→断言 PASS/FAIL，agent 自证修复 | **已实施**（2026-09-10：宿主 `VerifyScenario`（JSON 模型+中文解析）+ `VerifyBuildRunner`（dotnet build 默认输出 + `-getProperty:TargetPath` 产物自动拿取/排空/超时/文件名校验）+ `VerifyService`（步骤翻译=同进程直调 Session、断言原语、fail-fast、AgentActionLog）+ `debug_verify` 工具；e2e：DebugTarget 纯断点+evaluate+output+state PASS、期望错值/断点永不命中 FAIL、临时 console 工程 build 自动拿产物 PASS/坏工程编译摘要 FAIL/文件名不一致中文；Engine/Session 零改动；SensitiveValueRedactor 移入 Services 共享层（verify 断言输出同受 DB1 脱敏）；实施计划 `plans/2026-09-09-v1-verify-loop.md`） |
| `2026-09-08-w3-data-breakpoint.md` | **W3 数据断点 spec**（宿主 TODO P4）：字段/局部值变化即停；ClrDebug A/B 两路径 + spike 三问 | **已评估 → 降级收尾**（2026-09-10 spike 实测定案：A `CreateBreakpoint()` 恒 E_NOTIMPL、B 无创建端 → 均不可行，按计划 TaskD 转 ROADMAP 零 Engine 代码；spec §2 结论 + 根 README 指引 + ROADMAP 条目 + 宿主 TODO W3=已评估转 ROADMAP；实施计划 `plans/2026-09-09-w3-data-breakpoint.md`） |

## 阶段对应关系（decisions D7）

| 阶段 | 主题 | 对应实施计划 |
|---|---|---|
| P1 | 仓库改名与拆分（5 项目骨架 + 反编译代码迁入） | `archive/plans/2026-09-05-p1-rename-and-split.md`（**已完成** ✅ 2026-09-05，已归档） |
| P2 | 动态调试引擎 v1（Engine） | `archive/plans/2026-09-05-p2-engine-v1.md`（**已完成** ✅ 2026-09-05，已归档） |
| P3 | 会话层 + MCP 调试工具面（Session + McpHost） | `archive/plans/2026-09-05-p3-mcp-tools.md`（**已完成** ✅ 2026-09-05，已归档） |
| P4 | WebUI（Web，细节在 P4 前单独细化） | `archive/plans/2026-09-05-p4-1-documentservice.md`（**已完成** ✅，已归档）+ `plans/2026-09-05-p4-2-webui.md`（**进行中**） |
| P5 | 打磨与发布 | `plans/...-p5-release.md`（未写） |
