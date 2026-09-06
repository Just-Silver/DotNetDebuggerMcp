# Spec · P6 表达式读值安全子集

> 状态：**已完成**（2026-09-06 实施；三个取舍点经评审确认。与 spec 的差异仅一处澄清：v1 文法本就无括号，`!(a==b)` 会报错并指引子集范围——工具描述中已明确列出）。P7 条件断点挂接点：`ExpressionEvaluator.EvaluateAsync` + `DebugEvalResult.ScalarValue is bool`（求值失败/非 bool = 不命中）。
> 关联：宿主 TODO P6/P7；P7 条件断点与未来 `debug_evaluate` 工具共用本组件。

## 1. 背景与目标

agent 调试时的第三类高频诉求：**「此刻这个表达式是什么值」**——`user.Id`、`list[3].Name`、`i == retryCount`。现状只能 `debug_variables` 看栈顶全部变量（对象只到一级），深层路径、数组任意下标、布尔比较都做不到。

本 spec 定义**纯读值**的表达式子集：语法树遍历逐段解引用调试目标内存，**绝不执行目标进程代码**（无 FuncEval——P2/P3 已验证的 ICorDebug 读值管线天然无副作用）。它是 P7 条件断点的判定引擎，也是独立工具 `debug_evaluate` 的底座。

非目标：方法调用/赋值/自增等一切有副作用的求值（永久排除 FuncEval，理由见 ROADMAP v2 评估）；Roslyn 编译求值；字符串方法调用（`s.Length` 属属性同样受限，见 §4 属性）。

## 2. 现状锚点（起草时已核实）

| 设施 | 位置 | 关系 |
|---|---|---|
| `DebugSession.GetVariablesAsync(threadId)` | Engine/Session/DebugSession.cs | 顶层变量来源（locals/arguments/exception $exception）；一级展开 |
| `ReadValue` / `ReadObjectValue`（GetFieldValue）/ `ReadArrayValue`（GetElementAtPosition） | Engine/Engine/DebugEngineCore.cs（private） | 读值管线本体；P5 已提取 `ReadVariablesForThread` 供内部复用——**P6 再提取「按路径解引用」** |
| `ReadFieldTokens(modulePath, classToken)` | 同上 | 字段清单（名+fieldDef token）；路径段→字段定位复用 |
| `TypeNameResolver` / `SymbolNameResolver`（PDB 模式） | Engine | 值展示的类型名/变量名来源（沿用） |
| MaxChildren=32 截断 | DebugEngineCore | **树遍历方案的硬伤**：`list[50]` 取不到——本 spec 采用「引擎按路径直读」绕开（§3） |

## 3. 分层设计

```
Session · ExpressionEvaluator     语法解析（自研递归下降，~300 行，不引 Roslyn）+ AST 求值调度
Engine  · DebugEngineCore         EvaluatePathAsync(threadId, rootName, PathSegment[])：命令泵内
                                  从栈顶帧的 local/arg 起逐段解引用（字段→GetFieldValue，
                                  索引→GetElementAtPosition 任意下标），返回 DebugValue
宿主    · debug_evaluate 工具     参数校验 + 渲染（与 debug_variables 同款格式）
```

**为什么路径解析放引擎**：`ReadValue` 展开有 MaxChildren=32 截断，`items[50]`、深层链在「取树再走」方案下不可靠；引擎按段直读（每段解一次引用）无截断，且天然在命令泵 MTA 线程内（线程纪律不破）。路径段数上限 8（防失控），逐段解析失败即报错（附已解析到的段）。

**PathSegment**：`Field(string Name)` | `Index(int N)`（负索引=从尾数，支持 `^1`？v1 不做，仅非负）。索引仅作用于数组/字符串（字符串索引返回单字符字符串）。

**根解析**：rootName 在栈顶帧 locals + arguments（+`$exception`）中按名匹配（PDB 名优先，slot 回退）——与 `debug_variables` 同一来源，所见即可求。

