<!-- 职责：记录子会话 MCP/CLI 正式接入期间的基线、验证证据和未完成项，面向维护者；不写：功能设计、实施步骤或根因未验证的修复方案。 -->

# 子会话 MCP / CLI 接入验证记录

日期：2026-11-14。**状态：实施中，尚未完成正式接入，不能当作产品验收通过。**

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
