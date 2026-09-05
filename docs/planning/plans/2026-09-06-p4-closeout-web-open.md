# P4-2 收尾：web_open 幂等工具 + 默认去 --web（Implementation Plan）

> **进行中**（2026-09-06，分支 master——实际工作一直在 master，规划文档原写 feature/p4-monitor 系笔误，归档时更正）：P4-2 功能项全部完成后，本计划为最后一块拼图——agent 按需开 Web + 规划文档收尾 + P5 发布准备。

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans. 每个任务完成后运行验证再提交；提交信息用中文。

**Goal:** MCP server 默认不再带 `--web`；agent 经幂等 `web_open` MCP 工具按需打开 Web 监视器。完成 P4-2 关账（规划文档归档）与 P5 发布准备。

**Architecture:** Web host 与 MCP server 同进程并联（`Task.WhenAll`）。幂等启动收敛到 `DotNetDebugger.Web.WebHostBootstrap.EnsureStartedAsync`（静态单例状态与进程生命周期对齐：`SemaphoreSlim` 守卫 + 双重检查，缓存已启动的 `WebApplication` 与实际 URL；`TryOpenBrowser` 在一次性块内 → 浏览器只拉起一次；启动失败不写状态可重试）。`--web` 分支与 `web_open` 工具共用该入口，混用不双启。

**Tech Stack:** ModelContextProtocol SDK（`[McpServerToolType]`/`[McpServerTool]`，工具名 snake_case：`WebOpen` → `dotnetdebugger_web_open`）、ASP.NET Core `WebApplication`、McMaster CLI（`--web`/`--web-port` 保留）。

**Spec:** `docs/planning/open-questions.md` #7 待办段；产品方向出处 `src/DotNetDebuggerMcp/TODO.md`。

## Global Constraints

- stdout 只承载 MCP 协议消息：`DotNetDebuggerMcpCmd.OnExecuteAsync` MCP 启动分支与 `WebHostBootstrap.Build` 的日志纪律（`ClearProviders` + stderr）严禁改动；web_open 在 MCP 运行中起 Kestrel，日志仍必须全走 stderr。
- MCP 工具铁律：参数带默认值（不声明可空）、`[Description]` 中文注明默认值、末尾 `CancellationToken cancellationToken = default` 不写 `[Description]`、返回 `Task<string>` 错误返回中文提示不抛异常。
- 根 README 与代码改动同 commit（打包快照）。
- 每个 Task 结束提交；提交信息用中文。

## Tasks

- [ ] Task 0：Web TODO 注记 watch 表达式「暂不做」决策（人类专属；agent 无持久监视需求，求值走 v2 安全子集）
- [ ] Task 1：`WebHostBootstrap` 幂等启动改造——`EnsureStartedAsync(int port = 0)` + `PreferredPort` + `IsStarted`/`CurrentApp`/`CurrentUrl`；`RunWithBrowserAsync` 并入（唯一调用方是 Cmd）；Cmd `--web` 分支改走幂等入口（`WhenAll` 语义保持）
- [ ] Task 2：新增 `Tools/Web/WebOpenTool.cs`（`web_open`，port 缺省 0，幂等提示区分首次/命中，`Actions.Log("web_open")` 记轨迹）；`AppText.HandshakeFeatureIntro` 何时使用段加 web_open 触发条件
- [ ] Task 3：`opencode.json` 去 `--web`；根 README（简介/工具一览/Web 小节/CLI 参数表）；CHANGELOG `[Unreleased]` 加 web_open 条目（行为变化：server 默认不开启 Web）+ 修正引擎条目（单步/变量展开终态描述）
- [ ] Task 4：验证——build Release + 宿主全量单测 + Client 端到端 + `McpSessionConcurrencyTests` + 裸 stdio 握手 stdout 噪声 0 + 幂等手动验证（连续 web_open ×2、`--web` 混用）
- [ ] Task 5：P4-2 规划文档收尾——`plans/2026-09-05-p4-2-webui.md` 归档 `archive/plans/`；`docs/planning/README.md` P4-2 状态改已完成；`open-questions.md` #7 折叠已解决；根 `AGENTS.md` 「P4-2 进行中」同步
- [ ] Task 6：P5 发布准备——版本三处 1.4.0→1.5.0（csproj / server.json ×2）+ CHANGELOG `[Unreleased]` 转 `## [1.5.0] - <日期>` + 全量回归；**tag/Release/NuGet 发布待用户确认**
