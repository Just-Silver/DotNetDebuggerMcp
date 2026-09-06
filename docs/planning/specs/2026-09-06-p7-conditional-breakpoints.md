# Spec · P7 条件断点

> 状态：**待评审**（2026-09-06 起草）。通过后动码；完成态回写本行。
> 关联：宿主 TODO P7；复用 P6 表达式求值（挂接点已在 P6 spec §6/TODO P6 收账预留）。

## 1. 背景与目标

agent 调试第四类高频诉求：**「只在特定条件下停下」**——循环第 N 轮才出错的 bug，无条件断点要人工 continue 一遍遍看。现状 `debug_breakpoint_set` 有 hitCount（第 N 次起生效）但没有条件：`i == 3` 才停、`order.Total > 100` 才停做不到，agent 只能 trace 批量记轨迹再翻（token 昂贵）或逐轮 continue（轮次多时不可行）。

本 spec 定义**条件断点**：断点带一个 P6 表达式子集的条件，命中回调时求值——true 停（或 trace 记录），false/求值失败放行继续跑。求值**纯读、无副作用**（P6 同款，绝不执行目标进程代码）。

非目标：条件表达式扩展（算术/调用/括号——随 P6 子集 v1 边界）；条件运行时修改（= remove + 重设）；CLI `-dbg` 条件参数（v1 不加，一次性调试场景用不到）；Web 断点面板展示条件（agent 是主消费者，工具面足够）。

## 2. 现状锚点（起草时已核实）

| 设施 | 位置 | 关系 |
|---|---|---|
| `HandleBreakpoint`（CallbackHandler.cs:94） | Engine | 命中决策点：Match → `RegisterHit()` → HitCount 放行 → Trace 快照放行 → 停。**条件求值插在 RegisterHit 之前** |
| `DebugBreakpoint`（Module/Token/IlOffset/HitCount/Mode/Hits） | Engine/Session | 加 `Condition` 字段（string?，null=无条件） |
| `BreakpointManager.Add(moduleName, token, ilOffset, hitCount, mode)` | Engine | 加 `condition` 参数透传 |
| `ExpressionParser.Parse` / `ExpressionEvaluator`（P6） | Session/Evaluation | 条件表达式解析+求值；parser 纯语法可脱进程调（set 时校验用） |
| `DebugEngineCore.ReadPathValue`（private，泵内同步直读） | Engine | 泵内求值的路径解析器（绕开 MaxChildren、停住态可读） |
| skipped 异常/trace 轨迹反馈模式（SessionEventBuffer consume 式） | Session | 求值失败反馈复用同款（防静默空等，P2 精神） |

## 3. 分层设计与关键决策

### 3.1 求值时机：引擎命令泵内（P5 trace 同款路线）

条件判定发生在**命令泵处理 Breakpoint 事件的线程内**：进程恰处停住态（值可读），求值是微秒级内存读，false 即落默认 Continue——对目标进程不可见，与 trace 快照/skipped 异常同一模式。**否决「先停后判」**（Session 层等 BreakpointHit 事件再判 false 再 continue）：P5 已证伪——非命中也产生停点事件干扰 debug_wait、每轮迭代一次停/续往返（万次循环不可行）、与「回调只入队」纪律竞态。

### 3.2 跨层契约：委托注入（Engine 定义接口，Session 实现）

P6 把求值器放在 Session（宿主/Web 共享中枢），Engine 不得反向引用。但泵内求值要求求值逻辑活在引擎线程里。解法是**依赖倒置**——Engine 定义小接口，Session 提供实现，Engine 在命中时把自家 `ReadPathValue` 作路径解析器传入：

```
Engine（新）
  public delegate DebugEvalResult PathValueResolver(
      int threadId, string rootName, IReadOnlyList<PathSegment> segments);
  public interface IBreakpointConditionEvaluator
  {
      /// 命令泵线程内调用（进程停住）。返回 true=停 / false=放行；
      /// 抛异常=求值失败（引擎放行+计数）。pathResolver 仅本次调用栈内有效（泵线程、停住态）。
      bool Evaluate(int threadId, string expression, PathValueResolver pathResolver);
  }
Session（新）
  ExpressionConditionEvaluator : IBreakpointConditionEvaluator（无状态单例）
    = ExpressionParser.Parse（纯语法）+ ExpressionEvaluator 同步核（见 3.3），PathNode → pathResolver(...)
接线
  Engine DebugSession.LaunchAsync/AttachAsync 加可选 evaluator 参数（缺省 null）；
  Session 两个工厂固定传入单例。引擎无 evaluator 时 SetBreakpointAsync 带条件 → 抛中文提示。
```

