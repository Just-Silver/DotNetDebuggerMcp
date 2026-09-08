# Spec · V2 崩溃现场自动保留（dump + 轨迹）

> 状态：**计划中（草案）**——退出事件现状已查证，dump 路径需 spike。实施前置：宿主 TODO V2 立项 + V3（时间线）落地（"崩溃前轨迹"复用其历史缓冲）。
> 关联：宿主 TODO V2；依赖 V3 时间线（崩溃前最后 N 条）；与 ROADMAP「ClrMD dump 事后分析」同源。

## 1. 背景与目标

进程崩溃/异常退出后**现场即失**——agent 只能看到 `debug_output` 尾部（日志可能没刷完/环形缓冲被冲），栈、变量、调用现场全没了。目标：**崩溃时自动保留 dump + 崩溃前 trace/日志轨迹**，供 agent 复盘「死前发生了什么」。

v1 能力分级：
- **v1a（引导）**：会话退出时判定"是否异常退出"，给 agent 明确提示 + 引导 dump 命令。
- **v1b（自动抓）**：检测到崩溃信号时自动落 dump 文件 + 附 V3 时间线尾部。
- **v2（分析）**：ClrMD/dotnet-dump 读 dump 给结构化分析（ROADMAP 已有候选）。

非目标：完整事后分析引擎（v2）；所有退出都 dump（正常退出不 dump，浪费）。

## 2. 现状与技术查证（2026-09-08）

| 设施 | 现状 | V2 关系 |
|---|---|---|
| ExitProcess 回调 | `CallbackHandler.cs:52` 只发 `Exited` 状态（"process exited"） | **无退出码、无崩溃判定**——第一增量 |
| `Process.ExitCode` | Session 层 Process 对象有 | 判定"是否崩溃"的数据源之一 |
| V3 时间线（规划中） | 事件历史缓冲 | 崩溃前轨迹源 |
| `ProcessOutputCapture` | 环形 2000 行 | 崩溃前日志 |
| dotnet-dump / ClrMD | 外部工具 | dump 分析（v2） |

### dump 获取路径（查证后修正：原「WER 系统兜底」前提有误）

| 路径 | 机制 | 时机 | 结论 |
|---|---|---|---|
| **A. 调试会话内抓（主路径）** | 检测到崩溃前（异常停点），诊断口 `DiagnosticsClient.WriteDump` 或 MiniDumpWriteDump | 目标仍存活（first-chance 异常停点 = 进程活着的窗口） | **推荐**——你们已有异常停点 = 可靠抓取窗口；需 spike：被 ICorDebug 冻结的进程是否还能响应诊断口 IPC |
| ~~B. WER LocalDumps~~ | ~~Windows 注册表配置崩溃自动出 dump~~ | 进程崩溃瞬间（系统级） | **否决（2026-09-08 查证）**：① 默认不启用且需管理员 + WER 服务运行；② 微软文档明言 WER LocalDumps **不支持自定义崩溃报告的应用，.NET 在其列**（.NET 有自研 createdump）——对 .NET 进程此路径基本无效 |
| **C. `DOTNET_DbgEnableMiniDump=1`（辅助）** | .NET 运行时自研 createdump，崩溃自动出 dump（`DOTNET_DbgMiniDumpType=2` Heap / `DOTNET_DbgMiniDumpName` 指定路径） | 目标自崩溃（不经调试会话） | **可选**：`debug_launch` 时注入该环境变量，未托管崩溃也能留 dump；**但被 ICorDebug 调试时的触发行为需 spike**（debugger 附加时崩溃路径不同） |

> **关键事实（2026-09-08 查证）**：.NET 程序崩溃**默认不生成任何 dump**——`DOTNET_DbgEnableMiniDump` 默认 0、WER LocalDumps 默认不启用。唯一不经配置的抓取窗口是**调试会话内的异常停点**（路径 A），这正是 V2 与普通运行最不同的价值点。
>
> **最大 spike 点**：被 ICorDebug 调试的进程，崩溃时各自动 dump 机制是否触发/与调试会话冲突——需实测路径 A（冻结进程 WriteDump 可行性）与路径 C（调试下 createdump 行为）。

## 3. 分层设计

### 3.1 Engine：退出信息增强（第一增量，小）
ExitProcess 事件携带退出码（从 Process 取）+ 判定：
- 正常退出（exitCode==0 或明确 Exit）vs 异常退出（非 0/崩溃）
- 发布 `Exited` 状态时带 `Reason`（"process exited code=134" 等）——现有 `SessionStateChangedPayload.Reason` 已有承载位。

### 3.2 Session：崩溃信号 → 保留现场（依赖 V3）
- V3 落地后：崩溃判定时自动附「崩溃前时间线最后 N 条」到 debug_wait/debug_state 返回（消费式）。
- v1b 自动 dump：见 §3.3。

### 3.3 dump 落盘（spike 后定）
- **spike A**：异常停点（`HandleException2` first-chance 命中停住时）调用 `DiagnosticsClient.WriteDump(DumpType.WithHeap)`（目标仍存活+冻结）→ 验证 dump 完整性与耗时（冻结中抓 dump 是否可行——目标响应诊断口？被 ICorDebug 冻结的进程是否还能响应 IPC？）。这是**最大不确定性**。
- **spike B**：WER LocalDumps 预配置（`HKLM\SOFTWARE\Microsoft\Windows\Windows Error Reporting\LocalDumps\<exe名>`）——目标崩溃自动出 dump，agent 事后经 `dotnet-dump analyze` 或 v2 ClrMD 分析。零引擎改动，但只覆盖"系统级崩溃"（托管未处理异常若被 ICorDebug first-chance 拦住则不会走到 WER——**被调试的未处理异常行为需实测**）。

### 3.4 宿主：引导 + 新工具
- `debug_state`/`debug_wait` 在 Exited 时提示「进程异常退出（code=N）——可用 debug_dump 抓现场 / 已自动保留崩溃前轨迹」。
- 新工具 `debug_dump`（v1b）：`path`（dump 文件路径，默认会话目录）→ 触发 §3.3 dump。

## 4. 待拍板（立项时决策，多数需 spike 结论）
1. **崩溃判定标准**：exitCode!=0？还是有异常事件但未恢复？「正常退出 vs 异常退出」边界（.NET 程序可能 exitCode!=0 但"预期退出"）。
2. **路径 A vs B**（会话内抓 vs WER 系统兜底）——spike 结论决定；可能 A 不可行（冻结进程不响应 IPC）则走 B。
3. **自动 vs 引导**：v1a 引导（推荐，零风险）vs v1b 自动抓（需 spike 可靠才做）。
4. **dump 时机**：异常停点抓（可能太早——异常可能被 catch）vs 进程退出前抓（太晚——已不在）。需定义"什么算崩溃"。

## 5. 验证方案
- **Engine 单测**：DebugTarget 造崩溃（未处理异常/Environment.Exit(非0)）→ 断言 Exited 带退出码与崩溃判定。
- **spike 记录**：路径 A 可行性结论（冻结进程 WriteDump）+ 路径 B WER 配置实效。
- **宿主 e2e**：崩溃 → debug_state 提示 + 轨迹保留 + （若 v1b）dump 文件生成。

## 6. 依赖与工作量
- 依赖：V3 时间线（崩溃前轨迹）；Engine 退出信息增强（小，可独立先行）。
- 改动面：Engine（退出码+判定）+ Session（轨迹保留挂接）+ 宿主（提示 + debug_dump）。Spike 结果决定 dump 路径工程量。
- 难度：**中**（判定语义 + dump 路径 spike 是主要成本；v1a 引导版可先做，成本小）。
