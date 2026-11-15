<!-- 职责：记录子会话自动登录（凭据来源与注入）和持有器/会话持久化恢复的设计决策、边界与验证口径；不写：工具参数表全文（见 README 与工具 Description）、历史实测过程（见 research/08、11、12）。 -->

# 子会话自动登录与持久化恢复设计（2026-11-15）

## 1. 问题与根因

实测现象：`child_session(action=start)` 建立 RDP loopback 子会话时，ActiveX 停在 `Connected=2`，必须由用户在连接窗内手工输入 Windows 账户密码才能完成登录。

已排除的假设与证据都留在 `research/08-rdp-child-session-spike.md` §4.5：

- 不是 NLA 配置问题：`EnableCredSspSupport=false` 时 `Connect()` 直接 `E_INVALIDARG`，子会话必须走 CredSSP。
- 不是旧子会话脏：注销后重建仍要凭据。
- 不是客户端属性缺失：按子会话要求设置 `Server=localhost`、`ConnectToChildSession=true` 等后行为不变。

结论：本机（Windows Hello-only / `DevicePasswordLessBuildVersion=2`）不会自动把父会话凭据用于 loopback 子会话。官方自动登录只在特定凭据委派策略与配置下成立，不能作为所有设备的默认前提。要由程序完成登录，必须在调用 `Connect()` 前向 RDP 控件提供明确的登录凭据。

## 2. 方案比较与选择

| 方案 | 首次配置 | 后续自动化 | 安全与维护 | 结论 |
|---|---|---|---|---|
| 系统自动登录（CredSSP/凭据委派） | 无需输入 | 依赖策略与登录方式，本机不成立 | 不降低安全，但不可控、无法承诺 | 保留为「如果系统本来就免密就自然免密」的路径，不作为实现依赖 |
| Windows 凭据管理器 + 向控件注入 `UserName`/`ClearTextPassword` | 用户输入一次密码 | 每次 `start` 自动读库注入 | 由 Windows 按当前用户 DPAPI 保护；不写配置/源码/日志；可更新、可删除 | **采用** |
| 自建 DPAPI 加密文件保存密码 | 用户输入一次密码 | 可自动 | 需要自定义密钥/文件 ACL/轮换与损坏恢复，重复凭据管理器已有能力 | 不采用（增加维护面，不增加实质安全性） |
| 关闭 Windows Hello / 改系统认证策略 / 明文配置 | 可能免输入 | 可自动 | 降低系统安全或泄露凭据，任务明确禁止 | 不采用 |

选择的兼容性依据：RDP ActiveX 的 `IMsRdpClientAdvancedSettings::ClearTextPassword` 与 `IMsTscAx::UserName`/`Domain` 是官方脚本化属性；开源实现 LocalRDP 也用同一条路径为子会话提供显式凭据（`--user`/`--password`）。控件自身不读凭据管理器，必须由调用方取出并注入。

## 3. 凭据来源与写入

读取顺序（`ChildCredentialStore.Load`）：

1. 本产品目标 `DotNetDebuggerMcp/child-session`（`CRED_TYPE_GENERIC`，必要时也接受 `CRED_TYPE_DOMAIN_PASSWORD`）。
2. Windows 自己保存的 loopback RDP 凭据：`TERMSRV/localhost`、`TERMSRV/127.0.0.1`（两种类型都尝试）。

不读取指向其他主机的 `TERMSRV/*`，不遍历凭据库，不把用户名/密码写入实例记录、命令行、日志或工具返回值。

写入：`CredWriteW`，当前用户、`CRED_PERSIST_LOCAL_MACHINE`（在本机跨登录会话保留，不漫游），注释标明用途。凭据管理器内部使用 DPAPI 按用户加密，因此不再额外落一份自定义加密文件。

首次配置有两条等价入口：

