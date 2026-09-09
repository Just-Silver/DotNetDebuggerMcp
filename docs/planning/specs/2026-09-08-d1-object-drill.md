# Spec · D1 对象树深读（受控递归展开）

> 状态：**已立项**（2026-09-09 拍板）——v1 = **受控递归**下钻（`debug_object(path, depth=2, limit=32, threadId=0)`，depth 上限 6、同路径环输出 `<cyclic>`、每层字段预算）；独立新工具；path 支持 `$exception` 伪根与 locals/args 根。实施计划见 `docs/planning/plans/2026-09-09-d1-object-drill.md`，规格冻结。
> 关联：宿主 TODO D1；依赖 P6 路径求值链（`ReadPathValue`/`EvaluatePathAsync`）；设计已获 microsoft/DebugMCP 同款验证（maxDepth/cyclic-reference/field 预算，见宿主 TODO「DebugMCP 可借鉴点」）。
> 拍板前修正（2026-09-09）：原草案 v1「路径→展开一层」与既有 `debug_evaluate`（终值对象已带 children）**高度重叠**——拍板改为默认 depth=2 的受控递归，单层重叠被递归增量覆盖。

## 1. 背景与目标

现状两层读取各自受限：
- **`debug_variables`（展开一层）**：`ReadValue(expand:true)` → 对象/数组只取**一级** children（`MaxChildren=32`，children 内不递归），复杂对象只能看到"表面字段"，深层结构要反复 `debug_evaluate` 猜路径。
- **`debug_evaluate`（路径直读）**：`ReadPathValue` 能沿路径（如 `order.Customer.Address.City`）任意深直读，但 agent 得**先知道路径**——不知道对象里有什么时只能盲试。

**D1 能力**：给定一个"已定位的对象/数组"，**在其上继续展开下一级 children**（受控递归），让 agent 像人一样"点开变量树"逐层下钻，而不是盲猜路径或一次看全量。

对标：VS 调试变量窗口逐层展开 / DebugMCP `get_variable_children`（DAP variablesReference 模型）——**它们都是"引用对象 → 取该对象 children"**，这正是 D1 的形态。

## 2. 现状锚点（起草时已查证，2026-09-08）

| 设施 | 位置 | 与 D1 关系 |
|---|---|---|
| `ReadValue(value, expand)` 顶层展开（对象/数组一级） | `DebugEngineCore.cs:619` | 展开逻辑已存在，但**无递归、无对象引用带出** |
| `ReadObjectValue(obj)` | `:670` | 字段清单取自模块元数据（`ReadFieldTokens`），`GetFieldValue` 取值——**可复用为"给定对象读 children"** |
| `ReadArrayValue(arr)` | `:697` | 数组按位置取前 N——**可复用为"给定数组读元素"** |
| `ReadFieldTokens(modulePath, classToken)` | `:718` | 实例字段清单（静态跳过）——已封装可直接复用 |
| `MaxChildren=32` | `:533` | 每层截断阈值 |
| `ReadPathValue(threadId, rootName, segments)` | `:771` | P6 路径直读；D1 的"定位对象"入口 |
| `EvalValue.Raw`（P6 内部） | Engine | 携带活 `CorDebugValue`；但只在求值链内，不对外 |
| `DebugValue`（Scalar/Summary/Object） | `Models/DebugValue.cs` | **纯展示快照，无对象引用**——对外模型不含"可继续展开的锚" |
| `DebugVariable(Name, Slot, Value, IsArgument)` | `Models/DebugVariable.cs` | 无路径/父链信息 |

### 关键差异（D1 的增量点）

现有展开是一"次性快照"：`ReadObjectValue` 拿到 obj → 读 children → 返回 `DebugValue.Object(display, children)`，children 里又是 `DebugValue`（不再有 CorDebugValue 引用）。**要"继续下钻"，必须让每一层能携带"活的对象锚"**——这是 D1 相对现状的核心新增。

三条可选锚定方式（§4 取舍）：
- **a. 模型层加路径**：`DebugVariable` 补 `Path`（如 `locals:0.Customer.Address`），`debug_object path=...` 复用 P6 直读定位到对象再展开。改动模型 + 工具链，最通用。
- **b. 会话缓存对象 id**：引擎侧维护 `(会话内自增 id → CorDebugValue)` 映射，展开返回 children 带 `refId`，下钻按 refId 取。贴近 VS/DAP variablesReference 模型；但 id 生命周期/失效管理复杂（值变化、帧离开）。
- **c. 纯文本路径复用（推荐 v1）**：不做模型扩展，agent 用**已有 P6 路径文法**表达下钻目标——`debug_evaluate "order.Customer"` 后想要它的 children 就 `debug_object "order.Customer"`（路径定位到对象 → 复用 ReadObjectValue 展开一层）。**增量最小：只加一个"路径→该对象 children"的入口**，复用全部现有设施。

## 3. 目标工作流（agent 视角，v1 拍板版）

