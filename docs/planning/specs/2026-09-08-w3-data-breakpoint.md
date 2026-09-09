# Spec · W3 数据断点（值变化即停）

> 状态：**已评估 → 降级收尾（2026-09-10 spike 实测定案：A/B 均不可行，转 ROADMAP）**。原立项路径回顾——2026-09-09 拍板「spike 为计划 Task0（A/B 可行性 + 现代创建端查证三问），按 A 可行 / B 可行 / 降级 三分支走（分支在计划写死）；入口定案 = `debug_breakpoint_set` 加 `dataPath` 参数（A/B 任一可行时）；若 A/B 均不可行 → 降级 = 结论说明（含局限与「已知写入点用条件断点比较」指引）写入 spec/README 并转 ROADMAP，不写新 Engine 代码」。**2026-09-10 计划 Task0 spike 实测完成（结论见 §2）**：A `CreateBreakpoint()` 恒 E_NOTIMPL（运行时未实现）、B 无创建端——**两路均不可行，按计划降级分支 Task D 收尾**（spec §2 结论 + 根 README 指引 + ROADMAP 转远期；§3 方案 A/B 细则不再实施）。
> 关联：宿主 TODO W3（已评估转 ROADMAP）；复用 BreakpointManager/命令泵/CallbackHandler 停点体系 + P6 `ReadPathValue` 定位（值对象定位）原为 A/B 分支预案，已随降级不实施。

## 1. 背景与目标

字段/局部变量值被改时停下（VS「数据断点/值变化断点」等价）。场景：某字段被莫名改错值（多线程/回调/副作用），普通断点不知道何时改的，trace/条件断点轮询比较昂贵且时序敏感。

## 2. 技术查证结论（2026-09-08，本地 ClrDebug 源码实读）

ClrDebug 有**两条**相关能力，必须区分：

| 路径 | API | 语义 | 现状封装 |
|---|---|---|---|
| **A. ValueBreakpoint（值对象断点）** | `CorDebugValue.CreateBreakpoint()` → `ICorDebugValueBreakpoint` | 挂在**值对象**上（ICorDebugValue 生命周期），值对象被访问/变化时触发；**依赖值对象存活**（GC/变量离帧/值拷贝会失效） | ⚠️ 封装完整但**运行时未实现**：.NET 源码 `src/coreclr/debug/di/divalue.cpp` 的 `CordbValue::CreateBreakpoint` 恒返回 `E_NOTIMPL`（spike 实测确认）；官方 doc 亦标注「not implemented」 |
| **B. DataBreakpoint 回调（ICorDebugManagedCallback4）** | `OnDataBreakpoint` 事件 + `DataBreakpointCorDebugManagedCallbackEventArgs` | 数据断点命中通知（现代 .NET 运行时 ICorDebug 会话内） | ✅ 回调侧已封装（`CorDebugManagedCallback.cs:216`）；**回调只带 Process/Thread/Context——无断点身份/地址，须调试器自查线程寄存器（DR0-DR3）定位**，即配套硬件断点机制而非独立创建口 |

### 结论（2026-09-10 spike 实测定案：**A/B 均不可行 → 降级**，见计划 Task0/TaskD）

> 2026-09-10 真实 attach DebugTarget（probe-value 场景，ValueProbe.Run 入口断点停住）实测证据：

1. **问1（A-局部）不可行**：入口停点取到活 `CorDebugGenericValue`（I4 局部，有真实地址 0xD9EABFE7A0）→ `CreateBreakpoint()` **抛 `DebugException` HRESULT=0x80004001（E_NOTIMPL）**。值对象存活/地址都不是问题，是运行时压根没实现该 API。
2. **问2（A-字段）不可行**：同停点经 `GetFieldValue` 取到活字段值（I4，真实地址）→ `CreateBreakpoint()` **同样抛 E_NOTIMPL**。与运行时源码 `CordbValue::CreateBreakpoint => return E_NOTIMPL;` 一致（所有 ICorDebugValue 派生共用该实现）。
3. **问3（B）无创建端**：ClrDebug grep 全部 `ICorDebugProcess*`（1-11）无任何数据断点创建方法（仅 `ICorDebugProcess2::SetUnmanagedBreakpoint/ClearUnmanagedBreakpoint` 原生地址断点）；`CreateDataBreakpoint` 只在 DbgEng（WinDbg 调试引擎）服务接口。`OnDataBreakpoint` 回调整个 attach+运行窗口**零触发**（无创建端→无事件）。.NET Core 3.0+ 的 VS「值变化断点」由 VS 自有机制（内部读对象地址 + 线程硬件寄存器 DR0-DR3 注册 + 运行时 DataBreakpoint 通知）实现，非公开 ICorDebug 创建口，无法在 ICorDebug 会话内自建。

**结论**：W3 数据断点在现有 ICorDebug 通道**不可实现**（A 被运行时 E_NOTIMPL 硬性堵死；B 无创建端且需原生硬件寄存器机制）。按计划降级分支 Task D 收尾：**不写 Engine/宿主代码**，数据断点暂不支持，转 `docs/ROADMAP.md` 远期（触发条件：未来 ICorDebug 若暴露数据断点创建 API 再评估）。「字段被莫名改错（多线程/回调/副作用）」场景现可用**已知写入点条件断点**替代（见根 README debug_breakpoint_set 行指引）。

## 3. 方案（spike 后细化）

### 3.1 Engine
- 若 A 可行：`debug_breakpoint_set` 加 `dataPath`（如字段路径），命令泵内定位值对象（复用 W1/ReadPathValue 定位）→ `CreateBreakpoint()` → 登记；命中走 OnDataBreakpoint/ValueBreakpoint 事件 → 停（复用 BreakpointHit 体系）。生命周期：值对象失效时自动清理+提示。
- 若 B 可行且有创建端：按地址/变量注册数据断点。

### 3.2 宿主
`debug_breakpoint_set` 扩展 `dataPath` 参数（2026-09-09 定案入口）——定位数据断点目标（复用 P6 路径文法/`ReadPathValue` 定位），命中/计数/条件/清单/移除复用既有断点体系；返回描述注明「数据断点 @ 路径 X」。

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
