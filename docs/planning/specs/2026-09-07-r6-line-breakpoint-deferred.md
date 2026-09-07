# Spec · R6 行断点模块加载前可用（延迟解析登记）

> 状态：**已定稿**（2026-09-07，方案 C 经评审确认）。
> 关联：宿主 TODO R6；agent 实战反馈清单 R6（2026-09-06 收到）；P3 行断点（typeName+line 反编译行 / sourcePath+line PDB 源行）；P9 launch 早期接管（模块未加载是常态）。
> **关键参考：本地克隆 sharpdbg**（`src/SharpDbg.Infrastructure/Debugger/ManagedDebugger.cs:309` TryBindBreakpoint / `:376` TryBindPendingBreakpoints / `:119-127` HandleModuleLoaded）——源码行断点模型直接抄它。dnSpy/ILSpy 经核实**无此机制可参考**（dnSpy 断点设置时模块已定；ILSpy 无调试器）。

## 1. 背景与目标

agent 用行断点（`typeName`+`line` 反编译行 / `sourcePath`+`line` PDB 源码行）调试时，**目标模块未加载就设断点会被当场拒绝**——报「模块 X 未加载（行定位方式要求模块已加载）」（`DebugBreakpointTool` 的 `ResolveModuleForLineAsync`/`SetBySourceLineAsync`）。token 断点天然支持 pending（`BreakpointManager.Add` 未绑定 + LoadModule 自动重绑），行断点却要等模块加载后才能设。

痛点场景：P9 之后 `debug_launch` 返回即冻结在 Main 前，**目标业务模块尚未加载是常态**——agent 想「先看代码、在入口行设断点、再 continue」，却被迫多一轮往返。

目标：**sourcePath+line 断点支持延迟登记**——模块未加载时登记为 pending，模块加载后自动按 PDB 解析「源行 → token+IL」并绑定，agent 无需感知模块加载时机。

非目标：typeName+line（反编译行）断点延迟化（v1 不做——反编译行本质绑定具体模块的反编译视图，模块未加载无从 decompile，语义上就该等模块，保持现状报错+引导）；Web/CLI 断点面板；模块卸载重绑。

## 2. 现状锚点（起草时已核实）

| 设施 | 位置 | 关系 |
|---|---|---|
| `SourceLineResolver.Resolve(modulePath, sourcePath, line)` | Decompiler `Document/SourceLineResolver.cs` | PDB 源行 → token+IL。**纯 SRM + 磁盘 PDB，零 Decompiler/ICSharpCode 依赖**（文件头自注「纯 SRM，无 ICorDebug 依赖」），可直接移植引擎 |
| token pending 机制 | 引擎 `BreakpointManager.Add`（未绑定登记）/ `TrackModule`（LoadModule 自动重绑） | 现成。源行断点延迟 = 新增「未解析的源行延迟项」，TrackModule 时解析补设 |
| 模块加载信号 | 引擎 CallbackHandler LoadModule → `TrackModule` | **引擎内已有**（不需要宿主层事件）——延迟解析在引擎内 TrackModule 时做，天然覆盖「新模块刚加载」 |
| `BreakpointManager.TrackModule` | `Engine/BreakpointManager.cs:22` | 每次模块加载登记模块并重绑 pending token 断点——**源行延迟项解析补设的同款时机** |
| sharpdbg 参考 | `ManagedDebugger.cs` | 源码行断点 = 纯 `(FilePath, Line)` 登记（`Verified=false`）；每次模块加载 `TryBindPendingBreakpoints` 全量重试：遍历已加载模块、逐模块 PDB `ResolveBreakpoint(FilePath, Line)`，命中即绑；绑不到保持 pending + Message「no symbols」 |

## 3. 方案（C，sharpdbg 模型 + 引擎内闭环）

### 3.1 核心模型：源行延迟项（决策①，sharpdbg 同款）

不扩展现有 token `DebugBreakpoint`，而是新增**独立的源行延迟项**（登记时无 token）：

```
Engine（新，BreakpointManager 或独立 PendingSourceLineBreakpoint 表）
  record SourceLinePending(
      int RequestId, string SourcePath, int Line,
      int HitCount, DebugBreakpointMode Mode, string? Condition,   // 透传
      string? ModuleName)          // 显式模块提示（可选；省缺=全模块试）
```

- 登记 `AddPendingSourceLine(...)` → 返回 id，不绑定（pending）。
- `TrackModule`（模块加载）时，对每个源行延迟项：`SourceLineResolver.Resolve(模块磁盘路径, SourcePath, Line)`——命中（该模块 PDB 含此源文件+行）→ `Bind` 成真断点（落 token+IL）、从延迟项转正；未命中 → 保持 pending。
- sharpdbg 是**每次模块加载全量重试所有 pending**（遍历所有已加载模块逐个 Resolve）。我们同款：TrackModule 里对新增模块 Resolve；bind 成功后延迟项转正（后续模块不再试）。若一个源文件被多模块共享（罕见），sharpdbg 绑多个 binding——我们 v1 绑**第一个命中模块**（`debug_breakpoint_list` 展示绑定模块名；共享源文件多模块场景 agent 可显式给 moduleName 消歧）。

