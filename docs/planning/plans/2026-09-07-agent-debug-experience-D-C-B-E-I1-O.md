# 实施计划 · agent 动态调试体验升级（D/C/B/E/I1/O）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施。Step 用 `- [ ]` 追踪。

**Goal:** 落地 CoreMes 实证定案的 6 项 debug 体验改进：launch 工作目录默认修正（D）、栈帧真名化（C）、async 调试引导 + IL 兜底（B）、断点成员级定位（E）、run-to 工具（I1）、launch 返回状态准确性（O）。

**Architecture:** 全部改动横跨宿主 `Tools/Debugger/` + Session + Engine + Decompiler 四层，遵循既有依赖方向（Decompiler→Engine 零宿主；Session 依赖 Engine+Decompiler；宿主引全库）。按 D→C→B→E→I1→O 串行，每项独立可测可回退；C 是 B 前置、E 定位被 I1 复用、O 的 workingDirectory 描述随 D。

**Tech Stack:** C# net10.0、ClrDebug、ICSharpCode.Decompiler 11.0.0.9375、MCP SDK（ModelContextProtocol.Server）。

**Spec / 依据:** 宿主 `src/DotNetDebuggerMcp/TODO.md`「MCP 工具改进待办（CoreMes 实证）」区 L47-52（6 项待办完整方案 = 本计划的 spec）；四份只读探索报告为现状锚点；B 的 async 语义实证结论见 TODO L49 + R5 spec（Superseded 保留记录）。

## Global Constraints（根 AGENTS.md + 各层 AGENTS.md 铁律）

- 所有 MCP 工具参数带默认值（`string x = ""`，不声明可空）；`[Description]` 中文、面向 agent、注明默认值、不写实现细节。
- 每个工具方法带 `CancellationToken cancellationToken = default`（SDK 识别不暴露、不写 Description）。
- 工具返回 `Task<string>`，一切错误返回中文提示文本，不抛异常。
- stdout 只承载 MCP 协议帧；日志/Console 走 stderr。严禁改动 MCP 启动分支的 `ClearProviders`+`AddConsole(LogToStandardErrorThreshold=Trace)`。
- 改 MCP 工具（加参/加工具/改行为/改 Description）必须同 commit 改根 `README.md`（打包 PackageReadmeFile）。
- 新增错误提示必须扩展 `InProcessDecompiler.IsErrorResult`（如新 IL 入口加「反汇编失败」前缀）——否则管道误当正常结果写缓存。
- Engine 是纯能力层「不反编译、不解析类型名」——但 `TypeNameResolver`/`SymbolNameResolver` 已在 Engine 内且异常路径已用（先例），C 在 Engine 内解析类型名与既有先例一致，不越界反编译。Engine 测试不引 Decompiler，token 用 BCL System.Reflection.Metadata 读。
- Session AGENTS：`DebugSessionManager` 是单例，勿各处 new；`LaunchAndAttachAsync` 改动同步验证宿主 `DebugMcpToolsTests`。
- 测试前置 `powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`；Engine/Session 测试真实 attach 子进程、必须串行（勿删 ParallelMode.None）。
- 收尾验证：每层 build + 单测全绿 + 宿主全量 + Client/CLI 端到端。
- 提交信息用中文。工作区在 master——实施前需用户确认分支策略（本计划 Task 0）。

---

### Task 0: 分支/工作区准备（提交前确认）

- [ ] **Step 1**：与用户确认实施分支策略（master 直接实施 或 新建 feature 分支）。executing-plans 要求不在 master 未经同意实施。
- [ ] **Step 2**：跑 `generate-testdata.ps1` 确保测试数据就绪；`dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` 基线绿。

---

### Task 1: D — `debug_launch` workingDirectory 空默认 = exe 所在目录 + 返回报告实际工作目录

