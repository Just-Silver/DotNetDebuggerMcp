# AGENTS.md

基于 [ICSharpCode.Decompiler](https://github.com/icsharpcode/ilspy) + [ClrDebug/ICorDebug](https://github.com/tylerjensen/ClrDebug) 的 **.NET MCP 服务器**（NuGet 包 id / CLI 命令 `DotNetDebuggerMcp`）：对 .NET 程序集（dll/exe）做反编译、类型/成员/调用关系静态分析，并对 .NET 进程做**动态调试**（launch/attach、断点、单步、读栈/变量）；`--web` 可并联 Blazor 展示面实时观看 agent 调试。反编译与调试引擎均内置，随 NuGet 包分发。

## 项目地图（指令文件按项目拆分，先进对的门）

每个项目目录下有专属 `AGENTS.md`，含该项目的结构/边界/踩坑/验证——**改哪个项目先读哪个**：

| 路径 | 项目 | 进门前读 |
|---|---|---|
| `src/DotNetDebugger.Decompiler/` | 反编译/静态分析能力库 | `src/DotNetDebugger.Decompiler/AGENTS.md` |
| `src/DotNetDebugger.Engine/` | 动态调试引擎（ICorDebug） | `src/DotNetDebugger.Engine/AGENTS.md` |
| `src/DotNetDebugger.Session/` | 会话/状态层（宿主与 Web 共享中枢） | `src/DotNetDebugger.Session/AGENTS.md` |
| `src/DotNetDebugger.Web/` | Blazor Web 展示面（RCL，被宿主承载） | `src/DotNetDebugger.Web/AGENTS.md` |
| `src/DotNetDebuggerMcp/` | **宿主 exe**（MCP+CLI+Web 承载） | `src/DotNetDebuggerMcp/AGENTS.md` |
| `src/DotNetDebuggerMcp.Client/` | 端到端验证客户端 | `src/DotNetDebuggerMcp.Client/AGENTS.md` |
| `tests/` | 5 个测试项目 + TestData | `tests/AGENTS.md` |

**依赖方向**：`Decompiler`（只依赖 ICSharpCode.Decompiler）与 `Engine`（只依赖 ClrDebug + DbgShim.win-x64）是零宿主依赖的能力库；`Session` 依赖 Engine+Decompiler；`Web` 只引 Session+Decompiler（不反引宿主，经 `WebHostBootstrap.Configure` 静态注入）；`DotNetDebuggerMcp` 宿主引全部四库。各库**均不得反向引用宿主**。

**文档导航**：`docs/planning/README.md` 是 docs 规划目录的权威入口（P1-P4-1 已完成、P4-2 Web 进行中、specs/research 导航）；近期待办在**各项目目录 `TODO.md`**（与该目录 AGENTS.md 同放，按项目独立维护）；`docs/ROADMAP.md` 是远期待办（含未实现的 `web_open` 幂等工具）；`CHANGELOG.md` 是包使用者可见的发布记录（`[Unreleased]` 段即当前迭代）。实现细节查证优先读本地克隆 `E:\Code\Projects\Externals\DebuggerExternals\`（dnSpy / ILSpy / sharpdbg / ClrDebug / clrmd / diagnostics / BootstrapBlazor）。

## 开发铁律

> **原则：遇事不猜，先查后改。**

- 不熟悉 API/模块实现/报错原因时，**严禁凭经验臆测或盲目修改**。正确做法：① 本地克隆源码（`E:\Code\Projects\Externals\DebuggerExternals\`，比 websearch 快且准）② 官方文档/`--help` ③ `gh` 查 issue/PR ④ websearch ⑤ 再请教。
- 禁止：凭感觉试错、复制粘贴未经验证的代码、忽略官方/社区最佳实践。

## 关键约束（跨项目共享）

- **所有 MCP 工具参数必须带默认值**（如 `string assembly = ""`，不声明可空）。SDK 依据是否有默认值判断必填：无默认值缺参在绑定阶段抛 Tool Error，agent 拿不到原因；带默认值后缺参进入方法体由校验返回中文提示。`[Description]` 用中文、面向 agent（MCP 调用方）、**注明默认值**、必填标「（必填）」、不写实现细节措辞。
- **每个工具方法带 `CancellationToken cancellationToken = default`**（反编译类放 `timeoutSeconds` 后、元数据类放末尾；SDK 识别为取消令牌、不暴露为 MCP 参数、不写 `[Description]`）。超时/取消按「放弃等待」处理：返回提示、结果不入缓存可重试、后台任务协作式中断。
- 工具方法返回 `Task<string>`，一切错误（参数校验/反编译/调试失败）返回中文提示文本，**不抛异常**。
- **stdout 只承载 MCP 协议消息；日志必须走 stderr**——配置在 `DotNetDebuggerMcpCmd.OnExecuteAsync` MCP 启动分支（`ClearProviders` + `AddConsole(LogToStandardErrorThreshold = Trace)`），Web host 同款（`WebHostBootstrap.Build`），**严禁删除或改动**。历史教训：`Host.CreateApplicationBuilder` 默认 Console 日志写 stdout，并发请求下日志行与 JSON-RPC 响应字节交错会撕坏协议帧（agent 客户端 12 路并发 100% 挂死）。改启动逻辑/升级 Hosting 包后重验：裸 stdio 握手后 stdout 噪声行应为 0；回归护栏 `McpSessionConcurrencyTests`。另注意：日志走 stderr 后，凡自起子进程（Engine/Session/宿主测试、`-dbg`、`LaunchAndAttachAsync`）**必须持续排空子进程 stdout/stderr**，否则把子进程卡死在日志/输出写入上。
- **更新版本号同步三处**：`src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` `<Version>`、`.mcp/server.json`（顶层 + `packages[0].version`）、`CHANGELOG.md`（发布前把 `[Unreleased]` 转 `## [<version>] - <date>`）。CI 从 CHANGELOG 提取版本段作 GitHub Release 正文，缺段发布失败。CHANGELOG 面向包使用者，只记使用者可见变更。
- **改 MCP 工具（新增/删除/改名/加参/改默认值/改行为）必须把根 `README.md` 一并改到位**再提交——README 打包为 `PackageReadmeFile`（用户看到的是打包时快照），且与代码改动同 commit。
- **跨层/多处重复使用的字面量必须定义成常量**，改文案只在常量类改一处：`Configuration/AppText.cs`（转发 Decompiler 库 `DecompilerText` 单一来源）、`Configuration/CacheSignatures.cs`（缓存签名前缀 + `\u001F` 分隔符；**改动必须同步 `CacheStatsTool.ToolNames`**）、`MetadataNaming.FormatToken`、`OutputFormatter.MemberLine`（`#MEMBER` 行）、`SectionBuilder.EmptyPlaceholder`（`（无）`）。新增工具/提示先查这些常量类。
- **新增错误提示必须扩展 `InProcessDecompiler.IsErrorResult`**（六类前缀判定），否则管道会把错误提示误当正常结果写入缓存。
- 工程惯例：修改逻辑后 build 通过 + 单元测试通过 + 本机跑 Client/CLI 确认输出（CI 的 build.yml 只做 build/test/发布，不跑端到端）。

## 输出约定（agent 消费的 API 形状）

- 结果前置头部信息块（`程序集/目标` + 总量 + `当前输出` + `剩余` + `---`，纯文本不带行号）；命中缓存时 `目标` 行后追加 `缓存:   命中（重复查询成本低）`；写盘工具成功提示含「来源 <assembly>」。**不展示参数行**。
- 默认返回前约 8 KB，`lines="start-end"` 分页（单次最多约 32 KB）；`剩余` 行给出建议 lines 范围。头部之下按 `行号<TAB>内容` 标注，切片行号基于原始位置。总量：反编译 `总行数: N 行`；列类型/元数据 `匹配实体: N 个 + 总行数: N 行`。空结果 `无`。
- `decompile_member`/`call_chain` 多匹配合并输出、各成员前插 `#MEMBER {"name","token"}` JSON 分隔行（计入行号）；匹配数 > `MaxMemberMatches`（20）仅返回 `#MEMBER` 签名清单并注明「超过上限，仅列出签名」。`signature` 行尾附 token（`0x06` 方法/`0x04` 字段/`0x17` 属性/`0x14` 事件）——agent 取 token 闭环到 `decompile_member`/`call_graph`/`call_chain`/`field_access` 的 `token` 参数。
- `hierarchy`/`dependencies`/`call_graph`/`interface_usage`/`field_access` 等分段输出，空段输出 `（无）` 占位；`includeExternal`（`-x`）外部条目格式 `全名 [程序集名]`。反编译输出含 `//IL_` 未解析注释时头部追加提示「仅供结构参考」。
- stdout 反编译超 `DecompilerConfig.MaxOutputBytes`（64MB）时返回「建议改用 decompile_to_dir」提示，不入缓存。
- 动态调试约定：控制类工具**异步返回（带默认超时），不等停点**（等停点用 `debug_wait`，直接返回停点现场）；进程停在断点/异常后，用 `debug_state`（确认 Stopped）→ `debug_stack`/`debug_variables`（缺省 threadId=0 = 最近停点线程）→ `debug_continue`/`debug_step`。断点按 模块名+方法 token+IL offset 定位（token 取 `signature` 行尾；模块未加载时登记待绑定、加载后自动绑定，`debug_breakpoint_list` 查看绑定状态）。

## 命令

```bash
dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj          # 宿主全量单测
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "DotNetDebuggerMcp.Tests.DecompileCacheTests"   # 单套测试（xunit.v3 + MTP，--filter-class 写法验证可用）
dotnet run -c Release --project src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj   # 端到端验证（自启动 Release server，自动清理 tests/.dotnetdebugger-client-out/）
powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1               # 重新生成测试程序集（git 忽略，新克隆/CI 前置）
```

- **先跑 `generate-testdata.ps1` 再跑测试**：`tests/TestData/*.dll`/`*.exe`/`*.runtimeconfig.json` 全部 git 忽略（脚本产出 TestSamples.dll / TestSamplesExt.dll / DebugTarget.exe）。改脚本注意 `BigMethod` 用数组链而非常量链——Release 常量折叠会让方法只剩几行，无法触发截断。
- CLI 调试（改完 server 用 Debug 构建 exe 快速验证，行为与 MCP 工具一致）：`-a <dll> -t <TypeName>` 反编译 / `-mn` 搜成员 / `-s` 签名 / `-hc [-i]` 层级 / `-d [-x]` 依赖 / `-cg [-x] -tk` 调用图 / `-iu -gi -ss -fa` / `-cc` 调用链 / `-l c -nc Box -ns NS` 列类型 / `-o [-p]` 写盘 / `-c` 更新检查 / **`-dbg <exe> -dbg-bp <token> [-dbg-offset]`** 一次性调试 / **`--web [--web-port]`** Web 模式；通用 `-ln start-end` 分页、`--timeout` 秒数。完整参数表见 `DotNetDebuggerMcpCmd.cs` 与 README。
- 本地调试注意：根 `opencode.json` 把本仓库自身的 MCP server 绑定到 `src/DotNetDebuggerMcp/bin/Debug/net10.0/DotNetDebuggerMcp.exe`（带 `--web` 仅为联调）——**改完 server 代码需重新 build 并重启 opencode 才生效**；该进程运行时会锁定 Debug 输出文件导致 Debug 构建失败（MSB3021，会话内可见）。验证新行为以 Client/CLI 输出为准。
- 单测里经 `ToolPipeline` 的 assembly 路径解析基准是测试进程 CWD（`bin/Debug/net10.0`）；访问 `tests/TestData` 下 dll 用各测试项目 `TestDataPaths`/`TestPaths` 帮助类（上溯找 `DotNetDebuggerMcp.slnx`）。
