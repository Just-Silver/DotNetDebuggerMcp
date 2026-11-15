<!-- 职责：把已批准的子会话 MCP/CLI 接入设计拆成可执行、可验证的开发任务，面向实施者；不写：重新选型、历史实测全文或已发布功能使用指南。 -->

# 子会话 MCP / CLI 接入实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将已选定的 Windows 子会话正式接入现有 MCP 和普通 CLI，在不抢宿主输入的前提下运行程序/脚本、UIA、真实键鼠和截图。

**Architecture:** 父侧继续承载现有 MCP/CLI，独立 STA 持有器维护 RDP 连接并协调私有管道，child worker 执行 GUI 和进程任务。新增零宿主依赖的 `SharpSight.ChildSession`；宿主把既有 GUI 执行从工具包装中提取后复用，原默认路径不变。所有外部入口共享执行用例，不经第二套 MCP 服务。

**Tech Stack:** C# / .NET 10，`net10.0-windows10.0.22621.0`；WinForms RDP ActiveX、WTS、Task Scheduler COM、Windows Job Object、命名管道、System.Text.Json；既有 FlaUI 5.0.0、SharpSight.Capture、MCP 2.2.0、McMaster 5.1.0；xunit.v3 4.0.0 + MTP。

**Spec:** [子会话 MCP/CLI 正式接入设计](../specs/2026-11-14-child-session-mcp-cli-design.md)。编制时维护者已批准正文、计划尚待执行；当前执行进展和换设备接续以[验证记录](../research/11-child-session-integration-results.md#5-换设备接续背景2026-11-15按维护者要求暂停本机新验收)为准，下面保留原实施步骤。

## Global Constraints

- “客户端保持连接、非最小化且失焦即可；不要求隐藏、最小化、断开或锁屏后仍可工作。”
- “现有入口与默认行为保持兼容，调用方显式选择子会话。失败不得回退主会话输入。”
- “同一父交互会话至多一个本产品创建的活动实例”；不接管外部 child，不以 SessionId/PID 单值证明所有权。
- “不为本功能引入 DI 容器或给每个类建接口”；能力库不得反向引用宿主或调试 Session 库。
- “单次请求/文本响应上限 1 MiB”；请求与 observation 各保留最近 256 条；任务最多 256 个，满额拒绝新建。
- “每任务总日志上限 16 MiB”；实例任务日志总配额 256 MiB；输出默认约 8 KiB、最大 32 KiB，UTF-8 不截半个字符。
- “pointer/keyboard 范围 1–30 秒，UIA 沿用 1–300 秒，screenshot 沿用 0–30 秒”；传输等待为执行预算加 5 秒，管道连接最多 5 秒。
- “durationMs 范围 50–5000”；pointer 坐标为 child 虚拟屏物理像素，截图换算 origin + 图内像素。
- 所有 MCP 参数带默认值和中文 Description、末尾 CancellationToken；错误返回中文文本；截图沿用 CallToolResult 图片例外与 2MB 分流，不设 IsError。
- stdout 只承载 MCP 协议或 CLI 单个 JSON，日志走 stderr；自起进程必须持续排空 stdout/stderr。
- 不扩大到跨会话 Main 前 debug_launch，不改认证/证书/Hello 策略，不安装驱动，不自动重放未知动作。
- 先读根、宿主、tests 和 Client 对应 AGENTS；原生 API 查仓库相对 `../../Externals/` 源码，缺少才查官方文档，不能照搬实验中未闭环的假设。
- 测试只走业务公开入口，假件仅限原生系统/进程、IPC、文件、时钟等外部边界；不测 DTO 清单、枚举编号或私有字段结构。纯提取重构不新增平行业务测试。

## Review Focus

1. 参数含空格/中文/引号以及临时 PATH：应在 child 保留参数边界、明确工作目录与显式环境，不能转成父侧 shell；Task 4。
2. 输入中目标弹窗抢前台、PID 复用、显示器负原点：应中止误投并仅释放本轮按下状态，不把父窗坐标用于 child；Task 8。
3. MCP 被取消且调用方没拿到 requestId：另一客户端仍能列请求并查询同一结果，不再次执行；Task 1、9。
4. UIA 超时但 COM 仍未返回：不能过早释放客户端/执行门；普通 ui_wait 不阻止截图与输入；Task 6。
5. CLI 创建实例后不同 features 的 MCP 接入、工具被安装到另一目录：不受创建者门控污染，所有内部角色仍可定位并运行；Task 3、9、10。

---

## 进入执行前

- 本计划是一个贯通功能，任务顺序主要受进程/协议接口依赖，不拆成多份独立产品计划。
- 维护者在开始执行时明确更正：**不要建立工作区，采用新分支**。在原仓库功能分支 `feature/child-session-mcp-cli-integration` 执行，保留全部已有未提交研究、夹具和设计，**不得 reset/stash/搬空它们**；只提交本功能自己修改的文件/选定 hunk。
- 每任务提交只包含本任务文件/选定文档 hunk，提交信息用中文，不使用 `git add .`。保留旧 `NativeInputSpike`/Sandbox 源码和产物，不把它们作为生产依赖。
- 普通测试不自行启用子会话、不自动触发 UAC；真实 child 验收采用显式 `DOTNETDEBUGGERMCP_CHILD_INTEGRATION=1`，并要求系统已经启用且无人占用。需要改开关时另请维护者明确授权。

## 文件职责地图

以下是预计文件；内部小类可按职责合并，禁止把连接、任务、输入塞入一个万能类。

| 文件/目录 | 职责 |
|---|---|
| `src/SharpSight.ChildSession/Contracts/{ChildRequest,ChildReply,ChildIdentity,ChildArtifact}.cs` | 有界协议、实例/对端身份和产物记录，不含 MCP 类型 |
| `src/SharpSight.ChildSession/Requests/{RequestJournal,ChildRequestExecutor}.cs` | 原子接收/开始/取消、去重、请求发现和结果查询 |
| `src/SharpSight.ChildSession/Ipc/{ChildPipeClient,ChildPipeServer,PipePeerVerifier}.cs` | 长度前缀 JSON、当前用户限制、真实对端核验 |
| `src/SharpSight.ChildSession/Lifecycle/{ChildSessionClient,ChildCoordinator,ChildInstanceStore,ChildSessionPlatform}.cs` | 客户端发现、生命周期协调、原子状态落盘、WTS 系统边界 |
| `src/SharpSight.ChildSession/Connection/{ChildConnectionWindow,RdpActiveXHost,ChildWorkerLauncher}.cs` | 独立 STA 消息泵、失焦连接窗口、普通权限计划任务投送 |
| `src/SharpSight.ChildSession/Processes/{ChildTaskRunner,ChildTaskJournal,ChildOutputStore,WindowsJob}.cs` | 原路径任务、Job 所有权、输出/配额、取消 |
| `src/SharpSight.ChildSession/Input/{ChildInputController,ChildTargetResolver,PressedInputScope,KeyChordParser}.cs` | 目标/前台验证、完整输入序列、异常释放、键文法 |
| `src/SharpSight.ChildSession/Observations/ObservationRegistry.cs` | 观察引用来源和代际/几何验证，不改造成多目标 UIA 缓存平台 |
| `src/SharpSight.ChildSession/{SharpSight.ChildSession.csproj,AGENTS.md}` | 新能力库与维护边界；框架与宿主一致，启用 WinForms，不引用宿主 |
| `src/DotNetDebuggerMcp/Services/Gui/` | 共享 UIA/截图请求、执行及呈现，父侧和 worker 复用 |
| `src/DotNetDebuggerMcp/Services/Child/{ChildServices,ChildOperationDispatcher,ChildProcessRoles,ChildCallPolicy}.cs` | 显式组装、子进程角色、白名单派发、每入口 features 检查 |
| `src/DotNetDebuggerMcp/ChildCli/ChildCliCommand.cs` | enable/disable/call、JSON/JSON 文件、退出码；不实现业务编排 |
| `src/DotNetDebuggerMcp/Tools/Child/` | 四个新 MCP 薄包装；旧 UiTools/ScreenshotTool 加显式路由 |
| `tests/DotNetDebuggerMcp.Tests/Child*.cs` | 新功能行为与真实 IPC/进程测试；沿用现有测试项目，不新增测试套件 |
| `tests/Fixtures/ChildSessionTarget/` | 正式集成测试目标：进程报告/输出模式与自绘 GUI 模式；不提供产品功能 |
| `tests/Fixtures/generate-testdata.ps1` | 构建上述夹具至 `tests/TestData/ChildSessionTarget/` |

## 任务间接口约定

以下为本计划确定的新接口，不表示它们当前已存在。只固定跨任务接口，内部原生辅助签名由实现者按查证后的 API 决定。

```csharp
public sealed record ChildRequest(
    string Instance, string RequestId, string Operation,
    JsonElement Arguments, DateTimeOffset Deadline);
public sealed record ChildReply(
    string Instance, string RequestId, string Status, string Message, JsonElement Data);
public sealed record ChildIdentity(
    string Instance, string Sid, int ParentSessionId, int ChildSessionId,
    int HolderPid, long HolderStartedTicks, int WorkerPid, long WorkerStartedTicks);
public sealed record ChildArtifact(
    string RelativePath, string MediaType, long Length, JsonElement Metadata);
```

`Operation` 固定为 spec §7 白名单工具名，具体 action 位于 Arguments；私有引导/取消/握手帧独立于对外操作白名单。Status 使用 spec 定义的操作/请求状态字符串；Data 的 JSON 是外部协议形态，禁止传任意 CLR 类型名。

生产入口为 `ChildSessionClient.SendAsync(ChildRequest request, CancellationToken ct = default) : Task<ChildReply>`。宿主分派为 `ChildOperationDispatcher.ExecuteAsync(ChildRequest request, CancellationToken ct) : Task<ChildReply>`。实例客户端负责发现/首次 start 持有器，worker 分派绝不再次转发到自身。

测试支持 `ChildTestHost.StartAsync(CancellationToken ct = default) : Task<ChildTestHost>`（IAsyncDisposable），`SendAsync(string operation, object arguments, CancellationToken ct = default) : Task<ChildReply>`，只负责建立真实私有管道并使用真实 RequestJournal/协调器。可控系统状态由 `TestChildPlatform` 提供，它只替代 Win32/COM/进程系统边界，不替代协调器、引用表、队列和业务服务。各任务需要的系统事件控制方法随该任务加入，不为未知需求预建完整模拟器。

---

### Task 1：请求登记、截止与失联后的结果发现

**Files:** 创建 `Contracts/ChildRequest.cs`、`Contracts/ChildReply.cs`、`Requests/RequestJournal.cs`、`Requests/ChildRequestExecutor.cs`（以上相对新库）；创建库 csproj 并加入 `DotNetDebuggerMcp.slnx`、宿主 ProjectReference 和新库 AGENTS；更新根 `AGENTS.md` 的项目地图/依赖方向；创建 `tests/DotNetDebuggerMcp.Tests/ChildRequestTests.cs`。

**Interfaces:** 消费协议记录；产出 `RequestJournal`、`ChildRequestExecutor.ExecuteAsync(ChildRequest, Func<CancellationToken,Task<ChildReply>>, CancellationToken)`，以及 `List(long cursor, int limit) : JsonElement`、`Find(string requestId) : ChildReply`。持有器后续负责实例/对端验证后才进入执行器。

- [ ] **RED：** 通过请求执行公开入口编写 `排队截止后不会迟到执行`、`相同请求取回原结果而非重做`、`丢失响应仍可列出请求并查回结果`。时钟用 TimeProvider 的外部可控假件，业务副作用写临时文件并断言结果，不断言委托调用次数。断言：
  ```csharp
  Assert.Equal("not-executed", expired.Status);
  Assert.False(File.Exists(expiredOutputPath));
  Assert.Equal(first.RequestId, recovered.RequestId);
  Assert.Equal(first.Data.GetRawText(), recovered.Data.GetRawText());
  ```
- [ ] **运行 RED：** `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*ChildRequestTests"`；确认因缺请求执行能力失败，而非测试环境错误。
- [ ] **实现：** 创建上述库及真实请求对象；排队开始与截止原子互斥，调用端先生成 requestId。请求保留/分页/去重按 spec §6.1/10，控制查询不入日志；记录缺失返回 unknown，不能当未执行。满额不能丢失仍执行中的操作控制，优先淘汰已完成记录，无可淘汰空间则拒绝接受新操作。
- [ ] **验证 GREEN：** 同一命令零失败；追加超过保留容量后“已淘汰查询 unknown，不重做”的业务测试。不测试 record 属性数量或默认值。
- [ ] **提交：** 仅本任务文件与项目登记，中文消息 `新增子会话请求登记与结果查询`。

### Task 2：真实私有 IPC、身份与产物边界

**Files:** 创建 `Contracts/ChildIdentity.cs`、`Contracts/ChildArtifact.cs`、`Ipc/*.cs`、`Lifecycle/ChildInstanceStore.cs`；创建 `tests/DotNetDebuggerMcp.Tests/{ChildIpcTests,ChildTestHost}.cs`。

**Interfaces:** 消费 Task 1 执行器；产出 `ChildPipeClient.SendAsync(ChildRequest,CancellationToken) : Task<ChildReply>`、`ChildPipeServer.RunAsync(Func<ChildRequest,CancellationToken,Task<ChildReply>>,CancellationToken) : Task`，及 `ChildInstanceStore.ReadAsync/WriteAsync` 的私有实例引导记录。写入用临时文件原子替换，记录包含协议版本、identity、端点与随机凭据；公开 reply 不含凭据。

- [ ] **RED：** 建真实命名管道，发送错误实例/错误凭据/未知操作/超过 1 MiB 的信封，应拒绝且没有任务或文件副作用；发送截断 JSON 后下一连接仍可正常查询。编写 `伪造实例不能产生业务副作用`：
  ```csharp
  Assert.Equal("not-executed", rejected.Status);
  Assert.False(File.Exists(targetOutputPath));
  Assert.Equal("completed", nextHealthyQuery.Status);
  ```
- [ ] **运行 RED：** `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*ChildIpcTests"`，确认缺传输/核验导致失败。
- [ ] **实现：** 当前用户 ACL + 双向真实 PID/创建时间/SID/Session 校验，长度前缀 JSON，读取上限前置；引导凭据仅私有目录传递，单次客户端超时不得结束服务 accept 循环。产物只允许实例目录内的相对文件，拒绝绝对路径、`..`、越界链接/重解析点，不信任工作进程提供的任意路径。
- [ ] **验证 GREEN：** 同命令通过；增加“伪造外部 PNG 路径不能读取外部文件”的外部可观察测试。连接超时区分未发送与结果未知，转发保持 requestId。
- [ ] **提交：** `新增子会话私有通信与身份校验`。

### Task 3：建立、复用、停止与独立进程角色

**Files:** 创建 `Lifecycle/{ChildSessionClient,ChildCoordinator,ChildSessionPlatform}.cs`、`Connection/*.cs`；创建宿主 `Services/Child/{ChildServices,ChildProcessRoles}.cs`；修改宿主 `Program.cs`、csproj；创建 `ChildLifecycleTests.cs`。

**Interfaces:** 消费 Task 2 管道/身份存储；产出 `ChildSessionClient.SendAsync`（见约定），`ChildProcessRoles.TryRunAsync(string[] args,CancellationToken ct) : Task<int?>`（非内部角色返回 null），`ChildCoordinator.HandleAsync(ChildRequest,CancellationToken) : Task<ChildReply>`；Task 4/6 后续注册 worker 操作分派，不将调用逻辑写进 WinForms 事件。

- [ ] **RED：** `ChildTestHost` 用真实协调器及可控原生系统边界，写 `并发创建只得到一个实例`、`外部子会话不能被接管或注销`、`worker已退出仍可按有效持有器所有权停止`、`存在任何child时禁用被拒绝`：
  ```csharp
  Assert.Equal(first.Instance, second.Instance);
  Assert.Equal("not-executed", foreignStop.Status);
  Assert.Equal("Stopped", ownedStop.Data.GetProperty("state").GetString());
  Assert.True(platform.ExternalChildStillRunning);
  ```
- [ ] **运行 RED：** `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*ChildLifecycleTests"`，不触发真实设置/UAC。
- [ ] **实现：** SID+父 Session 互斥、显式设置、状态机、非激活连接窗、任务普通权限投送与就绪握手。入口在原 McMaster/MCP 启动前识别内部角色；STA 独立线程/消息泵，不在 top-level await 后假定线程仍为 STA。封装实际宿主启动命令：apphost 直接执行，`dotnet <dll>` 则保留宿主 DLL 前缀，不能只取 Environment.ProcessPath。通过同一安装产物角色启动，不把 secret 放命令行，不允许裸 SessionId 投送。enable/disable 的提升进程复查所有权与活动状态；默认路径不创建 ActiveX。
- [ ] **实现停止：** 先取消本实例动作/任务再注销，超时返回停止中；断连不冒充注销、不自动重连，worker异常不沿用旧 instance。拒绝不可核实的状态文件；只删本次临时计划任务。此阶段 worker 白名单未实现的操作明确拒绝，不假返回成功。
- [ ] **验证 GREEN：** 同命令通过，并用 Release exe 的内部模式错误参数验证中文拒绝、无窗口/无 MCP 握手输出。真实三轮启停保留给 Task 10，不在此重复人工验收。
- [ ] **提交：** `接入子会话连接生命周期与执行角色`。

### Task 4：程序/脚本任务、Job 取消与输出

**Files:** 创建库 `Processes/*.cs`；创建宿主 `Services/Child/ChildOperationDispatcher.cs`（先处理 lifecycle/process）；创建夹具 `tests/Fixtures/ChildSessionTarget/{ChildSessionTarget.csproj,Program.cs}`，修改生成脚本/tests AGENTS；创建 `ChildProcessTests.cs`。

**Interfaces:** 消费 Task 3 worker 身份；产出 `ChildTaskRunner.ExecuteAsync(ChildRequest,CancellationToken) : Task<ChildReply>`、`ChildOperationDispatcher.ExecuteAsync`。fixture `report` 模式输出参数/工作目录/指定环境/Session，`flood` 输出超过限额，`spawn` 启动自身子进程；所有模式可被明确结束，不能读写敏感环境。

- [ ] **RED：** 用真实进程/真实 Job，`参数中的空格中文引号不会被shell拆分`、`取消任务不结束另一个任务`、`日志截断仍持续排空且进程能退出`、`调用方取消等待不终止已启动任务`：
  ```csharp
  Assert.Equal(expectedArguments, report.Arguments);
  Assert.Equal(expectedDirectory, report.WorkingDirectory);
  Assert.True(firstTaskExited);
  Assert.True(secondTaskStillRunning);
  Assert.True(output.Truncated);
  ```
- [ ] **运行 RED：** 先用 `powershell -ExecutionPolicy Bypass -File tests/Fixtures/generate-testdata.ps1` 生成测试产物，再 `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*ChildProcessTests"`。
- [ ] **实现：** 调用端路径/PATH 解析与 JSON 数组/字典校验；worker 用已查证的原生挂起创建→分配 Job→恢复路径，不引入先运行后登记竞态。实现 start/list/status/output/cancel，任务/日志配额、UTF-8 字符边界与 nextCursor；脚本仅启动指定解释器，进程句柄/Job/排空任务有明确释放归属。
- [ ] **验证 GREEN：** 同命令通过；针对 broker/启动器退出只断言自有 Job 状态，不凭可见窗口猜“应用退出”。真实 child 的 Session 归属在 Task 10 验证；本层本机进程测试不得伪称跨会话已通过。
- [ ] **提交：** `支持子会话程序脚本任务与输出取消`。

### Task 5：提取既有 GUI 执行，保持主会话行为

**Files:** 创建宿主 `Services/Gui/{UiOperationRequests,UiOperationService,ScreenshotRequest,ScreenshotCaptureService,ScreenshotPresenter}.cs`；修改 `Tools/Debugger/UiTools.cs`、`Tools/Screenshot/{ScreenshotTool,ScreenshotDisplaysTool,ScreenshotWindowsTool}.cs`。

**Interfaces:** 产出 `UiOperationService.ExecuteAsync(string operation,JsonElement arguments,CancellationToken ct) : Task<ChildReply>`、`ScreenshotCaptureService.ExecuteAsync(ScreenshotRequest request,CancellationToken ct) : Task<ScreenshotExecution>`、`ScreenshotPresenter.ToMcp(ScreenshotExecution execution,string filePath) : CallToolResult`。`ScreenshotExecution` 由本任务定义为已有 CaptureResult、目标文案、notes、mode、帧及目标元数据的聚合；不包含路由和 MCP 注册。

- [ ] **基线：** 先运行既有 `DebugUiToolsTests`、`UiFrameRegistryTests`、`UiAutomationServiceTests`、`ScreenshotToolTests`；命令均为 `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*<类名>"`。记录现有失败，不归因于尚未发生的提取。
- [ ] **提取：** 把校验、结果格式化、截图定位/清单与产物呈现从工具类搬至上述 Services；保留现有 helper 测试入口的内部转发，避免重写既有测试。敏感值脱敏仍执行；日志在调用入口记录一次，不让 worker 初始化调试 Manager 只为写日志。
- [ ] **验证：** 重跑同组用例，比较原头部/PNG 分流、commit/focus/帧行为不变。纯重构不新增平行测试，不把服务重新调用工具类当复用。
- [ ] **提交：** `提取共享GUI执行保持原工具行为`。本任务不改变 MCP 参数面，故不更新工具快照。

### Task 6：实际执行完成跟踪与 worker UIA 分派

**Files:** 修改 `src/SharpSight.UiAutomation/UiAutomationService.cs`，创建同目录 `UiExecutionBoundary.cs`；创建库 `Requests/ChildGuiExecutionGate.cs`；扩展宿主 `ChildOperationDispatcher.cs` 与 `UiOperationService.cs`；创建 `ChildGuiExecutionTests.cs`，复用既有 UIA 超时回归位置。

**Interfaces:** 消费 Task 5 UI 服务；产出 `ChildGuiExecutionGate.RunAsync<T>(Func<CancellationToken,Task<T>> body,DateTimeOffset deadline,CancellationToken ct) : Task<T>`。UIA 增最小内部执行边界 `UiExecutionBoundary`：跟踪真实后台 Task，支持等待循环各探测短暂取门；默认 Instance 使用原边界，child worker 使用带门/生命周期跟踪的独立内部实例，**不公开全部 UIA 类型**。

- [ ] **RED：** 用真实执行门、TaskCompletionSource 控制外部 UIA 调用边界，走 UI 服务公开执行入口测试 `UIA超时后未完成前不能开始第二次GUI变更`、`等待控件出现时仍可截图和操作`、`队列到期后不迟到执行`。在临时文件上的业务副作用断结果，不断言内部锁次数：
  ```csharp
  Assert.Equal("running", timedOut.Status);
  Assert.False(File.Exists(secondMutationFile));
  Assert.Equal("completed", controlQuery.Status);
  Assert.Equal("completed", screenshotWhileWaiting.Status);
  ```
- [ ] **运行 RED：** `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*ChildGuiExecutionTests"`，确认现有公共 API 早返回不能满足真实完成约束。
- [ ] **实现：** 从 RunGateAsync/UiaBoundAsync 抽出真实任务生命周期，不仅包外层 await；child 未完成的后台任务持有 UIA 客户端和 GUI 门，请求登记也不得把上层超时文本当作真实完成。实际后台工作结束才发布终态；停止后只回收可安全结束的资源。ui_wait 订阅/探测/退订分段取门，不持门 sleep，不推进 UIA index/frame。控制通道与进程任务旁路。
- [ ] **验证 GREEN：** 同命令及 Task 5 基线通过；补 child 系统身份限定，UIA process 名解析仅在 child 内，向既有定位器传已核验 PID，禁用父侧 debug 目标兜底。超时消息在 child 模式指向 requests/result，不指导直接重试同一变更。
- [ ] **提交：** `支持子会话GUI执行完成跟踪与UIA分派`。

### Task 7：截图产物与观察引用

**Files:** 创建库 `Observations/ObservationRegistry.cs`、`Input/ChildTargetResolver.cs`；扩展共享截图服务/呈现与 worker dispatcher；创建 `ChildObservationTests.cs`、`ChildCaptureTests.cs`。

**Interfaces:** 消费 Task 6 GUI 门、现有 ScreenCapture；产出 `ChildTargetResolver.ResolveAsync(int processId,string hwnd,CancellationToken ct) : Task<ResolvedChildTarget>`（本任务定义 PID+创建时间/会话/HWND/几何身份）、`ObservationRegistry.Register(JsonElement source) : string`、`Validate(string observationId,JsonElement current,int frameId) : void`，及 worker screenshot 返回 ChildArtifact。

- [ ] **RED：** `普通窗口截图后原UIA清单仍可操作`、`再次查找后旧index引用被拒绝`、`别的实例或复用PID不能消费旧观察`、`截图产物只来自选定child`：
  ```csharp
  Assert.Equal("completed", actionAfterWindowCapture.Status);
  Assert.Equal("not-executed", staleIndexAction.Status);
  Assert.NotEqual(parentMarkerPixels, childMarkerPixels);
  Assert.Equal(expectedChildSession, captureSession);
  ```
- [ ] **运行 RED：** `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*ChildObservationTests"`；环境真实截图部分另跑 `*ChildCaptureTests`，不能用普通假截图宣称真实捕获通过。
- [ ] **实现：** observation 256 条多记录，UIA frame 仍单槽；同次门内绑定身份、校验 frame/observation、执行并登记。窗口/屏幕视觉引用不因无关采集失效；失配几何/布局拒绝。实现全部截图模式与两类只读清单在 worker 执行，原生 1:1 与来源/origin 保留，PNG 私有落盘后由父侧验证/呈现；外部 filePath 只在调用端处理。
- [ ] **验证 GREEN：** 重跑两类和现有 ScreenshotToolTests/UiFrameRegistryTests；测试画面来自独立可区分的目标，不以非黑/文件存在代替内容判断；检查 readback 产物失效时不会返回上一次 PNG。
- [ ] **提交：** `支持子会话截图与观察引用校验`。

### Task 8：真实鼠标键盘与中断释放

**Files:** 创建库 `Input/{ChildInputController,PressedInputScope,KeyChordParser}.cs`，扩展 ChildTargetResolver；创建夹具 `ProbeForm.cs`、`InputCanvas.cs`，扩展 fixture Program GUI 模式；创建 `ChildInputTests.cs`。

**Interfaces:** 消费 Task 6 执行门、Task 7 目标/观察；产出 `ChildInputController.PointerAsync(ChildRequest,CancellationToken) : Task<ChildReply>`、`KeyboardAsync(ChildRequest,CancellationToken) : Task<ChildReply>`，注册 worker 白名单分派。原生输入边界 `IChildInputPlatform` 只表示窗口/键态/SendInput 等外部系统能力，不假替换输入控制器。

- [ ] **RED：** 外部系统假件记录注入导致的目标文本/拖拽状态，写 `前台变化后不继续键入其他窗口`、`只释放本轮按下键`、`窗口负坐标仍按child像素命中`、`取消拖拽不会遗留鼠标按下`：
  ```csharp
  Assert.Equal("", otherWindow.Text);
  Assert.False(platform.OwnInjectedLeftButtonDown);
  Assert.True(platform.PreExistingPhysicalCtrlStillDown);
  Assert.Equal(expectedChildPoint, target.DragEnd);
  ```
- [ ] **运行 RED：** `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*ChildInputTests"`，普通测试绝不在父桌面真实 SendInput。
- [ ] **实现：** 完整 pointer move/click/doubleclick/drag/scroll、keyboard text/key/chord；按 spec 文法和范围拒绝无效组合。每次注入前验证 child/input desktop/目标前台及命中区域，首次只在 child 还原置前，事件带本轮标记。预先按下的修饰键不当成本轮所有权，影响 chord 语义时明确拒绝；异常释放仅针对成功提交的自有按下状态，不能为“清理”向已变化的交互环境投放事件。
- [ ] **验证 GREEN：** 同命令通过；invalid chord/目标隐藏/最小化及多窗歧义都有公开入口错误结果。GUI fixture 正常消息泵验证严格前台点击/拖拽/文本/快捷键，不直接调用 handler；真实 SendInput 只留 Task 10 显式 child 验收。
- [ ] **提交：** `支持子会话真实键鼠与安全中断`。

### Task 9：MCP/CLI 同批接线、能力族和 agent 说明

**Files:** 创建宿主 `Tools/Child/{ChildSessionTool,ChildProcessTool,ChildInputTools}.cs`、`ChildCli/ChildCliCommand.cs`、`Services/Child/ChildCallPolicy.cs`；修改 `Program.cs`、`DotNetDebuggerMcpCmd.cs`、`FeatureFlags.cs`、`ToolFeatureRegistry.cs`、`HandshakeText.cs`、UiTools/ScreenshotTool；更新 README、CHANGELOG、宿主 AGENTS、现有工具快照/握手测试；创建 `ChildMcpToolsTests.cs`、`ChildCliTests.cs`。

**Interfaces:** 消费 Tasks 1–8 的 ChildSessionClient/dispatcher；四工具方法名 `ChildSession/ChildProcess/ChildPointer/ChildKeyboard`，参数、默认值、类型按 spec §6（cursor 为 long，limit/timeout/坐标为 int，其他字符串），均 Task<string>；截图保持 Task<CallToolResult>。旧 UIA/截图在 CancellationToken 前追加 spec 的 instance/observationId。CLI `ChildCliCommand.RunAsync(string[] args,CancellationToken ct) : Task<int>` 早分发且不启动 MCP。

- [ ] **RED：** 同进程调用正式入口用例并连接 ChildTestHost 的真实 IPC，仅替代外部原生系统边界，测试 `不同调用入口可复用同一实例`、`未启用族不能经result或requests取回该族结果`、`指定实例时没有父侧副作用`、`MCP取消后另一客户端能发现并查询原请求`。真实 MCP stdio/CLI 子进程在本任务验证目录、缺参、门控与失败 JSON；真实 child 成功的跨进程闭环归 Task 10，**不能为测试给生产 exe 加跳过会话/身份校验的后门**。CLI stdout 解析单个 JSON，断言 unknown/失败退出 1、正常查询退出 0；非法 JSON/同时两种输入/未知操作不产生副作用。
- [ ] **运行 RED：** `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "*ChildMcpToolsTests"` 与 `--filter-class "*ChildCliTests"`，确认失败来自尚未接通入口。
- [ ] **实现接线：** 独立 child 族，Features 默认全族但不启动环境；MCP 每入口 ChildCallPolicy 先做门控再发送，实例完整白名单不受创建者限制。CLI `child enable/disable/call`、`--json/--json-file`、路径解析/错误/结果映射用同一业务用例，显式白名单 switch，不反射找工具方法。
- [ ] **实现兼容：** instance 非空先转发再进入本地 UIA/截图；instance 空保持旧路径，父侧 observation 非空拒绝。MCP stop 检查本服务器当前 debug 目标是否属 child，提示先 disconnect。延迟/失联结果含 requestId、分页发现与结果查询；截图 MCP image/路径及 CLI 路径分流。文本错误不设 IsError。
- [ ] **同步说明与验证 GREEN：** Description、参数默认值、按族握手和 README 在本任务同一提交；禁用族工具名/触发词不泄漏。重跑两新套件及现有 FeatureFlagsTests、ToolFeatureRegistryTests、DotNetDebuggerMcpCmdTests、McpSessionConcurrencyTests。经审核后用 `UPDATE_TOOL_SURFACE_SNAPSHOT=1` 定向运行 ToolSurfaceContractTests 生成基线，立刻清除环境变量，再正常重跑。不要新增默认值/枚举成员结构测试。
- [ ] **提交：** `接通子会话MCP与CLI共享入口`；文案、工具、门控同一次提交，不拆成先暴露工具再补握手。

### Task 10：安装产物、正式交叉验收与收尾文档

**2026-11-15范围裁决：** 本任务按[正式设计§13](../specs/2026-11-14-child-session-mcp-cli-design.md#13-2026-11-15-范围收敛只验收已确认的-exe-入口)收敛；以下保留原计划历史，涉及DLL直启的专项要求不再执行，其余产品/清理/验收门槛不变。

**执行状态：** 安装产物三轮及Task10A恢复专项已实测通过，最终全分支审阅/提交收尾中。完整宿主历史基线非全绿，不以本任务复选框表示全部测试成功；准确发现数、失败/跳过与证据统一归[验证记录§8](../research/11-child-session-integration-results.md#8-最终安装产物三轮业务验收通过2026-11-15)。

**Files:** 创建 `src/DotNetDebuggerMcp.Client/ChildSessionCases.cs`，修改 ClientRunner/Program 增显式 child 验收入口；创建 `tests/DotNetDebuggerMcp.Tests/ChildInstalledToolTests.cs`；创建 `docs/planning/research/11-child-session-integration-results.md`，更新规划地图、新库/宿主/tests/Client AGENTS、README/CHANGELOG 和本计划状态。

**Interfaces:** 消费已接通 MCP/CLI；Client 的 child 验收仅在明确启用且通过只读前置检查后运行，不并入默认反编译回归，也不由 CI 自动触发系统开关。

- [x] **建立验收场景：** 写 `安装后的工具可建立并复用内部执行角色` 安装路径验收与 `跨CLI和MCP的目标输入截图及停止闭环`；遇活动外部 child 要拒绝并记录，缺系统前置则明确 Skip/未验收而非成功。补 Client/安装脚本调用和所需打包配置；不得仅对开发 bin 验证。前序能力已完整实现时场景可直接通过，不为形式制造 RED；若发现安装/集成缺陷，先保留能复现的失败用例，再修复对应代码。
- [x] **自动回归：** 执行：
  ```powershell
  powershell -ExecutionPolicy Bypass -File tests/Fixtures/generate-testdata.ps1
  dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj
  dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -c Release
  dotnet run -c Release --project src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj
  ```
  预期构建零错误、受影响用例零失败、默认 Client 全绿；测试 skip 要写明原因，不能计为实测成功。
- [x] **安装回归：** 在 `tests/TestData/ChildSessionAcceptance/<run>/` 下 pack 和本地 tool install：
  ```powershell
  dotnet pack src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj -c Release -o <run>/packages
  dotnet tool install DotNetDebuggerMcp --tool-path <run>/tool --add-source <run>/packages --version <当前项目版本> --no-cache
  ```
  将占位路径替为本轮绝对路径，版本读取 csproj 而非硬编码；独立可执行文件与 `dotnet <宿主dll>` 两种启动方式均检查内部角色解析，不把 dotnet.exe 误当产品宿主。不改正式安装、不发版、不推 tag。
- [x] **真实交叉验收：** 经授权确保开关与会话前置后，设置 `DOTNETDEBUGGERMCP_CHILD_INTEGRATION=1`。安装路径 CLI 创建，MCP 启动原路径 WinForms/WPF Debug 目标，另一 CLI 列任务/输出/UIA、严格点击/拖拽、键码/IME显式提交/Unicode/chord、截图实际读图，再父侧现有 debug_attach→断点→读栈→disconnect，cancel 和 stop。至少三轮，覆盖断连与 worker 退出；只在自有 child 注入。
- [x] **清理与证据：** 核对无本轮 holder/worker/fixture/临时任务遗留，probe 确认自有 child 已注销；本轮若获授权改开关按约定恢复。报告记录构建/工具版本、run目录、准确命令、机器检查、画面/业务结果及未测限制；不重复要求已认可的人工循环，不把未测设备/COM/锁屏算通过。新报告以职责注释开头并同次登记地图。
- [ ] **审阅与提交：** 按 verification-before-completion 和 requesting-code-review 做全改动验证/独立审阅，修重要问题后复跑。文档与代码只提交本功能范围，中文消息 `完成子会话接入交叉验收与使用说明`。不自动合并、推送或发布。

---

### Task 10A：GUI请求前自动不激活还原连接窗

**Goal / Spec：** 实施[设计§14](../specs/2026-11-14-child-session-mcp-cli-design.md#14-连接窗最小化时自动不激活还原2026-11-15-已确认)；不改变原任务10三轮、所有权与输入隔离门槛。

**Files：** `src/SharpSight.ChildSession/Connection/ChildConnectionWindow.cs`（原STA内检查/还原）、`ChildHolderRuntime.cs`（接线）、`ChildForwarder.cs`（GUI请求门）；`tests/DotNetDebuggerMcp.Tests/ChildForwardingTests.cs`（真实Store/IPC行为回归）；宿主`ChildSessionTool.cs`/`ChildInputTools.cs`/`UiTools.cs`/`ScreenshotTool.cs`工具说明、`HandshakeText.cs`及必要共用文案常量、README/子会话AGENTS；显式Client新增连接恢复验收小文件及GUI场景接线。

**Interfaces：** `ChildConnectionWindow.EnsureVisibleWithoutActivationAsync(CancellationToken)` 返回 `Task<bool>`；`ChildForwarder` 构造接收必填 `Func<CancellationToken, Task<bool>> ensureVisibleWithoutActivation`，所有既有调用点显式接线，不加fail-open默认。原生互操作归能力库，宿主只更新说明。

**Global Constraints：** 仅本实例父侧连接窗；`ShowWindow(SW_SHOWNOACTIVATE=4)` 后以 `IsIconic` 复核；不使用WindowState=Normal/SW_RESTORE/SetForegroundWindow，不置顶/父侧输入/自动重连；child内业务目标最小化语义不变；`child_session`/`child_process`不触发恢复；未确认不投送GUI、不重放。

**Review Focus：** 已关窗拒绝；STA超时/取消后迟到回调不得变更；还原失败无worker副作用；控制请求不依赖还原且不弹窗；显式验收不得放宽50ms隔离判据。

- [ ] **RED：** 在现有真实IPC回归中更新/增加“GUI请求恢复连接窗后才执行”“无法还原连接窗时不执行GUI”“控制查询及进程启动不触发连接窗恢复”，边界委托替代原生窗口，其他Store/IPC/业务副作用真实；已关窗/取消从该边界返回未确认，断返回与副作用、不查调用次数。运行`dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -c Release -- --filter-class 'DotNetDebuggerMcp.Tests.ChildForwardingTests'`，确认RED来自缺少恢复门而非绑定错误。
- [ ] **实现原生恢复与转发门：** 原STA有界检查→必要时调用4→IsIconic复核；已关窗/释放返回false，已取消的排队回调在变更前退出。保持Ready/身份/WTS门；只有明确GUI操作使用新增门，false返回中文not-executed。
- [ ] **GREEN：** 运行上述类并加ChildConnectionReadinessTests/ChildLifecycleTests/ChildMcpToolsTests/握手与工具面完整类名，所有本项业务规则通过且发现数非0。宿主/测试一律Release，最后单独Debug构建业务目标，避免输出锁与覆盖。
- [ ] **说明与显式验收：** 对受影响工具Description和按child族握手同步“不激活还原；失败未执行”；跨工具重复文案用既有AppText等常量。Client第一轮在正常隔离采样前，核实holder身份与父侧失焦，故意最小化该连接窗、发一次ui_find，单列恢复证据（前后IsIconic/父前台/光标与业务输出）；随后原严格采样和完整三轮不变。不使用测试快捷键或父侧输入；不把恢复阶段初始最小化偷偷计为正常隔离通过。
- [ ] **交付审阅：** 不自动启动真实child；报告精确RED/GREEN/文件/顾虑、交独立审阅。主会话重新隔离安装，刷新定向回归/默认Client及Debug哈希，实机三轮与恢复证据归research/11；中文按文件提交与整体收尾沿用原门槛，不自动发布。

## 自检与覆盖索引

| Spec 主题 | 任务 |
|---|---|
| 兼容边界、进程架构、零宿主依赖 | 3、5、9 |
| 生命周期、授权、外部 child、disable/stop 所有权 | 3、9、10 |
| IPC 身份、长度、secret 与产物路径 | 2、7 |
| 任务/参数/工作目录/输出/取消/配额 | 4 |
| GUI门、真实完成、ui_wait、公用UIA能力 | 5、6 |
| screenshot全模式、产物、坐标/引用/帧 | 7、8 |
| 请求发现、去重、截止、unknown与取消 | 1、2、6、9 |
| MCP参数/门控/握手、CLI/脚本入口 | 9 |
| 安装路径、真实业务闭环、调试attach、清理与证据 | 10 |
| 连接窗最小化自动不激活还原、失败拒绝与控制旁路 | 10A |

执行者不得用“已编译”替代真实 child 验收；每任务先确认自己的测试行为和既有基线，变更若超出批准范围，停止并说明。计划不假定前述接口已存在，不以历史实验二进制替代正式安装产物。
