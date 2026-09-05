# 大重构规划目录（ILSpyMcp → DotNet 静态/动态分析 MCP 套件）

> **本目录用途**：持久化「ILSpyMcp 大重构」的调研资料、愿景、决策与计划，防止会话上下文丢失。
> 所有文档用简体中文，随进展持续更新并提交 git。规划分支：`plan/dynamic-debugging-and-rename`。
> 日期基准：2026-09-05 启动。

## 文档地图

| 文件 | 内容 |
|---|---|
| `01-vision-and-scope.md` | 愿景：三项目模块化拆分（静态分析 / 动态调试 / 主 MCP+Web）、范围、约束、里程碑草图、命名候选 |
| `research/01-debugger-tech-landscape.md` | **动态调试依赖库调研**：四路线能力/许可/工作量对比 + 推荐组合 |
| `research/02-dnspy-source-structure.md` | 本地 `E:\Code\Projects\Externals\dnSpy` 源码摸底：调试栈能否作为库嵌入 |
| `research/03-ilspy-source-structure.md` | 本地 `E:\Code\Projects\Externals\ILSpy` 源码摸底：反编译库/调试映射能力 |
| `research/04-webui-realtime-stack.md` | **Web 实时渲染技术调研**：代码视图 / SSE / 前端框架 / IL→行映射 |
| `decisions.md` | 决策记录（最新在上）：已拍板的关键决策与理由 |
| `open-questions.md` | 开放问题清单（最新在上）：待澄清/待调研，回答后移入 decisions.md |

## 当前状态

- **阶段**：调研与澄清中（brainstorming 前半程）。分支已建，调研资料已落盘。
- **已确认方向**（用户拍板）：
  1. 现有 ilspy（反编译/静态分析）改名保留，作为独立子项目/模块。
  2. 新增动态调试（dnSpyEx 式）子项目，先实现调试，再整合。
  3. 主项目作为对外 MCP 服务 + 拉起 WebUI 实时渲染「agent 主导调试过程」。
  4. 模块化开发，主项目引用两个子项目。
  5. 本机已有源码：`E:\Code\Projects\Externals\ILSpy`、`E:\Code\Projects\Externals\dnSpy`。
- **待定大项**：命名（见 `01-vision-and-scope.md` §7）、调试引擎技术路线确认、MCP 工具面设计、Web UI 设计、实施计划拆分。
