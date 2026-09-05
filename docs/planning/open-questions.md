# 开放问题（OPEN QUESTIONS）

> 最新在上。澄清后把「问题+结论」移入 decisions.md。
> 已解决条目已折叠为摘要（见 decisions.md 对应 D#）；**#7（P4-2 WebUI 待办）为当前活跃区**。

## #7 P4-2 Web 监视器树/联动待办（2026-09-05，agent 联调中暴露，待后续解决）
- **TypeTree 树组件**：
  - 已改数据驱动全量 Items（无懒加载时序坑）+ 虚拟滚动 `IsVirtualize`；`style="height:100%"` 官方写法（EnableKeyboard 示例范式）。
  - **待解决**：① 虚拟滚动下编程式选中深层节点（LoadAssembly 首次后 SetActiveItem）UI 不跳——稳定态 MCP 驱动可跳、首次 pending 补选中可 active/展开但**滚动不到顶部**；疑似 BB 虚拟滚动只渲染可视区、scroll js 找不到未渲染 active 行。曾用 `_pendingActive`（渲染后补）+ `_pendingScrollOnce`（再一轮 SetActiveItem 触发滚动）缓解，未完全验证。② `IsVirtualize` 与深层编程跳转可能根本冲突，需查 BB 官方/issue 是否有虚拟滚动下选中滚动正解，或接受非虚拟（跳转可靠，大树渲染量按 IsExpand 链可控）。
  - 已用机制：OnReady 回调（TypeTree 首渲后通知宿主）+ SelectTypeAsync 等 `_ready` TCS + pending 渲染后补选中。
- **首页默认直达**：WebHostBootstrap 拉浏览器带 `/debugger`（RunWithBrowserAsync `{url}/debugger`），首页保留说明入口。
- **待办 bug**：① 断点红点未在 Monaco 代码编辑器显示（glyph 区）。② 刷新后代码编辑器内容丢失（Blazor 组件状态重置，_doc 未持久化）。③ `JSDisconnectedException` 在电路断开时 Monaco 调用未 try/catch（无害但刷日志，已见 LogPanel）。
- **agent 动作事件时序**：Blazor Server 页面未开/电路未建立时 AgentView.Changed 无订阅者事件丢失；页面晚开靠 OnReady 补同步 AgentView.Snapshot。冷启动空树（产品：无 agent 动作不预加载）。
- **待办**：razor 双文件拆分（Debugger.razor + Debugger.razor.cs code-behind，Blazor 规范）；MCP server 不应默认 --web（产品方向：agent 调幂等 `web_open` 工具按需开，未实现——opencode.json 现仍带 --web 仅为联调）。

## #6 WebUI 代码视图与推送通道（已解决 2026-09-05）
- 已解决，见 decisions D4：Monaco 作 Blazor 互操作组件、推送走 Blazor Server SignalR 电路、无 React/Vite/SSE。残留（面板 BB 组件分工、事件→Blazor 刷新机制）已并入 P4 spec，不再单列。

## #4 WebUI 技术栈方向（已解决 2026-09-05）
- 已解决，见 decisions D4 定稿：Blazor Server + BootstrapBlazor + Monaco 互操作。

## #5 子项目库名与项目拆分（已解决 2026-09-05）
- 已解决，见 decisions D6/D8：5 项目拆分（Decompiler/Engine/Session/Web/McpHost exe，进程合一）；Client 端到端落地为 `src/DotNetDebuggerMcp.Client/`。

## #2 命名决策（已解决 2026-09-05）
- 已解决，见 decisions D6：主项目/仓库名 **DotNet-Debugger-MCP**（01-vision §7 已更新）。

## #1 动态调试引擎实现路线（已解决 2026-09-05）
- 已解决，见 decisions D3/D5：ClrDebug(MIT) + DbgShim(MIT) + 自研引擎，v1 最小闭环（实现细节与 spike 见 archive/plans/2026-09-05-p2-engine-v1.md）。

## #0 大重构先建分支 + 计划持久化（已解决 2026-09-05）
- 已解决：分支 `plan/dynamic-debugging-and-rename`；`docs/planning/` 多文件拆分 + git 提交。
