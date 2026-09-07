# Spec · R6 行断点模块加载前可用（延迟解析登记）

> 状态：**草案待评审**（2026-09-07 起草）。
> 关联：宿主 TODO R6；agent 实战反馈清单 R6（2026-09-06 收到，中）；P3 行断点（typeName+line / sourcePath+line）、P9 launch 早期接管（模块未加载是常态）。

## 1. 背景与目标

agent 用行断点（`typeName`+`line` 反编译行 / `sourcePath`+`line` PDB 源码行）调试时，**目标模块未加载就设断点会被当场拒绝**——报「模块 X 未加载（行定位方式要求模块已加载）」并附提示引导改用 token 或 continue 后重试（`LineBreakpointRetryHint`）。

痛点场景：P9 之后 `debug_launch` 返回即冻结在 Main 前，**目标业务模块尚未加载是常态**；agent 想「先看代码、在入口行设断点、再 continue」——token 断点天然支持 pending（`BreakpointManager.Add` 未绑定 + LoadModule 自动重绑），行断点却要等模块加载后才能设，多一轮往返且与「先设断点再放行」的直觉相悖。

目标：行断点支持**延迟解析登记**——模块未加载时把 `typeName`/`sourcePath`+`line`（+期望模块名）登记为延迟项，模块加载后自动重试「行 → token+IL」解析并补设绑定，agent 无需感知模块加载时机。

非目标：延迟项永不解绑的清理/超时（模块始终不加载则一直 pending，`debug_breakpoint_list` 可见，disconnect/clear 清理）；Web 断点面板交互（agent 是主消费者）；「模块加载但行解析失败」的自动重试风暴控制以外的复杂策略。

## 2. 现状锚点（起草时已核实）

| 设施 | 位置 | 关系 |
|---|---|---|
| `DebugBreakpointTool.SetByTypeLineAsync` / `SetBySourceLineAsync` | 宿主 `Tools/Debugger/DebugBreakpointTool.cs:104/:168` | 行断点两分支：解析模块（`ResolveModuleForLineAsync`）→ 行解析（DocumentService / SourceLineResolver）→ token+IL → `Session.SetBreakpointAsync`。**模块未加载当场返回错误**（:141/:179/:234） |
| `ResolveModuleForLineAsync` | 同文件 :223 | 行定位模块解析：显式给定 → 单模块（未加载报错）；省缺 → 全部已加载模块扫描/消歧 |
| `LineBreakpointRetryHint` | 同文件 :23 | 现状引导文案「先 debug_continue 再设行断点，或 methodToken 待绑定」 |
| token pending 机制 | 引擎 `BreakpointManager.Add`（未绑定登记）+ `CallbackHandler.cs:56-64` LoadModule `TrackModule` 自动重绑 | **行断点缺的就是把「行→token」转换延后的登记**；token 断点无需本 spec |
| `DocumentService.GetTypeDocument`/`GetBreakpointTargetAtLine` | Decompiler/Document | 反编译行 → token+IL（**静态、不依赖目标进程**，模块文件可读即可） |
| `SourceLineResolver.Resolve` | Decompiler/Document | PDB 源行 → token+IL（同上，需模块旁 PDB） |
| 模块加载信号 | 引擎 CallbackHandler LoadModule → `TrackModule` 重绑 | **仅 rebound>0 才发 BreakpointsChanged**（:62）；行断点延迟场景引擎里无 pending 断点 → **无任何事件发出**，宿主无从得知模块已加载 |
| `SessionEventBuffer` | Session | 消费引擎 DebugEvent 流，暴露事件（`BreakpointsChanged` 等）供宿主/Web 订阅——模块加载通知的中转点 |

## 3. 分层设计与关键决策

### 3.1 信号：引擎 LoadModule 发 ModuleLoaded 事件（决策①）

现状 LoadModule 只重绑不发事件（除非 rebound>0）。行断点延迟项要「模块加载后自动补设」，必须先有模块加载信号穿透到宿主。改 `CallbackHandler.HandleEvent` LoadModule 分支：**TrackModule 后无条件发一个轻量事件**（新 `DebugEventKind.ModuleLoaded`，payload 带模块名/路径），不再依赖 rebound>0。

- 事件频率：LoadModule 每个程序集一次（进程启动期集中几十个，可忽略）；SessionEventBuffer 不累计状态（模块加载不影响停点/状态快照），只透传给订阅方。
- 为什么引擎发而不是宿主轮询 `GetModulesAsync`：事件驱动与现有架构一致（P4 快照推送精神），零轮询延迟；引擎改动极小（一处 case + 事件类型）。
- 兼容：现有 BreakpointsChanged 语义不变（断点集合变化仍发）；ModuleLoaded 是新增独立事件，互不替代。

### 3.2 延迟项登记：宿主工具层持有（决策②）

