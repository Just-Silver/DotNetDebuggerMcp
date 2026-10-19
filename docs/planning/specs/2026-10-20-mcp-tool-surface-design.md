# MCP 工具面设计：以 agent 可预测性为目标的重构

<!-- 职责：定义 MCP 工具面（工具名/参数名/必填项/合并/缺陷修复）的重构方案与验收口径；不写：实施步骤与任务拆分（见 plans/）、历史决策理由（见 decisions.md） -->

- 日期：2026-10-20
- 状态：已实施（2026-10-20；42 个工具、required 全空、JSON 34 KB）
- 适用范围：`src/DotNetDebuggerMcp/`（工具面）、`Configuration/`（文本与握手）、`tests/`（快照与 e2e）、根 `README.md`
- 前置实测：本方案全部结论来自一次真实消费体验（AutoNikke.dll 静态分析 + DebugTarget 动态调试 + UI/截图），调用 47/53 个工具，约 60 次调用、17 次返工。

## 1. 问题：数量不是主因

实测基线（`--features=decompile,debug,ui,screenshot`）：

| 指标 | 数值 |
|---|---|
| 工具数 | 53 |
| `tools/list` 完整 JSON | 70 KB |
| 工具描述正文 | 11 871 字符 |
| 参数描述 | 10 025 字符 |
| 常驻估算 | 1.8–2.4 万 token |

体验结论（推翻直觉假设）：

1. **53 个名字从未造成"选哪个"的犹豫**。唯一一次犹豫是 `call_chain` / `call_graph`，原因是方向没有进入名字。
2. **17 次返工全部来自"名字不可预测"**：参数名与直觉不符 11 次、工具名与语义不符 4 次、参数集不一致 2 次。
3. **统一命名能消除返工**：`hierarchy` / `dependencies` / `interface_usage` / `generic_instantiations` 全部收 `typeName`，学到一次后不再错。
4. **描述写得再全也救不了猜参数**：`call_graph` 描述已写明"与 call_chain 方向相反"，`debug_verify` 已写"格式见 README"，但 agent 只在踩坑后才读——1.19 万字符在调用前无法内化。

因此本设计的优先级是 **可预测性 > 数量 > 描述长度**。

## 2. 目标 / 非目标 / 反目标

**目标**

- 同一语义的参数在所有工具里只有一个名字；agent 学一次即可用到底。
- 必填参数归零（全部带默认值），所有错误回到中文提示。
- 工具数 53 → 42；`tools/list` 常驻 token 降到约 1.2 万。
- 修复 7 个实测缺陷（§6）。
- 建立"零返工"的可重复验收方法。

**非目标**

- 不做动态工具面（attach 前后 `tools/list_changed` 推送）。客户端支持度不一，收益不足以承担复杂度。
- 不改 CLI 参数（`-t`/`-mn`/`-dbg` 等）；CLI 与 MCP 参数名本就独立。
- 不重写全部描述，只做 §7 的三段式与去重。

**反目标**

- 合并成 `debug(action=...)` 大杂烩：工具名本身是免费的动作清单，毁掉它 agent 必须读 schema 才知道能做什么。
- 为省 token 牺牲输出质量（分页、行号、头部信息块不动）。
- 把"低频"当"可删"：能力消失比多一个名字更糟。

## 3. 已拍板的决策

| # | 决策 | 来源 |
|---|---|---|
| 1 | 工具名不是对外承诺，破坏性改名可随 minor | 用户 2026-10-20 |
| 2 | 目标工具数 42（无损下限），不为更少而牺牲"一调用一答案" | 本设计 |
| 3 | 不做动态工具面 | 本设计 |
| 4 | 优先修缺陷，其次改名/合并，最后动描述 | 实测结论 |

## 4. 命名规范（P0）

### 4.1 参数角色 → 唯一名字

| 角色 | 唯一名字 | 备注 |
|---|---|---|
| 程序集/目标文件路径 | `assembly` | 已有工具全部遵守 |
| 类型全名 | `typeName` | 已有工具全部遵守 |
| 成员名（可子串） | `memberName` | |
| 方法元数据 token | `token` | 字段 token 单独用 `fieldToken` |
| 模块短名 | `moduleName` | |
| 源码/反编译行号 | `line` | |
| IL 偏移 | `ilOffset` | |
| 断点 id | `id` | 输出同步为 `id=N` |
| 输出目录 | `outputDir` | |
| 子串搜索 | `contains` | 替代 `search`（`nameContains` 保留给按名过滤） |
| 超时 | `timeoutSeconds` | `waitSeconds` 保留（语义是等待停点，不是超时） |
| 步进类型 | `kind` | 取 `over` / `into` / `out` |
| 进程 | `processId`（仅 pid）/ `process`（pid 或进程名） | 见 §4.2 |
| 启动命令行 | `commandLine` | `debug_launch` 保留（可带参数，不能叫 assembly） |

