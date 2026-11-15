<!-- 职责：记录独立 Windows Sandbox P0 spike 的实机证据、故障定位、性能样本与未验证项；不写：正式 Runtime/MCP 设计、产品能力承诺或使用命令详表。 -->

# Windows Sandbox P0 实测记录

日期：2026-11-14。**本机后台执行已取得循环实测与人工反馈，完整 P0 尚未收口。** 桌面条件对照实例为 `9e041023bb9b47a98d68b18f3e3f3e11`；默认入口复验实例为 `01fc1b1aa4a54b5d8ad383a1bf818f1c`；用户现场观察实例见 §4.6。尚未进行正式 Runtime/MCP 集成。

官方能力及原有库复用边界见 [前置核查](09-windows-sandbox-runtime-feasibility.md)；可重复运行命令见 [SandboxSpike 使用说明](../../../tests/Fixtures/SandboxSpike/README.md)。这是一份可丢弃试验，不属于正式 Host Manager、CLI 产品或 MCP 工具。

## 1. 运行环境与投送

- 维护者启用 Windows Sandbox 并重启后，`WindowsSandbox.exe` 已出现，文件版本 `10.0.26100.9949`；仍未找到 `wsb.exe`。
- `.wsb` 禁用网络、剪贴板、音视频输入、打印机重定向和 vGPU，内存配置上限 4096 MB。仅映射专用 bin/inputs（guest 只读）与 outputs（guest 可写），未映射源码和用户目录。
- LogonCommand 启动 Windows PowerShell 常驻 Runner；实测账户 `WDAGUtilityAccount`、会话 ID 1。guest PowerShell `5.1.26100.9818`，没有 `dotnet` 命令，网卡查询为空。
- 测试程序为宿主 SDK 自包含发布的 net10/win-x64 WinForms 探针；截图直接引用 `SharpSight.Capture`，UIA 采用 FlaUI 5 的最小调用。未修改生产库可见性、MCP 工具、FeatureFlags、握手或现有点击语义。
- 最新一轮宿主为 2560×1600、150% 缩放；guest 桌面为 2026×1220、150% 缩放。宿主与 guest 分辨率不同已覆盖，不同 DPI 未测。

## 2. 文件通道实测与修正

### 2.1 映射目录不能按本地 NTFS 假设处理

已观测：

1. host 在 guest 只读的 inputs 映射目录内创建/写文件成功，但 `File.Move` 重命名报文件被占用；从相邻未映射目录移入也失败。
2. guest 在可写 outputs 中使用 `MoveFileEx(REPLACE_EXISTING)` 更新心跳，host 轮询期间出现 Win32=5。host 读句柄改为 `FileShare.ReadWrite | FileShare.Delete` 后仍复现；这不是仅靠修改读句柄就解决的问题。
3. GUI 定时在映射 outputs 内用 `File.Move(overwrite:true)` 替换快照，也触发 AccessDenied 模态错误框；此后“无法获得目标前台”实际指向该错误框，不能归因于 Sandbox 输入隔离。

当前 spike 的收敛方式：

- 脚本、命令载荷先写完并关闭句柄，再以 `CreateNew` 创建空 `.ready` 标记；消费者只读已经提交的文件。
- guest 的心跳使用递增唯一文件名；running 与 terminal result 分开发布，均有 `.ready` 标记，避免覆盖正在读取的文件。
- 高频 GUI 快照留在 guest 本地 `C:\SandboxSpike\gui.json`，经 guest 内命名互斥锁读写，**不再使用任何 rename**；只把报告和截图写到映射 outputs。
- 此协议不声称崩溃安全的 exactly-once；Runner 重启自动恢复、幂等、清理及长期积累上限留给正式设计。当前心跳文件约每秒增加一个，不宜长期挂机当产品用。

这是本机当前映射层的观测，不推广为所有 Windows 版本都不支持 rename 的结论。

### 2.2 GUI 快照、报告与失败呈现

