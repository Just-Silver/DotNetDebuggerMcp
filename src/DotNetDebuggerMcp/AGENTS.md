# DotNetDebuggerMcp 宿主开发指南

宿主 exe（`DotNetDebuggerMcp`，CLI 命令/NuGet 包 id 同名）：**唯一的 MCP 服务器 + CLI 入口 + Web 承载进程**。无业务参数启动 MCP 服务器（stdio）；传 `-a`/`-c` 等进入命令行模式（复用与 MCP 工具相同的校验与执行逻辑）；`--web` 时并联 Blazor Web host。全部 MCP 工具与执行管线在此层，反编译/元数据能力来自 Decompiler 库、调试能力来自 Engine/Session 库、Web 来自 Web 库。

> 本文件 + 各子项目 AGENTS.md（`src/*/AGENTS.md`）按项目拆分；根 `AGENTS.md` 是仓库级总览与共享纪律（开发铁律/输出约定/版本发布）。

## 目录结构（每目录一个命名空间）

- `Tools/`（`DotNetDebuggerMcp.Tools`）— 反编译 4（Decompile/DecompileMember/DecompileIl/DecompileToDir，后者含 `project=true` 项目模式）+ 元数据 13（ListTypes/Signature/Hierarchy/Dependencies/CallGraph/AssemblyInfo/InterfaceUsage/GenericInstantiation/SearchString/FieldAccess/CallChain/CacheStats/…）+ **`Debugger/`（`debug_*` 工具类 + `UiTools` UI 自动化）** + **`Screenshot/`（独立截图工具，见下）**
- `Tools/Debugger/` — **动态调试 MCP 工具面**：
  - `DebugSessionTool`：`debug_launch`/`debug_attach`/`debug_session`（action=disconnect/terminate/reset）/`debug_wait`（waitSeconds=0 即时状态，>0 等停）
  - `DebugBreakpointTool`：`debug_breakpoint`（action=set/list/remove/clear；set 支持 token / typeName+memberName / typeName+line / sourcePath+line，模块未加载=登记待绑定）
  - `DebugControlTool`：`debug_continue`/`debug_step`(kind=into/over/out)/`debug_wait`(等停点 1-300s 直接返回停点现场，超时返回当前状态不报错)
  - `DebugInspectTool`：`debug_frame`（一次给调用栈 + 当前帧变量）/`debug_threads`
  - `DebugExceptionTool`：`debug_exceptions`（action=set/clear；typeName 空=全部，精确过滤 v2）
  - 全部是 `Services.DebugSessionService.Manager` 的薄包装；**控制工具异步返回（带默认超时），不等停点**（`debug_wait` 例外：其职责就是等停点，同样带超时上限与「超时=放弃等待」约定）；栈/变量读取前置校验 `Buffer.CurrentState == Stopped`（否则提示先 debug_continue 到停点）；缺省 threadId=0 用 `Buffer.StoppedThreadId`。每个工具调用后写 `Manager.Actions.Log(...)` 供 Web 回放。
  - `UiTools`：`ui_find`/`ui_action`/`ui_input`/`ui_get`/`ui_wait`——**全 UIA 语义 pattern，无物理输入**（不移动光标/不注入输入/不抢前台）。元素定位/语义 pattern 分派/状态读取/事件等待/帧登记（`FrameRegistry`）已迁出至 **`SharpSight.UiAutomation`** 能力库，本类为薄包装。`ui_find` 头部返回 `帧: frameId=N`；`ui_action`/`ui_input`/`ui_get` 有可选 `frameId`（= `ui_find` 帧号）**旧帧护栏**——非当前帧即拒绝并提示重新 `ui_find`/`screenshot`（0=不校验）。不要求活动 debug 会话。
