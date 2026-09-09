# Spec · DB2 变量按名白名单读取

> 状态：**已立项**（2026-09-09 拍板）——`debug_variables` 加 `names` 参数：逗号分隔白名单（空=全量）、精确忽略大小写匹配 局部/参数名 + `slotN` + `$exception`、>50 项拒绝、未知名反馈可用名（零值）不静默。实施计划见 `docs/planning/plans/2026-09-09-db2-named-whitelist.md`，规格冻结。
> 关联：宿主 TODO DB2（P2 批次小项）；参照 microsoft/DebugMCP `get_variables_values`/`normalizeRequestedNames`（本地 `../../Externals/DebuggerExternals/DebugMCP/`）；与 DB1 脱敏配合缩小暴露面。

## 1. 背景与目标

现状 `debug_variables` 全量返回栈顶帧局部/参数（含 `$exception` 节）——大帧/敏感环境**无关值全进 LLM 上下文**。目标：按名白名单读取，省 token、缩小暴露面（DB1 脱敏互补）。

非目标：不做独立 `list_variable_names` 工具（`names` 为空=全量模式已暴露名字）；不做通配/正则（无通配，明确白名单）；不做引擎级只读部分变量（白名单过滤在宿主渲染层，Engine/Session 零改动）。

## 2. 语义（2026-09-09 拍板）

| 规则 | 值 |
|---|---|
| 参数 | `debug_variables(names="", threadId=0)`；`names` 逗号分隔白名单；空 = 现状全量 |
| 匹配 | 精确、忽略大小写；匹配对象 = locals/arguments 的**名**（含 PDB 局部名/元数据参数名；无符号名按展示用 `slotN` 也可作白名单项）+ `$exception` 伪变量 |
| 上限 | 白名单项 > 50 拒绝（防 agent 传整帧当白名单，逼其用全量） |
| 未知名 | **不静默**：反馈「未找到 {未知项}——当前帧可用名：{名清单}（不含值）」 |
| 同名跨作用域 | 都返回（locals/arguments 分节保留，带作用域标注） |
| 渲染 | 命中项渲染复用现 RenderVariable；返回头注明「白名单 N 项 → 命中 M 项」 |

## 3. 架构

- **宿主**：`DebugInspectTool.DebugVariables` 解析 `names` → 白名单集合 → 读全量 `GetVariablesAsync` 后按名过滤渲染；未知名校验在渲染前完成（零值反馈）。Engine/Session 零改动（过滤属展示策略，同 DB1 脱敏层级）。
- 与 DB1 顺序：先白名单命中，再对命中的值走敏感脱敏管线。

## 4. 验证

- 宿主单测/端到端：白名单命中（名/slotN/$exception/跨作用域同名）、大小写不敏感、>50 拒绝、未知名零值反馈、空=全量回归、与脱敏叠加（命中敏感名仍被 DB1 脱敏）。
