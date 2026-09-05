# 开放问题（OPEN QUESTIONS）

> 最新在上。澄清后把「问题+结论」移入 decisions.md。

## #3 WebUI 技术栈确认
- 状态：**待澄清**
- 问题：WebUI 采用「Kestrel 内嵌 + SSE + Monaco + React/TS/Vite」（调研组合 A）是否可行？还是你倾向更轻（CodeMirror 6 / vanilla）？WebUI 与调试引擎是否按「同一事件总线的两个投影」（MCP 控制面 + Web 展示面）设计？
- 背景：见 research/04 + decisions D4。

## #2 命名决策
- 状态：**待澄清**
- 问题：仓库名 / NuGet 包名 / CLI 工具名 / MCP server 注册名最终定哪个？（候选讨论见 01-vision-and-scope.md §7）
- 子问题：
  - 主项目候选：`DotNet-Tools-MCP`？风险是与官方 `dotnet tool` 全局工具生态混淆，且未表达反编译+调试定位。是否接受？（我建议候选：`dotnet-mcp` / `dotnet-debug-mcp` / `dnstool-mcp`…可再议）
  - 子项目 A（原 ILSpyMcp 反编译）候选名？
  - 子项目 B（调试引擎）候选名？
  - 重命名是否含 git 仓库迁移（GitHub rename 保留跳转）？NuGet 包弃用旧名策略（1.4.0 已发布）？
- 背景：用户候选 `DotNet-Tools-MCP`（01 §7）。

## #1 动态调试引擎实现路线确认
- 状态：**待澄清**
- 问题：① 采用推荐路线（ClrDebug 底座 + 自研事件循环/断点/值树，MIT）？② 还是你希望先做「技术 spike」验证 ICorDebug 通道在本机 Windows + net10 的最小闭环（附加→断点→命中→读栈）再拍板？③ 是否接受 1–2 周先出 CLI 驱动的最小引擎（M1），而非一步到位 MCP 工具？
- 背景：见 research/01 + decisions D3。

## #0（已解决）大重构先建分支 + 计划持久化
- 状态：**已解决**（2026-09-05）→ 分支 `plan/dynamic-debugging-and-rename`；规划文档 `docs/planning/` 多文件拆分 + git 提交。
