# TODO（DotNetDebuggerMcp 宿主近期待办）

> 近期待办，完成一项删一项；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

## agent 动态调试体验升级（2026-09-06 立项，按优先级串行推进）

> 背景：项目定位是让 agent 用真动态调试替代「加日志-重跑」循环。**每项先计划后执行**：中大型项（P3/P6/P7）先在 `docs/planning/specs/` 立 spec 再动码；小型项先在条目下补 3-5 行方案段（关键取舍）再实现。收尾一律：build + 单测 + Client/CLI 端到端；改 MCP 工具（加参/加工具）须同 commit 改根 README（新参数带默认值、`[Description]` 中文注明默认值）。

- [ ] **P1 目标进程 stdout/stderr 转发**（Session+宿主｜小）——现状排空即丢弃（`DebugSessionManager.cs:84`），agent 看不到目标自己的日志/异常打印/崩溃码。推荐：进程输出环形缓冲（标注流别），`debug_state`/`debug_wait` 返回附尾部 N 行；保留持续排空纪律防管道阻塞。方案取舍点：推进事件流 vs 工具拉取、默认行数、进程退出后是否可取。
- [ ] **P2 异常现场增强 + 类型精确过滤**（Engine+Session+宿主｜小-中）——现状 first-chance 停点 Message 恒 null（`CallbackHandler.cs:122` 处 `CurrentException` 可取未取）、异常对象不可观察、过滤器设了即停全部。推荐：① 捕获 Message；② `$exception` 伪变量挂进 `debug_variables`（复用值树，可展开 StackTrace/InnerException）；③ 启用 `ExceptionBreakpointFilter.Matches` 类型精确过滤（骨架已在）。Engine 集成测试扩展异常用例。
- [ ] **P3 行断点：反编译视图行 + PDB 源行**（Decompiler/Session+宿主｜中）——agent 的坐标系就是反编译输出（`行号<TAB>内容`），现在断点只认 token+IL offset。推荐分两步：3a 反编译行（DocumentService 已有 IL→行映射，补 行→最近语句 IL 反向映射）；3b PDB 源行（System.Reflection.Metadata sequence points，源文件+行→token+IL）。`debug_breakpoint_set` 加可选参数（带默认值）；一行多语句取最近序列点、无 PDB 回退语义先入 spec。
- [ ] **P4 停点现场源码上下文**（宿主+Session｜小）——`debug_wait`/`debug_state` 加 contextLines（默认 3-5 行），附反编译视图中当前语句上下文，省每停点一次 decompile 往返。复用 DocumentService 映射（Web 已验证同款管线）。
- [ ] **P5 断点命中计数 + trace/log 模式**（Session+宿主｜小-中）——两档都不依赖表达式求值，先做：① hitCount（第 N 次命中才真停）；② trace 模式（命中→变量快照→自动 continue，`debug_wait` 批量返回轨迹，token 效率数量级提升）。ICorDebug 无原生支持，Session 层 wait 循环 hit-test 即可；注意与 waitSeconds 超时语义协同、trace 快照的变量范围控制。
- [ ] **P6 表达式读值安全子集**（Session 内新组件｜中-大）——后续条件断点/trace 表达式/`debug_evaluate` 工具的公共底座：AST 遍历纯读值（成员访问/索引/字段链、字面量、比较运算），禁赋值/调用等副作用；基于现有 DebugValue 值树 + 模块元数据字段解析。debug-mcp（AGPL-3.0）只借鉴产品行为，clean-room 自研。**先立 spec**：文法子集、错误语义、与 `debug_variables` 输出格式一致性。
- [ ] **P7 条件断点**（Session+宿主｜中，依赖 P6）——命中时条件求值 false → 自动 continue 继续等。spec 重点：条件求值失败/超时/变量不可见时的语义（建议视为不命中并计入 trace 计数，防「断点永不命中」静默空等）。
- [ ] **P8 debug_processes 进程发现**（Session+宿主｜很小）——列出 dotnet 系进程（pid/exe/cmdline），作 `debug_attach` 的前置（agent 自主定位目标进程）。
- [ ] **P9 launch 原生路径早期断点 + 初始断点清单**（Engine/Session+宿主｜小-中）——解除「目标程序须自带启动延迟窗口」约束（现走先起进程再 Attach 绕开）。先 spike：Engine `LaunchAsync` 在初始停点/LoadModule 时应用 pending 断点的时序，再定 `debug_launch` 参数形态（可接 P3 行断点坐标）。
