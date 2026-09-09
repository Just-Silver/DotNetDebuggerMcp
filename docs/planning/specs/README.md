# Specs（设计文档）目录

> 总览 spec + 分阶段实施计划（decisions D7）。本目录存**设计文档（spec）**；实施计划存 `docs/planning/plans/`。
> Spec 是冻结后供实施计划引用的依据；被替代时加 Superseded 标注，不重写正文。

| 文档 | 内容 | 状态 |
|---|---|---|
| `2026-09-05-overview-design.md` | **总览 spec**：五项目全局架构、命名布局、各层设计原则、线程/事件模型、阶段边界 P1-P5、风险与合规 | **已确认**（用户 2026-09-05 review OK） |
| `2026-09-05-p4-webui.md` | **P4 WebUI 细化 spec**：运行形态双模式、页面布局 v1 核心面、文档模型/行映射、技术集成定稿（BB 10.10.0 + 自研 Monaco 互操作 + 宿主 --web 接线） | **已冻结**（用户 2026-09-05 review OK） |
| `2026-09-07-r5-async-step.md` | **R5 async 状态机单步体验 spec**（agent 反馈 R5）：状态机帧识别 + step-over/out 自动连步滤帧 + step-into 诚实停住 + `debug_stack` 帧名真名化；参考 dnSpy DbgEngineStepperImpl | **Superseded 不实施**（2026-09-07 评审跳过，决策见 ROADMAP v2 候选） |
| `2026-09-07-r6-line-breakpoint-deferred.md` | **R6 行断点延迟解析登记 spec**（agent 反馈 R6）：SourceLineResolver 迁引擎 + 源行延迟项表 + TrackModule 解析补设（sharpdbg 模型，引擎内闭环）；sourcePath 先行，typeName 排除 | **已完成**（2026-09-07 实施，方案 C；Engine 23/Session 90 全过） |
| `2026-09-08-w1-set-value.md` | **W1 现场改写 spec**（宿主 TODO P1）：`debug_set` 停点改值后继续——对象字段/栈上值类型局部/数组元素/引用置 null + 对象重定向；返回原值回显；ClrDebug 写 API 已查证 | **已立项/冻结**（2026-09-09 用户拍板：三层覆盖+对象重定向+按目标类型转换；实施计划 `plans/2026-09-09-w1-set-value.md`） |
| `2026-09-08-v3-timeline.md` | **V3 统一时间线 spec**（宿主 TODO P1）：`debug_timeline` 日志↔事件↔agent 动作按 UTC 归并；事件历史环形缓冲补齐 + 退出码/attach 提示顺手项 | **已立项/冻结**（2026-09-09 用户拍板：500 含 EngineLog+动作入+双轨+顺手项；实施计划 `plans/2026-09-09-v3-timeline.md`） |
| `2026-09-08-db1-sensitive-redaction.md` | **DB1 敏感脱敏 spec**（宿主 TODO P1，参照 DebugMCP secretRedaction）：读值出口按变量名+内容形态双模式脱敏（宿主渲染层，Engine/Session 零改动） | **已立项/冻结**（2026-09-09 用户拍板：读值出口+表达式级、Web 不同步；实施计划 `plans/2026-09-09-db1-sensitive-redaction.md`） |
| `2026-09-08-d1-object-drill.md` | **D1 对象深读 spec**（宿主 TODO P1）：`debug_object` 受控递归下钻对象树（depth/limit/`<cyclic>` 环占位）——原草案单层与 debug_evaluate 重叠，拍板改受控递归 | **已立项/冻结**（2026-09-09 用户拍板：受控递归 v1，depth 默认 2 上限 6；实施计划 `plans/2026-09-09-d1-object-drill.md`） |
| `2026-09-08-d2-child-process.md` | **D2 子进程跟随 spec**（宿主 TODO P1）：debug_processes 标注当前会话目标的 .NET 子孙进程链 + 切换引导（Toolhelp 零依赖路径，Engine 零改动） | **已立项/冻结**（2026-09-09 用户拍板：Toolhelp 宿主 + debug_processes 全链 + 不主动提示；实施计划 `plans/2026-09-09-d2-child-process.md`） |
| `2026-09-08-db2-named-whitelist.md` | **DB2 按名白名单 spec**（宿主 TODO P2）：debug_variables 加 names 白名单（空=全量、≤50、未知名零值反馈） | **已立项/冻结**（2026-09-09 用户拍板：推荐默认语义；实施计划 `plans/2026-09-09-db2-named-whitelist.md`） |
| `2026-09-08-v4-copy-guard.md` | **V4 语料护栏 spec**（宿主 TODO P2）：关键文案/行为契约固化为片段断言；新工具强制同批补 V4 | **已立项/冻结**（2026-09-09 用户拍板：片段 Contains + 强制随新工具 + 只宿主层；实施计划 `plans/2026-09-09-v4-copy-guard.md`） |
| `2026-09-08-u1-ui-automation.md` | **U1 UI 自动化 spec**（宿主 TODO P3）：FlaUI 5.0 + ui_find/ui_invoke/ui_wait 三件套，无视觉文本清单 + 务实成员反查语义标注，与 debug_* 编排 UI 业务流闭环 | **已立项/冻结**（2026-09-09 用户拍板 4 项 + FlaUI 引用姿势线上核实；实施计划 `plans/2026-09-09-u1-ui-automation.md`） |

## 阶段对应关系（decisions D7）

| 阶段 | 主题 | 对应实施计划 |
|---|---|---|
| P1 | 仓库改名与拆分（5 项目骨架 + 反编译代码迁入） | `archive/plans/2026-09-05-p1-rename-and-split.md`（**已完成** ✅ 2026-09-05，已归档） |
| P2 | 动态调试引擎 v1（Engine） | `archive/plans/2026-09-05-p2-engine-v1.md`（**已完成** ✅ 2026-09-05，已归档） |
| P3 | 会话层 + MCP 调试工具面（Session + McpHost） | `archive/plans/2026-09-05-p3-mcp-tools.md`（**已完成** ✅ 2026-09-05，已归档） |
| P4 | WebUI（Web，细节在 P4 前单独细化） | `archive/plans/2026-09-05-p4-1-documentservice.md`（**已完成** ✅，已归档）+ `plans/2026-09-05-p4-2-webui.md`（**进行中**） |
| P5 | 打磨与发布 | `plans/...-p5-release.md`（未写） |
