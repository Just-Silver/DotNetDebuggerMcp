<!-- 职责：记录子会话 MCP/CLI 正式接入期间的基线、验证证据和未完成项，面向维护者；不写：功能设计、实施步骤或根因未验证的修复方案。 -->

# 子会话 MCP / CLI 接入验证记录

日期：2026-11-14起，最终实机验证2026-11-15。**状态：安装产物三轮业务验收已通过（§8）；最终全分支审阅与提交收尾中，尚未合并或发布。完整宿主套件仍须如实单列既有失败，不能称全套全绿。**

设计与任务归 [正式设计](../specs/2026-11-14-child-session-mcp-cli-design.md)及[实施计划](../plans/2026-11-14-child-session-mcp-cli.md)。

## 1. 开发位置

维护者明确要求原仓库新分支，不使用 worktree。当前分支 `feature/child-session-mcp-cli-integration`，起点 `495738e`（2.5.1）。先前未提交研究、夹具和设计均保留，未混入两个代码提交。

初始化时建立的临时 worktree 已解除 Git 登记、删除临时分支；剩余物理目录已空，但被其他进程占用，未强杀未知进程。后续开发只在原仓库功能分支进行。

## 2. 未改产品前的全套基线

在干净的 `495738e` 上生成夹具后执行：

```powershell
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -c Release
```

结果：794 项，785 通过、9 失败、0 跳过，约 10 分钟。该运行不包含任何新子会话产品代码，**原始基线不是全绿**。原始日志保存在本轮 `.superpowers/sdd/2026-11-14-child-session-mcp-cli/baseline.log`（忽略产物，不承诺长期保留原始文件）。

失败清单：

| 测试类 | 方法 | 直接观测 |
|---|---|---|
| `DebugUiToolsTests` | `UiActionInvoke_Button_TogglesState_ThenWaitEvent` | 前台发生变化，非目标窗口 |
| `DebugUiToolsTests` | `UiActionScroll_AndScrollIntoView_NoPhysicalInput` | 前台发生变化，变为目标窗口 |
| `DebugUiToolsTests` | `UiActionExpandCollapse_ComboBox_ChangesState` | 前台发生变化，非目标窗口 |
| `DebugUiToolsTests` | `UiInput_ValueAndRangeValue_AndReadOnlyRejected` | 前台发生变化，变为目标窗口 |
| `DebugUiToolsTests` | `UiActionToggle_CheckBox_TogglesAndReadsBack` | 前台发生变化，非目标窗口 |
| `DebugUiToolsTests` | `UiInput_ByIndex_ResolvesCachedTargetDescriptor` | 前台发生变化，非目标窗口 |
| `DebugUiToolsTests` | `UiActionSelect_ListItem_MarksSelected` | 前台发生变化，变为目标窗口 |
| `ScreenshotToolTests` | `Window_ByMultiWindowPid_SelectsRealWindow_NotTinyHelper` | 按 PID 选中无标题大窗口，业务断言未通过 |
| `ScreenshotToolTests` | `Element_ByName_ReturnsPngImageBlock_WithFrameId` | 元素矩形不在捕获到的顶层窗口帧内 |

以上只说明失败表现，不擅自归因为用户操作、环境或框架 bug；未完成单变量复现与根因闭环。不能通过放宽断言、跳过测试或改无关生产代码伪造绿色基线。

## 3. 已完成的基础能力定向验证

- `318833b`：请求登记与结果查询；`ChildRequestTests` 4/4，通过前已看到缺能力的 RED。
- `502b46a`：私有 IPC 与身份校验；`ChildIpcTests` 4/4，实际命名管道验证错误实例/凭据/未知操作/过大请求拒绝、截断连接不破坏后续请求、产物不能越界读取。
- `e6feea2`：生命周期与内部角色接线；`ChildLifecycleTests` 4/4，系统边界可控验证并发复用、拒绝外部 child、worker 退出后的所有权停止与禁止活动期间 disable。Release 内部非法角色退出 1、stdout 空、stderr 中文，未启动 GUI。
- 任务执行层定向验证 `ChildProcessTests` 5/5：真实挂起创建/Job 归属后运行，参数与环境、独立取消、日志截断后继续排空、取消等待不杀已接受任务及 stdin EOF。新增 EOF 用例曾 30 秒超时，确认继承父写端导致后修复并通过。
- `c0659d8`：共享 UIA/截图执行提取；语义 9、工具契约 3、纯截图 16 项通过。帧几何用例提取前后相同失败，完整截图套件一次 120 秒终止，不计通过。
- `62ae2cc` GUI 完成跟踪：`ChildGuiExecutionTests` 5 项与前四组 child 用例合计 22/22；包含真实共享门、可控外部 COM 边界、排队截止与晚完成后的终态对账、等待期间采集/变更、等待未命中及实际会话不符拒绝。兼容验证仍为 9+3+16 全通过。此阶段采集使用文件边界，不代表实际 PNG 或真实 child 已通过。
- 截图/观察定向验证合计 16 项：15 通过、1 项真实 child 对比跳过（尚未授权）。观察边界覆盖普通窗口采集不废弃 UIA、旧 index/复用 PID/跨实例/几何布局变化/引用淘汰拒绝；产物边界验证两次不同颜色的真实 PNG 编解码与对应文件读取、产物删除不回退旧图，图像来源由可控外部边界提供，不能作为真实 child 捕获证据。
- 完整宿主回归运行 600 秒被终止（exit 143），日志复现原始 9 项失败，未产生运行总量/最终摘要，不能计作全套通过或全套完成。之后明确范围兼容验证 9+3+16 全通过；`UiFrameRegistryTests` 为 4 通过、1 失败，元素 `{X=609,Y=357,Width=67,Height=21}` 与帧 `{X=898,Y=476,Width=764,Height=567}` 和提取前相同，未修复。详细日志保存在本计划执行工作区 `task-7-host-suite.log`、`task-7-compat.log`、`task-7-frames.log`。
- `ed0622d` 输入控制器最终 17/17；child 所有定向用例共 50 项，49 通过、1 项真实截图跳过。先观察“pointer 不接受键盘文本 / keyboard 不接受坐标”2 项 RED 及“已有物理鼠标按下不得移动或接管”1 项 RED，再修正并通过。覆盖失焦/环境失效中断、只释放本轮状态、负坐标、取消拖拽、修饰键归属、键名文法、隐藏/歧义/最小化与目标命令结果。原生边界已编译，但这些自动用例只替换外部系统边界，不代表真实输入成功。
- GUI 消息泵夹具 `ChildSessionTarget gui <outputDirectory>` 已构建成功（0 警告/0 错误）；`events.jsonl` 与 stdout 输出 ready/text/commit/chord/click/doubleclick/drag/wheel 真实事件。未运行父侧或真实 child SendInput，真实 UIA/键鼠业务闭环仍待授权验收。
- `32d4187` MCP/CLI 接线合并验证共 117 项：116 通过、1 项真实 child 图像待授权跳过。包含全部 child 用例、能力族/握手、工具面快照与真实 MCP stdio 并发；另有真实 CLI 子进程的失败 JSON/退出码与只读 probe 验证。新增管道断开取消、断连控制旁路、零秒截图不排队和 CLI 读参取消均先观察行为 RED 后验证 GREEN。此验证不等于安装产物、真实子会话业务或完整宿主套件已经验收。
- 新库没有 MCP 宿主或调试 Session 反向依赖；MCP/CLI 工具面已于第 9 项接通。
- 最终分别以完整测试类名复跑 `ChildRequestTests`、`ChildIpcTests`，各 4/4、0 失败、0 跳过。期间一次 `*Child*Tests` 聚合筛选运行零测试、退出 5，已改用精确类名验证；不将该次零测试运行计为通过。