- `Tools/Screenshot/` — **独立截图工具面**（`DotNetDebuggerMcp.Tools.Screenshot`；独立于调试会话，不属 `debug_*`）：
  - `ScreenshotTool`：`screenshot`（**独立截图**，**不要求调试会话**；返回 `CallToolResult` 图片块双轨（内联 image 块 / 落盘只回路径、**无 image 块**）——铁律「Task<string>」的图片类例外；不走 Manager/缓存/ToolPipeline、不写 Actions）。`mode=list-displays` / `mode=list-windows` 为只读清单模式（返回文本、不返回图片），取代原独立发现工具。
  - `ScreenshotDisplaysTool` / `ScreenshotWindowsTool`：上述两种清单模式的具体实现（保留为 C# 方法，**不再是独立 MCP 工具**）：显示器清单（1 基编号/主屏/物理边界/缩放）与可见顶层窗口（hwnd/pid/标题/前台/最小化，`filter`=标题子串；不限 .NET 进程）。

- `DebugCli/` — `DebugCliRunner`：`-dbg` 一次性调试（**与 MCP debug 工具完全独立**：绕过 Manager，直连 `DebugSession.AttachAsync` + 轮询事件流），供手动验证引擎。
- `Services/` — `DebugSessionService`（静态单例包装 Session `DebugSessionManager`）、`AgentViewService`（静态包装 Web `AgentViewContext`，Revision 机制）、`CheckTool`（非 MCP 工具，CLI `-c`）、`AppServices`（Cache/Pipeline/NuGet/Updater/StatusReport 单例）、`ToolExecutor`（`ResolveAssembly`/`RunPipelineAsync`/`RunMergedAsync`/`RunToDisk`/`RunMetadata`/`RunMetadataPe`）。**本层不得反向引用 Tools**。反编译管线执行时经 `AgentViewService.Context.Update` 写「agent 正在看什么」（当前仅 hook 反编译类，调试工具尚未写）。
- `Pipeline/ToolPipeline.cs` — 共享执行管道：缓存命中 → 进程内反编译回源（同 key 并发单飞）→ lines 分页；`IsErrorResult` 为 true 抛异常不入缓存。
- `Caching/DecompileCache.cs` — 64MB LRU + 30 分钟滑动过期 + 5 分钟清理；反编译与元数据共用。
- `Formatting/OutputFormatter.cs` + `SectionBuilder.cs` — 头部信息块、`#MEMBER` 行、`（无）` 占位、行号分页。
- `Configuration/` — `AppConfig`（宿主专属常量）、`AppText`（转发 Decompiler 库 `DecompilerText`）、`FeatureFlags`（能力族开关解析：`--features`/`DOTNETDEBUGGERMCP_FEATURES`）、`ToolFeatureRegistry`（工具类型→能力族显式注册表）、`HandshakeText`（按能力族拼装握手简介）、`CacheSignatures`（缓存键前缀 + `\u001F` 分隔符，**改动必须同步 `CacheStatsTool.ToolNames`**）、`ToolParameterText`（MCP 参数级 `[Description]` 模板常量，Description 要求编译期常量故全 const）。**调试工具文案目前各工具内联、未集中**——新增共享调试提示时考虑收拢。
- `Validation/ArgumentValidators.cs` — 共享校验（assembly/必填/memberName/token/list/outputDir/timeoutSeconds）。**debug 工具校验目前内联**（`TryParseToken`/`RequireStopped` 等），未走共享校验器。
- `UpdateCheck/` — NuGet 更新检查（`%LOCALAPPDATA%\DotNetDebuggerMcp\update-check.json` 磁盘缓存，成功 TTL 24h/失败 1h）。

## 入口（DotNetDebuggerMcpCmd.cs）