- 快照由 GUI 定时线程直接覆写 guest 本地文件，读方（`exercise`）与写方（`gui`）用 `Local\SandboxSpike.GuiSnapshot.<instanceId>` 命名互斥锁串行，等待上限 3s；读取时校验进程身份、帧号、拖拽区域几何与快照时间（超过 5s 视为 `observation_stalled`）。
- `exercise` 的 `report.json` 落在按 label + GUID 命名的唯一目录中，直接写文件，不做替换；截图同目录写入。
- GUI 线程异常改为 `Application.SetUnhandledExceptionMode(ThrowException)`，由 `Main` 捕获后返回 1，并在 outputs 写 `gui-error.txt`；这是当前失败处理实现，本轮没有额外故障注入验证这条 GUI 错误上报路径。

### 2.3 Windows PowerShell 重定向进程的退出码

`Start-Process -PassThru -RedirectStandardOutput/-RedirectStandardError` 后等待退出，可能得到 `HasExited=true` 但 `ExitCode=null`。

guest 内 `cmd /c exit 7` 对照：未额外持有 `Process.Handle` 时 ExitCode 为 null；启动后先访问一次 Handle 保留句柄时返回 7。Runner 据此保留句柄。不要把该问题误判为被测脚本没有退出码。

复现源码：`Scripts/exit-code-probe.ps1`。

## 3. 命令执行证据

原始产物位于被 Git 忽略的 `tests/TestData/SandboxSpike/<instanceId>/`，不把机器特定的全量日志写进仓库。

| 行为 | 实测结果 / 证据 |
|---|---|
| 跨独立 CLI 进程保留文件 | 实例 `142833b03e6340c2b95494b2f58741ee`；任务 `13b4fd9418024c6599cde3e57e620d94` 创建文件，`60250574da4541e8a724cf7a932b6cc6` 读回上条命令写入内容；脚本 PID 从 4480 变为 8032，Runner 未重启 |
| GUI 启动命令能返回 | 实例 `a5f5787388d1492894acb0e431c455ca`；任务 `1246196340d9482b90298a2ee3f50bd3` 完成，GUI PID 4456 随后仍被另一命令访问；GUI 不是同步 exec 的阻塞对象 |
| stdout/stderr/非零退出码 | 任务 `5bfbe6a60fb64672812cf8c506e796d4` 分别回传两条预期文本，`failed`、exitCode=7 |
| 执行超时 | 60s 脚本设置 3s 超时，任务 `2c31dcc7bb024a878b29f87e0bf9a1fd` 返回 `timed_out`，guest 记录约 3365ms |
| 分离任务与取消 | 任务 `4eee0470ed1c49f087ed2b400ee4e3be` 先 detach 返回，再独立 cancel，最终 `cancelled`；stdout 保留启动信息 |
| 输出上限 | 实例 `a5f5787388d1492894acb0e431c455ca`，任务 `dfe1238158684e79a2f4fb614692a6f9`：1.2M 字符输出 + sleep，2788ms 后 `failed`，`outputTruncated=true`，stdout 保留 32768 字符，`error` 为“输出超过 1 MiB，终止任务。” |
| 宿主保护 | 在宿主调用 `SandboxProbe.exe exercise host-must-refuse`，在创建 UIA 客户端/注入输入前抛出 guest 环境拒绝错误，退出码 1 |

超时和显式取消会在 guest 请求 `taskkill /T /F` 结束脚本进程树；客户端等待超时则返回 `wait_timed_out`，不会自动取消任务。当前证据证明脚本取消/超时可观测，尚未对任意派生进程树清理做穷尽验证。

最初 `stop` 在确认 guest 为 stopped 后，仅凭启动时间相近（±5s）唯一匹配客户端。最终审查指出“原客户端消失、同期出现另一客户端”时证据偏弱，因此收紧为：启动就绪时记录唯一关联客户端的 **PID、进程名、创建时间** 到未映射的 `client-identity.json`；stop 确认 guest 停止后，三项必须全部一致才 Kill，身份缺失/不符即不结束客户端。`current.json` 的 inputs/outputs 也必须等于当前实例规范路径。选择 Kill 是因为 CloseMainWindow 实测会弹宿主确认框；它不用于结束未知客户端或后台 Server。

