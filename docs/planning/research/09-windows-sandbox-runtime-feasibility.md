<!-- 职责：记录 Windows Sandbox 自动化隔离的官方能力核查、本地架构复用边界与 P0 前置探测证据；不写：已批准的产品设计、正式实施计划或未经实测的通过结论。 -->

# Windows Sandbox Runtime 可行性核查

核查日期：2026-11-14。背景：用户提供《Windows Sandbox 隔离自动化与持久化执行环境：项目交接 / 技术方案 v2》。

**状态更新：维护者随后启用 Sandbox 并重启，已进入独立 P0 spike；最新实测及未通过项见 [P0 实测记录](10-windows-sandbox-spike-results.md)。本文件保留最初能力核查与启用前证据，不代表 P0 已通过。**

## 1. 本轮需求边界

本轮交接要求以 Windows Sandbox 隔离真实桌面输入，保持同一运行实例的文件、GUI 进程及任务状态；Agent 脚本与后续 MCP 入口共用一个独立 Runtime。解释器变量持久化、多实例、跨虚拟化边界调试及正式 MCP 改造不属于 P0。

此前研究见 [桌面隔离全景](07-desktop-isolation-options.md) 和 [Child Session 实测](08-rdp-child-session-spike.md)。其中的选型与裁定属于当时背景，不代表本轮 Sandbox 已验证，也不把 Child Session 的观测外推为 Sandbox 的保证。

## 2. 官方能力核查

以下为资料事实，非本机实测。来源访问于 2026-11-14：

| 来源 | 核实内容 |
|---|---|
| [Windows Sandbox 概览](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/) | 独立内核、虚拟化隔离；关闭后丢弃内部状态；宿主软件不自动出现在 Sandbox；当前不支持同时运行多个实例；Home 版不支持 |
| [安装要求](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-install) | 支持的硬件/虚拟化前提；可选功能名 `Containers-DisposableClientVM`；启用需要管理员，可能需要重启；24H2 起不能依赖内置记事本、计算器、Terminal 等商店应用 |
| [`.wsb` 配置](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file) | 可配置映射目录、登录命令、网络、剪贴板、设备重定向、内存；目录映射发生在登录命令前；网络、剪贴板、音频输入默认启用；映射写入可持久影响宿主 |
| [`wsb` CLI](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-cli) | 文档注明 Windows 11 24H2 起提供 `start/list/exec/stop/share/connect/ip`，`--raw` 输出 JSON；`exec` 返回退出码但**不支持进程 I/O 捕获**；`ExistingLogin` 依赖活动用户会话，文档要求先 `connect` |

### 2.1 对交接稿的技术修正

- “没有通用的现成执行 API”不能理解成完全没有官方命令执行入口：已经存在 `wsb exec`；但它不等于所需的日志捕获、取消、任务管理和恢复 Runtime。
- `wsb` 文档存在不应照抄的配置例子：其中网络值写成 `Disabled`，而 `.wsb` 配置参考的合法值为 `Disable`。配置以配置参考为依据，并在安装后的实际版本上验证。
- `.wsb` 配置参考明确说明目前不支持相对映射路径，尽管 XML 占位示例措辞不一致。Host 应在本机生成绝对路径，不能在仓库中写死某台设备的路径。
- 官方概览当前明确限制同时运行一个实例。未来“多 Sandbox”不能仅视作增加调度器，需要另行核实平台能力。
- “持久化”在本轮只指 **Sandbox 保持打开期间跨命令保留状态**，不保证关闭后保留应用环境；宿主映射目录是例外。
- 官方 CLI 能力须以本机可执行文件及 `--help` 再核实，不能仅凭操作系统版本假定可用。
- 上述文档没有给出失焦、遮挡或最小化时 `SendInput` 与截图均可靠的保证。这些仍必须实测。

## 3. 本机只读探测证据（启用前）

| 项目 | 观测 |
|---|---|
| 操作系统 | `Win32_OperatingSystem.Caption` = Microsoft Windows 11 专业版；版本 `10.0.26300`；64 位 |
| 虚拟化 | `Win32_ComputerSystem.HypervisorPresent = true`；不单独证明 Sandbox 功能就绪 |
| 硬件概况 | 16 逻辑处理器；约 31.8 GiB 物理内存 |
| SDK | `dotnet --version` = `10.0.403` |
| 权限 | 当前进程非管理员 |
| PATH | `Get-Command WindowsSandbox.exe,wsb.exe` 未返回命令 |
| 标准路径 | `%WINDIR%\System32\WindowsSandbox.exe`、`%WINDIR%\System32\wsb.exe`、`%LOCALAPPDATA%\Microsoft\WindowsApps\wsb.exe` 均不存在 |
| 当前用户包 | `Get-AppxPackage *WindowsSandbox*` 无结果；不代表所有用户或系统组件安装状态 |
| 当前进程 | `Get-Process *Sandbox*` 无匹配结果 |
| 可选功能 | `Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM` 返回“请求的操作需要提升”，**未取得 Enabled/Disabled 状态** |