这里只支持上述分项行为，不代表实际子会话的完整产品验收。

## 4. 安装与真实产品验收（2026-11-15，进行中）

维护者已明确授权临时启用 Windows Child Session，仅在本轮自有 child 内运行真实键鼠、截图和调试，至少三轮；结束注销并恢复开关关闭。不改认证/Hello/证书策略，不安装驱动或第二套系统；UAC/认证由用户手动确认。

- 本轮目录：`tests/TestData/ChildSessionAcceptance/20261115-075429/`。
- `install-acceptance-tool.ps1` 从 csproj 读取版本 `2.5.1`，先 pack，再以仅本地源的 NuGet.Config 和独立 NUGET_PACKAGES 安装，不触碰正式安装、不推 tag。
- 安装宿主 DLL SHA256：`141A140ECE461420BD1180BF68957B2F2174DCFC9AE10A5FCB03A1FBDE9A2EC4`，准确 shim/DLL 路径见该目录 `installed.json`。
- 两种安装宿主形式的只读 probe 与内部角色拒绝路径测试：1 通过，真实项在未启用时 1 跳过；不将只读角色分发当作真实 holder/worker 成功。
- 夹具生成脚本完成；正式 `ChildSessionTarget` 额外按 Debug 构建，以提供业务处理器断点。Client 编译 0 警告、0 错误。
- 初始 probe 为 Enabled=false、ParentSessionId=1、ChildSessionId=null、ActiveXAvailable=true；已通过安装路径 `child enable` 获得 completed，正在执行显式三轮场景，尚未给出成功结论。
- 默认 Client 回归全部场景通过（含 679 个反编译写盘文件校验，finally 已清理默认输出目录）；未把真实 child 场景并入默认流程。

### 首次真实启动失败现场

安装实例 `3044b121c8234ba58be8a22ae2202b73`，holder PID=18832、创建 ticks=639302976104926739。用户手动登录后报告连接窗口卡住。只读 probe 报 child ID=2；实例报 Faulted，原因“无法读取会话连接状态”，登记 ChildSessionId=0、WorkerPid=0，没有启动业务夹具或真实 SendInput。

父侧原生对照查询：Session1 的 WTSConnectState 查询成功；Session2 查询失败、GetLastWin32Error=2（系统找不到指定的文件），`quser` 与 CIM 均未列出 Session2 的进程。不能据此把该状态当作已就绪的交互 child。

短暂附加自有 holder 并读栈，随后已 disconnect：STA 停在 `AxHost.DetachAndForward → IOleInPlaceObject.InPlaceDeactivate → AxHost.TransitionDownTo → Form.Dispose → Form.WmClose`；这证明连接正在自动关闭路径内阻塞，不是业务目标调试停点。对应代码在连接态查询抛异常后写 Faulted，finally 自动关闭 ActiveX；child ID 只在 Active 查询成功后才登记，因此此路径丢失了已观察到的会话编号。

按安装路径、PID、Session1、创建时间逐项核对后结束了本轮 holder。首轮真实测试失败（1 项失败，非超时冒充成功），日志见本轮 `client-acceptance.log` 与执行工作区 `task-10-cross-first.log`。后续对同一自有 child ID=2 的注销返回 Win32 error=2；未对其他会话操作。正在按原授权恢复系统开关，未声称清理完成或三轮通过。

随后一次性恢复脚本提权后返回成功，`WTSIsChildSessionsEnabled=false`；安装路径 probe 再次确认 Enabled=false。但 `WTSGetChildSessionId` 仍报 2，Session2 不可查询/注销，也没有对应 CIM 进程；这是**尚未核清的残留编号**，不能擅自当作可接管会话或声称已注销成功。未重建连接、未注销父会话，也未通过重启机器掩盖现场。

补充回归与当前修正（尚未重新打包进行真实复验）：