```
① debug_variables（停点）
   locals:
     order = object "N 字段" children:[Id, Customer=…]           ← 只展开一级
② debug_object "order.Customer"                                   ← 新工具：定位对象 + 受控递归
   → depth 默认 2：Customer 字段 Name/Phone + 其对象子级 Address 的字段（两层一次给出）
③ debug_object "order" depth=3 limit=64                          ← 一次看更深/更宽，防环 <cyclic>
④ debug_evaluate "order.Customer.Address.City"                   ← 直接取值仍可用（两路并存）
```

要点：**`debug_object` = `ReadPathValue` 定位对象 + 在值对象上做受控递归展开（depth 层、每层字段/元素预算、路径环检测）**——定位复用 P6，递归复用 `ReadObjectValue`/`ReadArrayValue`/`ReadFieldTokens` 的字段清单机制，增量 = 递归展开器 + 环/预算护栏。

## 4. 分层设计与关键决策（2026-09-09 拍板版）

### 4.1 v1：受控递归（选项 c 演进——路径锚 + depth 预算）

- **Engine**：新增 `ReadObjectAtPathAsync(threadId, expression, depth)` → P6 路径定位到 `CorDebugValue` → **受控递归展开**：
  - 递归展开器（新增私有）：给定值对象/数组 + 剩余 depth + 当前路径对象栈 → 产出 `DebugValue` 树（`DebugValue.Object`/`Array` children 递归嵌套）。
  - **环检测**：沿当前展开路径记录已访问对象（对象身份/地址集合），再次进入同对象输出 `<cyclic>` 占位，不再下钻——`DebugValue` 纯快照无环概念，占位符在展开器内显式生成。
  - **预算**：每层 children 截断沿用 `MaxChildren`（32）逻辑（`ReadObjectValue` 现行为），超限提示「共 N 字段，前 M 个」；depth 上限 `PathSegment.MaxDepth?`——新增常量 `MaxDrillDepth = 6`（同 DebugMCP maxDepth），depth 参数钳制在 1-6。
- **Session**：复用 `ExpressionParser`（P6）解析 path（不新增文法）；depth/limit 校验。
- **宿主**：新工具 `debug_object(path, depth=2, limit=32, threadId=0)`：
  ```
  参数：path（P6 文法，如 "order.Customer" / "$exception.InnerException"；根=栈顶帧局部/参数 + $exception 伪根）
       depth（递归深度，默认 2，上限 6）
       limit（每层 children 上限，默认 32）
       threadId（默认 0 = 最近停点线程）
  返回：对象/数组的 children 递归清单（渲染复用 debug_variables 格式），头部显示 depth/children 统计
  ```
  路径终值为标量/字符串/null → 中文提示「该路径不是对象/数组（当前为标量/字符串/null），请给对象或数组路径」；引用自动解引用展开。

### 4.2 与 debug_evaluate 分工（拍板确认）

- `debug_evaluate path` = 取**值**（标量比较/布尔判定主用；对象终值虽带 children 但单层）。
- `debug_object path depth` = 取**结构**（多级下钻探索对象树主用）。两路并存，README 说明分工。

### 4.3 为什么不做 options a/b（模型扩展/refId 缓存）
- a 会动 `DebugVariable`/`DebugValue` 公共模型 + Web 渲染（DebugVarRow 递归用）——波及面大。
- b 的 id 生命周期管理（帧离开/值变化后失效）复杂度高，而 P6 文法已能表达任意路径——**路径即锚，无需 id**。

## 5. 拍板记录（2026-09-09 用户拍板）

1. **工具命名**：独立新工具 `debug_object`（不与 `debug_variables` 二合一）。
2. **表达式根**：支持 `$exception` 伪根与 locals/args 根（复用 P6 根解析）。
3. **形态**：**受控递归 v1**——`debug_object(path, depth=2, limit=32, threadId=0)`；depth 钳制 1-6（`MaxDrillDepth`），每层字段/元素预算沿用 32（超限提示「共 N，前 M」），同路径环输出 `<cyclic>`；`depth=1` 行为与 debug_evaluate 对象结果一致（重叠被默认 depth=2 覆盖）。
4. **limit 语义**：每层上限（默认 32，参数可调 1-128）。

## 6. 验证方案
- **Engine 单测**：DebugTarget 深嵌套对象（WorkBag→Model→Child→Value 链）→ `debug_object` 逐级下钻断言每层 children 正确；越界路径/非对象目标（标量/字符串）中文提示。
- **宿主 e2e**：断点 → `debug_object` 多层下钻 → 与 `debug_evaluate` 直读结果一致（交叉验证）。
- **回归护栏**：children 渲染格式断言（V4 预留）。

## 7. 依赖与工作量
- 依赖：P6 文法/求值链（✅）、`ReadObjectValue`/`ReadArrayValue`/`ReadFieldTokens`（✅ 已封装）。
- 改动面：Engine（`ReadObjectAtPathAsync` 组合方法，约 20-40 行）+ Session（表达式根解析透传）+ 宿主（工具）。无模型/渲染改动。
- 难度：**小-中**（纯组合现成机制，无新概念）。
