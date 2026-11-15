<!-- 职责：说明 SandboxSpike P0 可丢弃试验的目录、构建与命令用法、guest 交换协议和运行边界；不写：实机结果、故障定位、性能数字与验收结论（见 docs/planning/research/10-windows-sandbox-spike-results.md）。 -->

# SandboxSpike（P0 可丢弃试验）

验证「Windows Sandbox 内执行命令 + 桌面输入/截图」这一条链路是否走得通的临时夹具。**不是正式 Runtime**：不在 `DotNetDebuggerMcp.slnx` 中，不接 MCP、不新增工具/能力族，也不改变既有 UIA 语义。**P0 尚未通过**，证据与结论见研究记录。

- 官方能力与复用边界：[09-windows-sandbox-runtime-feasibility.md](../../../docs/planning/research/09-windows-sandbox-runtime-feasibility.md)
- 实机结果、故障定位与性能样本（单一事实源）：[10-windows-sandbox-spike-results.md](../../../docs/planning/research/10-windows-sandbox-spike-results.md)
- 本文只讲怎么跑、文件怎么交换、哪些边界不能越。

## 目录

| 路径 | 内容 |
|---|---|
| `build.ps1` | `dotnet publish`（自包含、`win-x64`、net10）到 `tests/TestData/SandboxSpike/bin` |
| `sandbox-spike.ps1` | 宿主侧控制入口：`start/status/exec/task/cancel/artifacts/stop` |
| `GuestRunner.ps1` | 复制进 guest、由 LogonCommand 启动的常驻 Runner |
| `Scripts/*.ps1` | 提交给 guest 执行的小脚本（见下） |
| `SandboxProbe/` | guest 内 WinForms + FlaUI + Capture 探针（`gui` 快照进程 / `exercise` 输入验收） |
| `observe-host.ps1` | 宿主侧只读采样指针/前台窗口/Sandbox 外层窗口，可选最小化、还原或置底 |

正式可复现入口只有 `build.ps1`、`sandbox-spike.ps1`、`Scripts/launch-gui.ps1` 与 `Scripts/exercise.ps1`。诊断期的对照脚本（MTA、额外等待、telemetry、IME 对照、快照替换）不作为入口，也不在保留范围，用法不要依赖它们。

源码与脚本留在 `tests/Fixtures/`（受版本控制）；运行产物全部落 `tests/TestData/SandboxSpike/`（被 `.gitignore` 的 `tests/TestData/` 整体忽略），不需要额外忽略规则。

## 构建

```powershell
powershell -ExecutionPolicy Bypass -File tests/Fixtures/SandboxSpike/build.ps1
```

自包含发布 `SandboxProbe` 到 `tests/TestData/SandboxSpike/bin`；该目录在 `start` 时只读映射为 `C:\SandboxSpike\Bin`。前提是宿主已启用 Windows Sandbox（可用性由维护者确认，见 09）。

## 控制命令

```powershell
powershell -ExecutionPolicy Bypass -File tests/Fixtures/SandboxSpike/sandbox-spike.ps1 -Action start
... -Action status
... -Action exec -File <本地脚本.ps1> [-TimeoutSeconds 30] [-Detach]
... -Action task -TaskId <32位十六进制>
... -Action cancel -TaskId <32位十六进制>
... -Action artifacts
... -Action stop
```

- `start`：生成 `.wsb`、启动 `WindowsSandbox.exe`、等待 guest heartbeat 就绪（上限 120s）。同一 `start` 用命名互斥锁串行；已有本 PoC 且心跳存活的实例直接返回，不重复创建。状态存 `current.json`，旧状态改名为 `*.previous`。
- `status`：读取最新 heartbeat；`instanceId` 不匹配或非 `stopped` 且超过 10s 未更新即报错，不猜测结果。
- `exec -File`：把脚本复制为 `<taskId>.ps1` 并写入命令 JSON + `.ready` 标记，然后轮询结果。默认同步等待（`TimeoutSeconds + 15`上限）；`-TimeoutSeconds` 范围 1–600，默认 30，**只限制 guest 内任务超时与客户端等待**。`-Detach` 只返回命令信息，不等待。
- `task` / `cancel`：按 `TaskId` 查结果或写取消标记（取消会结束任务进程树）。
- `artifacts`：列出 outputs 下所有产物文件。
- `stop`：写停止标记，最多等 15s 让 Runner 完成当前任务并确认 `stopped`。启动就绪时会把唯一关联客户端的 PID、进程名、创建时间记录到 guest 不可写的 `client-identity.json`；只有停止时这三项仍全部匹配，才结束该客户端并返回 `ownedClientTerminated=true`。身份缺失或不符则不关闭进程；未确认停止则返回 `stop_requested`。不结束 `WindowsSandboxServer`/`vmmem*`。

`start` 的就绪阻挡只检查 launcher、client/RemoteSession 这类活动桌面进程；上一实例可能暂留的 `WindowsSandboxServer` 不算活动 Sandbox，也不被接管。

`exec` 只暂存传入的单个脚本，不自动复制它的相对路径依赖；先停止 Sandbox 再重新构建 bin，避免覆盖正在使用的只读二进制。

### 失败后如何重跑