**Files:**
- Modify: `src/DotNetDebugger.Session/DebugSessionManager.cs:89-115`
- Modify: `src/DotNetDebugger.Session/Models/SessionModels.cs`（ActiveDebugSession 定义在 DebugSessionManager.cs:10-42）
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugSessionTool.cs:30-55`（launch Description + 返回文案）
- Test: `tests/DotNetDebugger.Session.Tests/DebugSessionManagerTests.cs`、`tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`
- Doc: `README.md:116,315-318`

**Interfaces:**
- Consumes: `LaunchAndAttachAsync(commandLine, timeoutSeconds, workingDirectory, environment, ct)` 现空 wd 不设 psi。
- Produces: `ActiveDebugSession.WorkingDirectory`（string，实际生效工作目录）。宿主 launch/attach 返回文案含 `工作目录：{...}`。

- [ ] **Step 1: 写失败测试（Session 层）**

`DebugSessionManagerTests.cs` 新增：
```csharp
[Fact]
public async Task LaunchAndAttach_EmptyWorkingDirectory_DefaultsToExeDirectory()
{
    await using var manager = new DebugSessionManager();
    var active = await manager.LaunchAndAttachAsync($"{TestTarget.DebugTargetExe} 1 0",
        ct: TestContext.Current.CancellationToken); // workingDirectory 省略 → 空
    Assert.True(active.ProcessId > 0);
    Assert.Equal(Path.GetDirectoryName(TestTarget.DebugTargetExe), active.WorkingDirectory);
    // 目标自报 cwd 应与 exe 目录一致（generate-testdata.ps1 打印 [DebugTarget] cwd=）
    var tail = active.Output!.Tail(100, filter: ""); // 见现有测试同款取法
    Assert.Contains($"[DebugTarget] cwd={Path.GetDirectoryName(TestTarget.DebugTargetExe)}", string.Join('\n', tail.Select(l => l.Text)));
}
```
Run: `dotnet test --project tests/DotNetDebugger.Session.Tests/DotNetDebugger.Session.Tests.csproj --filter WorkingDirectory`
Expected: FAIL（`ActiveDebugSession.WorkingDirectory` 不存在 / cwd 不是 exe 目录）。

- [ ] **Step 2: 实现（Session）**

`DebugSessionManager.cs`：
1. `ActiveDebugSession` 加 `public string WorkingDirectory { get; }`（构造传入）。
2. L105-111 改为：
```csharp
var effectiveWd = string.IsNullOrWhiteSpace(workingDirectory)
    ? Path.GetDirectoryName(exePath)!   // 空默认 = exe 所在目录（对齐手动启动 exe）
    : Path.GetFullPath(workingDirectory);
if (!Directory.Exists(effectiveWd))
    throw new InvalidOperationException($"工作目录不存在：{effectiveWd}");
psi.WorkingDirectory = effectiveWd;
```
3. `Activate(...)` 调用点把 `effectiveWd` 传入 ActiveDebugSession 构造。
4. L85-87 XML 注释同步「默认=exe 所在目录」。

- [ ] **Step 3: 写失败测试（宿主 e2e）**

`DebugMcpToolsTests.cs`：现有 `DebugTools_LaunchBreakpointContinueInspect_ClosesLoop` 用不带 wd 的 launch——在 launch 返回后加断言 `Assert.Contains("工作目录", launch.Text())`（返回文案含实际工作目录）。可另加独立用例断言含 exe 目录。

- [ ] **Step 4: 实现（宿主）+ 同步 README**

`DebugSessionTool.cs`：L34 Description 改「默认空=目标 exe 所在目录（对齐手动启动；目标产物写自己目录）。可显式传目录覆盖」；L48 返回文案加 `工作目录：{active.WorkingDirectory}`。debug_attach 返回不带工作目录（attach 无启动目录概念，保持）。
README.md L116/L317/L326 workingDirectory 描述同步。Session AGENTS.md L15 若描述默认值也同步。

- [ ] **Step 5: 验证 + 提交**

Run: Session 测试、宿主 DebugMcpToolsTests 定向、`dotnet build`。
`git add` 相关 + README，`git commit -m "feat: debug_launch workingDirectory 空默认改 exe 目录并报告实际工作目录（D）"`

---

### Task 2: C — `debug_stack` 帧名真名化（Engine+宿主）

**Files:**
- Modify: `src/DotNetDebugger.Engine/Engine/SymbolNameResolver.cs`（加 ReadMethodName）
- Modify: `src/DotNetDebugger.Engine/Engine/DebugEngineCore.cs:1235-1253`（TryGetTypeName/TryGetMethodName 接 resolver）
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugInspectTool.cs:34-46`（渲染优先 类型.方法 + token 保留）
- Test: `tests/DotNetDebugger.Engine.Tests/StateReadTests.cs`、`tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs:189/202`
- 参考: `docs/ROADMAP.md:18`（顺手项）、R5 spec 帧名真名化设计