### 4.2 逐工具迁移表

只列需要改的；未列出的工具参数名已符合规范。

| 工具 | 现状 | 改为 | 理由 |
|---|---|---|---|
| `debug_breakpoint_set` | `methodToken` | `token` | 全仓唯一一个不叫 `token` 的方法 token；与 `ilOffset` 并列时不冲突 |
| `debug_breakpoint_remove` | `breakpointId` | `id` | `debug_breakpoint_list` 输出字段是 `id=1`，agent 照输出填参数即失败 |
| `debug_step` | `stepType` | `kind` | 与 `debug_verify` 的 `assert.kind` 一致 |
| `search_string` | `search` | `search_literals`（工具名）+ `contains`（参数） | 名字歧义：它只搜字符串字面量，不搜符号名 |
| `debug_run_to` | 只有 `typeName`+`memberName`/`line` | 增加 `token`、`moduleName`+`ilOffset` | 与 `debug_breakpoint_set` 的四种定位方式对齐 |
| `decompile_il` | 只有 `token` | 增加 `memberName` / `typeName` | 与 `decompile_member` 对齐（实测中它是最不一致的一对） |
| `ui_*` / `screenshot` | `process` vs `processId` | `ui_*` 保留 `process`（可传进程名），`screenshot` 保留 `processId`；两者的描述第一句必须写明差异 | 语义确实不同，不强行统一 |

### 4.3 必填参数归零（P0）

当前仅 3 个工具违反「所有 MCP 工具参数必须带默认值」铁律（`inputSchema.required` 非空）：

| 工具 | 必填参数 | 现状 | 改为 |
|---|---|---|---|
| `debug_set` | `path`, `value` | 客户端绑定阶段抛英文 `Missing key`，agent 拿不到原因 | 两者默认 `""`，方法体校验并返回中文 |
| `debug_evaluate` | `expression` | 同上（未实测触发，但 schema 已标必填） | 默认 `""` |
| `debug_object` | `path` | 同上 | 默认 `""` |

验收：`tools/list` 中 `required` 数组必须全部为空。

## 5. 工具改名与合并（P1/P2）

### 5.1 改名（方向进入名字）

| 现名 | 新名 | 理由 |
|---|---|---|
| `call_chain` | `call_sequence` | 正向序列：从起始方法展开被调成员并反编译其方法体 |
| `call_graph` | `call_edges` | 关系清单（**默认双向**）：类型级给出本类型调用的类型 + 调用本类型的类型；`token` 模式才收敛为"上游调用者" |
| `search_string` | `search_literals` | 只搜字符串字面量，不搜符号名 |

改名时**不得**让新名字暗示单一方向：`call_edges` 的默认模式是双向，首句必须写明"双向"；`call_sequence` 必须写明"正向（下游）+ 反编译被调成员体"。

`decompile` / `decompile_member` / `decompile_il` / `signature` 保留原名：四者粒度差异大，改名无法表达。

### 5.2 合并（53 → 42）

| 现状 | 合并后 | 减量 | 合并条件（必须同时满足） |
|---|---|---|---|
| `debug_breakpoint_set/list/remove/clear` | `debug_breakpoint(action=set/list/remove/clear)` | −3 | 纯 CRUD，参数是并集子集 |
| `debug_exceptions` / `debug_exceptions_clear` | `debug_exceptions(action=list/clear)` | −1 | 同上 |
| `debug_disconnect` / `debug_terminate` / `debug_reset` | `debug_session(action=disconnect/terminate/reset)` | −2 | 无参收尾动作 |
| `debug_state` / `debug_wait` | `debug_wait(waitSeconds=0)` | −1 | `state` 本质是 `wait(0)`；默认 0 时立即返回当前状态 |
| `debug_stack` / `debug_variables` | `debug_frame` | −1 | 同一停点的栈与当前帧变量，agent 每次都要连调 |
| `decompile_to_dir` / `decompile_to_project` | `decompile_to_dir(project=true)` | −1 | 差别只有 `project` / `nestedDirectories` 两个 bool |
| `screenshot` / `screenshot_displays` / `screenshot_windows` | `screenshot(mode=display/window/list-displays/list-windows)` | −2 | 后两者是纯列举，无独立参数 |

合计 53 − 11 = **42**。

**明确不合并**（写进实施时的 review 清单）：