**当时结论：本机 Sandbox 可用性未确认，P0 实机验证受前置环境阻塞；不能据此断言硬件不支持，也不能宣称功能已禁用。** 该次探测未提权、启用功能、启动 Sandbox 或重启。

维护者可在管理员 PowerShell 查询状态：

```powershell
Get-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM
```

若确需安装，由维护者确认后按官方安装页执行并处理重启提示：

```powershell
Enable-WindowsOptionalFeature -Online -FeatureName Containers-DisposableClientVM -All
```

## 4. 本地代码复用边界

核查根、Session、Engine、MCP Host、tests 的 `AGENTS.md`，以及下列当前源码。此表记录本次核查快照，不替代各项目开发指南。

| 实现入口 | 当前事实及影响 |
|---|---|
| `src/SharpSight.UiAutomation/SharpSight.UiAutomation.csproj`、`UiAutomationService.cs` | net10.0 Windows；依赖 FlaUI 5.0；门面是 `internal sealed`、私有构造及进程内单例，仅 IVT 给 MCP Host 与宿主测试。新增 Runner **不能仅加 ProjectReference 就直接调用门面** |
| `src/SharpSight.UiAutomation/UiPatternDispatcher.cs` | 真实点击为显式 `clickPoint` 路径，先激活目标窗口再 `element.Click(moveMouse: false)`；复用时必须在 guest 内调用，不能宿主先定位再直接点击；不改变既有语义 |
| `src/SharpSight.UiAutomation/FrameRegistry.cs` | 帧号只在当前进程内递增，没有跨实例身份；不能把宿主与 guest 的相同数字视为同一帧，也不能把重启前后引用混用 |
| `src/SharpSight.Capture/SharpSight.Capture.csproj`、`ScreenCapture.cs` | 无宿主依赖，可供 guest 引用；全屏/区域走 BitBlt，整窗走 WGC → PrintWindow → BitBlt；客户区依赖可见屏幕内容；需在 guest 内截图并保留实际来源与像素原点 |
| `src/DotNetDebugger.Session/DebugSessionManager.cs` | 管理 Engine 调试会话，不是通用 Sandbox 生命周期管理器；不能因名称相近就把任务队列放入该层 |
| `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs` | 薄包装使用进程内 UIA 单例，并向调试轨迹日志写结果；Runner 不应反向依赖该宿主 |
| `src/DotNetDebuggerMcp/Tools/Screenshot/ScreenshotTool.cs` | MCP 图片/文本返回、路径与调试目标兜底属于宿主适配；不宜整块搬进 Runner |
| `src/DotNetDebuggerMcp/Configuration/FeatureFlags.cs`、`HandshakeText.cs` | 当前四族 decompile/debug/ui/screenshot；P0 不新增工具、能力族或路由，后续集成必须同步工具说明与握手 |

复用 UIA 的候选是为 PoC Runner 显式增加有限 IVT，或设计小型公开门面；两者都需要单独批准。直接链接复制源码、反射绕过可见性、引用整个 MCP Host 均不作为推荐路径。P0 未通过前不提前做公开 API 重构。

## 5. P0 技术路径候选（未批准的建议）

1. **推荐：`.wsb` + LogonCommand 常驻 Runner + 映射目录。** 与交接主线一致，可离线验证，并将真实输入限定在 guest；代价是自行验证文件通道与生命周期识别。
2. **备选：官方 `wsb` 管理启动/连接 + 同一个 Runner 与文件通道。** 可减少生命周期控制代码，但依赖本机组件版本，且不消除日志/任务协议需要；不额外维护第二套任务状态。

TCP/HTTP 仅在文件通道测出无法接受的问题后再评估，不作为初始并行实现。

文件通道需验证的最小边界：

- guest 只读的 Runner、应用/脚本输入、命令投递目录，与 guest 可写的状态/日志/产物目录分开；仅映射专用暂存目录，不默认暴露源码树。
- 命令由宿主临时文件写完后同目录重命名提交；guest 将结果临时文件写完后同目录重命名发布。映射层能否提供所需可见性与原子语义必须实测。
- 只读命令目录不能让 guest 移动或删除命令领取任务；领取/终态写到独立输出目录，串行消费由唯一 Runner 保证。
- 每条消息关联 Sandbox 代际、Runner 启动标识和 taskId；心跳与实际握手决定 Ready，不用窗口进程存在代替。
- 任意脚本有副作用，文件重命名不能保证崩溃后的 exactly-once。执行后、结果发布前断连必须可报告结果不确定，不自动重放输入动作。
- GUI 动作串行；常驻 GUI 与执行脚本是不同生命周期。同步等待的结束不等于 GUI 退出，CLI 退出不应停止 Runner。
- stdout/stderr 持续排空，限制保留量并报告截断；任务取消/超时与“客户端不再等待”必须区分，不能因 MCP 请求取消而悄悄销毁环境。
- 对输出 JSON、产物路径、大小均做不可信输入校验；拒绝目录逃逸及链接穿越，不执行 guest 回传的宿主命令。guest 拥有专用输出目录写权不等于安全可信。