**Interfaces:**
- Consumes: `TypeNameResolver.Resolve(modulePath全路径, classToken)`（已存在）；`ilf.Function?.Class.Module.Name`（全路径）；`BreakpointManager.GetModulePath(moduleName)`（短名→全路径，GetStackFramesAsync 现不查）。
- Produces: 新 `SymbolNameResolver.ReadMethodName(modulePath, methodToken)`（static，返回 string?）；栈帧真名化渲染。

- [ ] **Step 1: Engine — 方法名解析能力**

`SymbolNameResolver.cs` 加：
```csharp
public static string? ReadMethodName(string modulePath, int methodToken)
{
    try
    {
        using var fs = File.OpenRead(modulePath);
        using var pe = new PEReader(fs);
        var md = pe.GetMetadataReader();
        var handle = MetadataTokens.MethodDefinitionHandle(methodToken);
        if (handle.IsNil) return null;
        var mdef = md.GetMethodDefinition(handle);
        return md.GetString(mdef.Name);
    }
    catch { return null; }
}
```
（参考现有 `ReadArgNames` L46-65 同款 PEReader 模式，可加缓存/并入 Resolve。）Run Engine StateReadTests 确认不破坏。

- [ ] **Step 2: Engine — TryGetTypeName/TryGetMethodName 接 resolver**

`DebugEngineCore.cs` GetStackFramesAsync 帧循环（L427-435）**已持有模块全路径** `rawModule = ilf.Function?.Module?.Name`（注释「CorDebugModule.Name 返回全路径」），真名化不需要查 BreakpointManager 登记表（attach 竞速时登记表可能缺模块，直接用帧内全路径更稳）。把 `rawModule` 传入两个 TryGet 方法：
`DebugEngineCore.cs` 帧循环内调用改为：
```csharp
var rawModule = ilf.Function?.Module?.Name ?? "<unknown>";
var moduleName = Path.GetFileName(rawModule); // CorDebugModule.Name 返回全路径，归一化为文件名
var token = ilf.FunctionToken.Value;
var ip = ilf.IP.pnOffset;
frames.Add(new DebugStackFrame(new FrameLocation(moduleName, (int)token, ip), idx++)
{
    TypeName = rawModule is "<unknown>" ? TryGetTypeName(ilf) : TryGetTypeName(ilf, rawModule),
    MethodName = rawModule is "<unknown>" ? TryGetMethodName(ilf) : TryGetMethodName(ilf, rawModule),
});
```
方法签名与实现（失败返回 null → 展示端降级 token）：
```csharp
private static string? TryGetTypeName(CorDebugILFrame ilf, string modulePath)
{
    try
    {
        var cls = ilf.Function?.Class;
        return cls is null ? null : TypeNameResolver.Resolve(modulePath, (int)cls.Token.Value);
    }
    catch { return null; }
}
private static string? TryGetMethodName(CorDebugILFrame ilf, string modulePath)
{
    try
    {
        var token = ilf.FunctionToken;
        return token.IsNil ? null : SymbolNameResolver.ReadMethodName(modulePath, token.Value);
    }
    catch { return null; }
}
```
（GetStackFramesAsync 是唯一调用点，直接改签名即可。原实现里 `ilf.Function?.Class.Module.Name` 也是全路径，与 rawModule 同源。）

- [ ] **Step 3: 宿主渲染 — 类型.方法 + token 保留**