实例 `9e041023bb9b47a98d68b18f3e3f3e11` 上已观察到：`stop` 返回 `state=stopped`（guest 已确认停止）、`ownedClientTerminated=true`；紧接着查询仍见 `WindowsSandboxServer` PID 31284（约 84 MB）。数十秒后、下一次构建前复查时，该 Server 与 `vmmem`/`vmmemWindowsSandbox` 均已无结果——它是自行退出，`stop` 未杀 Server。因此只记录为“显示客户端随 `stop` 结束，后台 Server 暂留后自行回收”，**不能**写成所有资源立即释放，也不能说 Server 永久常驻。`start` 的就绪阻挡只检查 launcher、client/RemoteSession，不把暂留的 Server 当活动桌面。

该 Sandbox 随后已停止，默认路径重新 publish；最终默认入口复验见 §4.5。临时对照脚本和重复发布目录已清理，保留报告与截图证据；variant 命令不再作为使用者入口。

## 4. 桌面验证

### 4.1 最新一轮故障：中文 IME 首次弹窗

实例 `9e041023bb9b47a98d68b18f3e3f3e11` 的 `outputs/telemetry-5339031d43a04c5bad7c22c81d1c2ba2/report.json`（任务 `819e8f91e8654f4a9e7e3a5a7b82a854`，label `telemetry`）记录：UIA 与真实点击通过，键盘与拖拽两项等待断言失败。

- 键盘检查失败（3314ms）：快照 frame=69、clicks=2、mouseDowns=1、keys=14、drags=0、`text` 为空；最近按键为 `ControlKey, Control;Delete` 加连续 `ProcessKey`。
- 拖拽检查失败（3479ms）：frame=86、drags=0，且 `panelDowns=0`、`panelUps=0`——拖拽区域根本没有收到鼠标按下。
- 最后截图 `telemetry-5339031d43a04c5bad7c22c81d1c2ba2/screen-after.png`（SHA-256 `60BFCE131AB685E2C92279F3D590B884754F6EDC555A8CCAAAD95942DF397A6E`，BitBlt 2026×1220）清楚显示中文 IME 首次运行的“启用来自必应的建议”弹窗遮住拖拽区域；同时按键全是 `ProcessKey`，说明文字停在输入法 composition、未提交到文本框。

即：失败不是“键盘/拖拽注入不可用”，而是探针被 IME 首次启动 UI 抢占。截图是人工查看确认，不是自动判定。

### 4.2 对照：MTA 与额外等待没有解决，也未证明为根因

为排除探针自身问题，做过两组对照：把 `exercise` 放到 MTA 线程；在 Ctrl+A 后加入 FlaUI 的输入队列等待，并改用内含输入等待的 `Mouse.Drag`。对应 report 为 `mta-56bc0f696fbe4924a3db6dc85cd7a4f7`（任务 `c99fa834646c4e48b60a2ee9522f6028`）与 `input-wait-73239c599c97429fa9ff504a775ced0d`（任务 `b0adcf5aaecd456a886341cae27f9e80`），均失败。没有通过增加业务断言超时来掩盖问题；根因判断以 4.1 的截图与按键证据为准。对照脚本不作为可复现入口。

### 4.3 仅在 fixture 输入框禁用 IME 后整组通过

`ProbeForm` 的输入框改为 `ImeMode.Disable` 后，同一实例下连续 4 组 UI 验收全部通过，覆盖 UIA Value/Invoke、真实点击（MouseDown 增量）、Ctrl+A 后键入、拖拽、全屏 BitBlt、整窗 WGC：

| 任务 | report 目录 | 任务状态 / 耗时 |
|---|---|---|
| `3392561307a94a32bfd5892ca7aaaadf` | `ime-off-502fd05765644dbcb6354d91760d97ec` | completed / exit 0，5938ms |
| `36b35eac304040bdbfac06a6ad848cb3` | `ime-off-031ecf3805924b7d9163037c07f0cf0e` | completed / exit 0，4461ms（宿主前台 Keep） |
| `44a2b36fcd2c4542951e49c3980c01bc` | `ime-off-f2db29450e7b41c19b57374744c33775` | completed / exit 0，4361ms（外层最小化） |
| `8c6f7b591d9f4ea087c21e560e4dec7c` | `ime-off-2a14392939b141159d2f8a9851f6bc41` | completed / exit 0，4498ms（外层置底） |