## 6. 后续验证证据要求（全部待测）

- 独立自包含测试 GUI：文本、点击计数、键盘、焦点、拖拽与结构化结果；不用记事本作唯一目标。
- 多次独立 CLI 进程连接同一运行环境，前一命令启动的 GUI 在后续命令仍存在；PowerShell 变量不承诺继承。
- 分别记录 Sandbox 客户端前台、失焦、宿主遮挡、最小化；不要混淆“外层 Sandbox 客户端最小化”与“guest 被测应用最小化”。
- 在 guest 内比较 UIA、真实点击、键盘、拖拽及截图结果；截图不仅验证非黑，还验证内容随状态更新，避免把旧帧当成功。
- 宿主持续人工移动鼠标、键入测试文本时观测隔离；不能要求宿主光标坐标始终不变，应确认没有 guest 注入造成的跳转、误输入或焦点劫持。测试日志避免收集真实敏感输入。
- 记录 guest 分辨率、DPI、截图物理像素原点与转换；不通过宿主 Sandbox 外框位置推算 guest 坐标。
- 冷启动、Runner 就绪、命令往返、截图传输与连续命令分别计时；说明内存指标是宿主虚拟化资源还是 guest 进程工作集，记录关闭后资源是否回落，不声称固定节省比例。
- 启动失败、脚本失败、输出过大、取消、超时、Runner 退出和 Sandbox 意外关闭均需可观测；所有失败路径禁止回退宿主输入。

本轮核查时没有上述项目的通过证据；后续进展仅登记于 [P0 实测记录](10-windows-sandbox-spike-results.md)。正式 Runtime 或 MCP 集成仍以 P0 验收为前置条件。

## 7. 补充核查：应用环境与宿主硬件依赖

2026-11-14，维护者进一步提出 .NET 10/11 与宿主硬件、驱动依赖问题。以下是官方资料边界与部署方式，不代表已经实现环境自动准备或验证任意应用兼容。

### 7.1 软件环境不会继承

- Sandbox 是干净的独立 Windows 环境，不继承宿主安装的 .NET Runtime/SDK、应用、服务或第三方驱动。不能把某个 .NET 版本当作预装前提；版本与架构须在 guest 内检查。
- 自包含发布携带应用所需的 .NET Runtime，无须 guest 预装对应共享运行时；但不等于携带 SDK、任意本机组件、系统服务或内核驱动。适用操作系统、架构及其他原生依赖仍须满足。
- 框架依赖发布需要在 guest 准备匹配的运行时：WinForms/WPF 需要 Windows Desktop Runtime，ASP.NET Core 需要相应共享框架；在 guest 编译则需要 SDK。不同主版本不可假定互相替代。
- 安装包或便携依赖可由宿主专用暂存目录投送，guest 启动脚本完成安装/配置；禁网模式不能依赖启动时在线下载。新建 Sandbox 后需重新准备，同一存活实例内可复用已准备环境。
- P0 自包含探针的通过只证明该探针的依赖已满足，不证明普通 .NET 应用直接复制进去就能运行。Runner 的依赖与被测应用的依赖是两个独立问题。

来源：[Sandbox 概览](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/)、[.NET 发布方式](https://learn.microsoft.com/en-us/dotnet/core/deploying/)、[`.wsb` 的 MappedFolders/LogonCommand](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)。

### 7.2 硬件访问不是复制驱动即可解决

| 依赖 | 官方能力边界 / 本项目尚未验证部分 |
|---|---|
| 普通 GPU 图形渲染 | `.wsb` 可启用 vGPU；禁用时使用 WARP 软件渲染。vGPU 不等于完整物理 GPU 直通，不据此承诺 CUDA、厂商 SDK 或特定驱动接口可用 |
| 麦克风、摄像头、打印机 | 有特定输入/打印重定向开关；重定向不等于访问完整原始设备或继承宿主驱动接口，需逐应用验证 |
| USB 加密狗、串口设备、采集卡、PCIe 板卡、专用内核驱动 | 官方 `.wsb` 配置未提供通用 USB/COM/PCIe 直通选项；不能承诺宿主可用即可在 Sandbox 使用。guest 安装驱动也不会凭空获得宿主物理设备 |
| 网络设备/服务 | 启用网络并满足路由、防火墙、协议及授权条件后可单独验证；网络可达不是硬件直通，也会扩大隔离边界 |

来源：[`.wsb` 配置参考](https://learn.microsoft.com/en-us/windows/security/application-security/application-isolation/windows-sandbox/windows-sandbox-configure-using-wsb-file)。本轮未做上述硬件实机测试；不能从配置文档未列通用直通进一步推导“任何间接方式都绝不可能”。

**适用性结论**：Sandbox 适合依赖可部署软件环境的桌面程序，不是宿主环境的透明替身。依赖真实专用硬件的程序须先确认具体设备/API；可能需要支持相应设备转发的完整 VM、专用测试机，或另行设计受限的宿主硬件服务。上述均非已批准的后端扩展，也不能在 Sandbox 失败时自动回退宿主执行真实输入。