`DebugInspectTool.cs` 渲染行改为：
```csharp
var lines = frames.Select(f =>
{
    var loc = f.Location;
    var name = (f.TypeName is not null || f.MethodName is not null)
        ? $"{f.TypeName}.{f.MethodName}"   // 两者皆 null 时不上真名
        : null;
    var tokenSuffix = $"  [{loc.MethodTokenText}]";   // token 保留，供下断点
    var pos = $"{loc.ModuleName}!{loc.MethodTokenText}+0x{loc.IlOffset:x}";
    return $"  {f.FrameIndex}: {name ?? pos}{tokenSuffix}";
}).ToList();
```
决策点：TypeName 形如 `Ns.Outer+<Foo>d__N` 时展示为 `Ns.Outer+<Foo>d__N.MoveNext`（C④ 状态机标注放 Task 3/B 做；C 先出真名即可）。**保留 token 后缀**使 DebugMcpToolsTests:189/202 的 `0x{token:x8}` 断言不破；同时保 `DebugTarget.dll` 子串。

- [ ] **Step 4: 同步 e2e/Client 断言 + 验证 + 提交**

核对宿主 e2e 中凡断言 `模块!token` 文本的测试（DebugMcpToolsTests:189/202 保 token 后缀即不破；其余 grep 定位）。Run Engine StateReadTests + 宿主 DebugMcpToolsTests 定向。
`git commit -m "feat: debug_stack 帧名真名化（类型.方法 + token 保留），状态机类型名可解析（C）"`

---

### Task 3: C④ 状态机标注 + B① async 引导（宿主，承接 C）