各 report 的 `uia/physical-click/keyboard/drag/screen-before/screen-after/window-after` 均 `passed=true`，截图 sha256 随报告可查（例如 `ime-off-031ecf3805924b7d9163037c07f0cf0e/screen-after.png` = `F45D92A2AB621EAC6016E11D73489F1DF8FF9AE10FF67C7ACCAA7F3DCFC617FB`）。

**限制**：这是 fixture 主动禁用 IME 后的结果，只证明“IME 不介入时该链路可跑通”，不能外推到一般应用；真实应用若启用 IME，键盘输入仍可能进入 composition 或被 IME 候选窗遮挡。

### 4.4 宿主侧状态对照与关键否决风险

`observe-host.ps1` 在实例根目录（guest 未映射）写 `host-observations-*.json`，只读采样宿主指针、前台窗口、Sandbox 外层窗口状态与工作集；采样期间不点击、不 `SendInput`、不 `SetForegroundWindow`。

| 条件 | 证据文件 | 观测 |
|---|---|---|
| 前台 Keep | `host-observations-20261114T051731612Z.json` | 24 样本 / 12383ms；前台始终 `WindowsSandboxRemoteSession`，窗口状态无切换；宿主光标出现 3 个位置 |
| 外层最小化 | `host-observations-20261114T051840429Z.json` | 30 样本 / 15439ms；30/30 `minimized=true`；前台始终 `WindowsTerminal`；宿主光标固定 (530,480) |
| 外层置底 Background | `host-observations-20261114T051956762Z.json` | 24 样本 / 12363ms；客户端可见未最小化；前台始终 `WindowsTerminal`；光标固定 (1524,98)；Terminal bounds `[78,69,2110,1425]`，Sandbox bounds `[255,159,2303,1439]`，几何上大部分覆盖含 GUI 区域 |

`Background` 的实现是：先最小化，再以 `SW_SHOWNOACTIVATE` 显示，随后 `HWND_BOTTOM` + `SWP_NOSIZE|SWP_NOMOVE|SWP_NOACTIVATE` 置底；不注入输入、不激活其他宿主窗口。

**否决风险（不能忽略）**：前台 Keep 那 24 个样本中，宿主光标从 (530,480) 变为 (450,382)、(590,350)，再回到 (530,480)。guest 报告的按钮中心约 (184,175)、输入框约 (324,143)、拖拽区域约 (264,273)；宿主光标减去这三组 guest 坐标均得到固定偏移 **(266,207)**。这强烈指向 RDP 客户端的光标位置同步，而不是宿主用户自行移动。**因此不能声称“真实输入完全不影响宿主”。**

最小化与置底两轮单次短测未见光标/前台变化，只能说明“这两次采样窗口内没有观察到”，不能等同于持续人工输入下的验收，也不能证明输入隔离。

### 4.5 默认入口的全新实例复验

最终源码经 `build.ps1` 自包含发布至默认 bin 后，新建实例 `01fc1b1aa4a54b5d8ad383a1bf818f1c`，就绪耗时 14613ms。重复 `start` 返回同一 instanceId 与 runnerId，没有新建桌面。

