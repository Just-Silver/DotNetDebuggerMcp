<!-- 职责：维护子会话能力库的依赖边界、所有权与验证入口；不写：产品工具用法或历史实验结果。 -->

# SharpSight.ChildSession

Windows 子会话执行能力库，不依赖 MCP、宿主或调试 Session 库。生命周期、请求、任务和输入分别管理资源；不得回退父会话输入。

- 请求结果未知时保留 requestId，禁止自动重放变更；执行中的请求不得因结果记录限额被淘汰。
- 真实输入必须核对实例、连接、实际会话、input desktop 和目标身份；停止只注销核实属于本实例的 child。
- ActiveX Connected 不代表 WTS 会话已可查询；首次观察到的自有 child 编号先登记，连接态 error=2 时继续等待，不在认证/注册间隙主动销毁 ActiveX。已登记编号变化必须拒绝接管。**Ready 需完成系统登录并收到 worker 握手**（worker 在自有 child 内落地并连上执行通道）；控件连接/ActiveX Connected 均不等于 Ready，认证待完成时保持当前状态并继续按 `status` 查询，不自动催认证。
- **自动登录凭据只从 Windows 凭据管理器取**（本产品目标优先，其次 `TERMSRV/localhost`/`127.0.0.1`），经 `UserName`/`Domain` + `AdvancedSettings.ClearTextPassword` 注入；密码不进实例记录、命令行、日志或返回。无凭据/读库失败/注入失败/用户取消一律退回系统连接窗口认证，不能把实例直接判 Faulted。凭据失效仍停在认证时，状态提示重新 `login`，不无限重试。
- 持有器失效时 `start` 仅按 child 归属做有界恢复：无 child 才重建新实例；登记编号与当前 child 一致才重连同一实例；编号不明拒绝接管。不得把「关闭连接窗」或「宿主退出」当成注销。
- 新代码单方法尽量不超过 60 行，超过 80 行必须拆分。
- 行为测试位于 `tests/DotNetDebuggerMcp.Tests/Child*Tests.cs`；真实 child 集成验收仅显式授权运行，不自动修改系统开关。
- 定向 MTP 验证优先传完整测试类名，并核对发现数；本机 `--filter-class "*Child*Tests"` 曾运行零个测试并退出 5，不能当作通过。已验证完整类名 `DotNetDebuggerMcp.Tests.ChildRequestTests` / `ChildIpcTests` 可用。
- 正式设计与任务见根规划地图中的子会话 MCP/CLI 设计及实施计划。

## IPC 与产物边界

管道使用 CurrentUserOnly，并在收发两端以原生对端 PID 核对创建时间、SID、Session 和映像；客户端自报字段不算证据。实例密钥仅放受 ACL 限制的实例目录，不进参数或正常日志。长度前缀在分配正文前校验，单帧至多 1 MiB；截断帧只结束当前连接。

每条连接只承载一份请求信封；核验后监视对端EOF并传播连接级取消，不能只取消调用端等待。停止监视的令牌与执行令牌分开，避免正常响应完成时误取消已受理工作；进程任务仍须显式cancel才结束。

长期 holder 启动前将当前宿主的有效标准句柄标为不可继承（不关闭或替换），holder/worker 自写系统临时目录 `%TEMP%\DotNetDebuggerMcp\child\<实例>\{holder,worker}.log`（256 KiB 轮转）、宿主不再重定向其 stdio；否则短命 CLI 即使退出，读者也可能等不到输出 EOF，宿主退出后无人排空的管道还会卡住长期 holder。`.NET Process.Start` 的重定向不等于句柄继承白名单；机制与回归证据归规划地图的正式接入验证记录。

计划任务投送 worker 时拿不到 `CREATE_NO_WINDOW`，控制台子系统程序会带出可见终端窗口（Windows 11 默认宿主是 Windows Terminal）；worker 必须先接管诊断输出（转临时目录日志）再 `FreeConsole` 脱离，否则子会话桌面会长期占用一个空终端窗。真实按钮/截图类操作前要确认子会话桌面没有本产品遗留窗口。

投送用的临时计划任务会在异常路径残留（无触发器，不会自行运行）。清理只允许删「实例记录不存在 / 实例已停止 / 持有器已退出」的 `SharpSight-child-*` 任务；活动实例的任务与任何其它计划任务都不得触碰，读不到实例记录时宁可不清。任务名前缀以 `ChildScheduledTaskCleanup.TaskNameFor` 为单一来源，投送与清理共用。

**不要再用「原生进程创建」替换 `RunEx`**：实测**未提权**令牌（UAC 过滤的 Medium）与 **UAC 提权后的 High 令牌**下，「复制会话内令牌 + `CreateProcessAsUser`」都是 1314（本机管理员令牌不含 `SeAssignPrimaryToken`），`WTSQueryUserToken` 也是 1314（要 LocalSystem + `SeTcb`）；`CreateProcessWithTokenW` 官方明确只在调用方会话创建，`PROC_THREAD_ATTRIBUTE_PARENT_PROCESS` 只伪造父子关系、**不迁移 session**（新进程仍落在调用方会话）。证据与 PoC 见规划地图的 `research/13-native-worker-launch-feasibility.md`；要打通只剩「改安全策略授权 `SeAssignPrimaryToken`」或「LocalSystem 服务」，均不在允许范围。另注意：`PARENT_PROCESS` 的属性值以指针存入属性列表，必须存活到属性列表销毁，否则 `CreateProcessW` 报 `ERROR_INVALID_HANDLE(6)`。