- `ownedClientTerminated=false` 或 Runner 已崩溃时，先确认并手动关闭**本轮** Sandbox 显示窗口，再重新 `start`；关闭会清空 guest，不要批量结束其他 Sandbox 进程。
- GUI 崩溃留下 `gui.json` 时，本 P0 不自动恢复 GUI。使用 `stop → start → launch-gui` 重建环境，不把遗留快照当成仍存活的进程。
- 不要手工拼接或复制 `current.json` 的路径；控制入口校验 inputs/outputs 必须属于其 instanceId，目录不符会拒绝操作。

## guest 脚本（`Scripts/`）

| 脚本 | 用途 |
|---|---|
| `environment.ps1` | 输出实例/机器/用户/PowerShell 版本/会话/是否找到 `dotnet`/网卡列表；并写 guest 本地 `C:\SandboxSpike\persistent.txt` 以便跨命令观察文件保留 |
| `failure.ps1` | 先写 stdout/stderr 再 `exit 7`，验证失败退出码与双流捕获 |
| `long-task.ps1` | 输出后睡 60s，用于超时/取消 |
| `exit-code-probe.ps1` | 对比 `Start-Process` 是否额外持有 `Handle` 时 `ExitCode` 的可用性 |
| `noop.ps1` | 最小成功脚本，用于命令往返开销 |
| `output-limit.ps1` | 输出 1.2M 字符后 sleep，用于输出上限终止路径 |
| `launch-gui.ps1` | 启动 `SandboxProbe.exe gui`，等 guest 本地 `gui.json` 快照出现并返回 PID |
| `exercise.ps1` | 调用 `SandboxProbe.exe exercise manual`，在 guest 内做 UIA/物理输入/截图验收 |

## 交换协议

- host → guest 只有 `inputs`（**只读**）：任务脚本 `<taskId>.ps1`、命令 `<ts>-<taskId>.command.json` + `.ready`、`<taskId>.cancel`、`stop`。
- guest → host 只有 `outputs`（**可写**）：`heartbeats/*.json` + `.ready`、`<taskId>/running.json`、`<taskId>/result.json`、`stdout.txt`/`stderr.txt`、`runner-error.txt`、`gui-error.txt`。
- 「payload 落盘 + `.ready` 标记」不依赖映射目录上的 rename（该路径的冲突见 10 §2.1）：host 只做唯一命名 + 标记，guest 用 `WriteAllText` 后 `CreateNew` 建标记；读取一律以 `.ready` 为准。
- heartbeat 由 guest 写、宿主只读，宿主不修改；`instanceId`/`taskId` 不符即拒绝，避免把别实例/旧实例的文件当成本次结果。
- 输入内容不可信：实现只接受本次 `instanceId`，且 **Runner 未就绪时直接报错，绝不回退到宿主执行**。`GuestRunner.ps1` 校验 `WDAGUtilityAccount` 且 `COMPUTERNAME` 不等于宿主；`SandboxProbe` 在此基础上再校验 `instance.txt` 与 `SANDBOX_SPIKE_INSTANCE` 一致，在宿主运行即拒绝。
- 未找到 Runner 的 PoC、或宿主已存在**非本 PoC** 的活动 Sandbox 桌面进程时，`start` 直接拒绝，不接管、不关闭未知实例。

## GUI 诊断与失败呈现

- 高频快照在 guest 本地 `C:\SandboxSpike\gui.json`，GUI 与 `exercise` 经 `Local\SandboxSpike.GuiSnapshot.<instanceId>` 命名互斥锁读写，**不做 rename**；`exercise` 读取时校验进程身份、帧号、几何与快照新鲜度。
- `report.json` 与截图写入按 label + GUID 命名的唯一目录，直接落盘。
- GUI 未处理异常不弹模态框：以 `ThrowException` 交给 `Main`，返回 1 并写 `outputs/gui-error.txt`，任务表现为明确失败。
- fixture 输入框设置为 `ImeMode.Disable`，只用于隔离 IME 变量，不代表一般应用的输入法行为。

## 边界

- guest 侧只用 **Windows PowerShell 5.1**；每次 `exec` 是独立 `powershell.exe` 进程，**脚本之间没有变量/会话继承**。保留的只有同一 Sandbox 实例内的文件状态与 GUI 进程状态。
- 映射只有三项：专用 `bin`（只读）、`inputs`（只读）、`outputs`（可写）；网络、剪贴板、音频/视频输入、打印机重定向、vGPU 均关闭。
- 超时与取消用 `taskkill /T /F` 结束任务进程树；`stop` 等当前任务结束再关机。**没有正式 HostManager、没有 MCP 集成、没有异常后的 Runtime 恢复**（guest 崩溃只能人工检查窗口与诊断目录）。
- 宿主前台、最小化、置底三种条件已做过单次短采样，但**不等于持续人工操作下的验收**，也不构成输入隔离证明；需要人工验收，见 10 §4.4。
- 桌面侧是 FlaUI 5 的**直接最小测试**，不声称复用了 `SharpSight.UiAutomation` 的 internal 门面；截图能力通过 `SharpSight.Capture` **项目引用**直接调用（guest 内截图）。
- 停止后显示客户端会被结束，但 `WindowsSandboxServer`/`vmmem*` 可能短暂留存、随后自行退出；不要据此断言“立即全部释放”，也不要把它们当成常驻组件。
- 最小化采样结束或正常异常退出会在 `finally` 中尝试还原原窗口；强制结束观测进程无法保证执行清理。Background 是否真正失焦以 `backgroundAchieved` 和实际前台 PID 采样为准，不能只看请求名称。