- 默认 `launch-gui.ps1` 任务 `cdf5cc06c0674bac93c9f4a6524f19f7` 完成，GUI PID 7836。
- 另一独立 CLI 执行默认 `exercise.ps1`，任务 `3deb904d070f433aa534cbe02ed673fd` 返回 completed、exitCode=0，全部七项输入/截图检查通过；guest 任务总耗时 4683ms，exercise 本身 2670ms。
- 报告目录 `outputs/manual-41a358935f724c91a4e767a04b9997ab/`。前后全屏 PNG SHA-256 不同，且 GUI 点击/键盘/拖拽状态变化，取得画面更新的基本证据；不等于逐像素证明每帧内容都正确。
- 同期 `host-observations-20261114T052553682Z.json` 虽请求 Background，但 20 个样本的宿主前台仍是 `WindowsSandboxRemoteSession`，因此**不计为新的失焦通过证据**。该轮宿主光标保持不变，也不推翻 §4.4 的前台同步观测；光标同步是否还依赖光标处于客户端内部需要单独验证。
- 最终默认可执行文件在宿主误运行 `exercise` 仍在输入路径前拒绝，退出码 1；12 个 PowerShell 脚本语法解析通过，`git diff --check` 通过。
- 默认 `stop` 返回 stopped、ownedClientTerminated=true；随后 status 为 stopped，复查未见 WindowsSandbox、WindowsSandboxClient、WindowsSandboxRemoteSession、WindowsSandboxServer 或 vmmemWindowsSandbox 进程。最终测试环境已关闭；这不等于测出了全部虚拟化内存回收时延。
- 身份收紧后另建实例 `e9964478fdd34bfe9f3809a231c6ca20`：13701ms 就绪，启动记录客户端 PID=5004、名称与创建时间经宿主逐项核对；随后 stop 返回 stopped、ownedClientTerminated=true。该轮没有重跑桌面输入，只验证修改后的生命周期控制；1s 最小化观测正常结束并还原。
- 修改当前实例的 outputs 为其他目录后，公开 CLI 的 status 在访问结果目录前返回路径归属拒绝、退出码 1；测试随后按原始字节恢复 current.json。未用伪造路径执行命令或关闭进程。

### 4.6 用户现场观察：真实输入循环与宿主对话输入并行

用户要求现场运行观察，启动实例 `e0c6ef3585604443922ceba15e463e4f`，GUI 启动任务 `c3ea503d35c04ef0a317e7f72a25bbc1`。循环任务 `17ae8383404a4328872ce7423b37bd27` 在约 63.6s 内执行 **17 轮** UIA、真实点击、Ctrl+A 与键入、拖拽和截图，17 份 report 均 passed=true，始终使用 GUI PID 8088；任务 completed、exitCode=0，guest 记录总耗时 65330ms。

宿主观测文件为该实例目录下 `host-observations-20261114T072300089Z.json`：75.45s 共 147 个样本，前 4 个宿主前台是 WindowsSandboxRemoteSession，后 143 个是 WindowsTerminal，只有一次前台切换，之后没有采到 Sandbox 抢回前台。采样记录 54 个不同光标位置；这不应被当作隔离失败，因为用户此时正在操作宿主。Background 刚请求时 backgroundAchieved=false，故不能说是脚本自动完成了失焦；后续实际前台由采样确认。

用户在测试期间明确反馈：**“不会抢我的操作，我现在还在跟你打字。”** 这构成本机本轮「宿主对话输入与 guest 真实输入并行，用户未感到干扰」的人工证据；不扩展为整段时间无间断键入、所有鼠标动作均经过人工确认、或所有窗口状态/应用都保证不干扰。

原始报告位于 `outputs/live-demo-*/report.json`；17 份已逐一读取确认。结束后 stop 返回 stopped、ownedClientTerminated=true。原始任务 JSON 中 humanAssessment=awaiting-user-feedback 是脚本结束时的原始值，不回写它；后收到的人工反馈以本节为准。

### 4.7 未验证项

更长时间持续操作、不同 DPI、更多应用框架以及前台光标同步的具体触发范围仍未覆盖。外层客户端状态与 guest 内被测应用最小化不是同一测试条件；请求 Background 不等于实际失焦，必须以采样的前台 PID 为准。

### 4.8 用户从 GUI 启动报初始化失败：排查与临时恢复

同日现场循环后，用户从 GUI 启动 Sandbox，报告“Windows 沙盒无法初始化。Exception of type 'System.Exception' was thrown.”，并询问是否有测试残留。

只读排查结果：

- 官方 `wsb list --raw` 当时返回空的 WindowsSandboxEnvironments；未见旧测试客户端。现有 WindowsSandboxServer PID 11976 创建于 15:23:20，晚于测试停止时间 15:23:02，不能把它当成旧测试客户端残留直接结束。
- vmcompute/hns/HvHost/vmms 服务均在运行；Default Switch 网卡存在且 Up。vmmemCmZygote 启动时间早于本轮测试，未擅自结束它或重启虚拟化服务。
- 当前用户已能查询到 Store 包 MicrosoftWindows.WindowsSandbox 0.8.114.0，`wsb.exe` 应用执行别名也已可用。这与早先前置探测不同，不应继续假定 CLI 不存在；没有证据确定其具体安装/注册时点。
- HCS/Hyper-V 相关详细日志需要提升权限，本轮未读取到；hcsdiag list 也未返回可用清单。因此不能声称已排除所有底层残余状态。

