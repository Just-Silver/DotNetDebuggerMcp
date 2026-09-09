# Spec · D2 子进程/多进程跟随（发现 + 引导切换）

> 状态：**已立项**（2026-09-09 拍板）——① 父子查询 = **Toolhelp P/Invoke 落宿主**（kernel32 `CreateToolhelp32Snapshot`，Engine 零新增依赖）；② 入口 = 增强 `debug_processes` 全链标注（多层缩进 + 切换引导），不新增 debug_children；③ launch 不主动提示（只标注，子进程输出边界写 README/描述）。实施计划见 `docs/planning/plans/2026-09-09-d2-child-process.md`，规格冻结。
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

### 父子关系获取：技术路径对比（2026-09-09 拍板版，含查证修正）

> **查证修正（2026-09-09）**：原稿「Engine 已依赖 System.Management（ClrDebug 也引）」**有误**——实读 `DotNetDebugger.Engine.csproj` 仅 ClrDebug + DbgShim 两包；FlaUI 尚未引入。另发现 spec 未评的 **Toolhelp** 零依赖路径。

| 路径 | 机制 | 优点 | 缺点 | 结论 |
|---|---|---|---|---|
| **Toolhelp（拍板）** | `kernel32 CreateToolhelp32Snapshot` + `PROCESSENTRY32.th32ParentProcessID`（P/Invoke，~30 行） | **零新包**、单次快照拿全表父子、无需 WMI 服务、无需逐 pid 开句柄 | 需 Windows 专属 P/Invoke（调试器本就 win-x64） | **采用**——落宿主（Engine 零新增依赖） |
| CIM/WMI `Win32_Process` | `System.Management` 包的 `ManagementObjectSearcher` | 含 CommandLine 等富信息 | 需加包（宿主或 Engine）；依赖 WMI 服务可用；一次全表 ~百 ms | 不用（拍板淘汰） |
| `NtQueryInformationProcess(ProcessBasicInformation)` | ntdll P/Invoke | 快 | 逐 pid 系统调用；x86/x64 结构体差异 | 不用（Toolhelp 更简） |

> 注：父子关系对**已退出父进程**会失效（PID 复用歧义），但对"父进程是当前调试会话目标（存活）"场景完全可靠。

## 3. 分层设计（2026-09-09 拍板：宿主 Toolhelp，Engine 零改动）

### 3.1 宿主：父子关系快照助手（新增只读，不碰 Engine/ICorDebug）

新内部助手（`Tools/Debugger/` 或 `Services/`，与 debug_processes 同层）：
```
ChildProcessSnapshot（internal）：
  QueryAll() → IReadOnlyDictionary<int,int>（pid → parentPid）：kernel32 CreateToolhelp32Snapshot 一次性快照
  ChildrenOf(pid, allProcesses) → 递归收集 .NET 子孙进程链（多层）：allProcesses=ClrProcessFinder.List() 交集
```
- 只读、无会话关联；快照失败（权限/P/Invoke 异常）降级：返回空字典，调用方提示"无法判定父子关系"。
- **子进程必须是 .NET**（dbgshim 探测交集）才标注——否则 attach 无意义。
- Engine/`ClrProcessFinder`/`ClrProcessInfo` **零改动**（spec 原案「Engine 进程信息扩展」随拍板取消）。

### 3.2 宿主：debug_processes 增强

现状输出行 `pid=28344 CoreMes (CLR 10.0.9) ← 当前会话`。增强（不动现有排序/过滤/截断/100 上限）：
```
新增：当存在"当前会话目标"时，其 .NET 子孙进程行追加父链标注（多层缩进）：
  pid=29102  Worker  (CLR 10.0.9)  ← 会话目标 CoreMes(28344) 的子进程
  pid=29110  WorkerChild (CLR 10.0.9)  ← 会话目标 CoreMes(28344) 的孙进程（父 29102 Worker）
```
返回文案附引导（见 3.3）。**不新增 `debug_children`**（拍板：发现入口集中在 debug_processes）。

### 3.3 切换引导（工具返回文案）

发现子进程时返回附引导：「子进程业务代码（如 testhost/worker）需停当前会话（debug_disconnect/停断点）后 debug_attach <childPid> 单独调试」——**明确单活动会话边界**，防 agent 误以为可同时调试。

## 4. 拍板记录（2026-09-09 用户拍板）

1. **父子查询机制/落点**：**Toolhelp P/Invoke 落宿主**（kernel32 `CreateToolhelp32Snapshot` + `th32ParentProcessID`，~30 行，零新包、免 WMI、免逐 pid 句柄）；Engine 保持仅 ClrDebug + DbgShim。spec 原「Engine 已依赖 System.Management」查证有误，CIM/ntdll 两路径拍板淘汰。
2. **入口形态**：增强 `debug_processes`（多层链缩进标注 + 引导文案）；不新增 `debug_children`。
3. **链深度**：全链多层（递归收集子孙；一次 Toolhelp 快照已含父子全表，只是过滤+递归）。
4. **launch 主动提示**：v1 不做（只在 debug_processes 标注 + 返回引导）；「子进程 stdout 不在 ProcessOutputCapture 范围」边界写 README/工具描述。

## 5. 验证方案
- **宿主单测**：能 spawn 子进程的测试目标（DebugTarget 加 `spawn` 模式：自起一个 `dotnet DebugTarget.dll <arg>` 子进程并等待）→ Toolhelp 快照断言父子链。
- **宿主 e2e**：launch 会起子进程的目标 → `debug_processes` 显示子进程标注 → 停会话 attach 子进程成功。
- 快照失败降级路径单测（P/Invoke 不可用提示）。

## 6. 依赖与工作量
- 依赖：无新包（kernel32 P/Invoke）；`ClrProcessFinder` 现有枚举原样复用。
- 改动面：宿主（Toolhelp 助手 ~40 行 + debug_processes 标注逻辑）；**Engine/会话模型零改动**。
- 难度：**小-中**。