- `child_session(action=start)`：先让系统尝试自动登录；认证持续等待（约 12 秒）仍无进展时，在桌面弹出一次标准 Windows 安全凭据窗口（`CredUIPromptForCredentialsW`，标题「DotNetDebuggerMcp 子会话」），保存成功后立即断开并按新凭据重连。这样在官方免密的机器上不会被无端打断。
- `child_session(action=login)` 或 CLI `child login`：主动弹出同一窗口保存/更新，便于在启动实例前完成配置。

`logout` / `child logout` 只删除本产品目标；用户自己保存的 `TERMSRV` 凭据不动。

每个持有器最多主动询问一次；取消或保存失败后不再反复弹窗，仍可在连接窗内手工认证，或显式执行 `login` 重新配置。

## 4. 注入与失败口径

`RdpActiveXHost.ConnectChild` 在 `Connect()` 前：

1. `Ocx.UserName = 用户名`；域存在时 `Ocx.Domain = 域`（`DOMAIN\user` 拆分；UPN 保持整体作为用户名）。
2. `AdvancedSettings7.ClearTextPassword = 密码`。
3. 已有 `EnableCredSspSupport=true` 保持；不修改系统认证策略、不关闭 Hello、不改证书或委派策略。

失败处理：

- 凭据库读失败/窗口取消/用户名为空：退回原有「系统连接窗口内手工认证」路径，实例继续建立，不能因此直接 `Faulted`。
- 属性注入失败：记录异常类型与 HRESULT（不记录消息，避免任何参数回显），同样退回手工认证，`status.reason` 给出提示。
- 保存了但密码已失效：连接停在 `AwaitingAuthentication`，`status.reason` 提示用 `login` 更新；不会自动重试或反复弹窗。
- 用户账户没有可用密码（仅 PIN/Hello）：显式凭据无法构造，按上一条退回手工认证；文档给出「由组织启用受支持的凭据委派/继续人工认证」的替代说明，不自动修改登录方式。

## 5. 持久化与恢复

### 5.1 持有器独立于调用端

- 持有器是独立进程；原先宿主重定向并排空其 stdout/stderr，宿主退出后管道无人读取，长期持有器可能在写日志时被卡住。现改为持有器与 worker 各自写系统临时目录 `%TEMP%\DotNetDebuggerMcp\child\<实例>\{holder,worker}.log`（256 KiB 轮转），宿主不再重定向 stdio；`ChildStandardHandles.PreventInheritance` 保留，短命 CLI 退出后调用端仍能立即收到 EOF。临时目录只放诊断日志，实例状态目录仍只放 `instance.json` 等生命周期数据。
- 只要持有器存活，后续 MCP/CLI 进程通过实例记录直接复用同一实例与 worker，不需要重新登录，也不需要重建 GUI 环境。
- MCP/CLI 退出不主动关闭持有器或注销 child；`child_session(action=stop)` 才显式注销整个 child。

### 5.2 `start` 的有界恢复

原有规则是「持有器失效则拒绝重建/接管」。新规则在保持「不接管归属不明 child」的前提下增加恢复：

| 情形 | 行为 |
|---|---|
| 持有器存活 | 直接返回现有实例（原行为） |
| 持有器失效，当前无 WTS child | 旧实例标记 `Stopped`，创建新实例并重新认证 |
| 持有器失效，当前 child 编号与实例登记一致 | 重连同一实例：先等旧 worker 自行退出（有界 3 秒），启动新持有器、清空 worker 记录，等待登录后重新投递 worker |
| 持有器失效，child 编号不一致或实例未登记编号 | 拒绝自动接管（可能是外部/他人会话），提示人工核对 |
| `Stopping` 状态 | 不恢复，保持停止流程 |

`stop` 对「持有器已失效且当前无 child」的残留实例允许直接标记 `Stopped`，不必先重建；有 child 时仍要求可核实的持有器，避免误注销。

### 5.3 状态区分

`status` 的 `health` 分开报告：

