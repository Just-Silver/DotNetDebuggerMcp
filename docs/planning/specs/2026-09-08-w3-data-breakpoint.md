# Spec · W3 数据断点（值变化即停）

> 状态：**计划中（草案）；关键技术已查证但需 spike 定路线**。立项前置 = spike 结论（本 spec §4）。
> 关联：宿主 TODO W3；复用 BreakpointManager/命令泵/CallbackHandler 停点体系。

## 1. 背景与目标

字段/局部变量值被改时停下（VS「数据断点/值变化断点」等价）。场景：某字段被莫名改错值（多线程/回调/副作用），普通断点不知道何时改的，trace/条件断点轮询比较昂贵且时序敏感。

## 2. 技术查证结论（2026-09-08，本地 ClrDebug 源码实读）

ClrDebug 有**两条**相关能力，必须区分：

| 路径 | API | 语义 | 现状封装 |
|---|---|---|---|
| **A. ValueBreakpoint（值对象断点）** | `CorDebugValue.CreateBreakpoint()` → `ICorDebugValueBreakpoint` | 挂在**值对象**上（ICorDebugValue 生命周期），值对象被访问/变化时触发；**依赖值对象存活**（GC/变量离帧/值拷贝会失效） | ✅ 全封装（`CorDebugValue.cs:162` + `CorDebugValueBreakpoint.cs`）；**命中回调**？——见下 |
| **B. DataBreakpoint 回调（ICorDebugManagedCallback4）** | `OnDataBreakpoint` 事件 + `DataBreakpointCorDebugManagedCallbackEventArgs` | 数据断点命中通知（现代 .NET 运行时 ICorDebug 会话内） | ✅ 回调侧已封装（`CorDebugManagedCallback.cs:216`） |

### 关键不确定点（spike 必答）

1. **A 的触发条件**：ICorDebugValueBreakpoint 触发需要什么？是否= "值对象被写"（写监视）还是"任何访问"？值对象**何时创建**（GetLocalVariable 拿到即活？还是需 EnumerateVariables 保持引用）？GC/帧离开后是否失效？
2. **B 的创建端在哪**：有 DataBreakpoint **回调**但没搜到"创建数据断点"的 ICorDebug API——.NET 数据断点创建是否走 `ICorDebugProcess` 扩展接口（如 `ICorDebugProcess5+`）/新版本接口？ClrDebug 是否封装？还是 B 只响应 A 触发的通知？
3. **两者关系**：B 是否就是 A（ValueBreakpoint）触发的通知通道？还是独立硬件断点机制？

> 判断（待 spike 证伪）：A（ValueBreakpoint）很可能受"值对象存活"约束——对**栈上局部**，值对象在 GetLocalVariable 时创建、帧还在就活（可行）；对**对象字段**，经 GetFieldValue 拿到的字段值对象可能只是快照、写不触发。B 若为现代数据断点通道则需先有创建 API。

## 3. 方案（spike 后细化）

### 3.1 Engine
- 若 A 可行：`debug_breakpoint_set` 加 `dataPath`（如字段路径），命令泵内定位值对象（复用 W1/ReadPathValue 定位）→ `CreateBreakpoint()` → 登记；命中走 OnDataBreakpoint/ValueBreakpoint 事件 → 停（复用 BreakpointHit 体系）。生命周期：值对象失效时自动清理+提示。
- 若 B 可行且有创建端：按地址/变量注册数据断点。

### 3.2 宿主
`debug_breakpoint_set` 扩展 or 新参数：`dataPath`（定位数据断点目标）+ 复用 hitCount/condition。

## 4. Spike 计划（立项前置）
1. 写最小验证：attach DebugTarget → GetLocalVariable 拿 int 局部 → CreateBreakpoint → continue → 改该变量 → 是否停？（A 可行性）
2. 对象字段同测（A 对字段是否有效）。
3. 查 .NET 现代数据断点创建 API：本地 ClrDebug `ICorDebugProcess*`/`ICorDebugProcess5` 是否有创建入口；grep 已无 DataBreakpoint 创建端——查是否走 `ICorDebugDataTarget`/诊断口而非 ICorDebug。
4. 结论三选一：A 可用（做 ValueBreakpoint 版）/ B 可用（做现代版）/ 都不行 → **降级方案**：条件断点比较（对已知写入点）+ 说明局限，W3 转 ROADMAP。

## 5. 验证
- spike 转正：DebugTarget 造"字段被改"场景，数据断点停住断言值。
- 降级路径：中文说明"数据断点不可用，用条件断点方案"。

## 6. 难度
- **大**（若 A/B 任一可行则中-大：值对象生命周期管理是新增复杂度；若不可行则降级为小+转远期）。
