# TODO（DotNetDebugger.Web 近期待办）

> 近期待办，完成一项删一项；P4-2 过程性待办以 `docs/planning/open-questions.md` #7 为准；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

- [ ] **TypeTree 虚拟滚动深层跳转**（P4-2 最棘手）：编程式选中深层节点 active/展开生效但**滚动不到位**（疑似 BB 虚拟滚动只渲染可视区，scroll js 找不到未渲染 active 行）。两条路：查 BB 官方/issue 正解，或放弃 `IsVirtualize`（跳转可靠，渲染量按 `IsExpand` 链可控）。详见 `docs/planning/open-questions.md` #7。
- [ ] **类型树 ↔ Monaco 双向联动**：当前只有 树→编辑器（点树节点切文档）单向；补反方向——编辑器光标移动（`onDidChangeCursorPosition`，经 CodeViewer 桥加事件回调）取当前行，用 `DocumentStore.GetIlStartAtLine` 的行→(token, ilStart) 反向映射定位方法叶子，联动树选中（幂等：已是选中方法则跳过，防打字式扰动）。
- [ ] **体验增强**（可选，不阻塞 P4-2 收尾）：调试面板加字段（对象/数组展开查看、watch 表达式输入）；事件日志 / agent 轨迹时间线；Monaco glyph 区点击手动设/删断点（当前红点只读展示）。