- `holderAlive`：持有器进程（PID+创建时间）是否仍匹配。
- `workerAlive`：worker 对端是否仍匹配。
- `childSessionId` / `childSessionMatches`：系统当前 child 与实例登记是否一致。
- `childSessionActive`：WTS 连接状态是否为 Active（`null`=查询失败/无权限，不当作可用）。
- `guiInteractive`：`Ready` + 持有器/worker 存活 + child 编号匹配 + Active 同时成立。
- `credentialSaved`：本产品或 loopback RDP 是否已有可复用凭据（不返回密码）。

`probe` 同样增加 `CredentialSaved` 只读字段。

### 5.4 明确不做与限制

- 不承诺跨 Windows 重启恢复运行中的进程。重启后凭据仍在，可重新 `start` 并自动认证。
- 子会话内通过 `child_process` 启动的任务归 worker 的 Job 管理；持有器失效/worker 退出时 Job 关闭会结束这些任务，这是防止遗留进程的既有安全设计，恢复只保证重新建立会话与 worker，不保证旧任务存活。
- 持有器失效后重连同一 child 是否成功取决于 Windows 是否仍保留该 child（断开态可以保留，注销后不行）；不把它当普通 RDP 断线重连的等价行为。
- `login` 只影响后续连接；正在等待认证的实例不会注入新凭据，需要手动在连接窗完成或 `stop` 后重新 `start`。
- 凭据库中的条目对当前用户的其他进程可读（与 mstsc 保存凭据相同），本能力不是安全沙箱。

### 5.5 画面自适应与子会话桌面干净

- **自适应**：拖动/缩放连接窗时，`ChildConnectionWindow` 用 400 ms 定时器合并连续 `Resize`，在「已连接 ≥1.5 s、非最小化、尺寸合法（200–8192）、与上次成功值不同」时调用 `IMsRdpClient9::UpdateSessionDisplaySettings(宽, 高, 毫米宽, 毫米高, 0, 100, 100)`。
  - 物理宽高按 96 DPI 换算成毫米（`px / 96 * 25.4`，夹到 10–10000）；MS-RDPEDISP 规定毫米值越界会被忽略。
  - 只用动态分辨率，不 `Disconnect/Connect`（会白屏），也不依赖 `SmartSizing`（不保证纵横比）。
  - 实测：客户区 1484×911 → 子会话 1484×911；缩到 884×661 同样跟随，`holder.log` 无错误。
- **不留终端窗口**：worker 由计划任务 `RunEx` 投送，拿不到 `CREATE_NO_WINDOW`，控制台子系统程序会在子会话桌面带出一个 Windows Terminal 窗口（原先显示空白）。现在 worker 启动时先把 `Console.Out/Error` 接到 `worker.log`，再 `FreeConsole()` 脱离控制台，窗口随之关闭；实测子会话内不再存在 `WindowsTerminal`/`conhost`/`cmd`。`child_process` 拉起的任务本来就用 `CREATE_NO_WINDOW`，不受影响。
- **为什么不去掉计划任务**：普通用户令牌下没有可替代的原生投送路径（`WTSQueryUserToken`/`CreateProcessAsUser` 均 1314，`PARENT_PROCESS` 不迁移 session），实测与 PoC 见 `../research/13-native-worker-launch-feasibility.md`。`RunEx` 是唯一不需要提权的方式，保持现状。

## 6. 验证

已落地的自动化验证：

- `ChildCredentialTests`：用户名/域/UPN 拆分、空用户名或密码拒绝、凭据库写入→读取→删除往返（随机测试目标，结束后清理）。
- `ChildLifecycleTests`：持有器失效且无 child 时重建、有自有 child 时重连同一实例、child 归属不明时拒绝、`status.health` 区分 worker 存活与 GUI 可交互、残留实例可直接停止。
- 既有子会话套件（IPC、转发、请求、进程、输入、连接就绪、CLI、MCP 工具目录）全量通过。

首次真实登录仍需要用户在本机输入一次账户密码（本文 §3）；该输入只发生一次，之后由凭据管理器自动完成。
