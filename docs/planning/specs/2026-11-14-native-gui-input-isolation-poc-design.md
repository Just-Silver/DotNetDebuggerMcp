<!-- 职责：为宿主原生 GUI 并发真实输入设计最小验证实验、验收判据与通过后的条件式集成边界，面向后续实施者；不写：机制资料全集、旧实验结果或正式产品实施任务表。 -->

# 不干扰用户的 Windows GUI 自动化：PoC 与条件式集成提案

日期：2026-11-14。**状态：维护者更正任务后，已授权设计并实施独立 PoC；未批准正式产品重构。** 实验代码在 `tests/Fixtures/NativeInputSpike/`，结果分别登记 research/07（后台消息）与 research/08（Child Session），不在本设计复制。

机制证据、可行性矩阵、来源及推荐依据归属 [research/07 §6](../research/07-desktop-isolation-options.md#6-2026-11-14-不干扰用户的-gui-交互复核)；Child Session 结果归属 [research/08](../research/08-rdp-child-session-spike.md)。本提案不把旧 UIA 结果重算为真实输入通过。

## 0. 更正后的优先级与本轮范围

维护者明确：**解决用户操作被干扰，而非为隔离而隔离**。独立 Session/Desktop/虚拟设备/所有操作都 SendInput 均不强制。可靠后台 UIA 和窗口级机制是有价值的部分能力；确需真实输入/前台的场景才需要补充环境。宿主环境、Debug 流程、轻量性是重要偏好，不应先把所有路线变成“必须完整复用所有硬件”的二元门槛。

因此本轮实际选择两个独立 PoC：**同桌面后台窗口消息能力对照 + Child Session 内前台真实输入**。Multiseat 仅保留为资料/准入备选，不安装驱动。Sandbox/VM 保留成本比较，不默认选，也不先验全部排除。

本机 child-session 开关初始关闭；维护者另行授权管理员启用并验证。没有更改凭据委派、Hello、证书或其他安全策略。已取得的成功/失败/未测条件以结果文档为准；下文是完整试验设计，并非每个阶段都已完成的清单。

### 0.1 后台运行验收澄清（维护者后续裁定）

维护者明确：“在后台就行，不抢宿主就行，最小化不是强求。”

- 核心验收是自动化在后台执行时不意外移动主会话光标、抢占键盘输入或改变用户前台窗口；子会话内目标可以正常可见并处于其自身前台。
- 允许连接客户端保持连接、非最小化并在主会话失焦；不要求最小化、彻底不可见、断开连接或锁屏后仍可执行真实输入。
- 因此已测得的“客户端最小化时输入失败”保留为能力限制，但**不作为当前路线的阻塞项或必修故障**；不得为追求最小化支持扩大系统改造。
- 默认不自动最小化客户端。隐藏连接窗口只作为可选能力，其长期可靠性另验，不把单次成功变成默认保证；需用户认证或查看时才显式展示，不在自动化期间强行抢前台。

本澄清限定当前验收范围，不改变下文历史试验设计和已记录的实际结果，也不等于批准正式产品重构。

## 1. 目标、不妥协项与候选

目标是消除自动化对用户的输入/焦点干扰，而非 OS/安全隔离：优先在宿主 Windows 直接运行 Debug 产物，复用软件及适用硬件；补齐 UIA 无法完成的真实交互，保留脚本、UIA、截图和调试工作流。

- 当前原生 PoC 不安装额外 .NET 环境、不 publish 自包含应用、不复制目标部署树；VM/Sandbox 只在环境成本可接受的条件下作为比较备选，Win+Tab 桌面不当输入隔离解。
- 不修改系统 DLL、不用 RDP Wrapper、不规避普通客户端会话限制；不放宽登录/证书策略来强求通过。
- `InvokePattern`、ValuePattern 和窗口消息只作为独立对照，不计入真实输入成绩。`SendInput` 是系统合成输入，不等于物理 HID 设备报告；若目标要求 Raw Input/专用硬件事件，另立实测项。
- 不以暂停用户输入、`BlockInput`、临时切主桌面、抢焦点后还原或光标移走再移回来证明隔离。

候选选择（研究者建议，不是维护者已批准的产品路线）：

1. **PoC A：后台窗口级交互。** 对缺 UIA pattern 的自绘区域验证消息点击/拖拽/字符的实际作用，并用严格前台/系统键态区域显露限制；不能因它只补一部分操作而全盘否决。
2. **PoC B：官方 RDP Child Session + 子会话内执行进程。** 专门验证确需前台/真实输入的补充能力；不把全部自动化强制搬过去，不先造完整 Runtime。
3. 隐藏 Desktop 作为机制候选/负对照，Multiseat 只有满足第 7 节准入才试。UIA 保持首选，不是被新路线替换的遗留方案。

## 2. PoC 规模与现有资产

主 PoC 只需：父侧 ActiveX 连接持有器、子侧 worker、CLI/脚本薄入口、专用测试目标和结果记录。首先用一轮短实验判生死，再补状态矩阵；不新增生产能力库、不改解决方案或现有 MCP 工具面。

后台对照复用同一测试窗，以非激活样式运行在父桌面；只发向自有 HWND 的消息，明确拒绝父侧 `real` 请求。该路径与子会话真实输入分别记分。

- 历史 `../../Demo/ChildSessionSpike` 仅作为资料线索：**本机该路径不存在**，不能假定可直接复用二进制。实施时先找回源码；找不到则在 `tests/Fixtures/NativeInputSpike/` 建独立实验，按 `tests/AGENTS.md` 进门，不默认加入 CI。
- 目标优先现有 WinForms/WPF 测试应用的 **Debug 框架依赖构建**，再补 WinUI 3/Avalonia 各一个实际目标；不把一种框架通过外推全部框架。
- `SharpSight.Capture` 可直接项目引用。UIA 门面当前 internal，PoC 可用已有 FlaUI 依赖做最短探测；如需完整复用，有限 IVT/小门面须另行批准，禁止复制实现或引用整个 MCP 宿主。
- 实验产物归 `tests/TestData/NativeInputSpike/`，源码与运行产物分开；不得清理已有 Sandbox/其他实验数据。

## 3. 主 PoC API 与执行顺序

### 3.1 P0：只读前置检查与用户确认

1. 记录 OS 版本/SKU、父 SessionId、账户 SID、完整性级别、RDP ActiveX 注册及依赖服务、`WTSIsChildSessionsEnabled`；不读取密码或导出凭据。
2. 通过 `WTSEnumerateSessions`、`WTSGetChildSessionId`、`WTSQuerySessionInformation(WTSConnectState)` 确认没有其他人在用的 Child Session。已有会话不抢占、不注销。
3. 若需 `WTSEnableChildSessions(true)`，列出当前值并请维护者授权一次性管理员操作；默认不改凭据委派、安全模式、Hello 或证书信任。必要时使用系统认证界面人工登录，不能为了免密阻塞输入机制验证。
4. 指定一个真实目标及其硬件/API 清单；没有设备授权或实测条件时标为“未测”，不能完成“所有硬件复用”签收。

本轮只读核查：Windows 11 专业版 `10.0.26300`，父进程 SessionId=1，指定 RDP ActiveX CLSID 已注册，`query session` 输出仅 console Active、未列出已有 child。该命令最终退出码为 1，不能把输出当完整成功证明；**未调用启用/连接 API**。

### 3.2 P1：建立子会话并核对执行归属

1. 父会话 STA 进程创建 `MsRdpClient` ActiveX；连接前通过 `IMsRdpExtendedSettings.Property` 设置 `ConnectToChildSession=true`，再调用 `Connect`。使用 localhost，正常校验证书，不伪造认证成功。
2. 用事件与有上限的查询等候真实连接、登录及有效 child ID，不能用窗口存在/`Connected` 某个瞬时值代替完整就绪。记录父子 WTS 状态、WinSta0、线程 Desktop 和 `OpenInputDesktop` 名称。
3. 优先验证历史可行的计划任务 `TASK_LOGON_INTERACTIVE_TOKEN` + `IRegisteredTask.RunEx(..., TASK_RUN_USE_SESSION_ID, childId, ...)` 投送 worker。任务为当前账户普通权限，任务名随机唯一。失败就记录，不暗中改为 SYSTEM 任意命令服务。
4. worker 回报自身 SID/SessionId/映像路径/创建时间；父侧验证确为本次 child 后才接受输入命令。命名管道限制当前账户，并校验本轮随机实例凭据与端点身份；不信任单独的 SessionId 或 PID（可能复用）。
5. CLI 退出不关闭连接持有器；先用最小常驻持有器证实跨两次 CLI 可用，不提前实现完整产品守护进程。

`WTSQueryUserToken` 需要 LocalSystem + SeTcb，不作为普通用户默认路径。`LogonUser`/`CreateProcessAsUser` 不会创建新的 TS Session；将来若使用后者，须分别处理 token、Desktop ACL、profile、环境和跨会话不能继承句柄的问题。

### 3.3 P2：不部署环境，直接运行现有 Debug 构建

1. 在宿主照常 `dotnet build -c Debug`，记录 exe/dll/PDB 绝对路径与哈希。worker 从**相同绝对路径**启动，不复制、不安装 Runtime。
2. 记录目标实际加载的 CLR 路径/版本、Windows App SDK 版本与身份/注册情况；父子分别执行宿主现有 `dotnet --list-sdks`/`--list-runtimes`。只有本机已安装的版本才能列为本轮通过，不能虚构 .NET 11 等版本测试。
3. 比较必要的 PATH/DOTNET_ROOT、工作目录、账户 SID 与测试专用 HKCU/HKLM 配置读取结果。不要输出整个环境变量、注册表或秘密。环境继承不假定等同父终端的临时变量；显式传递目标所需项。
4. 对选定 COM 组件做实际激活/调用，对 Windows App SDK 的目标验证正常启动；同一 Windows 安装不意味着 ROT、会话级 COM server、应用单实例和 package 状态完全一致。
5. 对每项硬件从目标内走其正常 API 执行获准的最小无破坏操作，记录设备/驱动/返回值并与父会话基线比较。设备能枚举不算功能访问成功；独占设备不得在父子同时抢占。

### 3.4 P3：真实输入的核心判生死实验

1. 目标有普通按钮、文本框、hover 显示区、只能靠命中测试完成的自绘拖拽区；测试夹具记录收到的输入与最终业务状态，不通过 UIA 调其处理器。
2. **所有移动/按键只由 child worker 调 `SendInput`**，包括 `MOUSEEVENTF_MOVE/ABSOLUTE/VIRTUALDESK`、左右键 down/up、分步拖拽、键盘 down/up 和快捷键。先测试键码/扫描码路径，再分开测 Unicode 与中文 IME；不禁 IME 来宣布一般键盘可用。
3. 注入事件设置本轮 `dwExtraInfo` 标记；记录提交数与 API 返回值，但返回事件数正确不等于目标消费成功。同时在目标验证位置/点击结果、hover、捕获、拖拽轨迹终点及输入文本。
4. worker 仅在 child 内设置目标前台与焦点；父侧不调用 `SetForegroundWindow`/`SetCursorPos`/`AttachThreadInput` 来帮助成功。若需要把 RDP 客户端带到父前台，隔离实验即失败。
5. 用户在父桌面的专用测试窗持续移动/拖拽/打预定非敏感文本；Child 同时循环执行。分别观察父子 `GetCursorPos`、前台 HWND、焦点、测试文本和带标记的注入事件；父侧观察器只记本次标记和测试窗结果，不记录用户其他按键。
6. 至少 60 秒短循环与一次 10 分钟连续运行，覆盖用户按住 Ctrl/Shift/鼠标键时 child 另一状态输入，检查跨边修饰键/按键状态串扰。父侧物理输入不应被重定向到 RDP 子窗，子侧输入不应出现在父测试窗。

**核心成功**：两边有同时重叠的真实操作证据；child 结果正确，父侧无标记事件/串字/相关跳鼠标/焦点劫持。用户正在移动时不能要求父光标坐标恒定；单纯“前后光标相同”也不能证明中途没干扰。采样无变化与人工无感均只能支持本轮，不能承诺绝对无遗漏。

**真实输入分项失败**：需用户暂停、需父侧激活客户端/切 Desktop、用消息/UIA 代替待测真实动作、目标只收到部分事件、出现串扰，或实际运行了复制/自包含目标。失败后停止注入，不回退父会话；仍单列其他已通过的能力，不能把局部失败归成方案毫无价值。

## 4. 必测状态矩阵：不隐藏失败项

“目标窗最小化”与“外层 ActiveX 客户端最小化”、“父侧失焦”与“child Disconnected”必须分列；状态通过 API/事件确认，不只靠请求参数。

| 条件 | 必须观测 | 预期与裁定方式 |
|---|---|---|
| client 保持连接，父侧失焦/被遮挡，child 目标正常可见 | 实际父子 WTS 状态、双端真实输入、目标帧 | **核心必过**；不能以客户端无焦点为由要求用户停止操作 |
| client 最小化/隐藏但不主动 Disconnect | 是否被节流/断开、输入是否消费、帧序号是否前进 | 未验证；失败就只给“有条件可行”，不能称全状态满足 |
| child 内目标被其他 child 窗口遮挡/失焦 | 命中位置、目标焦点、遮挡关系 | 允许仅在 child 内激活/置顶目标后再测，但要分别记录原状态失败与恢复后结果 |
| child 内目标最小化/隐藏 | 不还原时真实 hit-test、键盘焦点及帧 | 预期普通鼠标无法命中不可见窗口；如需还原，原场景记不支持，不能偷改为已还原后通过 |
| child Desktop 非 input desktop | `GetThreadDesktop` 对比 `OpenInputDesktop`、输入消费 | 负对照；仅 `SetThreadDesktop` 不作成功，禁止 SwitchDesktop 抢走父桌面 |
| 父会话锁屏 | 人工锁屏前后日志、child 连接状态、子侧目标动作与帧 | 单独授权后人工进行；child 自身不可锁不证明父锁屏时 ActiveX 保持工作 |
| 要求锁定 child 本身 | 官方不支持该状态 | 明记不支持，不能用“child 不会锁”冒充锁定态通过 |
| client Disconnect/关闭但不注销 child | WTS Disc、输入消费、每种截图源 | 历史截图已有退化证据；重新测真实输入，失败不能静默重连后算原条件成功 |
| parent 注销/child 注销 | 任务退出/连接丢失/结果不确定/剩余进程 | 父会话结束 child 结束是设计限制；只能验收可观测清理，不是持续执行通过 |

结果分别标 `PASS / FAIL / UNSUPPORTED / NOT_TESTED`，必须带状态、错误码和证据路径。核心通过不等于所有状态/所有设备通过；汇总保留每项缺口，不因最小化某状态不支持就抹掉正常连接态的能力。恢复/置前只在目标交互环境内进行时，可以作为正常编排步骤，但要记录原状态与恢复动作，不能偷改测试条件。

## 5. 截图、UIA、脚本、调试及重复启停

### 5.1 截图与 UIA 分开签收

- 截图从 child 调 `SharpSight.Capture`；记录实际 `WGC/PrintWindow/BitBlt` 来源、SessionId、Desktop、尺寸/DPI/像素原点、采集时间。
- 目标每轮显示序号/颜色并验证截图中确实更新；不能仅以非黑或 PNG 哈希不同代表目标帧正确。遮挡态屏幕 BitBlt 可能得到遮挡窗，不能当成目标截图。
- UIA 在 child 内执行 find/read/value/invoke，对照控件业务状态；记录使用的 pattern。UIA 成功不抵消真实输入失败，自定义控件缺 peer 不改成“只测按钮”。

### 5.2 CLI/脚本入口草案

以下是**拟议命令形态，当前不存在**，不要求 Agent 经 MCP：

```text
native-input-spike probe
native-input-spike start
native-input-spike exec --instance <id> -- <宿主程序绝对路径> <参数>
native-input-spike run-script --instance <id> -- <宿主现有 powershell/pwsh 等> <脚本路径>
native-input-spike input --instance <id> --case <输入试验>
native-input-spike capture --instance <id> --pid <目标pid>
native-input-spike status / task / cancel / artifacts
native-input-spike stop --instance <id>
```

- `exec/run-script` 真正在 child worker 下启动进程，不只是宿主 CLI 调一次本机 shell；执行前后回传实际 SessionId/账户身份。
- PowerShell/C# 等脚本可直接调用公开系统 API；C# 需要编译时用宿主已装 SDK。原始会在父会话运行的任意脚本不会被系统自动重定向，必须显式选择上述执行入口。
- 跨 CLI 保存 GUI/任务、stdout/stderr/退出码、超时/取消与产物，解释器变量不要求持久化；持续排空输出。CLI 超时退出是放弃等待还是取消任务必须显式区分。
- 无 OS 安全隔离：同账户脚本仍可能修改宿主文件/注册表/服务。输入路由保证限于正常 Session 内输入，不声称可以阻止恶意/提权脚本或应用主动发起跨会话操作。

### 5.3 调试工作流

1. 先从父侧用现有 `DebugSessionManager.AttachAsync(childPid)` / Engine 验证断点、读栈/变量、继续、断开；权限/位数/CLR 版本分别记录。不能从 Win32 DebugActiveProcess 文档直接推定 ICorDebug 已可用。
2. 首轮允许显式 attach 已运行目标，但这只签收 attach，不签收 Main 前 launch。随后在 child 内调用既有 `LaunchAndAttachAsync` 的启动期 CLR 回调路径，验证 Main 前停点与 PDB；不得新增固定 sleep 代替启动同步。
3. 如果父 attach 失败，再评估把现有 Engine/Session 库运行在 child 内并转发调试请求；这是**单列的备选实验**，不是未经验证即可保持全部调试行为的承诺。不重新实现调试器，不让 worker 依赖整个 MCP 宿主。
4. 调试器停住 GUI 线程时 UIA/截图可能超时，测试先 continue 再输入；不能把调试停顿误归为会话输入失败。

### 5.4 清理与恢复

- 执行 10 次启动→输入→截图→停止；保留一次跨两个独立 CLI 调用的 GUI 存活验证，以及取消/worker 异常退出/客户端断开各一轮。
- 先停止输入并释放本轮按下的键/按钮（仅原 child 仍存在且身份一致时），正常结束自有目标/worker，再用 `WTSLogoffSession` 注销**本轮创建且仍匹配父会话/账户的 child**，等待消失；Disconnect 不当成注销。
- 只删除本轮随机名称计划任务/IPC；不注销父会话，不杀任意 RDP 客户端，不用进程名批量清理。默认不把系统 child-session 开关强制关闭；如曾获准修改，需确认无其他使用者后按原值恢复。
- child ID/PID 被复用或身份无法确认时停止自动清理并报告人工处理项。崩溃后执行结果可能不确定，真实输入任务不得自动重放。

## 6. 项目集成设计（仅在对应能力 PoC 通过后）

分级执行为研究者建议：① 已验证无干扰的 UIA；② 按应用验证的窗口级交互；③ 必须前台/真实输入时，显式选择验证过的环境；④ 无可用路径就报告限制。不能静默启用物理输入、切 Desktop 或抢宿主焦点。现有 `focus/commit/restore` 的副作用也须按路径披露，不把所有 `ui_*` 一概标为无干扰。

```text
Agent shell / 普通脚本 ── CLI ─┐
现有 MCP 宿主 ── 薄适配器 ────┼── 父会话连接持有/任务状态 ── IPC ── child worker
                              │                               ├── 启动原 Debug exe / 脚本
                              │                               ├── SendInput / UIA
                              │                               └── SharpSight.Capture
                              └── 父侧现有 debug_attach（单独验证）
```

| 位置 | 条件式改动 / 不改什么 |
|---|---|
| 新增小型原生输入执行宿主及 CLI | 持有 ActiveX STA/连接生命周期、任务与脚本入口；不放进名字相近的 `DotNetDebugger.Session`（它管理调试而非 TS Session） |
| `SharpSight.UiAutomation` | 复用定位/语义/等待；如确需对外开放，仅提最小门面方案。真实拖拽/键盘目前不是现有完整能力，先留在 spike，不提前重构 |
| `SharpSight.Capture` | worker 直接引用；保留实际来源/DPI/原点，不把父窗坐标当 child 坐标，不把回退截图当新鲜帧保证 |
| `DotNetDebugger.Engine` / `DotNetDebugger.Session` | 先原样测试 attach；跨会话失败再研究 in-child 调试宿主。现有 launch 默认当前 Session，不可只加一个目标 SessionId 参数就声称投送完成 |
| `DotNetDebuggerMcp` | 最后为补充能力增加显式薄路由；原 UIA/截图/调试入口继续兼容，不全局改默认执行环境。新增面才同步 Description、握手与 README；本轮工具零改动 |

将来的请求/元素引用须同时关联 instance 代际、child SessionId、worker 标识、目标 PID+创建时间、Desktop 与 frameId；现有单进程 `FrameRegistry` 数字不能直接跨 worker 拼接。全方案不是多租户安全边界，不顺手引入 DI/策略框架或多后端平台。

## 7. Multiseat 备选的准入与最小试验

只有厂商提供可核验答案并获维护者授权后，才进入第二条 PoC：

1. 当前 Windows 11 版本/Secure Boot/HVCI 下有受支持签名驱动与明确卸载恢复方式；核对 Windows、目标应用、厂商各自授权，不把厂商功能页当微软许可批准。
2. 确认没有第二物理显示器/键鼠时，厂商是否支持 IddCx 显示输出 + 可分配虚拟 HID 的无人值守席位；不能用虚拟设备被枚举代替席位建立成功。
3. 确认席位进程的 Session/Desktop 身份、软件注入归属和可用 CLI/API；**能分配一只 USB 鼠标不证明 SendInput 可以定向它的席位**。
4. 在试用环境重复第 3–5 节：同路径 Debug exe、硬件实际调用、席位内 SendInput、父侧同时物理操作、截图与十轮清理；先做单次最小输入验证再投入完整脚本。
5. 若只有人工分配物理设备、无稳定自动管理接口或软件输入无法定向，记“成熟多席位产品，但不满足本项目自动化准入”，不继续包装成达标方案。

## 8. 最终判定与最小妥协

- **能力通过**：分别签收后台语义、消息级、前台真实输入、环境/设备、调试、CLI、截图及生命周期，不用某一层通过替代另一层证据。
- **条件可用**：例如只在 child 保持连接、目标在 child 内可见时成功；列清可自动恢复的条件与不可消除限制，不扩大宣传，也不直接判整个方案失败。
- **不满足该场景/未证实**：出现串扰、必要设备不可用、需不受支持的系统修改或缺真实输入证据；报告具体场景，不启动未经验证的产品重构。
- 最小妥协按损失由小到大供选择：保留 child 可见但失焦的连接窗；为确有冲突的设备/应用规定一次只在一侧使用；接受仅 UIA/消息而放弃通用真实输入；最后才是独占测试时段/专用测试机（后两者不满足原并发目标）。若硬要求是“当前 console 的全部设备、会话状态、隐/锁/断开均无差别 + 并发真实输入”，现有证据不足以支持任何方案，必须明确保留“不存在已证实全解”的结论。

## 9. 本轮交付边界

独立 PoC 已落地并完成基础实机验证，结果归 research/07 §6.8 与 research/08 §6，不把本节设计里的所有预期阶段写成完成。当前推荐分级组合：保留既有 UIA，窗口级路径按控件验证，Child Session 作为前台真实输入补充候选。未测的设备/应用/锁屏/压力场景仍未测；正式 Runtime、任意脚本投送与 MCP 路由未实施。实验会话已注销、系统 child-session 开关已恢复原值，生产 `src/`、slnx 与现有工具接口未改。