> B 拆分：B① 引导/状态机帧备注放宿主（本 Task）；B② IL 反汇编入口放 Decompiler+宿主（Task 4）。C④「状态机类型标注」实际在宿主展示端做（引擎已给真名 `<Foo>d__N`）。

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugInspectTool.cs`（debug_stack：`<Foo>d__N` 标注）
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugControlTool.cs`（debug_step 落状态机帧引导）
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/StopContextRenderer.cs`（停点上下文遇 d__N 备注）
- 参考: `src/DotNetDebugger.Decompiler/Metadata/CompilerGeneratedFilter.cs`（名含 `<` 判定）、TODO L49

**Interfaces:**
- Consumes: C 产出的帧真名（TypeName/MethodName）。
- Produces: 展示/提示层对「状态机类型 `<X>d__N`」的统一识别与标注文案。

- [ ] **Step 1: 状态机类型识别助手（宿主 Debugger 共享）**

在 `Tools/Debugger/` 加 internal 静态助手（或并入 StopContextRenderer 同级），从全名解析状态机原方法名：
```csharp
// 输入 Ns.Outer+<Foo>d__44 / <Foo>d__44 → ("Ns.Outer+", "Foo")；非状态机返回 null
internal static (string Prefix, string MethodName)? TryParseStateMachine(string fullName)
{
    var idx = fullName.LastIndexOf("<", StringComparison.Ordinal);
    if (idx < 0) return null;
    var close = fullName.IndexOf(">d__", idx, StringComparison.Ordinal);
    if (close < 0) return null;
    return (fullName[..idx], fullName[(idx + 1)..close]);
}
```

- [ ] **Step 2: debug_stack 状态机标注**

`DebugInspectTool.cs` 渲染：当 `f.TypeName` 经 `TryParseStateMachine` 命中，展示改为 `<Foo>d__N (状态机 Foo).MoveNext`（原方法名去掉 `<>`）。含 `(状态机 Foo)` 标注（C④ 语义）。

- [ ] **Step 3: debug_step 落状态机帧的引导提示**

`DebugControlTool.DebugStep`：step 提交前读 `Buffer.LastStop?.TopFrame`（step 前必 Stopped，顶帧即将单步的帧）。若顶帧 MethodToken 所属类型经反查是状态机（当前停点已在状态机内），返回文案追加引导：
`提示：当前在 async 状态机帧（对应 async 方法 X）。async 业务逻辑建议改用行断点：debug_breakpoint_set typeName+line 断还原源码的 await 行 + debug_continue（勿继续 step into——会停在编译器生成的状态机里无源码）。`
> 需借助 DocumentService.FindTypeByToken(modulePath, topFrame.MethodToken)（模块路径经 `active.Session.GetModulePathAsync`）判定。FindTypeByToken 已能在状态机类型上返回 `<Foo>d__N` 全名（探索 §5 已证）。

- [ ] **Step 4: 停点上下文遇 d__N 备注**

`StopContextRenderer.cs`：`typeFullName` 取得后、渲染前判定 `TryParseStateMachine`；命中则在头部加 `（编译器生成 async 状态机，对应 async 方法 X；业务代码见 X 外壳方法还原源码）` 备注，并建议用行断点。

- [ ] **Step 5: 验证 + 提交**

Run 宿主全量 + 手测 `-dbg` 落状态机场景（如 DebugTarget 无状态机则用现有目标验证「非状态机无备注」路径）。
`git commit -m "feat: async 状态机帧标注与行断点引导（B①/C④，宿主提示层）"`

---

### Task 4: B② — 新增「按 token 返回 IL 反汇编」入口（Decompiler+宿主）

> 依据：TODO L49「ICSharpCode.Decompiler MethodBodyDisassembler 现成」。全仓当前无引用（探索 §4 证实），ILSpy 用法 `new MethodBodyDisassembler(new PlainTextOutput(sw), false).Disassemble(peFile, MethodDefinitionHandle)`。

**Files:**
- Modify: `src/DotNetDebugger.Decompiler/.../InProcessDecompiler.cs`（新增 `DecompileIl(int token)` 公共入口 + IsErrorResult 扩展 + DecompilerText 前缀）
- Modify: `src/DotNetDebuggerMcp/Tools/Decompile/`（新工具或并入 decompile_member 的 mode；倾向新 `decompile_il` 工具保持参数面干净——对齐 I1 独立工具决策）
- Doc: README 工具表
- Test: `tests/DotNetDebugger.Decompiler.Tests` + `tests/DotNetDebuggerMcp.Tests`

**Interfaces:**
- Consumes: ICSharpCode.Decompiler.Disassembler.MethodBodyDisassembler（包已含，无需新依赖）。
- Produces: Decompiler `InProcessDecompiler.DecompileIl(int token)` → string（IL 文本）。宿主 `decompile_il(assembly, token)`。

- [ ] **Step 1: Decompiler 库 — DecompileIl 入口 + 文案落点**

在 `InProcessDecompiler` 增公共方法，仿 `RunWithTimeoutAsync`/`DecompileMember` 骨架：
```csharp
public string DecompileIl(string assemblyPath, int methodToken) => Execute(... => {
    using var pe = new PEFile(assemblyPath);
    var handle = MetadataTokens.MethodDefinitionHandle(methodToken);
    var sw = new StringWriter();
    new MethodBodyDisassembler(new PlainTextOutput(sw), false).Disassemble(pe, handle);
    return sw.ToString();
});
```
失败/无效 token → `$"{DecompilerText.IlFailurePrefix}..."`。同步：
- `DecompilerText` 加 `IlFailurePrefix`（如 `"反汇编失败："`）；
- `InProcessDecompiler.IsErrorResult` 加该前缀判定（否则管道误当正常写缓存）。

- [ ] **Step 2: 宿主 — 新工具 `decompile_il`（或 decompile_member 加 mode）**

倾向新工具 `decompile_il(assembly="", token="", timeoutSeconds=30, ct)`——token 必填，返回 IL 文本（行号体系独立，不追求与 C# decompile 行号对齐）。校验复用 `ArgumentValidators`/`TryParseToken` 同款。走 ToolPipeline（缓存键含 token，需 CacheSignatures 新签名或复用 decompile_member token 签名）。README 同步。

> 取舍记录：B② 的「状态机 IL 兜底」若仅调试场景用，也可放宿主 Debugger 侧新工具（`debug_il`）；但 IL 反汇编本质是反编译库能力，放 Decompiler + 反编译工具面更内聚。agent 经反编译工具面取 IL 后，token 闭环回 debug_breakpoint_set 不受影响。

- [ ] **Step 3: 测试 + 验证 + 提交**

Decompiler.Tests 加 token→IL 文本断言（含非方法 token 报错）；宿主 e2e 一条。build + 全量单测。
`git commit -m "feat: 新增按 token 返回 IL 反汇编入口（decompile_il，async 状态机 IL 兜底 B②）"`

---

### Task 5: E — `debug_breakpoint_set` 增 typeName+memberName 成员级定位

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugBreakpointTool.cs`（加 memberName 参数 + SetByMemberAsync 分支）
- Doc: `README.md:117,326`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

