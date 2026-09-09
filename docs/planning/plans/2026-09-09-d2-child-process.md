# 实施计划 · D2 子进程/多进程跟随（debug_processes 父子标注）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施。Step 用 `- [ ]` 追踪。

**Goal:** `debug_processes` 能标注「当前会话目标的 .NET 子孙进程链」（多层缩进）+ 切换引导（停会话 → debug_attach <childPid>），帮 agent 找到 Web/worker 类目标自起子进程里的业务代码。**只发现+引导，不做多会话并行。**

**Architecture:** 宿主新增 Toolhelp 快照助手（kernel32 P/Invoke，单次快照拿全表 pid→parentPid），与既有 `ClrProcessFinder.List()`（.NET 进程枚举）交集，把当前会话目标的子孙链标注到 `debug_processes` 输出行尾。Engine/Session/会话模型**零改动**。

**Tech Stack:** C# net10.0、kernel32 `CreateToolhelp32Snapshot`/`Process32First/Next` P/Invoke、ModelContextProtocol.Server。

**Spec:** `docs/planning/specs/2026-09-08-d2-child-process.md`（2026-09-09 拍板：Toolhelp 宿主 + debug_processes 全链标注 + 不主动提示）。

## Global Constraints（根 AGENTS.md + 宿主 AGENTS.md 铁律）

- MCP 工具参数带默认值、`[Description]` 中文注明默认值、`CancellationToken cancellationToken = default`、返回 `Task<string>`、错误中文不抛异常。
- 改 MCP 工具**输出行为**（debug_processes 增加标注行）同 commit 改根 `README.md`；`CHANGELOG.md` `[Unreleased]` 记使用者可见变更。
- 宿主新增代码走既有分层：助手放 `Tools/Debugger/`（被 DebugProcessTool 消费），不反向引用 Tools 的 Services 同理不跨层。
- Engine 纯能力层零改动；无新 NuGet 包（Toolhelp 走 `kernel32` DllImport）。
- Toolhelp 是 Windows 专属：本仓库调试器依赖 DbgShim.win-x64、测试跑 win32——`#if` 不需要（宿主目标平台既定 Windows 调试）。

---

### Task 0: 工作区/分支准备

- [ ] **Step 1**: 与用户确认实施分支策略。
- [ ] **Step 2**: `generate-testdata.ps1` 就绪 + Release build 基线绿。

---

### Task 1: 宿主 — Toolhelp 父子快照助手 + 单测

**Files:**
- Create: `src/DotNetDebuggerMcp/Tools/Debugger/ProcessTreeSnapshot.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/ProcessTreeSnapshotTests.cs`

**Interfaces:**
- Consumes: `ClrProcessFinder.List()` → `IReadOnlyList<ClrProcessInfo>`（Engine，.NET 进程权威枚举，pid 升序）；`DebugSessionService.Manager.Active?.ProcessId`（宿主读取——仅取 pid，不跨层操作会话）。
- Produces:
  - `internal static class ProcessTreeSnapshot`
  - `internal static IReadOnlyDictionary<int, int> QueryParentMap()`（pid → parentPid；快照失败返回空字典）
  - `internal static IReadOnlyList<(ClrProcessInfo Info, int Depth, int ParentPid)> DescendantsOf(int rootPid, IReadOnlyList<ClrProcessInfo> dotnet, IReadOnlyDictionary<int, int> parentMap)`（root 的子/孙/曾孙… .NET 进程链，Depth 1 起，防环）

- [ ] **Step 1: 写失败测试（纯单测，无调试器）**

`ProcessTreeSnapshotTests.cs`：以**当前测试进程为父**，起 1-2 个 DebugTarget 子进程（`TestPaths.DebugTargetExe` + `2 3` delay 参数，存活数秒），断言 `QueryParentMap` 含 (childPid → 测试进程 pid)、`DescendantsOf` 深度正确；子进程退出后从 map 消失不必测（快照语义）。运行期 P/Invoke 失败降级测试：无真实触发路径则用内部哨兵——`QueryParentMap` 返回空字典时 `DescendantsOf` 返回空（单测直接断言空字典输入路径）。

Run `dotnet test --project tests/DotNetDebuggerMcp.Tests/... --filter ProcessTreeSnapshot`，Expected: FAIL（类型不存在）。

- [ ] **Step 2: 实现 Toolhelp 助手**

