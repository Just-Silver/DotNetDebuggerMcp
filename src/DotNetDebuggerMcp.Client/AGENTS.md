<!-- 职责：维护端到端客户端的场景入口与运行前置；不写：产品实现细节或历史验收结果。 -->

# DotNetDebuggerMcp.Client 开发指南

端到端验证客户端：默认以真实子进程 + stdio 连接自启动的 server，对全部**反编译/元数据工具**做全参数场景验证（**不含 `debug_*` 调试工具与 `cache_stats`**）。是「改工具后本机手动验收」的主要手段（CI 不跑端到端）。非单测，不进 slnx 测试套件；独立 Exe。显式 child 验收额外用 System.Drawing.Common 解码 PNG 像素。

## 结构

- `*Cases.cs`（15 个）— 每工具一个场景集，覆盖全参数 + 错误场景：
  Decompile / DecompileMember / DecompileIl / ListTypes / DecompileToDir(+ToProject) / Signature / Hierarchy / Dependencies / CallGraph / AssemblyInfo / InterfaceUsage / GenericInstantiation / CallChain（含跨程序集 `ExtDll` 入口）/ SearchString / FieldAccess。
  **新增工具时新建对应 Cases 文件**（或并入既有文件），各工具全参数补一条用例。
- `ToolCallCase.cs` — 场景记录：`Tool`/`Label`/`Args`/`ExpectedContains`（结果必含子串，null 不查）/`MustNotContain`/`ExpectSuccess`。预期「返回中文错误提示」的场景设 `ExpectSuccess=false`（此时结果仍带 `IsError` 标记也算 FAIL——错误提示是工具的返回文本而非协议错误）。
- `ClientRunner.cs` — 执行器：`ConnectAsync` 以 `dotnet run --project src/DotNetDebuggerMcp/... -c Release` **自启动 server**（不依赖预先构建）；`ListToolsAsync` 断言工具数 ≥13 且含关键名；`CallAsync` 提取文本块跑断言、打印前 200 字符与 PASS/FAIL，累计 `Failures`。
- `TestDataHelper.cs` — 共享测试标识：上溯找 `DotNetDebuggerMcp.slnx` 定 `RepoRoot`；**显式指 `DotNetDebuggerMcp.TestSamples.dll`（`Dll`）——不能按「第一个 dll」取，字母序会选错 TestSamplesExt**；类型/成员标识集中在常量（BigClass/Class0001/…）。改测试数据时同步维护。
- `Program.cs` — 入口：组路径 → 跑 `ListToolsAsync` + 全部 Cases → 断言写盘产物（`tests/.dotnetdebugger-client-out/` 下 `.cs` 文件数 >0）→ **文件句柄释放断言**（Ext 反编译后紧随 `LockProbe`：server 进程仍存活时独占打开 Ext/主样本 dll——残留依赖句柄回归会锁住构建输出目录，用户侧 `dotnet build` 报 MSB3061/MSB3021）→ `finally` 清理该目录；失败数 >0 时 `Environment.ExitCode = 1`。

## 运行与纪律

```bash
dotnet run -c Release --project src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj
```

- 前置：`tests/TestData/*.dll` 需存在（`generate-testdata.ps1` 生成、git 忽略）。
- 端到端自启动 **Release** server；产物目录 `tests/.dotnetdebugger-client-out/` 已在 .gitignore 且 Program finally 必清。
- 改 server 代码 → 验证流程：build 通过 → 单元测试通过 → 跑本 Client 全绿（含工具列表断言 + 每工具全参数覆盖 + 错误场景文本断言 + 写盘产物断言）为验收依据。
- 工具的返回文本/提示会随反编译文案调整而变——**改文案（`AppText`/DecompilerText）后同步核对各 Cases 的 `ExpectedContains`**，否则端到端假红/假绿。

## 显式子会话验收

`ChildSessionCases*.cs` 仅在 `DOTNETDEBUGGERMCP_CHILD_INTEGRATION=1` 且传 `--child <本轮安装shim.exe> <本轮安装宿主.dll> <输出目录>` 时运行，默认 Client 不触碰系统开关、不启动 child、不注入真实输入。缺开关、存在其他 child 或本产品活动实例时拒绝，不接管外部会话；认证只能由用户在系统窗口完成。

先经授权准备系统，再运行 `tests/Fixtures/ChildSessionTarget/install-acceptance-tool.ps1` 生成独立本地源、隔离 NuGet 缓存与 `installed.json`。以该清单设置 `DOTNETDEBUGGERMCP_CHILD_INSTALLED_MANIFEST` 后，`ChildInstalledToolTests` 可运行只读安装验证；另显式设置上述 integration 变量才会启动三轮真实验收。夹具须另按 Debug 构建后用 `--no-build` 运行测试，避免测试构建把目标覆盖为 Release。

场景逐轮清理自有 child，保留 PNG、GUI 真实事件和 `evidence.json`；系统开关由获授权操作者单独恢复并 probe 核对。失败不重放未知 GUI 变更，也不自动恢复系统设置。
