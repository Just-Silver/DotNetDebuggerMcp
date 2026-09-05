# DotNetDebugger.Decompiler 开发指南

反编译/静态分析**能力库**（`DotNetDebugger.Decompiler`）：宿主 exe 与 Web 库的反编译、纯元数据读取、文档服务全部能力源头。进程内反编译经内置 ICSharpCode.Decompiler，结构类查询走 PEReader 元数据层，均不依赖外部工具。

## 边界纪律（本库的立身之本）

- net10.0，**唯一包依赖 `ICSharpCode.Decompiler`**（11.0.0.9375）——无 MCP/DI/日志/宿主依赖。改动 csproj 不得引入 Microsoft.Extensions.* / ModelContextProtocol 等。
- **不得反向依赖宿主 `DotNetDebuggerMcp`**；宿主访问库 internal 经 `InternalsVisibleTo`（`DotNetDebuggerMcp` 与 `DotNetDebuggerMcp.Tests`），避免过早 public 化内部 API。
- 宿主 `Configuration/AppText.cs` 经常量转发引用本库 `DecompilerText` 文案——**用户可见反编译文案改本库唯一落点**，宿主只转发。改文案后检查宿主判重逻辑（`StartsWithDecompileFailure`）同源感知。
- 本库无 AGENTS.md 专属约定外的架构文档；能力归属见根 `AGENTS.md` 结构节与各文件头注释（组件均标注供哪个 MCP 工具使用）。

## 目录结构（命名空间即目录）

- `Configuration/`（`DotNetDebugger.Decompiler.Configuration`）— 库自用内部常量，**自宿主 AppConfig/AppText 拆出的最小集，避免反向依赖宿主**：
  - `DecompilerConfig.cs`：`ExternalExpandMaxDepth`=5、`ExternalExpandMaxNodes`=200（call_chain 跨程序集展开预算）、`MaxOutputBytes`=64MB（单次反编译文本字符上限，超限返回「建议改用 decompile_to_dir」、结果不入缓存；与宿主缓存上限同值系设计一致而非同源）
  - `DecompilerText.cs`：`DecompileFailurePrefix`=「反编译失败：」+ `StartsWithDecompileFailure` 判定——**新增错误提示时检查是否需扩展判定，否则会被管道误当正常结果写缓存**（见下 InProcessDecompiler.IsErrorResult）
- `Metadata/`（`DotNetDebugger.Decompiler.Metadata`）— 17 个纯元数据组件，host 工具逐个对号：`AssemblyInfoReader`(assembly_info) / `CallChainScanner`+`ExternalCallExpander`(call_chain) / `CallGraphExtractor`(call_graph) / `FieldAccessScanner`(field_access) / `GenericInstantiationScanner`(generic_instantiations) / `Hierarchy`(hierarchy) / `InterfaceUsageScanner`(interface_usage) / `MemberResolver`(decompile_member 定位) / `ReferenceExtractor`(dependencies) / `SignatureRenderer`(signature) / `StringLiteralScanner`(search_string) / `TypeLister`(list_types)。共享底座（被上述组件复用，勿对工具直接暴露）：`IlScanHelper`（**库内唯一 internal 工具类**，基于 ICSharpCode ILParser 权威跳表解码方法体）、`MetadataNaming`（类型全名渲染/定位/token 格式化）、`CompilerGeneratedFilter`（编译器生成类型判定：全名含 `<` 双向精确，刻意不用 CompilerGeneratedAttribute——顶层语句 Program 类会误杀）、`SimilarNameMatcher`（编辑距离 ≤2 或公共前缀 ≥4）。
- `Decompiler/InProcessDecompiler.cs` — **唯一进程内反编译入口**，`public sealed class` 全静态：
  - `DecompileType` / `DecompileMember(assembly, token)` / `DecompileWholeModule` / `DecompileToDir` / `DecompileToProject`
  - `RunWithTimeoutAsync(work, timeout, ct, timeoutHint)`：同步 work 放 Task.Run + linked CTS 取消；**超时/取消立即返回 timeoutHint，不阻塞等后台任务**（后台经协作式中断）。所有公共入口 try/catch 兜底返回中文提示、不抛异常。
  - `internal IsErrorResult(string)`：判定结果是错误提示而非反编译结果（覆盖「反编译失败：」/「反编译已取消」/「未找到类型 」/「反编译输出超过上限」/「元数据 token 」/引号开头六类）——**新增错误前缀必须同步扩展**，否则管道会把它写进缓存。
  - 每次调用独立构建 PEFile+resolver+settings+CSharpDecompiler，用完即释放。
- `Document/`（`DotNetDebugger.Decompiler.Document`）— 文档服务，**只被 Web 库 `DocumentStore` 使用**（宿主反编译链不触碰）：
  - `SourceDocument`(干净文本, 1-based Lines, `IlToLineEntry` 映射) 与 `GetLineForIlOffset` / `GetIlStartForLine`（停点语句高亮 / 行反查 IL 设断点）
  - **关键管线勿改**：文本输出必须经 `TextWriterTokenWriter` 包 `TokenWriter.WrapInWriterThatSetsLocationsInAST` 回写 AST 节点位置，之后 `CreateSequencePoints` 才对无 PDB 程序集产出有效语句级映射——裸 `tree.ToString()` 位置留 (0,0) 映射全错。

## 语义红线

- `IlScanHelper` 解码对损坏 IL 异常安全中止（各 Scanner 有 `AbortedBodies` 降级计数约定）；宿主经 `RunMetadata` 的 `degradedProvider` 在结果头部显示降级提示。
- token 统一格式化在 `MetadataNaming.FormatToken`（`0x{:x8}`）；跨层复用文案/格式常量优先查本库 `Configuration/` + 宿主 `AppText`/`CacheSignatures`。

## 验证

```bash
dotnet test --project tests/DotNetDebugger.Decompiler.Tests/DotNetDebugger.Decompiler.Tests.csproj   # 元数据/反编译/文档服务单测
```

- `tests/DotNetDebugger.Decompiler.Tests/`：DocumentService 三套（正向映射语句级密度断言 >50 条、反向查询、降级/错误场景）覆盖 `Document/`；其余组件单测在宿主 `tests/DotNetDebuggerMcp.Tests`（经 InternalsVisibleTo）。
- 测试数据 `tests/TestData/*.dll` git 忽略，由 `tests/TestData/generate-testdata.ps1` 生成；访问用 `TestDataPaths.TestSamplesDll`（上溯找 slnx）。改 TestSamples 源码后需重新生成且 token 会变——不 rename/remove 既有类型保 token 稳定。
