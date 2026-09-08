# Spec · D2 子进程/多进程跟随（发现 + 引导切换）

> 状态：**计划中（草案）**——进程发现设施与父子关系获取技术已查证，供后续实施直接参照。
> 关联：宿主 TODO D2；复用 `ClrProcessFinder`（P8）/`debug_processes`（R2/R8 已做排序+会话标注）。

## 1. 背景与目标

Web/服务型目标常**自起子进程**（`dotnet run` 起 app、app 再 spawn worker/testhost、IIS 式宿主），实际业务代码跑在子进程里。现状单活动会话只能 attach 一个进程，agent 要调试子进程时：`debug_processes` 列出全部却**看不出父子关系**，只能靠猜哪个是目标的子进程。

**D2 能力**：`debug_processes`（或新入口）能发现"当前会话目标的子进程链"，标注父子关系，引导 agent 停当前会话 → attach 子进程。**不做多会话并行调试**（ROADMAP 已记 Engine 并行 attach 相互干扰实测）。

非目标：自动跟随/自动切换会话（v1 单活动会话模型下只做"发现+引导"）；多进程同时断点（需多会话引擎改造，远期）。

## 2. 现状与技术查证（2026-09-08）

| 设施 | 位置 | 与 D2 关系 |
|---|---|---|
| `ClrProcessFinder.List()` | `Engine/Engine/ClrProcessFinder.cs:18` | 已枚举全部可附加 .NET 进程（dbgshim 权威探测 CLR）；**无父子信息** |
| `debug_processes` 宿主工具 | `Tools/Debugger/DebugProcessTool.cs` | 已做 排序/当前会话标注/filter/100 截断；**无父子标注** |
| `Process`（System.Diagnostics） | — | **无 Parent API**（.NET 标准库不提供父进程读取） |

### 父子关系获取：技术路径对比（本地已具备 CIM 能力）

| 路径 | 机制 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| **CIM/WMI** `Win32_Process` | `Get-CimInstance Win32_Process`（PowerShell）或 `System.Management` NuGet 的 `ManagementObjectSearcher("SELECT ProcessId,ParentProcessId,... FROM Win32_Process")` | 一步拿全表（含 ParentProcessId/CommandLine/ExecutablePath）；语义清晰 | 慢（一次全表查询 ~百 ms 级）；需 WMI 服务可用 | **推荐**——Engine 已依赖 `System.Management`（FlaUI Core 引入的依赖链中有，ClrDebug 也引） |
| `NtQueryInformationProcess(ProcessBasicInformation)` | P/Invoke ntdll | 快、单进程查询 | 每 pid 一次系统调用（全表要 N 次）；x86/x64 结构体差异 | 备选（如需免 WMI） |
| 进程环境块（PEB 父链） | 读 PEB | 无 API 调用 | 复杂、易碎 | 不用 |

> 注：`Win32_Process.ParentProcessId` 对**已退出父进程**会失效（PID 复用歧义），但对"父进程是当前调试会话目标（存活）"场景完全可靠。

## 3. 分层设计

### 3.1 Engine：进程信息扩展（新增只读发现，不动 ICorDebug）

`ClrProcessFinder` 新增（或并列新方法）：
```
ClrProcessInfo 扩展：加 ParentProcessId(int?，可空——非 CIM 路径时为 null)
FindChildren(pid) → IReadOnlyList<ClrProcessInfo>：CIM 查全表一次，过滤 ParentProcessId==pid 且为 .NET 进程
```
- 只读、无会话关联（沿用 P8 纯发现定位）；CIM 失败降级：返回空 + 调用方提示"WMI 不可用，无法判定父子"。
- 复用 DbgShim 探测 CLR（子进程必须是 .NET 才列——否则 attach 无意义）。

### 3.2 宿主：debug_processes 增强

现状输出行 `pid=28344 CoreMes (CLR 10.0.9) ← 当前会话`。增强（不动现有排序/过滤/截断）：
```
新增：当存在"当前会话目标"时，其子进程行追加父链标注：
  pid=29102  Worker  (CLR 10.0.9)  ← 会话目标的子进程（父 28344 CoreMes），可停当前会话后 debug_attach 29102
多级链（孙进程）：缩进或标注 父→子→孙
```
或独立新工具 `debug_children`（当前会话目标 → 列出其 .NET 子进程链）。**倾向增强 `debug_processes`**（少一个工具、发现入口集中；父子标注仅在有会话目标时有意义）。

### 3.3 切换引导（工具返回文案）

发现子进程时返回附引导：「子进程业务代码（如 testhost/worker）需停当前会话（debug_disconnect/停断点）后 debug_attach <childPid> 单独调试」——**明确单活动会话边界**，防 agent 误以为可同时调试。

## 4. 待拍板（立项时决策）
1. **CIM 依赖**：`System.Management` 包引入（FlaUI 生态同款，见 U1 spec 的 FlaUI.Core 依赖 System.Management 10.x）vs P/Invoke `NtQueryInformationProcess` 免包。倾向 System.Management（标准、Engine 依赖面已接近）。
2. **入口形态**：`debug_processes` 加父子标注 vs 新 `debug_children`。倾向前者（见 §3.2）。
3. **链深度**：只一层子进程 vs 全链（孙进程缩进）。倾向支持多层链标注（一次 CIM 全表已含，只是过滤+递归）。
4. **launch 场景子进程**：`debug_launch` 目标自起子进程时，是否在会话信息里主动提示"发现子进程 X"（配合 debug_output 已捕获父进程输出，但子进程输出没捕获——**子进程 stdout 不在 ProcessOutputCapture 范围**，需说明边界）。

## 5. 验证方案
- **Engine/宿主单测**：用能 spawn 子进程的测试目标（DebugTarget 加"spawn child"模式 或 宿主测试直接起 `dotnet <child>.dll`）→ FindChildren 断言父子链。
- **宿主 e2e**：launch 一个会起子进程的 .NET 程序 → debug_processes 显示子进程标注 → 停会话 attach 子进程成功。
- CIM 不可用降级路径单测。

## 6. 依赖与工作量
- 依赖：`System.Management`（新）或 ntdll P/Invoke；`ClrProcessFinder` 现有枚举。
- 改动面：Engine（进程信息扩展 ~40 行）+ 宿主（debug_processes 标注逻辑）。无 ICorDebug/会话模型改动。
- 难度：**小-中**。
