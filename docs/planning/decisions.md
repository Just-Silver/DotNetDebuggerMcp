# 决策记录（DECISIONS）

> 最新在上。每项记录「决策 / 理由 / 日期 / 来源(会话)」。回答开放问题后把结论移入此处。

## D1 · 三项目模块化拆分（用户拍板）
- 决策：现 ilspy（反编译/静态分析）**改名保留为子项目 A**；**新增子项目 B 动态调试引擎**（dnSpyEx 式，先实现）；**主项目作为对外 MCP 服务 + 拉起 WebUI** 渲染 agent 主导的调试过程；主项目引用两个子项目，模块化开发。先实现动态调试，再做主项目整合。
- 理由：反编译功能已完善，只缺动态调试；模块化便于独立演进与复用（CLI/测试/CI 各自独立）。
- 日期：2026-09-05。

## D2 · 规划文档落盘拆分策略（用户拍板）
- 决策：超大型计划配套调研/决策/计划文档**持续落盘**；**按主题拆多文件**（本目录 + research/），单文件不无限膨胀；README.md 作导航地图。
- 理由：防止会话上下文丢失；多文件便于外部引用与分工。
- 日期：2026-09-05。

## D3 · 动态调试技术主通道（调研结论，待用户最终确认）
- 决策（建议）：`ClrDebug (MIT)` + `Microsoft.Diagnostics.DbgShim (MIT)` + 自研 ICorDebug 事件循环/断点/栈帧/值树/求值子集。dnSpyEx(GPL)/debug-mcp(AGPL) 只 clean-room 参考不链接不抄码。
- 理由：活动调试唯一下层通道是 ICorDebug；ClrDebug 是 MIT 全量 COM 封装底座；自研量级被 debug-mcp/sharpdbg 证明可行（1 人维护规模）。
- 状态：**待用户确认**（open-questions.md #1）。
- 日期：2026-09-05。

## D4 · WebUI 技术栈（调研结论，待用户最终确认）
- 决策（建议）：Kestrel 单进程内嵌 + **SSE**（快照+增量）+ **Monaco Editor**（read-only + deltaDecorations 断点/当前行）+ **React+TS+Vite** + 时间线自绘 / mermaid 按需。IL→反编译行映射**全部服务端**（SequencePointBuilder 思路）。
- 状态：**待用户确认**（open-questions.md #3）。
- 日期：2026-09-05。
