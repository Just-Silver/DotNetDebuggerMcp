# 决策记录（DECISIONS）

> 最新在上。每项记录「决策 / 理由 / 日期 / 来源(会话)」。回答开放问题后把结论移入此处。

## D13 · P4 Web 定位澄清 + #2/#3 高层编排参考 PTC（用户 2026-09-05）
- **产品定位澄清（用户）**：反编译与动态调试都是给 **agent** 用的，人类经对话与 agent 交互；**WebUI 主要目的是可视化 agent 在干什么**（监视器，非人工操作台）。因此 #2「Web 页内反编译+调试联动」应重新理解为 **agent 动作实时可视化**（agent 反编译了什么/断点设哪/停在哪，Web 同步画出），而非人工在 Web 上点按钮。
- **#3 高层编排工具参考（用户点名，防忘）**：DeepSeek Harness 的 **PTC 模式**（Programmatic Tool Calling，即 Code Mode）与 Anthropic Advanced Tool Use / OpenAI Responses API 同款能力——**模型写一段程序（TS/Python）批量编排工具调用**（循环/分支/汇总/过滤），工具表折叠成一个 `run_code`，其余工具作为生成的 SDK。效果：N 次往返 → 1 次代码执行，token 降 17-37%（模型只返回自己 curate 的结果）。
- **重要边界**：PTC 是**宿主（opencode/DeepSeek Harness 侧）的呈现模式**，非 MCP server 职责——我们的 server 提供工具，宿主决定怎么呈现。若我们自己做「高层编排」，应参考 PTC 思路提供**批量/组合型工具**（如一次反编译+签名+断点定位汇总），但需实践评估是否正优化（生成代码 token > 多轮调用即负优化，腾讯云拆解文章实证）。
- 待办：#2（agent 动作可视化）与 #3（高层编排，参考 PTC）均未实现，P4.2 或后续；事件日志/agent 轨迹时间线同列为可选待办。
- 日期：2026-09-05。

## D12 · P3 调试工具面与握手简介（用户拍板）
- 决策：调试 MCP 工具用 `debug_*` 前缀（debug_launch/attach/disconnect/state/breakpoint_*/continue/step/stack/threads/variables/exceptions）。
- 决策：控制工具（launch/continue/step）**异步返回 + 默认 timeoutSeconds 参数**；停点信息经查询工具（debug_state/debug_stack/debug_variables）获取，控制工具不等停。
- 决策：**握手 ServerInstructions 改为触发条件导向**——去掉「工具一览」，只留「## 何时使用」（反编译/调试两类场景触发条件），agent 经 MCP 工具目录发现工具。
- 日期：2026-09-05。

## D11 · P2 测试目标与验证形态、交付边界（用户拍板）
- 决策：**P2 单测调试目标 = generate-testdata.ps1 追加生成 `DebugTarget.exe`**（含固定方法/断点锚点/按需跑固定逻辑等待断点；沿用脚本生成+git 忽略模式，token 稳定可预测）。
- 决策：**Engine 验证形态 = 测试进程直接 attach**（xUnit 测试进程作为调试器宿主，启动 DebugTarget 子进程并 attach；符合真实形态：宿主进程调 ClrDebug attach 子进程）。
- 决策：**P2 交付边界 = Engine 库 + 单测 + CLI 驱动调试命令**（宿主 `-dbg` 子命令供手动验证；MCP 调试工具面留 P3）。
- 日期：2026-09-05。

## D10 · P1 测试样本命名空间保留（用户拍板）
- 决策：tests/TestData 样本命名空间 `ILSpyMcp.Samples`/`ILSpyMcp.SamplesExt` 与 dll 名 `ILSpyMcp.TestSamples(.Ext).dll` **保留不变**（后续再改）。产品代码/配置已全部脱钩新名；测试样本是虚构隔离程序集，不影响脱钩目标。
- 理由：改动面最小、回归风险低（改名需动 40 文件/312 处断言 + 重新生成 dll）。
- 后续改时联动：generate-testdata.ps1、Client Cases、Tests 全部 `ILSpyMcp.Samples*` 字符串、TestDataPaths/TestDataHelper/TestAssemblyWriter、`.gitignore` 无关。
- 日期：2026-09-05。

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

## D9 · P1 执行策略（用户拍板）
- 决策：**同仓重建**——在现有 git 仓库内新建目标 5 项目结构（`src/DotNetDebugger.Decompiler`、`src/DotNetDebugger.Engine`、`src/DotNetDebugger.Session`、`src/DotNetDebugger.Web`、`src/DotNetDebuggerMcp`），源码按归属拷贝进对应新项目并一次性替换命名空间，编译+全量测试+Client 端到端验证绿后删旧 `src/ILSpyMcp/`、`src/ILSpyMcp.Client/`、`tests/ILSpyMcp.Tests/` 结构。
- 理由：保留 git 历史与既有配置；避开「活结构上做手术」的中间态；从第一天就是干净 5 项目布局，编译错误直指目标文件。
- 执行顺序：**先重建目标结构并验证，再删旧**（不是边改边拆）。
- 日期：2026-09-05。

## D8 · 解决方案项目拆分（用户拍板）
- 决策：**5 项目（一个 exe + 4 库）**，进程永远是一个宿主 exe；拆的是程序集。MCP 与 Web 不拆进程（共享会话）。
  | # | 程序集 | 类型 | 职责 | 依赖 |
  |---|---|---|---|---|
  | 1 | `DotNetDebugger.Decompiler` | 库 | 反编译/静态分析（现 ILSpyMcp 全量迁入） | 无内部依赖 |
  | 2 | `DotNetDebugger.Engine` | 库 | 调试引擎底层：ClrDebug 封装、断点/步进/栈/值/异常 | ClrDebug、DbgShim |
  | 3 | `DotNetDebugger.Session` | 库 | 会话服务：命令串行化 + DebugEvent 事件总线 + 状态快照 + agent 轨迹日志 + token/IL→反编译行映射 | Engine + Decompiler |
  | 4 | `DotNetDebugger.Web` | 库 | WebUI host：Blazor Server + BootstrapBlazor + Monaco 互操作（宿主 exe 内嵌 Kestrel） | Session + Decompiler |
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

## D4 · WebUI 技术栈（定稿：Blazor Server + BootstrapBlazor + Monaco 互操作）
- 状态：**已定稿**（2026-09-05）。
- 决策：
  1. **Web 前端 = Blazor Server + BootstrapBlazor 组件库**（纯 .NET 全栈，替代 React/SSE 组合 A；用户选择，理由：.NET 生态、有 BB skills）。
  2. **代码视图 = Monaco 作 Blazor JS 互操作组件**（用户拍板）：C# 侧封装，JS 只作 Monaco 宿主；read-only + deltaDecorations 断点/当前行/滚动定位；可参考现成封装（BlazorMonaco）；不引 React/Vite，JS 资产预编译静态托管。
  3. **推送 = Blazor Server SignalR 电路**承载调试事件（Session Channel → 组件刷新），省去自研 SSE+快照协议。
- 已查证（bb-llms，2026-09-05）：BB 无代码编辑器/高亮组件（editor→EditorForm；code/highlight 空；textarea→Textarea）。
- 遗留：各面板具体用哪些 BB 组件 → P4 细化 spec 时定；事件→Blazor 刷新机制（IAsyncEnumerable/订阅）P4 细化。
- 日期：2026-09-05。

## D7 · 实施节奏与文档拆分（用户拍板）