未清目录、未重启服务、未提权的情况下，执行官方 `wsb start --raw`（未传 PoC 配置）成功，得到新环境 `743d88ab-dc3c-46b3-8703-2333fedf73b0`；随后 `wsb connect --id 743d88ab-dc3c-46b3-8703-2333fedf73b0` 返回 0。`wsb list` 确认该环境存在，客户端 PID 15576 响应正常，WGC 截图确认图形桌面已显示。

该环境未投送 Runner、未映射项目目录，当时为用户正常使用保留运行，不按 PoC 自动关闭。截至该次 CLI 首次恢复，只取得“官方 CLI 创建并连接成功”的证据，尚未复测 GUI 启动入口，也没有确认初始化失败根因或修复了永久故障，不能简单归因于网络/vGPU或我们的关闭逻辑。

随后复查发现客户端 PID 15576 已退出，而 wsb list 仍列出该环境、Server PID 31564 仍存在。尚不知道窗口是否被用户手动关闭或客户端又发生异常；没有擅自重连、停止该环境，也不能把一次截图显示成功说成图形窗口持续稳定运行。

用户随后明确表示是自己关闭了窗口，并报告“启动过一次关掉，再次启动就初始化失败”。15:35 的受控复验：

1. wsb list 仍返回同一个环境 743d88ab-dc3c-46b3-8703-2333fedf73b0，只有 Server 存在，没有显示客户端。
2. 仅执行 `wsb stop --id 743d88ab-dc3c-46b3-8703-2333fedf73b0 --raw`，随后 list 为空；没有重启 Server、虚拟化服务或删除目录。
3. 直接启动系统 GUI 入口 `%WINDIR%\System32\WindowsSandbox.exe`，新环境 0203a293-db65-48d6-9800-09500e79be0f 出现；客户端 PID 27544 无 --id 参数，与之前 CLI connect 模式不同。截图确认桌面正常；Server 仍为 PID 31564，未被重启。

截至该次恢复，已确认“CLI connect 的窗口被关闭后环境仍存在；用官方 stop 结束它后，普通 GUI 入口再次启动成功”。当时尚未完成纯 GUI 启停独立复验；后续复现见下文。新 GUI 环境当时留给用户，不自动关闭，且未运行 PoC 自动化脚本。

