以下计划按优先级执行，每个 Task 单独提交（中文提交信息）。先落地为计划文档 `docs/planning/plans/2026-09-06-p4-closeout-web-open.md`（按现有计划文档格式：Goal/Architecture/Global Constraints/Task checkbox），随首次提交。

## Task 0：P3 决策记录（1 分钟）
`src/DotNetDebugger.Web/TODO.md` 的 watch 表达式条目注记：**暂不做**（决策 2026-09-06：Web 面板为人类监视器，watch 输入人类专属；agent 侧用 `debug_variables` 每停点即取最新值，无持久监视需求；agent 可用的求值走 v2 表达式求值安全子集）。

## Task 1：`WebHostBootstrap` 幂等启动改造（P1 核心）
- 新增 `EnsureStartedAsync(int port = 0)`：`SemaphoreSlim` 守卫 + 双重检查（快路径无锁读 `_app`；慢路径锁内复查后 Build/Start 一次），静态字段缓存 `WebApplication` 实例与实际 URL（归属 Web 库 bootstrap 层，进程生命周期对齐，无失效问题）；`TryOpenBrowser` 在一次性块内 → 浏览器只拉起一次；启动失败不写状态、返回中文提示、可重试。
- 新增静态 `PreferredPort`（默认 0=自动选空闲端口）：Cmd 侧解析 `--web-port` 后写入，web_open 工具缺省读它。
- 重构 `DotNetDebuggerMcpCmd.cs` 的 `--web` 分支改走同一 `EnsureStartedAsync`（`--web` 与 `web_open` 收敛同一入口，混用不双启；MCP+Web 并联 `Task.WhenAll` 语义保持；MCP 启动分支的 stdout 纪律代码严禁改动）。

## Task 2：新增 `WebOpenTool`（P1）
- 新建 `src/DotNetDebuggerMcp/Tools/Web/WebOpenTool.cs`：`[McpServerToolType]`，方法 `WebOpen([Description("...")] int port = 0, CancellationToken cancellationToken = default)`，参数带默认值、中文 Description 注明默认值；返回 `Task<string>` 中文提示（首次「Web 已启动 <url>」/ 幂等命中「Web 已在运行 <url>」）；错误返回中文提示不抛异常；经 `DebugSessionService.Manager.Actions.Log("web_open", ...)` 记 agent 轨迹（Web 时间线可见）。
- 同步 `AppText.HandshakeFeatureIntro` 工具一览（握手简介铁律）。

## Task 3：默认去 `--web` + 配置/文档同步（P1）
- `opencode.json`：MCP 绑定命令去掉 `--web`（保留 exe 路径；改完需重启 opencode 才生效，属已知事项）。
- 根 `README.md`（与代码同 commit 铁律）：工具一览加 `web_open`；「Web 调试展示面」小节改写为 web_open 按需开启 + `--web` 保留为手动模式；CLI 参数表 `--web` 描述同步。
- `CHANGELOG.md` `[Unreleased]`：Added 加 web_open 条目；记录 server 默认不再自动起 Web 的行为变化。

## Task 4：验证（P1）
- `dotnet build -c Release` + 宿主全量单测 + Client 端到端。
- 并发护栏回归：`McpSessionConcurrencyTests` + 裸 stdio 握手 stdout 噪声为 0（web_open 在 MCP 运行中启动 Kestrel，重点验证日志仍全走 stderr）。
- 手动验证：连续调 `web_open` 两次幂等（第二次不重复起 Kestrel、不重复开浏览器）；`--web` 与 `web_open` 混用不双启。

## Task 5：P4-2 规划文档收尾（P2）
- `docs/planning/plans/2026-09-05-p4-2-webui.md` 归档至 `archive/plans/`；`docs/planning/README.md` 状态行 P4-2 改「已完成」；`open-questions.md` #7 折叠为已解决（含 web_open 落地记录）；分支不符澄清：规划写的 `feature/p4-monitor` 实际工作全在 `master`，在归档注记中说明。根 `AGENTS.md` 的「P4-2 进行中」表述同步。

## Task 6：P5 发布准备（P4）
- 版本三处同步：csproj `<Version>` 1.4.0→1.5.0、`server.json`（顶层 + packages[0]）、`CHANGELOG.md` `[Unreleased]` 转 `## [1.5.0] - <日期>`。
- 发布前回归：Engine + 宿主测试全量 + Client 端到端 + README 打包快照核对。
- **打 tag / GitHub Release / NuGet 发布本身是对外动作，准备就绪后停下等你确认再执行。**