**Interfaces:**
- Consumes: `MemberResolver.FindMembers(modulePath, typeName, memberName, includeAccessors)`（Decompiler.Metadata，public，子串+忽略大小写）；`MetadataNaming.FindTypes`（歧义判定，仿 decompile_member `LocateMembers` L134-155 先 FindTypes 后 FindMembers）；模块磁盘路径经 `Session.GetModulesAsync`。
- Produces: `debug_breakpoint_set` 新分支——typeName+memberName 命中 MethodDefinition（0x06）即设断点；多匹配回 `#MEMBER` 清单。

- [ ] **Step 1: 加 memberName 参数 + 触发分支**

参数面加 `[Description("成员名（方法，子串忽略大小写）；与 typeName 组合按成员级定位（line 默认 0 即触发）；属性/事件/字段成员会提示改用方法或访问器；多匹配返回清单用 methodToken 精确重设。")] string memberName = ""`。
触发——在既有分支前插入 **memberName 优先**（因它与 typeName 可能同时给；memberName 非空即成员级，line 忽略）：
```csharp
// 既有 L68 前插入：memberName 定位优先于 sourcePath/typeName 行定位（memberName+line 同给时以成员级为准）
if (!string.IsNullOrWhiteSpace(memberName))
    return await SetByMemberAsync(active, moduleName, typeName, memberName, hitCount, modeValue, conditionNorm, cancellationToken);
```
SetByMemberAsync 内校验 typeName 非空（缺 typeName 提示「请提供 typeName（成员级定位需类型全名）」）；moduleName 可省（跨已加载模块扫描消歧）。

- [ ] **Step 2: SetByMemberAsync 实现**

仿 SetByTypeLineAsync 模块/类型解析（ResolveModuleForLineAsync + TypeFullNamesInModule 判歧义），唯一候选类型全名 → `MemberResolver.FindMembers(modulePath, fullName, memberName)`：
- **token 种类过滤（关键）**：`MemberResolver` 枚举字段/方法/属性/事件四类，`MemberMatch` 只有 Name/Token/TypeName 三字段、无法判种类。而 `BreakpointManager.Bind` 用 `GetFunctionFromToken`，**属性(0x17)/事件(0x14) token 不能作函数断点**。故在宿主侧按 token 前缀过滤：
  - `match.Token.StartsWith("0x06")`（方法，含构造 .ctor）→ 可设断点候选；
  - 非 0x06（字段 0x04/属性 0x17/事件 0x14）→ 若唯一命中则报「成员 {name} 是{属性/事件/字段}，不能设方法断点——请用其所在方法定位（typeName+line）或 decompile_member 看访问器 token」；若与 0x06 混在匹配里则从候选剔除。
- 单方法匹配 → `Session.SetBreakpointAsync(module, token, 0, ...)` → DescribeSetWithPosition（位置文案 `类型 {fullName} 成员 {name} → {module}!0x{token:x8}+0x0`）。
- 多方法匹配 → 返回 `#MEMBER` 签名清单文本（对齐 decompile_member 超限清单格式；token 可闭环 methodToken 重设）+ 提示「匹配 N 个方法成员，用返回的 methodToken 精确重设」。
- 0 方法匹配 → 未找到 + 相近名（MemberResolver 的 SimilarNames）。
- 触发语义：typeName+memberName+line=0 时成员级优先；line>0 时维持反编译行定位（现有 SetByTypeLineAsync）。**校验 typeName 非空**。