- `decompile` 家族四个：输出量级差十倍，合并后描述膨胀、参数互相污染。
- `debug_launch` / `debug_attach` / `debug_processes`：参数零重叠，且是进入调试的唯一第一站。
- `ui_*` 五个：定位 / 动作 / 写值 / 读值 / 等待，切分已经清晰。
- 任何 `debug(action=...)` 式大杂烩。

### 5.3 合并后的约束

- 每个 `action`/`mode` 枚举的第一句必须列出全部合法值，且顺序与文档一致。
- 合并工具的 `[Description]` 必须保留"何时选我 / 不要选我"（§7）。
- `debug_breakpoint(action=set)` 需同时支持 `token`、`typeName+memberName`、`typeName+line`、`sourcePath+line` 四种定位，错误提示保持现有中文枚举。

## 6. 缺陷修复（P1，独立于瘦身）

| # | 现象 | 复现 | 期望（已裁决） | 所在层 |
|---|---|---|---|---|
| D1 | `debug_disconnect` 在 Attaching 状态失败并抛英文 COM 错误 | `debug_attach` 后立即 `debug_disconnect` → `CORDBG_E_PROCESS_NOT_SYNCHRONIZED ... from a call to a COM component`，并提示"需重启目标恢复" | **先修根因**：断开前把会话推进到可同步状态（必要时先 resume 再 detach）；同时宿主兜底——任何 COM 错误一律包成中文并保证会话仍可 `debug_reset`。若根因修复在实施中受阻，允许降级为"仅中文兜底 + 明确指引"，并在 CHANGELOG 记录降级 | Engine + 宿主 |
| D2 | `call_edges(token)` 漏 async 调用者 | `AutoNikke.dll` 中 `MatchTemplateHelper.FindBest` 被 `TemplateTestService.TestAsync` 调用，返回"（无）"；同步调用者正常 | **本期只做标注，不做真收录**：反向调用结果为空且程序集内存在 async 状态机时，追加一行"async 状态机内的调用点未收录（见 ROADMAP）"。真收录（扫描 `MoveNext` 并映射回原方法名）列 ROADMAP | 宿主文案 + Decompiler 探测 |
| D3 | `debug_wait` 偶发把运行中的进程报成"已退出" | 断点已绑定、进程 16 秒后才跑完，`wait` 立即返回"进程已退出" | 区分 `Exited` 事件与状态快照：只有在读到退出码且无待处理停点事件时才报已退出；事件丢失时回落到状态查询并重试一次 | Session |
| D4 | apphost `.exe` 报英文且无引导 | 对 `DebugTarget.exe`（apphost）调 `assembly_info` → `PE image does not have metadata.` | 中文提示 + 引导"这是 apphost，请改用同名 `.dll`（托管代码在其中）" | 宿主 |
| D5 | `debug_verify` 的 `memberName` 子串匹配会撞名 | `memberName="Work"` 撞 `Work`/`WorkBag`/`WorkScores` | 保持子串匹配；现有错误提示已列出候选，保留原样，不额外改 | — |
| D6 | `decompile_il` 与 `decompile_member` 参数集不一致 | 前者只收 `token` | 见 §4.2 | 宿主 |
| D7 | `debug_set` 修改后 `debug_step` 值被覆盖（断点在初始化前） | 方法入口设断点 → 改局部变量 → step → 值回到原值 | 不改行为；`debug_set` 成功文案追加提示"若断点在方法入口，局部变量尚未初始化，step 后可能被覆盖" | 宿主文案 |

## 7. 描述与输出瘦身（P3）

### 7.1 三段式描述模板

每个工具描述统一为：

```
<做什么，一句话>。何时选我：<触发意图>。不要选我：<相邻工具与正确去向>。
```

- 第一句决定 agent 是否点开；第二句决定选不选；第三句决定不选错。
- 参数级说明留在参数 `[Description]`，正文不再重复。
- 合并工具（§5.2）额外在首句列出 `action`/`mode` 合法值。

### 7.2 去重

- `lines` 分页模板目前在 4 个工具正文里逐字重复，统一收进参数描述。
- 工具正文目标：中位数 ≤ 160 字符，最长 ≤ 400 字符（现为 224 / 658）。

### 7.3 输出侧

- 头部 `程序集: <绝对路径>` 改为 `程序集: <文件名>`，完整路径仅在写盘/报错时出现。
- 预估：每次调用省约 60–120 字符，且不损失可定位性（路径可由参数还原）。

## 8. 验收

### 8.1 定量