通过 `gh issue view 117 --repo microsoft/Windows-Sandbox --json number,title,state,body,comments,url` 核查 [上游同名错误报告 #117](https://github.com/microsoft/Windows-Sandbox/issues/117)：仍为 OPEN，但报告版本为 0.5.3.0，与本机 0.8.114.0 不同；只能作症状参考，不能据此判定同一原因。用户要求后续 GitHub 资料统一通过 gh CLI 查询。

后续生命周期设计应重新评估已可用的官方 wsb start/list/connect/stop，而不是只依赖显示客户端进程的退出；本轮没有改动现有 PoC 关闭实现，也没有把官方 CLI 的一次恢复外推为完整生命周期可靠性证明。

#### 后续复现与延期决定

用户随后关闭纯 GUI 启动的新窗口，再启动仍报同样初始化错误。15:40:22 查询 `wsb list --raw` 为空，Server 新 PID 31572；因此“运行环境始终残留”不足以解释全部现象。`certutil -error 0x8037011b` 确认此前 hcsdiag 失败表示当前用户权限不足，不是容器损坏证据。

用户明确要求暂停 GUI 故障调查，先集中功能。两路静态分析随即停止，仅归档已有摘要：

- Frontend 在 StartVM 异常路径仅保留 HResult，服务端 StartSandbox 的异常包装也仅回传 HResult；界面经 `Marshal.GetExceptionForHR` 展示，原始异常消息/栈可能丢失。不能仅凭 generic Exception 文案确认根因或异常产生方。
- `--id` 打开的连接关闭时不停止 VM；无参数 GUI 则发起 Shutdown 后退出，不等待 VM 完全终止。静态行为与已观察的连接关闭后保留环境一致，但不能据此证明本次故障必为拆除竞态。
- Server 的列表仅显示 Running/Rebooting，不包含所有创建中状态；列表为空不能单独证明后端不存在其他状态。相关状态阻塞启动仍只是待检验假设。
- 后续若恢复调查，可优先采集 ETW provider `Microsoft.Windows.Containers.WindowsSandbox`（GUID `1726c8ec-0d47-5694-2771-36e5427e03c7`）的 `ActivityError` 原始异常，不再凭通用弹窗试错。此次未取得该现场证据。

**当前处理：GUI 二次启动故障延期，不阻塞功能设计；已验证的 CLI 路径可用，不承诺所有异常场景可靠。** 原计划中的持久 Runtime、正式 CLI、任务/GUI 生命周期、日志与产物恢复为后续工作方向，MCP 适配仍后置；尚未落地正式工程或批准详细实施设计。应用环境与硬件适用性边界见 [可行性核查 §7](09-windows-sandbox-runtime-feasibility.md#7-补充核查应用环境与宿主硬件依赖)。

## 5. 性能样本与口径

- 首次启用后首次启动到 Runner 就绪：58324ms（单样本）。后续重建就绪：15470、14857、14493ms。它们不是多次完整冷启动统计，不推算固定性能承诺。
- PowerShell Runner 初始工作集约 87–96 MiB；只表示 guest 进程工作集，不是 Sandbox 总内存，也不是宿主虚拟化资源总量。
- 5 次新 CLI 进程执行最短 `noop.ps1`：guest 任务耗时 1954/1665/1706/1748/1624ms；包括宿主新 `pwsh` 启动在内的往返为 2749/2216/2345/2416/2188ms。该 spike 的新 PowerShell 子进程启动成本明显，不能将它称为毫秒级命令分发。原始摘要：实例目录 `host-latency.json`。
- 最新一轮 4 次 `ime-off` 任务耗时 5938/4461/4361/4498ms，其中 UI 检查本身约 2.5–3.9s；同样只是单轮样本。
- 一次 GUI 运行中截图样本：2026×1220、150% 缩放；全屏 PNG 约 3.9–4.0 MB，采集及共享写入约 0.13–0.34s；524×477 整窗 WGC 约 0.79–1.94s。这里只是单轮 guest 采集/写共享目录耗时，没有独立测出纯传输延迟。
- guest 关机后可能留下宿主 `WindowsSandboxRemoteSession` 旧显示窗口；本轮按归档实例启动时间明确匹配旧 PID 11812/16448 后请求关闭。不能只查 `WindowsSandbox`/`WindowsSandboxClient` 来判定所有相关显示资源已释放。

以上均不是最终性能结论。

## 6. 待验证 / 待补证据

- 更长时间、更多应用及点击/切窗口组合下的宿主影响；已有一次约一分钟循环期间的人工对话输入反馈，见 §4.6。
- 不同 DPI（缩放比不同，而非仅分辨率不同）与画面新鲜度、退出后的资源释放。
- 一般应用启用 IME 时的键盘可用性；fixture 的 `ImeMode.Disable` 只是隔离变量，不是产品方案。

## 7. 当前结论

运行载体、离线文件通信、自包含 GUI、跨命令状态、以及“禁用 IME 后”的完整桌面链路（UIA/真实点击/键入/拖拽/截图）已经有实机证据；**P0 仍未通过**。主要原因不是链路完全不可用，而是：

1. 前台采样存在与 guest 坐标固定偏移的宿主光标同步，输入隔离不能被声称成立；
2. 键盘可用性依赖 fixture 关闭 IME，尚不能外推到真实应用；
3. 已有一次约一分钟后台循环与用户并行打字的正面反馈，但长时间、多应用与更广的人工操作组合尚未验收。

正式 Host Manager、MCP 集成和异常恢复原本就不在本轮 P0 范围。最初阶段裁定不进入这些阶段；后续用户调整工作优先级见 §4.8「后续复现与延期决定」，不回溯改变上述实测结论，也不承诺最小化可靠、宿主操作完全无干扰或资源开销可接受。
