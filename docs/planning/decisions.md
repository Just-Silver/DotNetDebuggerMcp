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

## D7 · 实施节奏与文档拆分（用户拍板）
- 决策：**先引擎/MCP 后 Web**：M1–M2 专注动态调试引擎 + MCP 工具面（无 Web），跑稳后 M3 再上 WebUI。Web 设计在 M3 前单独细化。
- 决策：**总览 spec + 多份实施计划**（按里程碑拆）：一份总览设计文档 + 分阶段实施计划（阶段一 改名+仓库拆分 / 阶段二 动态调试引擎 / 阶段三 MCP 工具面 / 阶段四 WebUI），每阶段独立落地交付。
- 理由：超大计划降低单文档规模与跨会话上下文压力；每阶段可独立 review/交付。
- 日期：2026-09-05。

## D8 · 解决方案项目拆分（用户拍板）
- 决策：**5 项目（一个 exe + 4 库）**，进程永远是一个宿主 exe；拆的是程序集。MCP 与 Web 不拆进程（共享会话）。
  | # | 程序集 | 类型 | 职责 | 依赖 |
  |---|---|---|---|---|
  | 1 | `DotNetDebugger.Decompiler` | 库 | 反编译/静态分析（现 ILSpyMcp 全量迁入） | 无内部依赖 |
  | 2 | `DotNetDebugger.Engine` | 库 | 调试引擎底层：ClrDebug 封装、断点/步进/栈/值/异常 | ClrDebug、DbgShim |
  | 3 | `DotNetDebugger.Session` | 库 | 会话服务：命令串行化 + DebugEvent 事件总线 + 状态快照 + agent 轨迹日志 + token/IL→反编译行映射 | Engine + Decompiler |
  | 4 | `DotNetDebugger.Web` | 库 | WebUI host：Kestrel 内嵌、SSE、REST、前端静态资源 | Session + Decompiler |
  | 5 | `DotNetDebuggerMcp` | **exe (tool)** | 装配根：CLI + MCP 工具注册 + 握手 + 拉起 Session/Web | 引 1-4 |
- Session 独立价值：MCP 与 Web 共同依赖的「会话 API」层；Engine 保持纯底层可替换；agent 轨迹日志（Web 回放数据源）不污染引擎。
- 理由：进程合一避免跨进程事件同步；程序集分层清晰各层独立测试。
- 日期：2026-09-05。

## D6 · 命名决策（用户拍板）
- 决策：主项目/仓库名 **DotNet-Debugger-MCP**。用户明确：「就 DotNet-Debugger-MCP 了」（2026-09-05）。
- 完整映射（**按 D8 五项目版定稿**）：
  | 对象 | 命名 |
  |---|---|
  | GitHub 仓库 | `DotNet-Debugger-MCP`（原 ILSpyMcp，rename 保留跳转） |
  | 解决方案 | `DotNetDebuggerMcp.slnx` |
  | 子项目 1 反编译/静态分析库 | `src/DotNetDebugger.Decompiler/`，命名空间 `DotNetDebugger.Decompiler` |
  | 子项目 2 调试引擎库 | `src/DotNetDebugger.Engine/`，命名空间 `DotNetDebugger.Engine` |
  | 子项目 3 会话服务库 | `src/DotNetDebugger.Session/`，命名空间 `DotNetDebugger.Session` |
  | 子项目 4 WebUI 库 | `src/DotNetDebugger.Web/`，命名空间 `DotNetDebugger.Web` |
  | 子项目 5 宿主 exe (tool) | `src/DotNetDebuggerMcp/`，命名空间 `DotNetDebuggerMcp` |
  | 主 NuGet 包 / CLI 命令 | `dotnet-debugger-mcp`（PackAsTool；ToolCommandName 同） |
  | MCP server 注册名 | 建议 `dotnetdebugger`（工具前缀 `dotnetdebugger_*`；待实施确认） |
  | 测试 | `tests/` 各项目对应 `*.Tests`（InternalsVisibleTo 同步） |
  | Client 端到端 | `src/DotNetDebuggerMcp.Client/`（或并入宿主测试，待实施确认） |
- 理由：用户拍板；5 项目程序集（D8）+ 统一词干；反编译与调试经 Session 汇集于宿主 exe。
- 日期：2026-09-05。

## D5 · v1 动态调试引擎能力范围（用户拍板）
- 决策：**v1 = 最小但完整的 agent 调试闭环**：
  1. 会话管理：启动进程 / 附加已运行进程 / 断开
  2. 断点：按方法 token + IL offset（复用现有元数据层定位）+ continue + 命中事件
  3. 单步：step into / over / out
  4. 状态读取：线程列表 → 调用栈（每帧：方法 + IL offset → 反编译行映射）→ 局部变量/参数（标量 + 简单对象首层字段）
  5. first-chance 异常断点（类型过滤）
  6. 统一 DebugEvent 事件流（Channel），同一事件源喂 MCP 与 Web
- **v2 再上**：表达式求值（安全子集）、PDB 行断点、模块延迟绑定。
- 理由：1–6 覆盖 agent 自主调试一个 bug 的全部动作；求值/行断点是最大工作量（+1–1.5 人月），后置让闭环先转起来。
- 日期：2026-09-05。

## D4 · WebUI 技术栈（调研建议，**待用户确认**）
- 状态：**待用户确认**（2026-09-05 修正：调研结论≠已拍板；详见 open-questions.md #4）
- 建议内容：Kestrel 单进程内嵌 + **SSE**（快照+增量）+ **Monaco Editor**（read-only + deltaDecorations 断点/当前行）+ **React+TS+Vite** + 时间线自绘 / mermaid 按需。IL→反编译行映射**全部服务端**（SequencePointBuilder 思路）。
- 备选：CodeMirror 6（更轻、无 worker，C# 高亮弱）；vanilla TS。
- 注意：WebUI **实施时机**已定（D7：先引擎/MCP 后 Web，P4 再上）；**技术栈**本身仍是开放项。