- 业务参数：`-a` 反编译系 / `-l -nc -ns` list_types / `-s -hc -d -cg -iu -gi -cc -ai` 元数据系 / `-ss` search_literals / `-fa -fn` field_access / `-mn -tt` decompile_member / `-tk` token 定位 / `-o [-p]` 写盘 / `-ln` 分页 / `--timeout`(默认 30) / `-c` 更新检查 / **`-dbg <exe>` + `-dbg-bp <token>` + `-dbg-offset`** 一次性调试 / **`--web` + `--web-port`** Web 模式 / `-v` `-h`。分发逻辑在 `DispatchCliAsync`（与 MCP 工具同一执行逻辑）。
- `--web` 分支（L350-360）：`WebHostBootstrap.Configure(Manager, AgentView)` → `Build(port)` → 并联 `Task.WhenAll(mcpTask, webTask)`（任一侧结束等另一侧自然完成）。
- MCP 启动分支：`builder.Logging.ClearProviders()` + `AddConsole(o => o.LogToStandardErrorThreshold = LogLevel.Trace)`——**严禁删除或改动**（stdout 只承载 MCP 协议帧；并发下 Console 日志与响应交错会撕坏帧，回归护栏 `McpSessionConcurrencyTests`）。启动期按 `FeatureFlags` 决定注册哪些能力族的工具（`ToolFeatureRegistry.EnabledToolTypes`，**未启用族不进工具目录**），并组装按族拼装的握手简介（`HandshakeText.Build`）+ 「更新状态」段（`EnvironmentChecker.BuildHandshakeText`）注入 `ServerInstructions`；未知/零能力族取值 → stderr 中文 + 非零退出。后台 fire-and-forget `RefreshIfStaleAsync`。

## 铁律速查（完整版见根 AGENTS.md）

- **所有 MCP 工具参数必须带默认值**（`string x = ""`，不声明可空）——SDK 按是否有默认值判断必填，缺默认值缺参会返回 Tool Error 而非中文提示。`[Description]` 用中文、面向 agent、**注明默认值**、不写实现细节措辞。
- **每个工具方法带 `CancellationToken cancellationToken = default`**（SDK 识别并注入、不暴露为参数、不写 Description）。反编译类放 timeoutSeconds 后、元数据类放末尾。
- 工具返回 `Task<string>`，一切错误返回中文提示文本，不抛异常。——**图片类工具例外**：`screenshot` 返回 `Task<CallToolResult>`（<2MB 附 image 块、≥2MB/指定 filePath 落盘返回路径），错误仍为纯文本 content、不设 IsError（spec §3.3，见 `docs/planning/specs/2026-09-22-screenshot-tool-design.md`）。
- 更新版本号同步三处：csproj `<Version>` + `.mcp/server.json`（顶层与 packages[0] 两处）+ CHANGELOG `[Unreleased]`。改工具面必须同步 **工具 `[Description]`（agent 在工具目录看到的唯一说明：参数/默认值/行为一变就改）** 与 **握手简介（`HandshakeText` 按能力族拼装；新增/删除能力族或独立工作流必须补/删对应触发条件；配合扩 `握手_启用族触发条件齐备` 回归断言）**——二者是 agent 的「可见」与「触发」通道，漏改等于新能力不可被发现；根 `README.md`（打包为 PackageReadmeFile）面向包使用者，也一并更新。

## 验证

```bash
dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj    # 单测（含 debug 工具端到端 / MCP+Web 共存 / 会话级并发回归）
dotnet run -c Release --project src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj   # 端到端（自启动 server，覆盖反编译/元数据工具面）
./src/DotNetDebuggerMcp/bin/Debug/net10.0-windows10.0.22621.0/DotNetDebuggerMcp.exe -dbg tests/TestData/DebugTarget.exe -dbg-bp 0x06000003   # CLI 一次性调试（改引擎后手动验证）
```

- 测试需先 `generate-testdata.ps1` 生成 `tests/TestData/*.dll`（git 忽略，CI 已自动跑脚本）。
- 关键回归：`McpSessionConcurrencyTests`（stdout 零噪声护栏）、`DebugMcpToolsTests`（真实子进程 debug_launch→断点→continue→state→stack/variables→disconnect 闭环）、`McpWebCoexistTests`（MCP+Web 同进程）、`CacheStatsToolTests`/`ToolPipelineTests` 等 AppServices collection 串行。
- 本地调试注意：根 `opencode.json` 把本仓库 MCP server 绑定到 `bin/Debug/net10.0-windows10.0.22621.0/DotNetDebuggerMcp.exe`（带 `--web` 仅为联调）；改代码后按根 `AGENTS.md`「本地调试注意」的热重载步骤生效（改 `opencode.json` 再改回即触发动态重载，无需重启 opencode），会话内 `dotnetdebugger_*` 随之反映新二进制。