- [ ] **Step 3: README + 测试 + 验证 + 提交**

README L117/L326 三种定位 → 四种（加 memberName）。测试：单方法命中设断点、属性/事件提示、多匹配返回清单、未找到相近名、跨模块。build + 定向 + 全量。
`git commit -m "feat: debug_breakpoint_set 增 typeName+memberName 成员级定位（E）"`

---

### Task 6: I1 — 新工具 `debug_run_to`（一次性断点 → continue → 命中移除）

> 决策：I1 用**纯宿主编排**（不加 Engine one-shot 概念）——SetBreakpointAsync 记 id → 若进程在 Stopped 则 ContinueAsync → WaitForStopAsync(timeout) → 命中（LastStop.BreakpointId==id）或超时/退出都 RemoveBreakpointAsync 兜底防残留。定位复用 E（typeName+line / typeName+memberName）。

**Files:**
- Create: `src/DotNetDebuggerMcp/Tools/Debugger/DebugRunToTool.cs`
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugBreakpointTool.cs`（暴露 E 定位内部供复用或 run_to 直调同款解析）
- Doc: `README.md`（工具表 + 参数表 + 用法段）
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

**Interfaces:**
- Consumes: `Session.SetBreakpointAsync`、`ContinueAsync`、`Buffer.WaitForStopAsync(TimeSpan)`、`Buffer.LastStop?.BreakpointId`、`RemoveBreakpointAsync`。
- Produces: 新 MCP 工具 `debug_run_to(typeName, line, memberName, moduleName?, timeoutSeconds=30)`。

- [ ] **Step 1: 新工具方法 + 定位解析复用**

`DebugRunToTool.cs`：参数 `typeName=""`/`memberName=""`/`moduleName=""`/`line=0`/`timeoutSeconds=30` + ct。把 E 的「成员级/行级解析出 (module, token, ilOffset)」抽成 DebugBreakpointTool 的 internal 助手（`ResolveBreakpointTargetAsync`），run_to 复用；无 E 时 run_to 至少支持 typeName+line（走 SetByTypeLineAsync 内部解析）。

- [ ] **Step 2: run-to 编排逻辑**

```csharp
var active = ...; if (active is null) return "当前无活动调试会话。";
// 1. 设断点（复用 E/行定位解析 → 得到 bp）
var bp = await SetOnceAsync(active, typeName, memberName, moduleName, line, ct);
if (bp is null) return 定位失败提示;
var targetId = bp.Id;
StopContext? stop = null;
try
{
    // 2. 若进程 Stopped → continue；若已 Running 直接等
    if (active.Buffer.CurrentState == DebugSessionState.Stopped)
        await active.Session.ContinueAsync(ct);
    // 3. 等命中
    stop = await active.Buffer.WaitForStopAsync(TimeSpan.FromSeconds(timeoutSeconds), ct);
    if (stop is null)
        return $"等待 {timeoutSeconds} 秒未命中（当前 {StateText(...)}）。断点 {targetId} 已保留——可再 debug_run_to 继续等，或 debug_breakpoint_remove {targetId} 放弃。";
    if (stop.BreakpointId != targetId)
        return $"已停下但命中其它断点 id={stop.BreakpointId}，非本次 run-to 目标。临时断点 {targetId} 已保留（未到目标行）；可继续 debug_continue 或 debug_breakpoint_remove {targetId} 放弃。";
    return $"已运行到目标。断点 {targetId} 命中并已自动移除。用 debug_stack/debug_variables 观察。";
}
finally
{
    // 命中/未命中/超时都清理临时断点（防残留副作用；Remove 幂等，已停/已退出皆安全）
    if (stop?.BreakpointId == targetId || stop is null || stop.BreakpointId != targetId)
        await TryRemoveSafeAsync(active, targetId, ct);
}
```
> 语义对齐 VS Run to Cursor：命中即停且临时断点自动移除；未命中超时提示「保留断点可继续等或放弃」。「其它断点先命中」诚实反馈 + 同样兜底清理。**取舍**：run_to 只对「目标会自然执行到」有效（async 用 typeName+line 断 await 行）；对「停住不会自己走」的目标无意义——Description 写明。

- [ ] **Step 3: README + 测试 + 验证 + 提交**

README 工具表加 `debug_run_to`、参数表、用法段（Run to Cursor 对标）。测试套 DebugMcpToolsTests 骨架：launch → continue → run_to(Work 方法/行) → wait 命中 → 断言 list 无残留临时断点；未命中超时路径。build + 定向 + 全量 + Client。
`git commit -m "feat: 新增 debug_run_to 运行到目标工具（一次性断点自动移除，对标 VS Run to Cursor，I1）"`

---

### Task 7: O — `debug_launch` 返回状态准确性 + Description 同步（3 处）

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugSessionTool.cs:174-183`（StateText None case）、`:30`（Description 随 D 同步）、`:48`（launch 返回）
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