**红线**：Session 实现在泵线程内只做纯计算 + pathResolver 调用，**绝不**触碰 session/命令泵（再入 = 死锁）——接口注释写明。

### 3.3 ExpressionEvaluator 抽同步核（消除双份分发）

P6 求值是 async（PathNode 经 `EvaluatePathAsync` 投泵）；泵内需要同步版（pathResolver 直调）。抽公共同步核，两入口共用一份比较/字面量/`!` 逻辑：

```csharp
// Session ExpressionEvaluator 新增：
public static DebugEvalResult EvaluateCore(ExpressionNode ast, Func<PathNode, DebugEvalResult> resolvePath)
// P6 async 门面改为：EvaluateCore(ast, pn => session.EvaluatePathAsync(...).GetAwaiter().GetResult())
//   —— 工具线程 ≠ 泵线程，阻塞等待投泵任务安全（行为不变，少一份逻辑）
```

### 3.4 命中次序（取舍点①，推荐条件先）

```
HandleBreakpoint: Match → [有条件?] 求值：异常/false → 放行（不 RegisterHit）
                          → RegisterHit → Hits<HitCount 放行 → Trace 快照放行 → 停
```

即 **Hits = 条件为真的通过次数**（VS 风格）。备选「先计数再判条件」被否：trace+condition 组合下 false 轮也会记轨迹（噪声），且 list 的「命中 N/目标 M」含义变混。无条件断点行为零变化（P5 回归保障）。

### 3.5 失败语义（取舍点②，TODO 已预批方向）

| 场景 | 行为 |
|---|---|
| set 时语法错 | **当场拒绝**（parser 脱进程校验），断点不设，返回 parser 中文错误——「写错条件永不命中」的最大源头的门口杀掉 |
| 命中时求值失败（未知名/缺字段/非布尔/变量未初始化） | 引擎 catch → 发新事件 `BreakpointConditionFailed(BreakpointId, ThreadId, Error)` → 放行 |
| 条件求值结果非布尔（如 `i`） | 求值器抛「条件须为布尔标量（当前：System.Int32 0）」→ 同上（条件必须显式比较或布尔量，与 C# 一致） |
| 失败反馈 | Session buffer 折叠为 **consume 式**（P2 skipped 同款）：次数 + 最近（断点 id + 错误）；`debug_state`/`debug_wait` 附「断点 N 条件未通过 K 次（最后：…）」 |
| 真性 false（条件正常但为假） | 静默放行，**不**发事件不计数（每轮都发是噪声）；agent 可用 Hits 判断条件是否曾为真 |

无独立超时：求值随命令泵同步执行（解析微秒级 + COM 读内存），与现有读值命令同风险面，v1 不加。

## 4. 工具面

```
debug_breakpoint_set 新参 condition=""（默认空=无条件；三种定位方式通用）
  → set 时 ExpressionParser.Parse 校验：语法错当场拒绝（错误文案=P6 parser 原文）
debug_breakpoint_list → 命中行附「条件: <expr>」；Hits 含义=条件为真次数
改条件 → remove + 重设（v1，与改 hitCount 同款）
debug_state/debug_wait → 附条件未通过反馈（consume 式，仅当 K>0）
CLI -dbg → 不加条件参数（v1）
```

## 5. 测试计划

- **Session 纯单测**：`ExpressionConditionEvaluator`（resolver 桩）——true/false/失败抛异常/非布尔抛异常；`EvaluateCore` 与 async 门面同结果（若干表达式对拍）。
- **Engine 集成**（DebugTarget bag 模式，WorkScores/WorkBag 锚点）：条件 `n == 999` 永假 → 全程不停、进程退出、Hits=0；条件恒真 → 首次命中即停；条件 `zzz == 1` → 每次命中发 BreakpointConditionFailed 且不停；condition + hitCount 组合（第 N 次条件为真才停）。
- **宿主 e2e**：`condition="b.A == 7"` 停在正确轮次且 `debug_evaluate` 现场一致；永假条件跑完退出后 `debug_wait`/`debug_state` 反馈未通过计数；`condition="b.A +"` set 被拒含 parser 提示；list 显示条件。
- **回归**：全量五套件 + Client（P5 trace/hitCount 用例必须零变化）。

## 6. 工作量与顺序

Engine 接口+命中接线+Condition 字段（0.5）→ Session 同步核重构+求值器实现（0.5）→ 宿主工具面+反馈（0.5）→ 测试三层（0.5），≈ 2 人日。顺序即依赖序；P6 行为（async 门面回归）由既有测试护栏。