```csharp
// Tools/Debugger/ProcessTreeSnapshot.cs
using System.Runtime.InteropServices;
using DotNetDebugger.Engine.Engine;

namespace DotNetDebuggerMcp.Tools.Debugger;

internal static class ProcessTreeSnapshot
{
    private const uint TH32CS_SNAPPROCESS = 0x00000002;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct PROCESSENTRY32
    {
        public uint dwSize;
        public uint cntUsage;
        public uint th32ProcessID;
        public IntPtr th32DefaultHeapID;
        public uint th32ModuleID;
        public uint cntThreads;
        public uint th32ParentProcessID;
        public int pcPriClassBase;
        public uint dwFlags;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] public string szExeFile;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr CreateToolhelp32Snapshot(uint dwFlags, uint th32ProcessID);
    [DllImport("kernel32.dll")]
    private static extern bool Process32First(IntPtr snapshot, ref PROCESSENTRY32 entry);
    [DllImport("kernel32.dll")]
    private static extern bool Process32Next(IntPtr snapshot, ref PROCESSENTRY32 entry);

    /// <summary>全表 pid → parentPid 快照；失败（句柄异常/权限）返回空字典。</summary>
    internal static IReadOnlyDictionary<int, int> QueryParentMap()
    {
        var map = new Dictionary<int, int>();
        var snapshot = CreateToolhelp32Snapshot(TH32CS_SNAPPROCESS, 0);
        if (snapshot == IntPtr.Zero) return map; // 失败降级：空
        try
        {
            var entry = new PROCESSENTRY32 { dwSize = (uint)Marshal.SizeOf<PROCESSENTRY32>() };
            if (!Process32First(snapshot, ref entry)) return map;
            do { map[checked((int)entry.th32ProcessID)] = checked((int)entry.th32ParentProcessID); }
            while (Process32Next(snapshot, ref entry));
            return map;
        }
        catch { return map; } // 损坏快照：空（调用方提示无法判定父子）
        finally { CloseHandle(snapshot); }
    }

    [DllImport("kernel32.dll")]
    private static extern bool CloseHandle(IntPtr handle);

    /// <summary>root 的 .NET 子孙进程链（BFS 防环，Depth=1 起）。parentMap 空 → 返回空。</summary>
    internal static IReadOnlyList<(ClrProcessInfo Info, int Depth, int ParentPid)> DescendantsOf(
        int rootPid, IReadOnlyList<ClrProcessInfo> dotnet, IReadOnlyDictionary<int, int> parentMap)
    {
        if (parentMap.Count == 0 || dotnet.Count == 0) return [];
        var children = new Dictionary<int, List<(int Pid, string Name)>>(); // parentPid → children
        foreach (var kv in parentMap)
            if (!children.TryGetValue(kv.Value, out var list)) children[kv.Value] = list = new(); list.Add((kv.Key, ""));

        var byPid = dotnet.ToDictionary(p => p.ProcessId);
        var result = new List<(ClrProcessInfo, int, int)>();
        var visited = new HashSet<int> { rootPid };
        var queue = new Queue<(int Pid, int Depth)>();
        foreach (var c in children.GetValueOrDefault(rootPid) ?? []) queue.Enqueue((c.Pid, 1));
        while (queue.Count > 0)
        {
            var (pid, depth) = queue.Dequeue();
            if (!visited.Add(pid)) continue; // 防环（PID 复用竞态理论兜底）
            if (byPid.TryGetValue(pid, out var info))
                result.Add((info, depth, /* 父 pid 由队列携带更稳——见 Step3 细化 */ 0));
            foreach (var c in children.GetValueOrDefault(pid) ?? []) queue.Enqueue((c.Pid, depth + 1));
        }
        return result;
    }
}
```
> 实现注：① `DescendantsOf` 结果元组里 `ParentPid` 用「入队时的父 pid」而非占位 0——队列项带 (pid, depth, parentPid)，根的直接子 parentPid=rootPid、其子=该 pid；② `ClrProcessInfo` 引用 Engine（宿主已引全库，合法）；③ 深链 BFS 自然返回父先于子的层序。

- [ ] **Step 3: 运行测试 + 提交**

Run 定向测试 + 宿主编译。`git commit -m "feat: ProcessTreeSnapshot Toolhelp 父子快照助手（D2）"`

---