- [ ] **Step 1: StateText 加 None 显式 case**

`_ => state.ToString()` 改为显式处理 `DebugSessionState.None`：
```csharp
DotNetDebugger.Engine.Models.DebugSessionState.None => "未就绪（会话刚建立，事件缓冲尚未追上——用 debug_state 查询最新状态）",
```
保留其它未知态兜底。修复「launch 返回当前状态：None」误导（R3 修过 Attaching，None 未处理）。

- [ ] **Step 2: launch 返回文案不等快照 / 语义一致**

launch 成功语义 = 进程已冻结在 Main 前（P9）。返回文案把「当前状态：{StateText(快照)}」改为固定语义说明（不依赖 buffer 竞态）：
`"已启动并附加调试会话。目标 pid={pid} 工作目录={wd}：{commandLine}。进程已冻结在 Main 前（状态 Attaching/Stopped 视事件追赶而定）；用 debug_breakpoint_set 下断点（未加载模块自动待绑定）后 debug_continue 运行。"`
即消除「None」出现在 launch 返回的可能。debug_state 仍展示实时 StateText（含新 None 文案）。

- [ ] **Step 3: 同步 README + 验证 + 提交**

README L320 debug_state 措辞若提到 Attaching 保持；launch 返回文案含「冻结在 Main 前」已有。跑宿主 e2e 确认无断言依赖旧文案。`git commit -m "fix: debug_launch 返回状态准确性（None 误导修复 + 文案随 D 同步，O）"`

---

## 收尾（全部 Task 后）

- [ ] build -c Release 全解决方案 + 各测试项目全量（Decompiler/Engine/Session/Web/宿主）+ Client 端到端 + CLI 手测（`-dbg`）。
- [ ] 核对 TODO.md：D/C/B/E/I1/O 勾选；确认「已评估关闭」区不动。
- [ ] CHANGELOG `[Unreleased]` 记 6 项（面向包使用者可见：新工具 decompile_il/debug_run_to、breakpoint_set 新定位、launch 默认工作目录、stack 真名、状态机引导）。版本号三处同步视发布节奏（本次若发布则 1.6.0）。
- [ ] 若在 feature 分支：合并 master（ff）后确认。

## Self-Review（writing-plans 内审，实施前自查）

- **Spec 覆盖**：D(workingDirectory 默认+报告)/C(真名+token 保留)/B(async 引导①②)/E(memberName 定位)/I1(run_to)/O(StateText None+Description) 全部有 Task 对应。
- **占位符**：无 TBD；每个 Step 含具体代码/文件/验证。
- **类型一致**：`ActiveDebugSession.WorkingDirectory` 在 Task1 定义 Task1 用；`SymbolNameResolver.ReadMethodName` Task2 定义 Task2 用；E 的 `ResolveBreakpointTargetAsync` Task5 产出、Task6 复用。
- **风险点已标**：C 改字段语义（token文本→真名）可能影响依赖 TypeName/MethodName 的现有消费（grep 确认宿主仅 DebugInspectTool 消费）；D 行为变化影响 attach（不设 CWD 维持）；B 的 FindTypeByToken 反查需模块路径已登记；I1 与既有断点并发命中语义。