## 4. 语法子集（v1 文法）

```
Expr    := Comparison
Comparison := Unary (('=='|'!='|'<'|'<='|'>'|'>=') Unary)?      // 单次比较，不链
Unary   := '!' Unary | Path
Path    := Primary ('.' Field | '[' Int ']')*                    // 段数 ≤ 8
Primary := Identifier | Literal
Literal := int | string | true | false | null
```

- **支持**：字面量（int/string/bool/null）、成员访问、数组/字符串索引、一元 `!`、单次比较（操作数须为标量）。
- **不支持（明确报错）**：算术运算、方法调用、赋值/自增、链式比较、泛型/类型操作——报错文案给出「子集范围」提示。
- **属性（取舍点①）**：属性不可直接读（属性 getter 是目标进程代码）。提供**约定降级**：`X` 依次尝试字段 `X` / `_x` / `_X` / `<X>k__BackingField`，全部未命中才报错，报错附「该对象可用字段清单」（引擎返回，agent 可见后改写字段名——如 `list` 的字段里有 `_size`，agent 写 `list._size`）。约定清单写死在引擎路径解析里。
- **字符串**：索引得单字符；长度经约定降级 `Length`→`_stringLiteralLength`? 不可行（string 字段特殊）——**`Length` 明确不支持**，报错提示用数组/集合替代或经 `debug_variables` 查看字符串整体。

## 5. 错误语义（全部中文、可诊断）

| 场景 | 提示要素 |
|---|---|
| 语法错 | 位置 + 附近原文 + 子集范围一句话 |
| 未知根名 | 「栈顶帧无变量 X（可用：a, b, iterations…）」列实际可用名 |
| 段解析失败 | 到达的段号 + 对象类型全名 + 可用字段清单（§4 约定降级失败时） |
| 索引越界 | 段号 + 长度 |
| 对 null 解引用 | 段号 +「对象为 null」 |
| 非标量比较 | 两侧类型名 |

进程未停 → `debug_evaluate` 返回「进程需停在断点/异常（当前 Running）」（与 debug_variables 同前置校验）。

## 6. 工具面

```
dotnetdebugger_debug_evaluate(expression 必填, threadId=0, cancellationToken)
→ 「表达式: list[3].Name = \"Alice\"（System.String）」
   复杂值（对象/数组）渲染与 debug_variables 同款（Display + children 一级）。
```

`threadId` 缺省 0 = 最近停点线程（全工具一致）。P7 挂接点：`EvaluateConditionAsync(expression)` = 本求值 + 结果标量化布尔判定（非 bool → 视为不命中并计 trace，见 TODO P7）。

## 7. 测试计划

- **Engine 集成**（DebugTarget：`Work` 内停于断点）：字段链（`bag.A`）、数组任意下标（构造 n>32 场景用 PDB 行断点停在循环中段后 `arr[40]`——DebugTarget 无此形态则经 TestSamples 的 List 模拟：`TestSamples.dll` 有 GenericBox 等，可 attach TestSamples 进程？TestSamples 是库——用 DebugTarget.Work 的 `acc`/`i` 覆盖标量链，深链/越界/null 用单测直调引擎内部？引擎路径解析依赖进程——集成测以「正路径 + 越界/未知名错误」为主，parser 纯单测穷尽文法）。
- **Session 纯单测**（无进程）：parser 全文法（合法/非法各若干）、AST 渲染、段数上限、比较类型校验。
- **宿主 e2e**：launch → 停点 → `debug_evaluate("i")` / `"iterations"` 标量正确；未知名错误含可用清单；`debug_variables` 一致性（同一变量 Display 相同）。
- **回归**：全量五套件 + Client。

## 8. 工作量与顺序

Engine `EvaluatePathAsync` + 约定降级（1）→ Session parser/AST/求值调度（1）→ 宿主 `debug_evaluate`（0.5）→ 测试三层（1）→ 文档（0.5），≈ 4 人日。parser 与引擎路径解析可并行开发、先后集成。