### Task 2: debug_processes 父子标注 + README/CHANGELOG + e2e

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugProcessTool.cs`（`:44` 排序循环内标注）
- Modify: `README.md`、`CHANGELOG.md`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

**Interfaces:**
- Consumes: `DebugSessionService.Manager.Active?.ProcessId`（`DebugProcessTool.cs:42` 已取 `currentPid`）；`ProcessTreeSnapshot.QueryParentMap()/DescendantsOf`；`ClrProcessFinder.List()`。
- Produces: debug_processes 输出增强——当前会话目标的子孙 .NET 进程行尾标注父链 + 全量返回尾部引导句。

- [ ] **Step 1: 增强输出**

`DebugProcessTool.DebugProcesses` 现有 `hits` 排序循环（`:44-51`）前：
```csharp
var parentMap = currentPid > 0 ? ProcessTreeSnapshot.QueryParentMap() : new Dictionary<int, int>();
var chain = currentPid > 0
    ? ProcessTreeSnapshot.DescendantsOf(currentPid, all, parentMap).ToDictionary(d => d.Info.ProcessId)
    : new Dictionary<int, (ClrProcessInfo Info, int Depth, int ParentPid)>();
```
行渲染（现 `pid=… name (CLR …)` + `← 当前会话` 之后）：
```csharp
if (currentPid > 0 && chain.TryGetValue(p.ProcessId, out var node))
    sb.Append($"  ← 会话目标({currentPid}) 的{(node.Depth == 1 ? "子进程" : "第" + node.Depth + "代孙进程")}（父 {node.ParentPid}）");
```
> 因 `chain` 需在排序前建立、渲染时按 pid 查，当前会话目标自身也在 `hits` 内正常显示「← 当前会话」不动。全量列表构建后，若 `chain.Count > 0` 返回尾部附引导：`发现 {chain.Count} 个会话目标的 .NET 子进程链——业务代码在子进程时需停当前会话（debug_disconnect/停断点）后 debug_attach <childPid> 单独调试（子进程输出不在 debug_output 范围）。` 排序/过滤/100 截断保持现逻辑；标注计算基于截断前全量。

- [ ] **Step 2: README + CHANGELOG**

README `debug_processes` 描述补一句「当前会话目标存在时标注其 .NET 子进程链与切换引导」+ 边界（单活动会话；子进程输出不捕获）。`CHANGELOG.md` `[Unreleased]` 记该增强。

- [ ] **Step 3: e2e（DebugTarget spawn 模式）→ 验证 + 提交**

`DebugTarget`（generate-testdata.ps1）`Main` 加 `spawn` 分支（append，不影响既有 token）：
```csharp
if (args.Length > 0 && args[0] == "spawn")
{
    // 自起一个 sleep 子进程（同 exe，delay 8s 给调试器观察窗口），父进程等待
    var child = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(
        Environment.ProcessPath!, "sleep 8") { UseShellExecute = true });
    Console.WriteLine("[DebugTarget] spawned child pid=" + child.Id);
    child.WaitForExit();
    return;
}
if (args.Length > 0 && args[0] == "sleep")
{
    Thread.Sleep((args.Length > 1 && int.TryParse(args[1], out var sec) ? sec : 3) * 1000);
    return;
}
```
> UseShellExecute=true 让子进程独立于父重定向管道（父输出捕获不受子进程污染）。重跑 generate-testdata.ps1。
`DebugMcpToolsTests` 加：`debug_launch "DebugTarget.exe spawn 0"` → 等 child pid 打印到输出 → `debug_processes filter=DebugTarget` 返回含 `子进程（父 …）` 标注与引导文案；停会话后对 childPid `debug_attach` 成功、`debug_state` 显示会话切换。Run 定向 + 宿主全量 + Client。`git commit -m "feat: debug_processes 标注当前会话目标的 .NET 子进程链与切换引导（D2，同步 README/CHANGELOG）"`

---

## 收尾

- [ ] Release build + 宿主全量单测 + Client 端到端。
- [ ] 核对 `src/DotNetDebuggerMcp/TODO.md` D2 状态；`docs/planning/specs/README.md` 收录 D2 spec 行。

## Self-Review（writing-plans 内审）

- **Spec 覆盖**：Toolhelp 宿主（Task1）、debug_processes 全链标注（Task2 Step1）、切换引导与单活动会话边界文案（Task2 Step1/2）、只标注不主动提示（无 launch/debug_state 改动）、子进程输出边界说明（Task2 Step2）。
- **占位符**：无 TBD；P/Invoke 结构与 Toolhelp 语义为公开稳定 API；`DescendantsOf` 元组 ParentPid 明确由队列携带。
- **类型一致**：`QueryParentMap`/`DescendantsOf` Task1 定义 Task2 用；返回 `(ClrProcessInfo, int Depth, int ParentPid)` 元组两处一致；`ClrProcessFinder.List()`（Engine）返回类型即 Task2 `all`。
- **风险点已标**：PID 复用防环（visited）；快照失败降级空字典（提示）；UseShellExecute=true 隔离子进程输出不污染父捕获管道。
