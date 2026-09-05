# 调研 · Web 实时渲染技术选型（agent 主导调试过程可视化）

> 主题：主项目内嵌 WebUI，实时渲染「agent 主导的 .NET 动态调试过程」，观感对齐 dnSpyEx 调试窗口但以展示/回放为主。服务端 Kestrel 内嵌，本机 localhost。日期：2026-09-05。

## 结论速览（推荐组合）
| 层 | 主选 | 备选 | 不建议 |
|---|---|---|---|
| 服务端 | ASP.NET Core / Kestrel 单进程内嵌（静态资源 + REST + SSE 同端口） | — | 前后端两端口 |
| 实时推送 | **SSE**（REST 全量快照 + SSE 增量） | SignalR | 裸 WebSocket |
| 前端框架 | **React 18/19 + TS + Vite**（zustand 可选） | Vue 3；极简可 vanilla | 复杂多面板用无框架 |
| 代码视图 | **Monaco Editor**（read-only + deltaDecorations） | CodeMirror 6 | Shiki+自绘（仅纯静态演示） |
| 图/时间线 | 时间线自绘 DOM 列表；时序/调用图 mermaid.js 按需动态加载 | — | 重型图形库做时序 |

一句话：**调试画面是「读多写极少」的单向推送 + 编辑器装饰问题；SSE + Monaco（装饰模式照抄 VS Code/Theia）是拿到 dnSpy 观感的最短路径；真正的难点在「method token + IL offset → 反编译源码行」映射，全部在服务端解析后再推浏览器。**

## 1. 代码视图组件：Monaco vs CodeMirror 6 vs 更轻

| 维度 | Monaco | CodeMirror 6 | Shiki+自绘虚拟行 |
|---|---|---|---|
| 本质 | VS Code 编辑器内核 | 模块化可裁剪编辑器 | 纯语法高亮非编辑器 |
| 体积 gzip | ~2–5 MB + worker | ~50–120 KB 起步 | 按需 |
| C# 高亮 | **内置**（Monarch；可升级 TextMate） | ⚠️ **官方无 C# Lezer grammar**，第三方参差 | 强（复用 TextMate） |
| 行装饰 | `deltaDecorations`（isWholeLine/glyphMargin/linesDecorations） | Decoration.line + 自写 gutter StateField | 自绘 |
| 断点 gutter | 内置 glyph margin + codicon | ~30–60 行自定义 | 自绘 |
| 定位滚动 | revealLineInCenter/setPosition | scrollIntoView effect | 自绘 |
| 嵌入复杂度 | **高**（worker/bundler） | 低 | 中（选择/查找/滚动都要补） |
| 调试 UI 先例 | **VS Code / Theia 整个调试界面** | Replit/Firefox | cherry-studio 流式代码块 |
| 大文件 | 优但内存高（10k 行 ~124MB） | 优（~89MB，视口渲染） | 需虚拟滚动 |

**判断**：实时高亮当前行/断点三者都不贵（Monaco 一次 deltaDecorations 事务 = VS Code 做法）。**分水岭是 C# 高亮质量 + dnSpy 观感对齐** → Monaco 胜出（codicon-debug-breakpoint 图标、黄顶帧/绿调用返回装饰可逐行照抄 Theia/VS Code）。体积在本机 localhost 不是问题；要付的税是 worker/Vite 配置（`vite-plugin-monaco-editor`，只选一条路，勿混用）。CodeMirror 6 备选接受高亮让步。

## 2. 服务端实时推送：SSE（主选）
- 数据流 = server→browser 单向洪泛 + browser→server 极少指令 → **SSE 是默认答案**。
- 对比：SSE 客户端依赖 0（原生 EventSource）、自动重连（Last-Event-ID）、.NET 10 `TypedResults.ServerSentEvents` 一等公民（包 IAsyncEnumerable + 心跳）；SignalR ~45KB gz 且单机单用户是负资产；裸 WS 全自理。
- **事件架构**：连接即 `GET /api/state` 拉全量可视化快照（停点/栈/变量/断点/agent 决策），此后 SSE 只推增量/事件；EventSource 断线重连后重拉快照再续增量 → 幂等，无丢事件焦虑。
- **生产形态**：调试引擎写 **有界 `Channel<DebugEvent>`**，SSE 端点 `await foreach` 读 Channel 作 IAsyncEnumerable（背压，防慢浏览器拖垮调试线程）。事件合并节流（~50–100ms）防高频 step 压垮浏览器。
- 浏览器控制指令（pause/step 按钮）走普通 fetch POST；agent 控制面本来走 MCP（stdio），Web 是人/展示面 → 控制面与展示面解耦。

