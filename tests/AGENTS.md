# tests 测试套件导航

仓库测试按被测项目拆分（`slnx` `/tests/` 下 5 个项目，xunit.v3 4.0.0 + Microsoft.NET.Test.Sdk，global.json 配置 MTP 运行器）。各测试项目细节见对应 `src/*/AGENTS.md` 的「验证」节；此处只记跨套件共享要点。

## 项目映射

| 测试项目 | 被测 | 关键点 |
|---|---|---|
| `DotNetDebugger.Decompiler.Tests` | Decompiler 库 | DocumentService 三套（语句级映射）+ 经库 internals；其余组件单测在宿主测试项目 |
| `DotNetDebugger.Engine.Tests` | Engine | 真实 attach DebugTarget 子进程；**必须串行**（AssemblyInfo.cs ParallelMode.None） |
| `DotNetDebugger.Session.Tests` | Session | 真实 attach；**必须串行**；AgentActionLogTests 纯内存可快跑 |
| `DotNetDebugger.Web.Tests` | Web 库 | TypeTreeData/DocumentStore/AgentViewContext 纯服务端（razor/JS 人工验收） |
| `DotNetDebuggerMcp.Tests` | 宿主 | 最全：缓存/管道/校验/工具/更新检查 + debug 工具端到端 + MCP+Web 共存 + 会话级并发回归 |

## 共享纪律

- **`tests/TestData/*.dll`/`*.exe`/`*.runtimeconfig.json` 全部 git 忽略**（`.gitignore`），唯一受跟踪文件是 `generate-testdata.ps1`——脚本产出 `ILSpyMcp.TestSamples.dll`（反编译/元数据用例）+ `ILSpyMcp.TestSamplesExt.dll`（跨程序集用例）+ `DebugTarget.exe/dll/runtimeconfig`（调试用例）。**新克隆/CI 必须先生成**：`powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`。
- 改 TestSamples/DebugTarget 源码后需重跑脚本；**token 随源码变化，断点/成员定位类断言会漂移**——不 rename/remove 既有类型保 token 稳定。
- 各测试项目的 `TestDataPaths.cs`/`TestPaths.cs` 都从测试进程 CWD 上溯找 `DotNetDebuggerMcp.slnx` 再拼 dll 路径——路径解析基准是测试进程 CWD（`bin/Debug/net10.0`）。
- 宿主测试里串行化使用 `AppServices` 静态状态的测试类（`CheckToolTests`/`ToolPipelineTests`/`CacheStatsToolTests`/各 ToolTests 等）标注 `[Collection("AppServices")]`——新增改静态单例注入的测试时加入同集合，避免并行竞态。
- 真实调试/子进程套件（Engine.Tests、Session.Tests、宿主 `McpSessionConcurrencyTests`/`DebugMcpToolsTests`/`McpWebCoexistTests`）相对慢，改相关代码时定向跑即可；全量测试前先 build + 生成 TestData。