### 3.2 层向论证：SourceLineResolver 进引擎不破边界（决策②）

引擎 AGENTS 边界「不反编译、不解析类型名，全用 token」——但 `SymbolNameResolver`/`TypeNameResolver` 已是引擎内读模块元数据（PDB 局部名在引擎内做）的先例。`SourceLineResolver` **纯 SRM 读 PDB 序列点，不反编译**，逻辑上属引擎已有能力（与 SymbolNameResolver 同族），**拷贝/迁移进引擎不引入 Decompiler 依赖、不破依赖方向**。

实施：把 `SourceLineResolver` 从 Decompiler **复制**到 Engine（Engine 不引 Decompiler，只能复制；Decompiler 原版保留——宿主/Web 反编译行断点仍用）。两处同源，后续若漂移以引擎版为准（或注释互指）。

### 3.3 工具面（宿主）

`debug_breakpoint_set` sourcePath+line 分支：模块未加载/未命中时**不再报错**，改登记源行延迟项：

```
SetBySourceLineAsync：
  现有逻辑（已加载模块内 Resolve → 立即绑）不变；
  若显式 moduleName 且未加载，或省缺 moduleName 且已加载模块全试不命中：
    → Session/引擎新增 SetSourceLineBreakpointAsync(modulePath: null 或 ""，sourcePath, line, ...)
      登记 pending，返回「断点已登记: id=…（模块加载后按 PDB 自动解析绑定）」
debug_breakpoint_list → 延迟项展示「待绑定（源行，模块加载后解析）」
debug_breakpoint_remove/clear → 同 id 移除
disconnect/会话结束 → 延迟项随断点表清理
```

### 3.4 引擎 API 形状

```
Engine DebugSession（新，仿 SetBreakpointAsync）
  Task<DebugBreakpoint> SetSourceLineBreakpointAsync(string modulePath, string sourcePath, int line, int hitCount=1, mode=Stop, condition=null, ct)
    —— modulePath 非空：登记延迟项（附模块提示）；TrackModule 命中该模块才试
    —— modulePath 空：登记延迟项（省缺=任意模块命中即试，sharpdbg 语义）
    登记即返回 id（pending）；模块已加载时若可立即 Resolve 命中则当场绑定（IsBound=true）
宿主 DebugBreakpointTool sourcePath+line 分支改为调它（原来先查模块再 Resolve 的逻辑保留为「已加载快速路径」，未加载走延迟登记）
```

### 3.5 失败/清理语义

| 场景 | 行为 |
|---|---|
| 模块一直不加载 | 延迟项保持 pending；list 可见；disconnect/clear 清理 |
| 模块加载但 Resolve 失败（无 PDB/无此源文件/行无映射） | 保持 pending（可能后续模块命中），不阻塞 |
| 多模块含同源文件 | v1 绑首个命中模块；显式 moduleName 消歧 |
| 绑成功后 | 延迟项转正为普通断点（id 不变，IsBound=true），list 显示模块+token |

## 4. 测试计划

- **Engine 集成**（DebugTarget，P9 launch 冻结 Main 前是天然场景）：launch 后（DebugTarget.dll 未加载）立即 `SetSourceLineBreakpointAsync(modulePath:"", "DebugTarget.cs", Work 内行)` → 返回 pending id、IsBound=false；continue → DebugTarget.dll 加载 → TrackModule 解析补绑 → 命中停。**必须先 continue 场景（attach 已加载/运行中）**：模块已加载时登记即绑。
- **源行延迟项单测**：TrackModule 命中/未命中/多模块首中；remove/clear 清理。
- **宿主 e2e**：`debug_breakpoint_set sourcePath+line` 未加载 → 返回「断点已登记」非报错；continue → 自动补设 → `debug_wait` 命中（无需手动重设）。
- **回归**：模块已加载 sourcePath 断点路径零变化（P3 用例）；typeName+line 现状零变化；全量套件 + Client。

## 5. 工作量与顺序

SourceLineResolver 迁引擎 + 源行延迟项表 + TrackModule 解析补设（1.5）→ 引擎 API SetSourceLineBreakpointAsync（0.5）→ 宿主工具面改造（0.5）→ 测试（1），≈ 3.5 人日。

## 6. 与旧草案差异（2026-09-07 定稿修订）

旧草案设想「引擎发 ModuleLoaded 事件 → 宿主登记延迟项 → 订阅补设」；查证 sharpdbg 后废弃——**源行延迟解析全程引擎内闭环即可**（TrackModule 是现成时机），无需新事件、无需宿主订阅、无需跨层回调；「省缺 moduleName 全量重扫」从取舍点改为 sharpdbg 同款默认语义。typeName+line 排除在范围外（反编译行语义绑定模块视图）。
