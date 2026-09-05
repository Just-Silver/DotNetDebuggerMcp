# 开放问题（OPEN QUESTIONS）

> 最新在上。澄清后把「问题+结论」移入 decisions.md。

## #2 命名决策（仍在最前）
- 状态：**待澄清**（最新会话继续）
- 问题：仓库名 / NuGet 包名 / CLI 工具名 / MCP server 注册名最终定哪个？（候选讨论见 01-vision-and-scope.md §7）
- 子问题：
  - 主项目候选：`DotNet-Tools-MCP`？风险是与官方 `dotnet tool` 全局工具生态混淆，且未表达反编译+调试定位。是否接受？（我建议候选：`dotnet-mcp` / `dotnet-debug-mcp` / `dnstool-mcp`…可再议）
  - 子项目 A（原 ILSpyMcp 反编译）候选名？
  - 子项目 B（调试引擎）候选名？
  - 重命名是否含 git 仓库迁移（GitHub rename 保留跳转）？NuGet 包弃用旧名策略（1.4.0 已发布）？
- 背景：用户候选 `DotNet-Tools-MCP`（01 §7）。

## #1 动态调试引擎实现路线确认
- 状态：**方向已确认**，实施细节待设计（见 decisions D3/D5）
- 结论摘要：技术路线 = ClrDebug(MIT) + Microsoft.Diagnostics.DbgShim(MIT) + 自研引擎（clean-room 参考 dnSpy dndbg/Impl 协议）；dnSpyEx(GPL)/debug-mcp(AGPL) 只读不链不抄；v1 = 最小闭环（decisions D5）；包清单 research/05。
- 待澄清残留：无重大项。spike（ClrDebug 最小验证）建议在正式开工前或作为 M1 第一步执行以降低风险。

## #0（已解决）大重构先建分支 + 计划持久化
- 状态：**已解决**（2026-09-05）→ 分支 `plan/dynamic-debugging-and-rename`；规划文档 `docs/planning/` 多文件拆分 + git 提交。