- 先等价提取连接就绪判定，再观察两条 RED：会话查询 Win32 error=2 导致抛错；已登记会话编号可被新编号静默覆盖。当前先登记观察到的自有编号，error=2 继续等待而非主动销毁认证窗口，编号变化则拒绝；连接/生命周期/转发共 11 项通过。
- 首次 CLI 启动结果直到 holder 退出才完整到达。新增真实进程/管道回归，以短命调用端启动长期持有器后退出，观察到输出 EOF 等待超时 RED。已核对 [.NET 10 `Process.Windows.cs`](https://github.com/dotnet/runtime/blob/v10.0.0/src/libraries/System.Diagnostics.Process/src/System/Diagnostics/Process.Windows.cs) 的 `StartWithCreateProcess`：使用 `bInheritHandles=true`，重定向并不排除调用端原有可继承标准句柄。当前在启动持有器前清除当前宿主标准句柄的继承位（不关闭句柄、不改变读写），并为 holder 单独重定向 stdin 后关闭写端。真实 EOF 回归通过。
- 以上就绪判定、输出生命周期、既有生命周期和真实 MCP stdio 并发合计 12 项通过、0 跳过。此结果不代表三轮真实验收或完整宿主套件已经通过。
- 修正后的 child/握手/工具面/stdio 明确范围合计 122 项：119 通过、3 跳过（未提供安装清单的只读安装项、未启用真实安装三轮项、未提供活动实例的真实图像项）；真实首轮失败仍单独保留，不被这次跳过覆盖。最终只读 probe 仍为 Enabled=false、ChildSessionId=2；未发现本轮 holder/worker/ChildSessionTarget 或 Session2 的 CIM 进程。

### 修正包复验前置与残留恢复

维护者要求继续验收后，新包隔离安装到 `tests/TestData/ChildSessionAcceptance/20261115-083726/`，安装宿主 SHA256=`3EDF07F81755CBE71D5F3B74418E947884717619E804435A47967E4B13F64C3D`。安装测试 2 项：只读宿主形式测试通过；真实项在前置探测拒绝残留编号 2，失败而非跳过或成功，尚未开始三轮。

Windows TerminalServices 只读事件补证：首轮 08:03:12 认证成功，08:03:13 Session2 断连；没有正常用户 shell 就绪的现场证据。官方 [Child Sessions](https://learn.microsoft.com/en-us/windows/win32/termserv/child-sessions) 与 [WTSGetChildSessionId](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsgetchildsessionid) 没有承诺仅关闭客户端或关闭开关就注销 child，因此不把关闭开关等同会话清理。

按原授权尝试**仅重连已知本轮残留用于注销**：重新构建 `NativeInputSpike` 当前源码 Debug，恢复窗口/记录单独放 `recovery-only/`。此为验收前置的手动恢复，不是产品入口验证、不计作三轮之一，也不使用试验结果代替安装产物。启用脚本核对当前编号仍为 2、原 holder 已不存在、Session2 无 CIM 进程；未放宽生产接管保护、未把原 Faulted 登记伪造为 Stopped。恢复结果待核对。

恢复连接实际取得编号 3，holder PID=33072、ticks=639303001112969552；`quser` 列出同账户 Active。按本轮 connection.json、holder PID/创建时间/父会话及系统当前编号交叉核验后，WTSLogoff(3) 成功，holder 正常关闭；probe 确认 ChildSessionId=null。因此先前残留状态已经通过受控重连后正常注销恢复，不需要注销父会话或重启机器。

原故障登记目录核对后整体归档到 `%LOCALAPPDATA%/DotNetDebuggerMcp/child/control/recovered-3044b121c8234ba58be8a22ae2202b73`，保留原 Faulted 内容和受限 ACL，不伪造 Stopped、不将 secret 复制到公开产物。正式 status 确认无活动实例后，才启动修正安装包的三轮场景；此时开关保持 true，正式验收结束仍须恢复 false。

### 第二次真实启动仍失败；远程连接关联尚未成立

修正安装包首轮实例 `a2fdc3a2be6e49719743fe15b632f6f1`，child ID=4、holder PID=7544、ticks=639303002220250925、WorkerPid=0。`evidence.json` 记录 08:43:54 即报 Faulted，原因仍为“无法读取会话连接状态”；随后 stop 报“请求注销子会话失败”。本轮真实测试 1 项失败，未进入输入/截图/调试业务，不计作完成任何一轮。

用户再次输入密码后报告卡住，并询问是否因为正在远程操作。事件时序：

- 08:42:10：恢复连接的 child3 已收到 Shell 启动通知，之后正常注销。
- 08:43:53：child4 认证成功；08:43:54：child4 断连，产品已经报 Faulted。
- 08:44:26：父 Session1 才从本地断连；08:44:27：父 Session1 重新连接到 `rdp-tcp#0`。

故父会话这次 RDP 重连发生在 child4 故障之后，**不能直接将故障归因于用户使用远程软件**。也不能据此完全排除特定远程环境的影响；尚未做受控单变量对照，不应建议用户停用软件或改认证策略。

本轮再次短暂调试自有 holder，确认 STA 仍阻塞在 `AxHost.DetachAndForward → IOleInPlaceObject.InPlaceDeactivate → Form.Dispose/WmClose`，调试已 disconnect。核对路径、PID、Session1 与创建 ticks 后结束本轮 holder；提权恢复脚本确认开关 false，安装 probe 再确认 Enabled=false，但 child 编号4仍残留，未伪造 Stopped 或声称注销成功。

**纠正诊断边界**：之前得到的 Win32 error=2 是故障发生后的原生查询结果，不是已捕获的首个致错异常码。因此“仅将 error=2 继续等待”的回归通过，不能证明已修复真实首个异常。本轮重现已否定其充分性。现有状态只保存 `ex.Message`，丢失了首个 Win32 码；当前仅补充 Faulted reason 的 Win32/HRESULT 诊断，不继续凭猜测扩大捕获码或要求用户重复登录。

另以只读原生 A/W 对照排查封送：在当时 Active 的父 Session1，`WTSQuerySessionInformationA/W(WTSConnectState)` 均成功返回0；在故障后的 child4，两者均失败并返回 error=2。当前证据不支持把 ANSI/Unicode 入口差异当根因。

## 5. 换设备接续背景（2026-11-15，按维护者要求暂停本机新验收）

### 最新确证：首个异常为 Win32=5，而不是 error=2

诊断安装包位于 `tests/TestData/ChildSessionAcceptance/20261115-085204/`，宿主 SHA256=`FEC8085544B1A23CE18761FBF62C0A34B106BDFEF3E93DAB97B6B3C81CB5BA58`。生产入口 `child_session(action=start)` 返回实例 `d9eded99afa04597a79a795c540f92d6`、child ID=7、holder PID=41076、ticks=639303008762975831、WorkerPid=0，Faulted reason：

```text
无法读取会话连接状态。（Win32=5，HRESULT=0x80004005）
```

这确证触发关闭流程的是 WTSConnectState 查询的 **Access Denied**。官方 [WTSQuerySessionInformationW](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtsquerysessioninformationw) 说明，查询其他用户会话需要 Query Information 权限；但当前尚未证实本例是认证前的权限时序、令牌差异还是远程环境所致，不直接改权限或简单忽略全部错误。

同轮受控恢复的 child6 曾在父 Session1 已为 `rdp-tcp#0` 的条件下正常 Active；通过反射调用**同一诊断安装包中的生产 `WtsNative.Active`** 查询 child6，返回 true。随后用同一生产库的 Logoff 注销 child6、正常关闭恢复 holder，确认 probe 无 child。此正反对照说明远程父会话并非必然失败条件，但没有排除特定远程环境/登录时序的影响。

### 本机停留状态

- 已核对路径、PID、Session 与创建时间后结束本轮 holder41076；没有 worker/业务目标启动，没有进行本轮真实 SendInput。
- 一次性提权恢复脚本及安装 probe 均确认 **Enabled=false**。
- 系统仍返回**残留 child 编号7**，未声称注销成功；原实例 Faulted 登记保留，未伪造 Stopped。这属于旧设备，**新设备不得复用这些 PID、SessionId、实例目录或恢复脚本**。
- 旧设备没有正在运行的正式三轮测试；三轮验收、安装形式互通、真实 UIA/键鼠/PNG/调试闭环、最终独立审阅均未完成。

### 代码与继续位置

- 原仓库分支 `feature/child-session-mcp-cli-integration`，起点 `495738e`，任务1–9已提交到 `32d4187`，**不要重做**。维护者随后明确要求整理提交并推送，以当前分支最新 Git 检查点接续；不新建 worktree、不合并或发布。
- 任务10及启动故障修正在交接时尚未提交，现随维护者要求保存为**未完成验收的换设备检查点**，不标记任务10完成。新设备拉取本分支即可获得源码、独立研究夹具与本文；`.superpowers/` 执行账本和 `tests/TestData/` 原始产物仍忽略，不强行入库。若执行工作区未迁移，依据本文、原计划与 Git 提交历史重建账本，从任务10继续。
- 当前修正包括：后台 holder 不继承调用端 stdio 写端；首次观察到的自有 child 编号提前登记；就绪阶段仅 error=2 继续等待；编号变化拒绝；Faulted reason 保留 Win32/HRESULT。**尚未实现/验证 error=5 的正确处理**。
- 显式验收入口为 `ChildSessionCases*.cs`、`ChildInstalledToolTests.cs`；打包脚本 `tests/Fixtures/ChildSessionTarget/install-acceptance-tool.ps1` 读取项目版本、隔离本地源与 NuGet 缓存。真实集成需 `DOTNETDEBUGGERMCP_CHILD_INTEGRATION=1`，默认 Client 不触碰子会话/系统开关。目标夹具 `ChildSessionTarget` 按 Debug 构建，随后真实测试用 `--no-build`。
- 新设备先做只读 probe 与环境记录，明确采用本机直连还是远程操作，再对比首个查询的错误码及认证前后状态；不要继续把故障后 error=2 当首个根因，不改 Hello/凭据/证书/会话 ACL，不提升整个 worker 来绕过检查。
- 回归证据：最近明确范围122项为119通过、3跳过；默认 Client 全部通过。完整宿主原始基线已有9项失败，另有既知帧几何失败，维护者要求延期且未经许可不得顺手修复。上述绿色均**不代表真实产品验收通过**。
- 原有 research07/08、09/10、`NativeInputSpike`/`SandboxSpike` 及混合修改的 `tests/AGENTS.md` 属独立研究工作。维护者本次要求 Git 清理干净并提交推送，故一并保留入库，不删除或当作生产依赖，也不将它们的结果替代正式安装验收。本功能长期资料及后续结论继续归本文，导航以规划地图为准。

### Git 交接检查点验证

此次保存为“未完成验收的换设备检查点”，不宣称修复 Win32=5，不标记任务10完成，不合并主分支、不发布版本。提交前重新执行：

```powershell
dotnet build src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj -c Release
dotnet build src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj -c Release
dotnet build tests/Fixtures/NativeInputSpike/NativeInputSpike.csproj -c Release
dotnet build tests/Fixtures/SandboxSpike/SandboxProbe/SandboxProbe.csproj -c Release
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -c Release -- --filter-class 'DotNetDebuggerMcp.Tests.Child*' --filter-class 'DotNetDebuggerMcp.Tests.FeatureFlagsTests' --filter-class 'DotNetDebuggerMcp.Tests.ToolFeatureRegistryTests' --filter-class 'DotNetDebuggerMcp.Tests.DotNetDebuggerMcpCmdTests' --filter-class 'DotNetDebuggerMcp.Tests.McpSessionConcurrencyTests' --filter-class 'DotNetDebuggerMcp.Tests.ToolSurfaceContractTests'
dotnet run -c Release --no-build --project src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj
```

四次构建均0警告/0错误；定向测试122项，119通过、3跳过；默认Client全部场景通过、679个反编译文件校验后清理。真实安装/图像项因未启用集成条件明确跳过，先前真实失败证据仍成立；本次没有重新登录、修改系统开关或执行真实输入。完整宿主既有失败与最终独立审阅仍待后续，不能把此检查点当可发布签收。

## 6. 新设备本机直连接续（2026-11-15）

### 环境与授权边界

- Git 检查点 `5f3283f`，分支 `feature/child-session-mcp-cli-integration` 与 origin 一致，接续时工作树干净；从任务10继续，未重做任务1–9。
- 当前设备 `YIN`，CIM 返回 Windows 11 专业版 `10.0.26300`、x64；.NET SDK `10.0.301`，执行进程未提权，父 Session1 为 console Active。维护者明确确认**当前不是远程，是本机执行**。
- 开始仅获准只读探测，随后维护者明确授权开启子会话。本轮仅做一次安装产物真实启动诊断，没有真实输入、业务目标或调试附加。
- 初始生产 probe：`Enabled=true`、`ParentSessionId=1`、`ChildSessionId=null`、`ActiveXAvailable=true`；status 无本产品活动实例，没有匹配的临时计划任务。本轮没有修改开关，不能套用旧设备恢复 false 的约定。

### 安装与单次启动结果

本轮目录 `tests/TestData/ChildSessionAcceptance/20261115-091534/`。先执行 Release 宿主构建，0警告、0错误，再运行正式 `install-acceptance-tool.ps1` 隔离打包/本地安装版本2.5.1。安装宿主 SHA256=`60C9D3B38E9B8B071D87DAAF0F938E0658454794EEE522ADFB7E0F843720C927`，两种准确宿主路径保存在本轮 `installed.json`；未改正式安装或发布。

安装 shim 再次核对 probe/status 后执行 `child call child_session --json '{"action":"start","timeoutSeconds":5}'`。09:16:26 返回实例 `8c7443b370044904bac95a0ca2d3dd32`，AwaitingAuthentication，holder PID=26044、创建 ticks=639303021812738908、worker=0。随后 status 已为 Faulted，登记 child ID=2，reason：

```text
无法读取会话连接状态。（Win32=5，HRESULT=0x80004005）
```

本机直连也复现首个 Win32=5，因此**远程操作不是复现该故障的必要条件**；这并不直接证明认证时序或任何权限设置是根因。没有进入 Ready，也没有完成三轮中的任何一轮。

TerminalServices-LocalSessionManager/Operational 本轮事件包含09:16:42的 Session2 断连，以及从 Terminated 响应 EvDisconnected 时的错误 `0x800708CA`。这只是关闭过程的补充现场，不把这些后续事件当作首个异常。启动、status、probe、事件及清理输出分别保存在本轮目录；这些忽略产物不承诺长期保留。

### 当前清理状态与待查边界

- 首先经安装 shim 正式 stop 本轮实例，返回 `not-executed`、“请求注销子会话失败”，退出1。stop 已写入 Stopping 后注销失败，不能把接口返回的 not-executed 理解为完全没有状态变更。
- 按本轮清单路径、父 Session、PID与创建 ticks 交叉核对后，仅结束自有 holder26044，确认该进程退出；未按名称批量结束其他进程，未注销父会话。
- 09:17:52只读 WTS 查询：父 Session1 的 WTSConnectState=0，child2 查询失败 Win32=2；probe 仍为 `Enabled=true`、`ChildSessionId=2`，CIM 未列出 Session2 进程。**编号2仍残留，尚未确认注销成功**；没有伪造 Stopped、重启机器或自动重连掩盖现场。
- 当前不修改生产代码，不把扩大 error=5 忽略范围当作修复。维护者要求使用网络搜索/GitHub CLI补充查证，并允许子代理并行研究；后续外部依据与实验结论仍归本文，任务10保持未完成。

### 外部资料补证与本轮回归边界

- 通过 `gh api` 直接核对 [BetterGI 固定 commit 的登录完成处理](https://github.com/babalae/better-genshin-impact/blob/cc3099a1a8e6470bdbd649dd232aa06011cd60a0/BetterGenshinImpact/Service/ChildSession/ChildSessionService.cs#L664-L723)：`OnRdpLoginCompleted` 才完成连接等待并投送应用；[同文件822–824行](https://github.com/babalae/better-genshin-impact/blob/cc3099a1a8e6470bdbd649dd232aa06011cd60a0/BetterGenshinImpact/Service/ChildSession/ChildSessionService.cs#L822-L824)明确记录登录错误后 ActiveX 仍可能处于连接状态。官方 [OnLoginComplete](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onlogincomplete) 定义的是成功登录通知。因此 `Connected==1` 本身不足以证明用户登录已完成；这项契约差异成立，但还不能单独证明 Win32=5 的具体成因。
- 子代理对照 [LibreAutomate 的固定 commit](https://github.com/qgindi/LibreAutomate/blob/3d245d85c3c9e070e9fd1e532ee312e43b9e04a7/Au.Editor/xMisc/Pip.cs#L185-L210)：它以 OnConnected 发布连接标志，不同于 BetterGI；[worker 另探测 input desktop](https://github.com/qgindi/LibreAutomate/blob/3d245d85c3c9e070e9fd1e532ee312e43b9e04a7/Au.Editor/xMisc/PipIPC.cs#L114)。不能把两项目都归纳成“等 OnLoginComplete”。LA 窗口代码没有显式 Dispose 也不意味着 WinForms 基类不会释放 AxHost，不能据此推出其关闭不会阻塞。
- `gh issue view` 核对 [BetterGI #3867](https://github.com/babalae/better-genshin-impact/issues/3867)：报告 RDP 失败后 WTSGetChildSessionId 仍有编号、WTSLogoffSession 报2且窗口未关闭。它是相似现场，issue仍Open，评论含明确标记的AI建议，**不将其归因或“错误2即已注销”建议当作已验证结论**。
- 官方 [WTSLogoffSession](https://learn.microsoft.com/en-us/windows/win32/api/wtsapi32/nf-wtsapi32-wtslogoffsession)说明返回成功才表示请求接受，`bWait=false`仍需后续核实；本轮stop返回失败，不能用故障后的查询失败代替“注销成功”证据。
- 本机 RemoteConnectionManager Event1149 于09:16:41记录身份验证成功；当前Faulted快照于09:16:43落盘，LSM于09:16:42断连，未取得本轮Session2登录或Shell启动事件。该时间线区分“身份验证成功”与“用户桌面就绪”，尚缺 ActiveX OnLoginComplete 与首个 WTS 查询的同进程时间线，不能靠秒级事件顺序宣布根因。
- 重新生成夹具后，定向运行 `ChildInstalledToolTests`：2项，1通过、1明确跳过（本次未设置integration，不重启三轮）。通过项仅支持安装shim及`dotnet <dll>`两种形式的只读probe和非法内部角色拒绝，不代表holder/worker成功。`ChildConnectionReadinessTests`、`ChildLifecycleTests`、`ChildForwardingTests`合计11/11、0跳过；这些既有回归并未修复真实Win32=5，也不覆盖本轮新产品修正（本轮尚无生产代码变更）。
- 上游补丁追踪经主会话再次核对：[BetterGI PR #3861](https://github.com/babalae/better-genshin-impact/pull/3861)当前Open、head=`e85c65547d1e9eaec022d3308c672b9cf450b533`，正文明确实际远程切换场景待复测；其新增[只读探针](https://github.com/babalae/better-genshin-impact/blob/e85c65547d1e9eaec022d3308c672b9cf450b533/BetterGenshinImpact/Service/ChildSession/ChildSessionNativeMethods.cs#L157-L193)要求WTS Active且用户名非空，查询失败仅表示未确认，不抛失败。但该兜底**仅用于已有会话重连**，新建仍等原登录事件；不能将它宣传为本例新建时首次Win32=5的已验证修复，也不引入其自动重连行为。
- 子代理核查已合并的[PR #3896](https://github.com/babalae/better-genshin-impact/pull/3896)与[DesktopTwin诊断文档](https://github.com/batvbs/DesktopTwin/blob/acf8071f6353f62f841ffb75e6dd9e1b9950a04b/docs/auto-repair-feasibility.md)：未提供针对本例WTSConnectState首次Win32=5的已验证修复。文档中的服务控制Access Denied不是本例会话查询错误；诊断工具还含Hello/策略写入功能，本轮只读源码，没有安装或运行修复器。
- 两路检索汇合后，主会话直接复核官方[Connected属性](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscax-connected)、[OnConnected事件](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onconnected)及[会话权限](https://learn.microsoft.com/en-us/windows/win32/termserv/terminal-services-permissions)：Connected只描述控件连接，OnLoginComplete才通知成功登录；文档明确用户登录后获得对自身会话的其余权限。公开检索未找到“Child Session登录前查询WTSConnectState必返回5”的直接证据，因此“登录前权限时序”仍是假设，不能写成根因。
- 研究报告中的推断已收紧：缺失Event21/22不能单独证明首个Win32=5的精确阶段，也不能据此证明如果不关窗就一定会成功登录；RDP事件、WTS状态与系统事件不具有本轮已验证的全序关系。OnLoginComplete/WTSActive也不等于Shell或业务worker就绪，最终仍需worker身份握手。未采用同时改变登录门控、error=5重试、失败后关窗三项行为的“单变量”方案；当前缺口仍是同一持有进程中事件与查询的关联取证。

### 单次受控重连：捕获暂态5并确认注销

维护者随后明确授权**仅重连本轮自有残留，记录登录事件和WTS查询，最后注销**；不修改认证/权限策略，不执行真实键鼠。本次是独立、丢弃式恢复/观测夹具，**不是安装产物验收或生产修复**。

探针源码位于本计划忽略工作区的 `connection-probe/`，未改 `src/` 或既有研究夹具；构建0警告、0错误。它沿用相同RDP CLSID和连接参数，用 `ComEventsHelper` 订阅事件、每250ms在同一STA采样父/child的WTS状态。读取失败只记录、不主动销毁连接；这是本次明确选择的观测边界，不能称作“仅加日志、完全保持生产失败路径”。未调用worker投送、任务调度、UIA、截图或输入API。

启动前再次核对原实例SID/父会话、当前child仍为2、原holder与Session2均无进程且child查询为error=2，才允许恢复。观测目录 `tests/TestData/ChildSessionAcceptance/20261115-091534/recovery-observation-093656/`；观测进程PID=47856、创建ticks=639303034259974310、父Session1。RDP事件接收器实测连接成功，9个事件订阅、经典RCW，退出无解绑错误；本轮只用OnLoginComplete确认登录，不以OnConnected或任意OnLogonError事件冒充终态。

同一进程、同一child编号2的关键时间线：

| 本机时间 | 直接观测 |
|---|---|
| 09:37:06.199 | 连接前残留编号2，WTS查询error=2；父Session1查询Active |
| 09:37:20.057 | 收到OnConnected |
| 09:37:20.058–09:37:21.105 | Connected=1、尚未收到OnLoginComplete；6份采样的child查询均为Win32=5，父查询持续Active |
| 09:37:21.104 | 收到OnLogonError(-2) |
| 09:37:21.221 | child查询首次成功返回WTSActive=0；尚未收到OnLoginComplete |
| 09:37:21.310 | 收到OnLoginComplete；之后未采到Win32=5 |
| 09:37:23.463 | 登录完成、同一编号且Active再次核对后，WTSLogoffSession(2,false)返回成功、error=0 |
| 09:37:25.701–25.793 | OnDisconnected(3)，WTS返回无child（false/1168），观察器记录cleaned=true并退出 |

86份采样的父会话查询全部成功且Active。整个过程中未提权、未改权限或认证策略、未改变观察进程身份；这**确证本次Win32=5是同一连接登录过程中的暂态读取失败**，并直接证明Connected=1时尚不能用一次WTS查询失败认定连接不可恢复。它未证明所有设备/阶段的5都可忽略，也未定位Windows内部具体哪项会话权限何时授予。首次WTSActive比OnLoginComplete回调早约89ms，进一步说明二者不存在本轮可假定的严格全序。

官方 [OnLogonError](https://learn.microsoft.com/en-us/windows/win32/termserv/imstscaxevents-onlogonerror)将 `-2` 定义为 `ARBITRATION_CODE_CONTINUE_LOGON`（Winlogon继续登录），并非终止性错误；不能只凭事件名把所有OnLogonError都升级为失败。本轮系统事件同时补齐Session2的登录成功（21）、Shell启动（22）及注销成功（23）。

09:39:30再次通过**安装产物**独立probe确认 `Enabled=true`、`ChildSessionId=null`；CIM确认原holder26044、观察器47856及Session2均无进程。开关保持本机初始true，未注销父会话。原产品登记仍为Stopping并保留用于取证，未伪造Stopped或归档/删除它；因此这里只声称系统child已注销，不声称产品状态收尾完成。

原始证据为本轮目录中的 `timeline.jsonl`、`state-transitions.json`、`query-summary.json`、`terminal-services-events.json`、`verified-product-probe.json`、`verified-summary.json`；timeline SHA256=`87E8F199CE2AF586F76801227C5189CE552803A2B1AA000B28DA77505D337972`。本机 `mstscax.dll` 文件版本 `10.0.26100.9949`，`wtsapi32.dll` 为 `10.0.26100.9549`。未取得可使用的观察器进程退出码，不将其记为exit0；清理结论依据原生注销返回、事件23、独立probe与进程核对。以上仍不代表正式产品三轮、worker业务闭环或任务10完成。

## 7. 生产修复与正式验收接续（2026-11-15，进行中）

维护者要求先完成生产修复和验收、之后再收尾合并；主会话负责计划与验收，实现和审阅可交给子代理。没有把上一节恢复取证当作交付完成。

- 登录门控生产回归：新增“登录完成前保留编号而不查询尚无权限的连接态”，先实测RED（Win32=5），再加入OnLoginComplete门控；连接/生命周期/转发定向12/12、0跳过。登录后仍拒绝访问继续保留原生错误，没有一律吞掉5。
- 新安装目录 `tests/TestData/ChildSessionAcceptance/20261115-094705/`，宿主DLL SHA256=`23567E22C54783F8F24EE1544671D5FB9A54146D09C3E6B1E0FD3C3655396058`。Client Release与业务夹具Debug均构建成功；正式安装用例明确启用integration、使用`--no-build`，没有用研究探针代替生产角色。
- 此包首次真实建立到Ready：实例 `0a162eb00580430ca1b57c3d4d87ea73`，child3、holder51136/ticks639303040601657317、worker54940/ticks639303040716329243。随后首个MCP `child_process`返回“管道客户端身份不符”；CLI使用安装exe、MCP使用`dotnet <同一安装dll>`，在生产`ChildPipeServer.Validate`中被“客户端映像等于创建者映像”的额外条件拒绝。该条件不同于实际peer与信封Image的核实，违反两种安装宿主形式交叉复用的约定。
- 这次安装测试2项：只读项通过，真实项失败，0跳过；未完成第一轮业务。finally经正式CLI stop返回Stopped，确认holder/worker退出；未发生真实业务输入。原始证据在本轮 `client-acceptance.log` 与执行账本 `fixed-acceptance-first.log`。
- 原故障实例 `8c7443b370044904bac95a0ca2d3dd32` 已在无系统child、无原holder且SID/父会话/创建时间核对后，持生命周期锁整体归档至 `%LOCALAPPDATA%/DotNetDebuggerMcp/child/control/recovered-8c7443b370044904bac95a0ca2d3dd32`；原Stopping内容及ACL保留，没有伪造Stopped。归档后正式status确认无本产品活动实例，才允许启动上述新包。

### 定向修正与后续验收准备

- IPC 修正仅去掉“客户端映像必须等于持有器映像”的额外绑定；真实对端与信封身份、SID/Session、凭据、白名单及 worker 固定 holder 检查保留。真实跨进程回归先出现3项失败，修正后13/13、邻接17/17；临时移除信封身份核实会使伪造映像回归失败，已恢复原核实。独立定向审阅通过；仍不等于最终安装业务闭环成功。
- 登录首版审阅发现事件订阅失败可能被 AxHost 吞掉、生产接线回归不足、fail-open 默认参数、一次 STA 超时误判故障及旧登录状态复用。第1轮修正将订阅失败反馈到既有故障原因、由生产单轮决策消费同一份连接/登录快照、移除登录参数默认值，并在读取超时后保持未确认、复用未完成读取任务，sink 生命周期复位状态。回归先6项中2项失败，修正后连接6/6、生命周期4/4、转发5/5，构建0警告0错误。真实 COM 订阅失败和断连事件尚无本轮独立实机验证，定向复审及最终安装复验待完成。
- 当前发布/配置入口与后续 DLL 范围裁决归[正式设计§13](../specs/2026-11-14-child-session-mcp-cli-design.md#13-2026-11-15-范围收敛只验收已确认的-exe-入口)，不把维护者询问必要性写成要求删除。后续三轮以正常安装 exe 为业务入口，不再将 DLL 启动差异描述成 MCP 与 CLI 的架构区别。
- 新一轮验收前只读复核：`Enabled=true`、`ChildSessionId=null`、无本产品活动实例；没有为准备回归修改系统开关。父侧隔离观察和 agent 就绪说明同步仍在实现，尚未打包最终安装产物，也未宣称任何一轮完整通过。

后续关窗回归确定性复现了两种旧快照误判（已关窗仍查询/投递、未确认快照下关窗仍保持等待），最小修正后连接10/10、生命周期4/4、转发5/5；关窗现在先于旧连接快照，监视循环不继续等待已关闭窗口的未完成读取。此项仍须定向复核，不把单测状态冒充真实断连证据。

主会话重新生成通用夹具，宿主测试及 Client Release 构建0警告0错误；默认 Client 实跑全部场景通过（含679个反编译写盘文件校验、finally清理）。完整宿主套件已在未启用真实 child 集成条件下开始运行，尚无最终摘要；后续真实 child 不与该套件并发，避免父侧 GUI 回归污染隔离观察。

验收观察器的首版会在 `confirmed=false` 时仅打印警告、仍可能输出轮次 PASS；独立审阅后已收紧为：先停止采样并保留完整证据，业务成功但父侧隔离未确认则该轮失败，不打印轮次/三轮成功；业务原本失败则保留原异常，仍走正式 stop 清理。初始 holder 前台/最小化与采样无前台同样计入未确认。此修正仅有 Client Release 编译与静态路径证据，没有在本轮独立执行负向真实输入，定向复审待完成。隔离证据明确为50ms采样点观测，不能排除采样间瞬态变化，不将采样未见变化表述成绝对无扰动。

### 冻结构建与完整回归

- 登录关窗修正与隔离失败门槛均经定向复审，规格/质量通过，无阻塞。尚无真实 COM 订阅失败、负向隔离失败的独立实机证据。
- 完整宿主套件以 Release `--no-build` 正常运行9分08秒并产出最终摘要：890项，878通过、9失败、3跳过，退出2。跳过为无安装清单的只读安装项、未启用integration的安装实机项、未提供活动实例的真实PNG项；没有伪称全绿。
- 失败中7项 `DebugUiToolsTests` 与 `Window_ByMultiWindowPid_SelectsRealWindow_NotTinyHelper` 和§2基线重合。`Screen_ReturnsPngImageBlock_WithTargetLine` 本次新增失败（图片块为空），原先的 `Element_ByName_ReturnsPngImageBlock_WithFrameId` 本次未失败，不能因为失败总数相同就声称清单相同。
- 全屏失败只读诊断：本机该时段存在两张3440×1440全屏PNG，分别3,784,889 / 3,737,370字节，按现有base64阈值应走自动落盘而不附图片块；阈值/分支与基线代码等价。日志未包含该用例返回正文，自动落盘的响应原文直证尚缺；未改测试断言或宣称已解决。
- 新隔离安装目录 `tests/TestData/ChildSessionAcceptance/20261115-132043/`，宿主DLL SHA256=`6659CE2B2CE7EF743754B497311B14A06C82285A950A756228F82087E0BD82ED`。冻结后明确范围回归134项，132通过、2真实项跳过；覆盖child、握手、能力族、工具面、stdio并发及安装只读探测。Debug业务目标DLL与`obj/Debug`哈希一致：`D088870DDC8A6009884A2F0F7BEFC1F76EEE91C90489FF67392A6CC6801F4A7C`；Client Release SHA256=`54234781C932DABC6EC1CA2BB0EB6A980FF535ACB98204BC436B9B5D84609479`。清单与核对原文为该目录 `installed.json` / `frozen-build.json`。

### 安装首轮：Ready后被验收客户端日志共享冲突打断

实例 `e28b522395a9498f9118796a0fc4695d`，child4、holder24968/ticks639303169218358131、worker18052/ticks639303169702653725，正式入口已到Ready。MCP `child_process` 成功启动GUI进程60760/ticks639303169717531685，证明本次exe入口的IPC已越过原误拒点。

随后Client的 `WaitEventAsync` 在读取 `events.jsonl` 时抛共享冲突 `IOException`（`File.ReadAllTextAsync`），没有进入UIA/真实键鼠/图片/调试验证。安装测试2项中只读通过、真实失败、0跳过，完整三轮仍为0轮；不将GUI进程启动等同业务通过。原始现场在安装目录 `client-acceptance.log` 和 `cross-d500d504f3c444d1889e5d5de6217e97/round-1/`。

finally正式stop返回Stopped。13:23:13主会话独立核对：probe `Enabled=true/ChildSessionId=null`、status无本产品活动实例；holder/worker/GUI及Session4剩余进程数0。未改变本机初始开关。日志读取缺陷由Client实现者继续核实读写共享模式并做真实文件边界回归；维护者新提出“连接窗最小化时自动还原且不抢父侧焦点”，另行核实API，不能把现有验收前置检查称为已经具备生产自动恢复。

日志读取已在真实文件边界复现同一共享冲突：写端持有写访问并允许读，新读端却未允许既有写句柄。改为共享读写并仅消费最后一个换行之前的完整UTF-8行；主会话复跑同一实际reader源码的7项磁盘断言通过（持有中可读、半行等待、补齐/追加后可读、不截断、UTF-8尾部等待、无换行不产出事件）。Client新SHA256=`9CD5FE79D7F37F29E4F68C2553725B917406FA0638B2BF2778FD87BFA33B5D03`；临时验证程序不在CI，定向审阅及真实业务复跑仍待完成。

连接窗最小化的历史实测依据归 [research/08 §6.4](08-rdp-child-session-spike.md#64-外层客户端状态与-child-内目标状态)，不是本轮新增推测；该旧PoC证据不外推为当前正式实现全部能力均会失效。

13:30:08主会话通过当前已连接的开发MCP（不是132043安装实例）直接调用 `screenshot(mode=screen)`，未给filePath，实际只返回“目标: 屏幕 / 3440×1440 / BitBlt / 已落盘”文本，文件为 `%TEMP%/DotNetDebuggerMcp/screenshots/screenshot-20261115-133008117-screen.png`，2,082,875字节、SHA256=`1A423BCF88AFD0C8DF1FCC52795DAFECD89FC5C8B92CD620D4D9BCC9DE387071`。这直接验证了本机当前桌面内容会触发大图自动落盘，支持全屏用例“必须有内联图片块”的前提不总成立；没有重跑原失败用例，也不将其标为通过或修改断言。

### 按需恢复连接窗的新增范围

维护者确认[设计§14](../specs/2026-11-14-child-session-mcp-cli-design.md#14-连接窗最小化时自动不激活还原2026-11-15-已确认)，实施计划补Task10A；此时仅完成API与参考实现核对，尚未实现或实测。参考WPF源码在最小化→Normal且ShowActivated=false时使用 `SW_SHOWNOACTIVATE=4`；本轮不采用会激活的WinForms `WindowState=Normal`/`SW_RESTORE`，原生返回值也不作为恢复成功的证明。正式结果仍需确认 `IsIconic`、真实GUI业务和父侧前台/光标；不把文档语义替代为本机ActiveX恢复成功。

首版实现经真实Store/IPC的恢复门回归RED8项中2失败→GREEN相关40/40，其余Child65通过/1真实图像跳过；主会话另跑最新明确范围137项，135通过/2真实项跳过，默认Client全部通过且清理679个文件。曾隔离安装到 `20261115-183811`（宿主SHA256=`EC320BEFF73708BBC3A67755CEA90C4BFE0E855B4E90C78C6DF236F4175584EB`），尚未执行该包真实child恢复验收。

独立审阅建议收口marshal回调异常，主会话进一步确认首版存在确定的迟到回调生命周期缺陷：回调执行时才读取`budget.Token`，如果等待已超时并释放CTS，则Token getter在进入恢复函数之前即抛`ObjectDisposedException`，可能逸出到WinForms默认异常处理。主会话在本机显式调用已释放CTS的`get_Token()`复现该异常；提前保存的`CancellationToken`值在源取消/释放后仍能读取取消状态。PowerShell属性适配器可能吞getter异常，不能把`$cts.Token`未直接报错当反证。此项正在原实现者修正，未以“极窄原生调用竞态”掩盖该确定错误；183811包不再作为最终验收产物。

### 恢复修正包185505：实机输入已达，PNG客户端读取尚未通过

迟到回调修正完成并独立复审通过：保存排队前Token值，marshal异常回传任务/中文未执行，预算单源；确定性RED13项中4失败，GREEN相关45/45。主会话冻结后回归142项，140通过、2真实项跳过；默认Client全通过、679个文件校验后清理。

新隔离包 `tests/TestData/ChildSessionAcceptance/20261115-185505/`，宿主SHA256=`4E2DF79EE87159D0A1BB0B4351678726D1CBA34A33C6F3AE51A16895892EA7A3`；Client=`A4B327D134821450EDE6416E5BF89EE254281773482027BF9024592DD3B5821C`；Debug业务目标与`obj/Debug`均=`D99C4FD09B377D4E37F3A7CFB722D447EFA883C7D7CFAA55A581E476F5D48FA3`。

- 首次实例 `58fa969165034462ab339e6f7485b6b3` / child5：最小化后经正式GUI查询恢复、UIA命中、父前台不变，但父光标 `(817,1074)→(832,1065)`，验收明确失败。只有前后快照，不能确认变化由还原/ActiveX/外部输入中哪一个造成，不将其归因为用户。正式stop及独立核对均无残留；保留 `client-acceptance-restore-cursor-change.log`。
- 同包、无代码改动复验实例 `c49140686b6746febd56e773e521a59c` / child6：最小化后恢复成功且前后父侧光标/前台相同；GUI进程63580真实处理UIA写读、`中文😀原生输入`、commit、Ctrl+K、doubleclick、拖拽140px、滚轮120。正常执行区间344个50ms父侧样本，光标/前台均0变化、0读取失败、holder未成为父前台/未最小化，`confirmed=true`。这是本轮分项证据，不扩写为采样间瞬态绝对不存在。
- GUI截图 `cross-be237a122e214eaeab039890c38e5922/round-1/gui.png` 可解码，主会话实际读图看到中文输入、Ctrl+K状态与拖拽轨迹；不是只核对文件存在。
- 随后Client在标记图 `new Bitmap(stream)` 处失败：`lime.png` 实为75,848字节Base64文本，头部 `iVBORw0KGgo...` 而非PNG二进制，说明 `ImageContentBlock.Data.ToArray()` 的消费口径不正确。另见marker `ui_wait`文本为语义超时，不能仅凭reply completed视作命中；其clientArea路径也不抗遮挡。交Client子项核对SDK契约与实际就绪/捕获方式，不改生产截图或去掉像素断言。
- 两次真实安装测试均为只读项通过、实机项失败，完整三轮仍0通过。child6正式stop到Stopped；独立probe无child、status无活动实例，holder62376/worker54968/GUI63580/marker57692与Session6进程数0；开关保持true。PNG失败原日志独立保留为 `client-acceptance-png-failure.log`。

### Client协议修正后：图片已通过，停点断言词汇需同步

本地反编译SDK `ModelContextProtocol.Core 2.2.0` 证实：`ImageContentBlock.Data` 是Base64 UTF-8字节，`DecodedData` 才是原始图片字节；`FromBytes`接收原始字节，故宿主路径无需改变。Client改用DecodedData，并实际解码后校验；主会话独立复跑同一实际解码源码的SDK JSON协议往返7项通过。marker无后代控件，旧`ui_wait(text=标题)`条件无法命中，改用截图工具有界等待与实际像素判据；整窗捕获优先WGC/PrintWindow，BitBlt回退仍受遮挡影响，以真实Lime像素判准，不降低像素要求。

Client新哈希=`08E0699F3080B7D68D7A6F43CEC1FCAB07863949FE667BC5628DEDB2EC2E90D0`，仍使用185505宿主包及同一Debug目标。实例 `031cac1220174422a08ac205b58765fe` / child7，恢复前后父侧光标与前台相同，随后真实输入再次通过，Lime图片402×312、中心ARGB=-16711936；主会话实际读图看到绿色标记窗，GUI截图也可解码。

调试已真实完成attach→设置`OnCommit`断点（token `0x0600008b`）→continue→child内真实点击→命中BreakpointHit，线程51716，`debug_wait`带当前OnCommit源码。Client却期待英文`Stopped`，实际工具返回中文“已停下”加`BreakpointHit`，所以验收错误地在该断言停止；这是测试词汇与现有工具输出不一致，不是“未命中断点”。仍须修正断言后继续核验栈帧、附加时stop保护及后续cancel/断连/worker退出；不据此签收完整调试闭环或三轮。

本次正常执行区间199个50ms父侧样本，光标/前台0变化、0读取失败、confirmed=true。finally已disconnect，正式stop到Stopped；19:07:55独立probe无child/status无活动实例，holder45128/worker32148/GUI38632及Session7进程数0。现场 `client-acceptance-debug-wording.log` 与 `cross-4fdcb17e14cc4e4f99253391ed214e56/` 保留。

## 8. 最终安装产物三轮业务验收通过（2026-11-15）

使用§7所记185505同一安装宿主与Debug业务目标；Client仅将旧停点预期改为真实的`BreakpointHit`，实际验收Client SHA256=`9823C5CE22E7E4D3169EF4C675662E9F455410893DD107E05253974062ACBDF8`。正式 `ChildInstalledToolTests` 2/2通过、0跳过、约1分53秒，未用开发bin/研究探针代替安装exe，MCP/CLI三轮都从同一安装exe进入。

原始证据：`tests/TestData/ChildSessionAcceptance/20261115-185505/cross-71266593eae0483782c4974982872cf6/` 的 `evidence.json`、各轮 `events.jsonl`、`gui.png`、`lime.png`；安装目录 `client-acceptance.log`、`controller-verification.json` 与 `controller-png-event-check.json`。原始产物仍在忽略目录，以下事实记录为长期保留，不承诺原始文件永久存在。

| 轮次 | 实例 / child | holder / worker / GUI PID | 父侧50ms样本 | 专项与清理 |
|---|---|---|---|---|
| 1 | `56fcc3e509de4e1c90ed404629356bb8` / 8 | 7988 / 63248 / 54176 | 188 | 连接窗故意最小化→正式ui_find触发不激活还原，前后光标/前台一致；随后cancel与stop完成 |
| 2 | `40695d132b6f4efca4e7669dc8530b02` / 9 | 8276 / 51012 / 53928 | 180 | 真实断连后状态Disconnected，仍可list/output/cancel，GUI明确拒绝；最后stop完成 |
| 3 | `02d92a4331d84f0b9d6f65e35c97f2fb` / 10 | 61968 / 17388 / 65488 | 186 | 核实身份后结束worker，新请求not-executed而非伪报完成；最后stop完成 |

每轮均实际经过：

- UIA写读、真实Unicode“中文😀原生输入”、提交、Ctrl+K、点击/双击、140px拖拽与120滚轮，目标消息泵事件齐全。
- GUI PNG可解码702×532；MCP图片实际解码402×312，中心ARGB=-16711936（Lime）。主会话另独立读取三轮落盘PNG与事件文件复核，三轮均符合，不只采信Client PASS。
- 父侧debug_attach→OnCommit断点→continue→child内真实点击→BreakpointHit→debug_frame含OnCommit栈与字段；附加期间stop保护触发；finally disconnect，目标继续运行。
- 任务output/cancel后running=false；正式stop到Stopped且holder/worker退出。

三轮正常执行区间的父侧采样均为：0光标变化、0前台变化、0读取失败、连接窗未成为父前台且未最小化，3/3 `confirmed=true`。恢复专项在正常采样之前单列，只证明前后状态与业务可用；50ms正常采样也不排除采样间瞬态，不扩写成任何设备/应用/长期并行都绝对无扰动。

19:13:11主会话独立核对三轮holder/worker PID与child8/9/10均无剩余进程；安装exe probe为`Enabled=true/ChildSessionId=null`，status无本产品活动实例。保留本机初始开关true，未改认证、权限或系统策略，未注销父会话。

验收后仅做无业务行为变化的收尾：为Windows图片解码小类补平台标注、限定BitBlt回退的遮挡敏感性、失败提示不作未经证实的因果判断；Client重建0警告0错误，SHA256=`28C0F0BCC41C638B86CD6C19E65215738B919F1724F8969EF8CFE33F0822F74C`。三轮原始证据仍归上述9823C5CE构建，不伪称在新哈希上重跑过三轮。生产宿主包未变。

最新生产代码完整宿主套件已正常结束：898项，886通过、9失败、3跳过，9分26秒，退出2。9项失败与本机前一轮890项运行逐项相同（7项DebugUiTools前台不变断言、Screen内联图片块前提、Window_ByMultiWindowPid选窗），没有把相同失败总数当作清单一致的替代证据；3跳过仍为未提供安装清单/真实集成/活动实例的明确条件。已有失败不通过放宽断言或跳过来“修绿”，不能称全套全绿。

维护者随后发现child登录重复启动后台软件，要求先定位启动来源、最小PoC后再接入；合并因此暂停，新增问题归[启动项隔离研究](12-child-session-startup-isolation.md)。全分支独立审阅只评估既有冻结实现，本文不声明已合并或发布。

### 独立整分支审阅返回（未开始处理新增发现）

审阅结论为“有条件通过、未发现Critical”，不等于可立即合并。报告提出两项需要后续核实并裁决的Important：holder失效后正式stop可能无法恢复清理；标准用户以另一管理员账户提权enable/disable时，生命周期目录ACL可能与原账户不一致。两项均未在最终安装包上完成专项实机验证，本次启动隔离研究不顺手修改生产，也不未经核实直接采用审阅修复建议。

另待收尾核对child路径的AgentActionLog说明、CLI帮助的child入口及实际验收Client的构建记录归属。最终审阅原文在忽略工作区`whole-branch-review.md`；其中“research12未跟踪”是提交完整性提醒，不是当前文件不存在。保持本研究与文档地图同次提交，但当前尚未提交、合并或发布。
