# TODO（DotNetDebugger.Web 近期待办）

> 近期待办，完成一项删一项；P4-2 过程性待办以 `docs/planning/open-questions.md` #7 为准；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

- [ ] **体验增强**（可选，不阻塞 P4-2 收尾）：~~变量名显示~~（已完成 2026-09-06）；~~agent 轨迹时间线~~（已完成 2026-09-06：`AgentTimeline` 组件，`AgentActionLog.Changed` 推送）；~~对象/数组展开~~（已完成 2026-09-06：引擎一级 Children + `DebugVarRow` 递归渲染）；watch 表达式输入——**暂不做**（决策 2026-09-06：Web 面板为人类监视器，watch 输入人类专属；agent 侧经 `debug_variables` 每停点即取最新值，无持久监视需求；agent 可用的表达式求值走 ROADMAP v2「表达式求值安全子集」，若日后做优先做 `debug_watch` MCP 工具而非 UI 输入）。