| 项 | 现状 | 目标 |
|---|---|---|
| 工具数 | 53 | 42 |
| `tools/list` JSON | 70 KB | ≤ 45 KB |
| 描述正文 | 11 871 字符 | ≤ 8 000 字符 |
| `inputSchema.required` 非空工具 | 3 | 0 |
| 描述中位数 / 最长 | 224 / 658 | ≤ 160 / ≤ 400 |

### 8.2 定性（零返工验收）

派一个**未参与本设计讨论**的 subagent，用仓库内可复现的样本跑固定任务组，统计"因名字猜错导致的返工次数"：

1. 对 `tests/TestData/TestSamples.dll`：列类型 → 看实现 → 查调用者 → 查接口实现者。
2. 对 `tests/TestData/DebugTarget.exe`：启动 → 设断点 → 到断点 → 看现场 → 改值 → 复验。
3. 对一个桌面窗口（`UiSampleApp`，由 `generate-testdata.ps1` 产出）：列控件 → 读值 → 触发一次无副作用动作。

验收线：返工 ≤ 2 次（基线 17 次，取自本设计的前置实测）；每次返工必须记录"我猜了什么 / 实际是什么"。

> 前置实测用的是本机真实项目 `AutoNikke.dll`（不在仓库内，不参与验收）。D2/D5 的复现改由 `TestSamples` + `DebugTarget` 承载：**实施时需在 `generate-testdata.ps1` 补一个 async 调用者样本**（async 方法体内调用具名静态方法），作为 D2 的回归用例。

### 8.3 回归

- 全量单测通过；工具快照测试（工具名集合、参数名集合）同步更新。
- `DotNetDebuggerMcp.Client` 端到端跑通（用例中的工具名/参数名同步）。
- 握手文本（`HandshakeText.cs`）与 `README.md` 工具表同步；能力族触发条件齐备的护栏测试保持通过。
- `CHANGELOG.md` 记一条破坏性改名说明（工具名/参数名变更清单）。

## 9. 影响面与边界

工具面重构（§4/§5/§7）**只动宿主与文档**。缺陷修复（§6）按各自所在层动，且必须带该层测试：

| 改动 | 层 | 必需测试 |
|---|---|---|
| 改名 / 合并 / 参数默认值 / 描述 / 输出头 | 宿主 | `DotNetDebuggerMcp.Tests` 快照与工具测试 |
| D1 断开同步时序 | Engine（+宿主兜底） | `DotNetDebugger.Engine.Tests` 真实会话用例（attach→disconnect） |
| D3 停点事件与退出判定 | Session | `DotNetDebugger.Session.Tests`（该套串行执行） |
| D2 async 标注 | 宿主文案 + Decompiler 只读探测 | 宿主单测 + `DebugTarget` async 样本 e2e |

其余边界：

- **CLI 不受影响**：`-a`/`-mn`/`-s`/`-dbg` 等参数独立于 MCP 参数名。
- **不触碰** `--features` 语义与默认值。
- **不触碰** 缓存的 key 生成（`CacheSignatures`）与输出契约（行号/分页/头部块），仅缩短头部路径显示。
- `SharpSight.*` 零改动。

## 10. 已裁决的开放项

| 项 | 裁决 | 理由 |
|---|---|---|
| `debug_state` / `debug_wait` 是否合并 | **合并**为 `debug_wait(waitSeconds=0)`，默认 `0` | `state` 本质是"立即取状态"；默认 0 不改变现有显式传参的调用节奏，只让不传参的调用秒回 |
| 合并后默认值 | `0`（立即返回当前状态） | 与"查询"语义一致；等待必须显式传秒数，避免 agent 不知道自己在等 |
| 合并后输出形状 | 状态头（pid / 会话状态 / 最近停点）+ 停点上下文（有停点时）+ 目标输出尾部 + trace/跳过/条件反馈 | 取两者并集，信息只增不减 |
| `decompile_to_dir` / `decompile_to_project` 是否合并 | **合并**为 `decompile_to_dir(project=false)` | 差别只有 `project` / `nestedDirectories`；布尔开关比枚举更自然 |
| 改名是否保留兼容别名 | **不保留**，直接替换 | 用户已确认工具名不是对外承诺 |

## 11. 留给 builder 的决定

以下不改变"做什么"，实施时可自行决定：

- 合并工具内部是拆私有方法还是小类；遵循仓库「单方法 ≤ 80 行」纪律。
- `debug_frame` 的输出分节顺序与段落标题（建议：调用栈 → 当前帧变量 → 输出尾部）。
- 快照测试的实现方式（字符级快照 vs 结构化断言）。
- D1 根因修复受阻时的降级触发点与记录方式（需写进 CHANGELOG）。