连接窗自适应只走 `IMsRdpClient9::UpdateSessionDisplaySettings`（经 IDispatch 调用）：物理宽高单位为**毫米**（按 96 DPI 换算，`<10`/`>10000` 会被忽略），方向 0、桌面/设备缩放 100；连接后至少 1.5 s 才下发（过早返回 `E_UNEXPECTED`），尺寸未变或最小化时不发。不用 `Disconnect/Connect`（会白屏），也不依赖 `SmartSizing`（不保证纵横比）。

核验 API：[GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)、[GetNamedPipeServerProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid)。这不是同账户恶意程序的安全沙箱。

产物不能越过实例目录或经过重解析点；父侧读取核对长度，单个落盘产物读取上限 64 MiB，避免伪造长度造成无界内存分配。PNG 真正内容及其新鲜性仍由截图业务验证。

## 任务进程

先挂起创建、归入自有 Job、再恢复；不能先让应用运行再追认所有权。每任务 Job 使用关闭即结束标志，仅取消本任务进程树，不按进程名结束应用。任务日志达到限额后继续排空管道；stdin 的父写端不可被 child 继承，否则 child 会因自己的写端而永远收不到 EOF。

原生契约：[AssignProcessToJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject)（Job 内进程必须同 Session）、[扩展限额结构](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)。

## GUI 执行边界

实例共享 `ChildGuiExecutionGate`。已开始的 UIA COM 调用必须等待真实后台 Task 结束，期间保留客户端和门；截止只让调用方取回 running，不将其登记为终态。超预算仍未返回时拒绝新 GUI 操作，控制查询与进程任务不经过此门。

`ui_wait` 仅在订阅、探测和退订时取门，等待信号或轮询睡眠时放门；不推进帧、不替换清单。门内顺序调用允许同一执行上下文重入，不能把重入上下文用于并发派生 GUI 工作。

断连时进程list/status/output/cancel仍可经控制通道到达存活worker，不能依赖GUI Ready/Active才能取消任务。截图0秒表示立即取门尝试，门忙就拒绝、不排队，实际采集仍受内部护栏。

连接窗（父侧 holder 窗口）被最小化时，只有 `ui_*`/`screenshot`/`child_pointer`/`child_keyboard` 在 holder 转发前由原 STA 用 `ShowWindow(SW_SHOWNOACTIVATE=4)` 按需还原并以 `IsIconic` 复核；不激活、不置顶、不发父侧输入，未确认则不向 worker 投送（返回未执行，不重放）。放弃等待会撤销已排队的 STA 回调，迟到回调在变更前检查取消/截止。`child_session` 与 `child_process`（含 start）不触发该还原；child 内业务目标窗原有的 `SW_RESTORE`+前台语义不变（`ChildInputNative.ShowRestore` 不得用于连接窗）。

## 观察与截图

`ObservationRegistry` 保留最近 256 条来源；普通视觉采集不使 UIA 清单失效，新 UIA 帧使旧清单/元素引用失效。引用同时核对实例、worker、进程创建时间、窗口几何和显示布局；纯视觉引用不能充当 index 清单。

`ChildTargetResolver` 用线程 DPI 上下文读取物理窗口矩形并恢复原上下文，不改变用户窗口的 DPI 策略。Win32 约定已对照本地 `../../Externals/wpf/src/Microsoft.DotNet.Wpf/src/Shared/MS/Win32/UnsafeNativeMethodsCLR.cs` 的 `SetThreadDpiAwarenessContext` 返回旧句柄语义。

真实 child 图像测试须显式设置 `DOTNETDEBUGGER_CHILD_TEST_INSTANCE` 指向本产品已授权的活动实例；不设置时只运行产物/引用边界测试，真实项明确跳过，不能把跳过算作真实截图成功。此测试临时启动两个不同纯色标记窗口，结束后取消自有任务并清理父侧夹具，不启用开关或接管其他会话。

## 输入所有权

`ChildInputController` 与 UIA/截图共用同一个实例门；系统窗口、键态和 SendInput 仅由 `IChildInputPlatform` 承载。每个事件前重新检查会话、input desktop、目标身份/前台及指针命中区域。键盘语义受到非本轮修饰键影响时拒绝，不为凑成期望组合而释放别人的键。

`PressedInputScope` 只登记系统确已接受的本轮 down，结束或异常时逆序释放。失焦允许释放自己的状态，但原会话/input desktop 不可确认时连清理事件也不向新环境提交，结果为 unknown；成功只表示事件已提交，不代表应用已消费。

原生布局与事件标志以官方 [INPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-input)、[MOUSEINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-mouseinput)、[KEYBDINPUT](https://learn.microsoft.com/en-us/windows/win32/api/winuser/ns-winuser-keybdinput) 为准；生产代码不得依赖前期 `NativeInputSpike` 试验工程。
