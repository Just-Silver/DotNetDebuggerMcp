# 开放问题（OPEN QUESTIONS）

> 最新在上。澄清后把「问题+结论」移入 decisions.md。
> 已解决条目已折叠为摘要（见 decisions.md 对应 D#）；**#7（P4-2 WebUI 待办）为当前活跃区**。

## #7 P4-2 Web 监视器树/联动待办（2026-09-05，agent 联调中暴露）
- **TypeTree 树组件**：
  - 已改数据驱动全量 Items（无懒加载时序坑）；`style="height:100%"` 官方写法（EnableKeyboard 示例范式）。
  - ~~① ② 虚拟滚动下编程式选中深层节点滚动不到位~~（**已解决 2026-09-06**）：BB 源码实锤根因——`SetActiveItem` 的 scroll js 在 `OnAfterRenderAsync` 跑一次 `querySelector(".tree-content.active")`，而 `<Virtualize>` **异步**渲染可视区行，active 行尚未进 DOM 必然找空。按预案放弃 `IsVirtualize`（非虚拟同步渲染全部展开行，scroll 必中；DOM 量按 IsExpand 链可控，默认全收缩极小）。停点跟随与刷新恢复两条路径浏览器实测均滚动到位。
  - 已用机制：OnReady 回调（TypeTree 首渲后通知宿主）+ SelectTypeAsync 等 `_ready` TCS + pending 渲染后补选中。
- **首页默认直达**：WebHostBootstrap 拉浏览器带 `/debugger`（RunWithBrowserAsync `{url}/debugger`），首页保留说明入口。
- **已修复（2026-09-06，浏览器实测通过）**：
  - ~~断点红点未在 Monaco 显示~~：Engine/Session 新增 `GetBreakpointsAsync`（经命令泵读快照），Web 侧 `ApplyDecorationsAsync` 统一渲染 断点红点 + 停点当前行（断点 IL→行映射，签名变化才重推装饰）。
  - ~~刷新后代码编辑器内容丢失~~：`DocumentStore` 改 DI 单例（跨电路存活）+ `LastView` 记录最近查看；`OnTreeReady` 恢复优先级 agent 快照 > LastView（注意空快照 Revision=0 会吞 else 分支，已按「快照无内容再落 LastView」实现）。
  - ~~`JSDisconnectedException` 刷日志~~：CodeViewer.razor 互操作边界统一吞 `JSDisconnectedException`/`ObjectDisposedException`（官方指引），其它异常照常上抛。
  - **实测追加修复（第 4 个 bug）**：Monaco `create` 在 `monaco` 全局未就绪时静默返回且永不重试（AMD editor.main.js 异步初始化晚于 script 标签），编辑器永久空白、装饰/内容全失效——桥改为轮询重试创建 + `setValue` 先到时暂存文本建后回放。
  - ~~停点跟随要求 agent 恰在看命中模块~~：Engine 新增 `GetModulePathAsync`（模块短名→全路径），Web 停点无条件跟随（跨模块时 `FindTypeByToken` 由 token 解析类型整页切换）。
  - ~~类型树与 Monaco 单向联动~~：双向联动落地——编辑器光标行经 `DocumentStore.FindMethodTokenAtLine`（方法行区间映射）联动树选中；仅派发**用户交互后**的光标事件（`onMouseDown/onKeyDown` 置粘性标记；`hasTextFocus` 门禁实测误伤点击已弃），setValue 程序性光标移动由桥侧抑制一次。
- **agent 动作事件时序**：Blazor Server 页面未开/电路未建立时 AgentView.Changed 无订阅者事件丢失；页面晚开靠 OnReady 补同步 AgentView.Snapshot。冷启动空树（产品：无 agent 动作不预加载）。
- **待办**：MCP server 不应默认 --web（产品方向：agent 调幂等 `web_open` 工具按需开，未实现——opencode.json 现仍带 --web 仅为联调；见 `src/DotNetDebuggerMcp/TODO.md`）。
- **规划流程待办**：本计划完成后 `plans/2026-09-05-p4-2-webui.md` 归档 `archive/plans/` + 勾选 checkbox + 更新 `README.md` 状态行；`feature/p4-monitor` 分支本地不存在（实际工作在 `master`），规划文档与实际分支不符需澄清。

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
