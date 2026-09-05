# DotNetDebuggerMcp.Client 开发指南

端到端验证客户端：以真实子进程 + stdio 连接自启动的 server，对全部**反编译/元数据工具**做全参数场景验证（**不含 `debug_*` 调试工具与 `cache_stats`**）。是「改工具后本机手动验收」的主要手段（CI 不跑端到端）。非单测，不进 slnx 测试套件（独立 Exe，唯一包依赖 ModelContextProtocol 2.2.0）。

## 结构

- `*Cases.cs`（14 个）— 每工具一个场景集，覆盖全参数 + 错误场景：
  Decompile / DecompileMember / ListTypes / DecompileToDir(+ToProject) / Signature / Hierarchy / Dependencies / CallGraph / AssemblyInfo / InterfaceUsage / GenericInstantiation / CallChain（含跨程序集 `ExtDll` 入口）/ SearchString / FieldAccess。
  **新增工具时新建对应 Cases 文件**（或并入既有文件），各工具全参数补一条用例。
- `ToolCallCase.cs` — 场景记录：`Tool`/`Label`/`Args`/`ExpectedContains`（结果必含子串，null 不查）/`MustNotContain`/`ExpectSuccess`。预期「返回中文错误提示」的场景设 `ExpectSuccess=false`（此时结果仍带 `IsError` 标记也算 FAIL——错误提示是工具的返回文本而非协议错误）。
- `ClientRunner.cs` — 执行器：`ConnectAsync` 以 `dotnet run --project src/DotNetDebuggerMcp/... -c Release` **自启动 server**（不依赖预先构建）；`ListToolsAsync` 断言工具数 ≥13 且含关键名；`CallAsync` 提取文本块跑断言、打印前 200 字符与 PASS/FAIL，累计 `Failures`。
- `TestDataHelper.cs` — 共享测试标识：上溯找 `DotNetDebuggerMcp.slnx` 定 `RepoRoot`；**显式指 `ILSpyMcp.TestSamples.dll`（`Dll`）——不能按「第一个 dll」取，字母序会选错 TestSamplesExt**；类型/成员标识集中在常量（BigClass/Class0001/…）。改测试数据时同步维护。
- `Program.cs` — 入口：组路径 → 跑 `ListToolsAsync` + 全部 Cases → 断言写盘产物（`tests/.dotnetdebugger-client-out/` 下 `.cs` 文件数 >0）→ `finally` 清理该目录；失败数 >0 时 `Environment.ExitCode = 1`。

## 运行与纪律

```bash
dotnet run -c Release --project src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj
```

- 前置：`tests/TestData/*.dll` 需存在（`generate-testdata.ps1` 生成、git 忽略）。
- 端到端自启动 **Release** server；产物目录 `tests/.dotnetdebugger-client-out/` 已在 .gitignore 且 Program finally 必清。
- 改 server 代码 → 验证流程：build 通过 → 单元测试通过 → 跑本 Client 全绿（含工具列表断言 + 每工具全参数覆盖 + 错误场景文本断言 + 写盘产物断言）为验收依据。
- 工具的返回文本/提示会随反编译文案调整而变——**改文案（`AppText`/DecompilerText）后同步核对各 Cases 的 `ExpectedContains`**，否则端到端假红/假绿。