延迟项放**宿主 `DebugBreakpointTool`（静态表）**，而非 Session/引擎——因为「行→token」转换是宿主/Decompiler 能力（DocumentService），引擎无此能力也不该引。登记项：

```
record PendingLineBreakpoint(
    int RequestId,                 // 登记序号
    string LocatorKind,            // Type | Source
    string TypeName | SourcePath,  // 定位方式二选一
    int Line,
    string? ModuleName,            // 显式给的模块（未加载）；省缺=首次扫描未命中，模块加载后重扫
    int HitCount, DebugBreakpointMode Mode, string? Condition)   // 透传参数
```

登记时机（工具方法内）：行解析已就绪、但模块未加载/未命中 → 不再报错返回，改**登记延迟项并返回「已登记待绑定」**（文案对齐 token 断点 pending 的「断点已登记」）。模块已加载的既有路径零变化。

### 3.3 触发补设：宿主订阅 ModuleLoaded（决策③）

`DebugBreakpointTool` 订阅 Session 的 ModuleLoaded 事件（经 `DebugSessionManager.Active` 切换时重订阅，与 Web 重订阅模式同款），收到模块加载后重扫延迟项：

1. 取延迟项中 `ModuleName` 匹配新模块（或省缺项）的候选；
2. 对新模块做行解析（Type → `GetTypeDocument`+`GetBreakpointTargetAtLine`；Source → `SourceLineResolver.Resolve`）→ 得 token+IL；
3. 调 `Session.SetBreakpointAsync` 补设（**先查重**：同一 模块+token+IL+参数 已存在则跳过）；
4. 补设成功 → 移除延迟项、发 BreakpointsChanged（list 自动反映）；仍失败（该模块不是目标/行不在该模块）→ 保留延迟项等下一模块。

补设调用链与 set 工具内一致，复用同一解析逻辑（抽公共方法），避免双份实现。补设异步执行（不进工具请求上下文，ModuleLoaded 事件线程自行驱动），失败记 AgentActionLog + 日志，不抛。

### 3.4 失败/清理语义

| 场景 | 行为 |
|---|---|
| 登记后模块一直不加载 | 延迟项保持 pending；`debug_breakpoint_list` 展示（含「行待解析」标注）；disconnect/clear 清理 |
| 模块加载但行解析失败（行不在方法区间等） | 保留延迟项（可能后续模块命中），记日志；最终 disconnect 清理 |
| 行断点补设后重复命中 | 查重防重复补设 |
| 会话结束 | 延迟项表随会话清理（静态表按 session id 隔离或随 Active 切换清空） |
| agent 主动 remove | remove 时同步移除对应延迟项（按 RequestId/位置匹配） |

## 4. 工具面

```
debug_breakpoint_set（typeName+line / sourcePath+line）
  模块未加载 → 不再报错：登记延迟项，返回「断点已登记: id=… 位置=…（模块 X 尚未加载，加载后自动解析绑定）」
  模块已加载 → 现状不变（立即解析设断点）
debug_breakpoint_list → 延迟项展示「待绑定（行解析，模块加载后）」
debug_breakpoint_remove → 按 id 同时移除对应延迟项
LineBreakpointRetryHint 文案 → 更新（不再需要「先 continue 再设」的引导，改述延迟语义）
```

## 5. 测试计划

- **宿主 e2e**（DebugTarget，P9 launch 冻结 Main 前是天然场景）：launch 后立即 `debug_breakpoint_set typeName+line`（目标模块未加载）→ 返回「断点已登记」非报错；`debug_continue` → 模块加载后自动补设 → `debug_wait` 命中（无需手动重设）。sourcePath+line 同场景各一。
- **补设查重**：延迟项 + 同位置手动 set 不重复；remove 移除延迟项后模块加载不再补设。
- **回归**：模块已加载设行断点路径零变化（P3 既有用例）；token pending 机制零变化；全量五套件 + Client。

## 6. 工作量与顺序

引擎 ModuleLoaded 事件（0.5，含 SessionEventBuffer 透传）→ 宿主延迟项表 + 登记/清理（0.5）→ ModuleLoaded 订阅 + 补设（含解析逻辑复用抽取）（1）→ 工具面文案 + list/remove（0.5）→ 测试（1），≈ 3.5 人日。顺序即依赖序。

## 7. 待评审取舍点

① 引擎 LoadModule 无条件发 ModuleLoaded 事件 vs 只在「有延迟项」时才发（后者引擎需知道宿主有延迟项——耦合，倾向无条件发、事件轻量）；② 延迟项登记宿主静态表 vs Session 持有（倾向宿主——行解析在宿主侧，Session 引 Decompiler 会加重中枢依赖）；③ 省缺 moduleName 的延迟项在每次模块加载时全量重扫（v1 简单）vs 按类型名预判模块（P3 消歧复杂，v1 不预判）。