## 3. 前端框架与图/时间线
- 多面板联动（源码/栈/变量树/线程/日志/时间线）→ **React+TS+Vite** 稳；回放 scrubber 与多面板建议上框架。
- **Agent 轨迹时间线/回放：自绘纵向 DOM 列表**（每步卡片：agent 决策文本+调用工具+关键状态；点击回放=重新 apply 该步可视化状态快照：高亮行/栈/变量）。不要用图库做回放（难精确还原状态）。
- 时序/调用图：**mermaid.js 动态 import**（全量 ~1MB+ gz，打开图面板才拉）；call_graph MCP 输出拼 flowchart 源文本是纯字符串拼接。

## 4. 整体方案（组合 A 主 / B 备）
**组合 A（推荐）**：Kestrel 单进程（静态中间件 + REST `/api/state`、`/api/docs/{id}`、`/api/control/step|pause` + SSE `/events` + 调试引擎作 Hosted Service 常驻、事件→Channel→SSE；`127.0.0.1:<固定端口>`，启动 `UseShellExecute` 自动开浏览器）。代码视图 Monaco（read-only、glyphMargin、关 minimap 等杂项；断点 glyph + 黄顶帧 + 绿其余帧 + revealLineInCenter 照抄 Theia/VS Code 主题色 `#ffff0033`/`#7abd7a4d`）。推送 SSE。前端 React+TS+Vite。时间线自绘 + mermaid 按需。
**组合 B（更轻）**：代码视图换 CodeMirror 6（basicSetup + 社区 C# 包 + 官方 gutter 断点示例 + Decoration.line 当前行）；省 ~2–4MB gz 无 worker，代价高亮弱 + 自写 gutter 扩展。

## 5. 关键：IL offset / method token → 反编译行（映射做服务端）
- 停点事件给「模块+method token+IL offset」，前端只认「文档+行」→ **全部在服务端解析**。
- dnSpy/VS Code 靠 ICSharpCode.Decompiler **`SequencePointBuilder`**：反编译时同步产出 `methodToken → List<(ilStart,ilEnd,line,col)>`；命中后二分查区间 → 行号。反向设断点取行 sequence point 的 ilStart。**同一 DecompilerSettings 出文本与映射**（列偏移耦合）。
- **映射风险（ILSpy #1901）**：Release/优化/表达式体/异常路径可能无 sequence point 或区间重叠 → 降级策略「映射不到不高亮假行，只高亮该方法第一行或提示」；列号不可靠 → 只读展示优先行号；**反编译文本若带 `行号\t` 前缀或分页，映射表必须基于去前缀后与服务端 SyntaxTree 行号一致的坐标**，推送前换算展示行。
- 超大文件按方法/类型切片成多个 doc；多 doc 内存注意（Monaco 每 model 大文件 ~百 MB 级）。

## 6. 可借鉴开源参考
| 项目 | 借鉴点 |
|---|---|
| dnSpyEx（WPF） | 语义：活动语句黄/调用返回绿/断点 glyph；CallStackMarker 刷新思路 |
| Theia packages/debug（Monaco+DAP 浏览器 UI） | deltaDecorations 断点/栈帧装饰、主题色定义——几乎可直接抄 |
| VS Code breakpointEditorContribution.ts | 断点 glyph margin + changeDecorations 批量更新防闪烁 |
| ILSpy SequencePointBuilder + issue #1901 | 映射算法本体 + 已知边界/坑（必读） |
| Augur Runtime Debugging Agent | agent 每次决策/观察/结果归一化 trajectory schema + Web replay |
| k0in/debug-graph（VS Code ext，MCP+Monaco） | MCP 控制面与 Monaco 展示面共存架构（Vue3+Monaco+Comlink） |
| Sharppad | .NET 内嵌 Monaco 工程组织 |
| monaco-node-debug-sample | 线程/栈/输出面板与调试事件最小接线 |

## 7. 关键 URL
- Monaco/CM6 对比：pkgpulse、johal.in、Replit blog；Monaco ESM/Vite：github monaco-editor docs/integrate-esm、vite-plugin-monaco-editor
- Theia debug-editor-model.ts；VS Code breakpointEditorContribution.ts
- CodeMirror gutter 官方示例（就是为调试断点写的）、decoration 示例；社区 C# 包 @replit/codemirror-lang-csharp / @codincod/codemirror-lang-csharp
- SSE/SignalR/WS 对比；.NET 10 SSE 教程（Results.ServerSentEvents + IAsyncEnumerable + 心跳）；MS SignalR 文档
- ILSpy SequencePointBuilder.cs；ILSpy issue #1901
- Augur / debug-graph / Sharppad / monaco-node-debug-sample / Shiki 性能指南

> 补充架构建议（采纳）：Agent 控制面（MCP，stdio）与 Web 展示面（HTTP/SSE）做成**同一调试服务的两个投影**：调试引擎统一发规范化事件（MethodHit/StepCompleted/LocalsChanged/AgentDecision…），MCP 译成引擎调用、SSE 译成可视化消息 → agent 每步决策自然落成回放日志，时间线就是这份日志的只读播放器，两侧不各写一套状态。
