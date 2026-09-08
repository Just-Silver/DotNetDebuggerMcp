# Spec · D1 对象树深读（受控递归展开）

> 状态：**计划中（草案）**——现有展开/直读双机制已查证，供后续实施直接参照。实施前置：按宿主 TODO「agent 自动化调试闭环缺口清单」立项确认。
> 关联：宿主 TODO D1；依赖 P6 路径求值链（`ReadPathValue`/`EvaluatePathAsync`）；设计已获 microsoft/DebugMCP 同款验证（maxDepth/cyclic-reference/field 预算，见宿主 TODO「DebugMCP 可借鉴点」）。

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

## 3. 目标工作流（agent 视角）

```
① debug_variables（停点）
   locals:
     order = object "N 字段" children:[Id, Customer=0x..→<object> …]   ← 只展开一级
② debug_object "order.Customer"          ← 新工具：路径定位对象 → 展开其 children
   → object "M 字段" children:[Name, Address=0x..→<object>, Phone …]
③ debug_object "order.Customer.Address"  ← 沿路径继续下钻（P6 文法天然支持）
   → object children:[City="苏州", Street=…]
④ 或 debug_evaluate "order.Customer.Address.City" 直接取值（已有能力，两路并存）
```

要点：**`debug_object` = `ReadPathValue`（定位到对象）+ `ReadObjectValue`/`ReadArrayValue`（展开一层）**——两个现成机制拼装，新增量最小。

## 4. 分层设计与关键决策

### 4.1 推荐 v1：路径 → 展开一层（选项 c）

- **Engine**：新增 `ReadObjectAtPathAsync(threadId, expression)` → 用 P6 路径解析定位到 `CorDebugValue`（对象引用或数组）→ 复用 `ReadObjectValue`/`ReadArrayValue` 展开一层 → 返回 `DebugValue.Object`。环风险低：只展开**一层**，与 `debug_variables` 同级（不引入递归深度，天然无环）。
- **Session**：复用 `ExpressionParser`（P6）解析路径表达式（不新增文法）。
- **宿主**：新工具 `debug_object`：
  ```
  参数：path（对象路径，P6 文法，如 "order.Customer" / "$exception.InnerException"）
       limit（children 上限，默认 32）
  返回：该对象的下一级 children 清单（渲染复用 debug_variables 格式）
  ```

### 4.2 深递归展开（v1 后可选，需防环）

若未来要 `depth=N` 参数一次多级展开：沿路径记录已访问对象（`CorDebugObjectValue` 引用比较或对象地址），遇环输出 `<cyclic>`（DebugMCP 同款占位）。**注意**：`DebugValue` 是纯快照无环感知——多级递归时 children 树要带环标记或限深度（maxDepth=6、字段预算=100 可参照 DebugMCP）。**v1 不做**，一层展开已覆盖"下钻"主场景（agent 逐级调 `debug_object`，token 可控）。

### 4.3 为什么不做 options a/b（模型扩展/refId 缓存）
- a 会动 `DebugVariable`/`DebugValue` 公共模型 + Web 渲染（DebugVarRow 递归用）——波及面大。
- b 的 id 生命周期管理（帧离开/值变化后失效）复杂度高，而 P6 文法已能表达任意路径——**路径即锚，无需 id**。

## 5. 待拍板（立项时决策）
1. **工具命名**：`debug_object`（路径→children）vs `debug_variables` 加 `path` 参数二合一。倾向独立工具（语义清晰、参数表不臃肿，与 W1 `debug_set` 并列）。
2. **表达式根**：`debug_object` 的 path 是否支持 `$exception` 伪根（P6 已支持）与 locals/args 根——**应支持**（复用 P6 根解析）。
3. **limit 语义**：每层 32 是否够；超出提示"共 N 字段前 M 个"（现状格式已有）。
4. **一层 vs 多级**：v1 一层（推荐）；多级递归深度参数留 v1.5。

## 6. 验证方案
- **Engine 单测**：DebugTarget 深嵌套对象（WorkBag→Model→Child→Value 链）→ `debug_object` 逐级下钻断言每层 children 正确；越界路径/非对象目标（标量/字符串）中文提示。
- **宿主 e2e**：断点 → `debug_object` 多层下钻 → 与 `debug_evaluate` 直读结果一致（交叉验证）。
- **回归护栏**：children 渲染格式断言（V4 预留）。

## 7. 依赖与工作量
- 依赖：P6 文法/求值链（✅）、`ReadObjectValue`/`ReadArrayValue`/`ReadFieldTokens`（✅ 已封装）。
- 改动面：Engine（`ReadObjectAtPathAsync` 组合方法，约 20-40 行）+ Session（表达式根解析透传）+ 宿主（工具）。无模型/渲染改动。
- 难度：**小-中**（纯组合现成机制，无新概念）。
