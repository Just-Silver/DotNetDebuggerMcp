# 实施计划 · D1 对象树深读（debug_object 受控递归）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施。Step 用 `- [ ]` 追踪。

**Goal:** 新增 `debug_object(path, depth=2, limit=32, threadId=0)`：P6 路径定位对象/数组 → 受控递归展开 children 树（depth 预算 + `<cyclic>` 环占位 + 每层 limit），让 agent 逐级/多级下钻对象结构，不盲猜路径。

**Architecture:** Engine `DebugEngineCore` 新增泵内 `ExpandNode` 受控递归展开器（路径定位复用 P6 `ResolvePathValue`；递归在值对象/数组上按 depth 层展开 children，`HashSet<long>` 沿路径记引用地址防环），`DebugSession` 暴露 `ReadObjectAtPathAsync`；宿主 `debug_object` 复用 `ExpressionParser` 解析 path 后调用并渲染。Session 层零改动（文法/渲染均在现成层）。

**Tech Stack:** C# net10.0、ClrDebug（`CorDebugReferenceValue`/`CorDebugObjectValue`/`CorDebugArrayValue`）。

**Spec:** `docs/planning/specs/2026-09-08-d1-object-drill.md`（2026-09-09 拍板：受控递归 v1，depth 默认 2、上限 `MaxDrillDepth=6`、`<cyclic>`、每层 limit）。

## Global Constraints（根 AGENTS.md + Engine/宿主 AGENTS.md 铁律）

- MCP 工具参数带默认值、`[Description]` 中文注明默认值、`CancellationToken cancellationToken = default`、返回 `Task<string>`、错误中文不抛异常。
- 改 MCP 工具同 commit 改根 `README.md`；使用者可见变更记 `CHANGELOG.md` `[Unreleased]`。
- Engine 纯能力层纪律：递归展开只在命令泵内同步执行（经 `PostAsyncResult`），不触 session/命令泵再入；Engine 不反编译（字段清单用既有 `ReadFieldTokens`/`EnumerateInstanceFields`，与 `debug_variables` 同源）。
- Engine/Session 测试真实 attach DebugTarget、必须串行；测试数据先跑 `generate-testdata.ps1`；DebugTarget 新增类型只**追加**不改既有（token 稳定）。
- 渲染复用 `DebugInspectTool.RenderVariable`（已递归处理嵌套 children）——debug_object 输出即「children 行列表」。

---

### Task 0: 工作区/分支准备

- [ ] **Step 1**: 与用户确认实施分支策略。
- [ ] **Step 2**: `generate-testdata.ps1` 就绪 + Release build 基线绿。

---

### Task 1: Engine — DebugTarget 深链样本 + 受控递归展开器

**Files:**
- Modify: `tests/TestData/generate-testdata.ps1`（DebugTarget `$dbgSrc` 追加 `DrillNode` 类型与 `drill` 分支）
- Modify: `src/DotNetDebugger.Engine/Engine/DebugEngineCore.cs`（新增 `ReadObjectAtPathAsync` 公共投泵 + `ExpandNode` 私有递归 + `MaxDrillDepth` 常量）
- Modify: `src/DotNetDebugger.Engine/Session/DebugSession.cs`（门面 `ReadObjectAtPathAsync`）
- Test: `tests/DotNetDebugger.Engine.Tests/ReadObjectDrillTests.cs`

**Interfaces:**
- Consumes: `DebugEngineCore.ResolvePathValue`/`ReadPathValue`（既有私有，P6 路径定位）；`EnumerateInstanceFields`/`ReadFieldTokens`；`DebugValue`/`DebugVariable`（Engine Models，`Children` 递归嵌套已支持）。
- Produces:
  - `Engine.DebugEngineCore` 常量 `public const int MaxDrillDepth = 6`；
  - `DebugSession.ReadObjectAtPathAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments, int depth, CancellationToken ct = default)` → `Task<DebugValue>`（终值为对象/数组时含嵌套 children 树；标量/字符串/null 抛中文 `InvalidOperationException`）。

- [ ] **Step 1: DebugTarget 追加 drill 样本（链 + 环）**

`generate-testdata.ps1` 的 `$dbgSrc` 末尾追加（新类型，token 追加不漂移）：
```csharp
// D1 对象深读样本：三层链 + 自引用环（drill 模式入口锚点 Drill(drillRoot)）
public class DrillNode
{
    public string Name = "";
    public int Value;
    public DrillNode? Next;
}
```
`Main` 的 `probe` 分支后加 `drill` 分支：
```csharp
if (args.Length > 0 && args[0] == "drill")
{
    var a = new DrillNode { Name = "A", Value = 1 };
    var b = new DrillNode { Name = "B", Value = 2 };
    var c = new DrillNode { Name = "C", Value = 3 };
    a.Next = b; b.Next = c; c.Next = a; // 故意成环：验证 <cyclic> 而非死循环
    Drill(a);
    return;
}
public static void Drill(DrillNode root)
{
    Console.WriteLine("[DebugTarget] drill " + root.Name);
    Thread.Sleep(2000); // 让调试器从容设断点/观察
}
```
重跑脚本（UTF16LE 写回同前）。确认 `Generated DebugTarget.exe`。

- [ ] **Step 2: 写失败测试（Engine）**

`ReadObjectDrillTests.cs`（骨架复刻 `EvaluatePathTests`：attach `drill 0` → 断点 `Drill` 入口 offset 0 → `WaitForHit`）：
```csharp
// depth=1：仅一层 children（与 debug_variables/Evaluate 一致）
var d1 = await session.ReadObjectAtPathAsync(tid, "root", [], 1, ct);
Assert.NotNull(d1.Children);
Assert.Contains(d1.Children!, c => c.Name == "Next");

// depth=2：Next 的 children（Name/Value/Next）也展开
var d2 = await session.ReadObjectAtPathAsync(tid, "root", [], 2, ct);
var next = d2.Children!.Single(c => c.Name == "Next");
Assert.NotNull(next.Value.Children);
Assert.Contains(next.Value.Children!, c => c.Name == "Name");

// 环：depth 足够深时环路径上出现 <cyclic> 占位而非死循环/栈溢出
var deep = await session.ReadObjectAtPathAsync(tid, "root", [], 6, ct);
Assert.Contains("cyclic", deep.Display) ; // 或沿 Next.Next… 链断言某层出现 <cyclic>（实现按 spec 用 Summary("<cyclic>", …)）

// 错误语义：标量/字符串/null 非对象目标
var scalarErr = await Assert.ThrowsAsync<InvalidOperationException>(() =>
    session.ReadObjectAtPathAsync(tid, "root", [new PathSegment.Field("Value")], 1, ct));
Assert.Contains("不是对象/数组", scalarErr.Message);
// depth 越界（>MaxDrillDepth）拒绝
```
Run，Expected: FAIL（类型/方法不存在）。注：环路径断言以实际调试目标行为为准——DebugTarget 链成环后 `Next.Next.Next` 回到 a，expander 遇到已访问地址返回 `<cyclic>`，测试落在「deep 展开能正常返回且含 cyclic 标记」。

- [ ] **Step 3: 实现受控递归展开器**

`DebugEngineCore`：
```csharp
/// <summary>debug_object 展开深度上限（防失控长链；同 DebugMCP maxDepth）。</summary>
public const int MaxDrillDepth = 6;

public Task<DebugValue> ReadObjectAtPathAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments, int depth, CancellationToken ct = default)
    => PostAsyncResult(() => ReadObjectAtPath(threadId, rootName, segments, depth), ct);

private DebugValue ReadObjectAtPath(int threadId, string rootName, IReadOnlyList<PathSegment> segments, int depth)
{
    if (_process is null) throw new InvalidOperationException("无被调试进程。");
    var d = Math.Clamp(depth, 1, MaxDrillDepth);
    var thread = FindThread(threadId) ?? throw new InvalidOperationException($"找不到线程 threadId={threadId}。");
    var raw = ResolvePathValue(thread, rootName, segments);   // P6 定位（含 $exception/字段/下标）
    if (raw is not EvalValue.Raw r)
        throw new InvalidOperationException("路径终值不可展开（字符串索引单字符等合成标量）。");
    var visited = new HashSet<long>();                        // 引用地址集合：防环
    return ExpandNode(r.Value, d, visited);
}

private DebugValue ExpandNode(CorDebugValue value, int depth, HashSet<long> visited)
{
    var shallow = ReadValue(value, expand: depth > 1);        // 到深度边界或标量时用现成浅读
    switch (value)
    {
        case CorDebugReferenceValue r when r.IsNull:
            return DebugValue.Summary("null", "null");
        case CorDebugReferenceValue r:
            return ExpandReference(r, depth, visited);
        case CorDebugObjectValue obj:                          // 装箱标量/结构体对象（罕见）
            return DebugValue.Summary("object", ReadValue(obj).Display);
        default:                                              // 标量/字符串/数组由 ReadValue 已处理
            return shallow;
    }
}

private DebugValue ExpandReference(CorDebugReferenceValue r, int depth, HashSet<long> visited)
{
    if (r.IsNull) return DebugValue.Summary("null", "null");
    var address = r.Value;                                     // 被引用对象地址（ClrDebug 属性名以本地源码为准）
    if (!visited.Add(address)) return DebugValue.Summary("cyclic", "<cyclic>"); // 环：占位不再下钻
    try
    {
        var deref = r.Dereference() ?? throw new InvalidOperationException("解引用失败。");
        return deref switch
        {
            CorDebugObjectValue obj => ExpandObject(obj, depth, visited),
            CorDebugArrayValue arr => ExpandArray(arr, depth, visited),
            CorDebugStringValue s => DebugValue.Scalar($"\"{s.GetString(s.Length)}\""),   // 字符串不再下钻
            _ => ReadValue(deref),
        };
    }
    finally { visited.Remove(address); }   // 沿路径集合：兄弟分支同地址不算环
}

private DebugValue ExpandObject(CorDebugObjectValue obj, int depth, HashSet<long> visited)
{
    var fields = EnumerateInstanceFields(obj).Take(limitOfDepth).ToList(); // limit 走读常量 MaxChildren（见 Task1 Step4 细化）——v1 用 32 沿用
    var children = new List<DebugVariable>();
    foreach (var (cls, name, token) in fields)
    {
        try
        {
            var fv = obj.GetFieldValue(cls.Raw, new mdFieldDef((uint)token));
            children.Add(new DebugVariable(name, -1,
                depth <= 1 ? ReadValue(fv) : ExpandNode(fv, depth - 1, visited), IsArgument: false));
        }
        catch (Exception ex) { children.Add(new DebugVariable(name, -1, DebugValue.Summary("error", $"<读取失败:{ex.Message}>"), IsArgument: false)); }
    }
    var display = fields.Count > children.Count ? $"字段 {fields.Count} 个（前 {children.Count}）" : $"{children.Count} 字段";
    return DebugValue.Object(display, children);
}

private DebugValue ExpandArray(CorDebugArrayValue arr, int depth, HashSet<long> visited)
{
    // 数组元素逐个展开：rank=1，线性取前 MaxChildren；元素为引用时沿 ExpandNode 继续
    // display/children 形态复用 ReadArrayValue 现逻辑，仅把「元素值」改经 ExpandNode(…, depth-1, visited)
}
```
> 实现注（动手前先查再写）：① `ReadValue(value, expand:)`/`ReadObjectValue`/`ReadArrayValue` 现为 static，字段清单机制直接复用 `EnumerateInstanceFields`（`DebugEngineCore.cs:891`）与 `ReadFieldTokens`；② 数组展开细节以现 `ReadArrayValue`（`:697`）为准（Rank/元素取法），只把元素值换成深度递归；③ `CorDebugReferenceValue` 读地址属性名以本地 ClrDebug 源码为准；④ 环检测用「引用地址 + 沿路径集合（finally remove）」语义 = 兄弟同对象不误判环、祖孙链成环才 `<cyclic>`。

- [ ] **Step 4: limit 参数透传（常量 `MaxChildren` 沿用，工具侧可调后段实现）**

v1 Engine 内部展开沿用既有 `MaxChildren=32` 每层截断（display 带「前 N 个」）；`limit` 的宿主可调值先收敛为常量默认（32）——工具参数保留但 v1 固定 32，README 注明「每层上限当前固定 32」（避免引擎模型为 limit 额外带参，后续 v1.5 再动态化）。此取舍在 Task 2 工具 Description 同步。

- [ ] **Step 5: 验证 + 提交**

Run Engine `ReadObjectDrillTests` + 既有 `EvaluatePathTests`（读不回归）+ `StateReadTests`。`git commit -m "feat: Engine 受控递归对象展开 ReadObjectAtPathAsync（depth+防环 <cyclic>，D1）"`

---

### Task 2: 宿主 — `debug_object` 工具 + README/CHANGELOG + e2e

**Files:**
- Create: `src/DotNetDebuggerMcp/Tools/Debugger/DebugObjectTool.cs`
- Modify: `README.md`、`CHANGELOG.md`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

**Interfaces:**
- Consumes: `DebugInspectTool.TryRequireStopped`；`ExpressionParser.Parse`（path → `PathNode`）；`active.Buffer.StoppedThreadId`；`active.Session.ReadObjectAtPathAsync`；`DebugInspectTool.RenderVariable`（children 渲染）；`DebugSessionService.Manager.Actions.Log`。
- Produces: MCP 工具 `debug_object(path, depth=2, limit=32, threadId=0)` → `Task<string>`。

- [ ] **Step 1: 实现工具**

```csharp
[McpServerTool]
[Description("查看对象/数组的下一级结构（受控递归下钻，进程需停）：path 用 P6 文法定位对象（根=栈顶帧局部/参数，支持 $exception），depth 递归层数（默认 2，上限 6，同路径环输出 <cyclic>），返回该对象的 children 递归清单。与 debug_evaluate 分工：debug_evaluate 取标量值，debug_object 探索对象结构。")]
public static async Task<string> DebugObject(
    [Description("对象路径（必填），如 order.Customer、$exception.InnerException（根为栈顶帧局部/参数名）。")] string path,
    [Description("递归深度（默认 2，范围 1-6）。")] int depth = 2,
    [Description("每层 children 上限（当前固定 32，v1 仅接受默认）。")] int limit = 32,
    [Description("线程 id；缺省 0 = 用最近停点线程。")] int threadId = 0,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(path)) return "缺少 path（必填）。对象路径如 order.Customer、$exception。";
    if (!DebugInspectTool.TryRequireStopped(out var active, out var error)) return error;
    var tid = threadId > 0 ? threadId : active.Buffer.StoppedThreadId;
    if (tid <= 0) return "无停点线程可读（先 debug_continue 运行至断点停下）。";
    try
    {
        if (ExpressionParser.Parse(path) is not PathNode p)
            return $"path「{path}」不是有效路径（应为变量名/字段/下标链）。";
        var d = Math.Clamp(depth, 1, DebugEngineCore.MaxDrillDepth);
        var value = await active.Session.ReadObjectAtPathAsync(tid, p.Root, p.Segments, d, cancellationToken);
        DebugSessionService.Manager.Actions.Log("debug_object", $"{path} depth={d}", "ok");
        if (value.Children is not { Count: > 0 } children) return $"对象 {path}：{value.Display}（无 children 可展开——{path} 可能是标量/空对象/字符串，见提示）。";
        var sb = new StringBuilder($"对象 {path}（depth={d}，{children.Count} 项）:");
        foreach (var c in children)
            sb.AppendLine().Append(DebugInspectTool.RenderVariable(c, depth: 1));
        return sb.ToString();
    }
    catch (ExpressionEvaluationException ex) { return ex.Message; }
    catch (InvalidOperationException ex) { return ex.Message; } // 引擎路径/类型错误（不是对象/数组等）
    catch (Exception ex) { return $"展开失败：{ex.Message}"; }
}
```

- [ ] **Step 2: README + CHANGELOG**

工具表加 `debug_object`（动态调试工具段，紧邻 debug_evaluate）；README「读值 vs 结构」一句话分工 + depth/`<cyclic>` 说明。`CHANGELOG.md` `[Unreleased]`：记「新增 debug_object 对象结构受控递归展开（depth/<cyclic> 环占位）」。

- [ ] **Step 3: 端到端失败测试 → 验证**

`DebugMcpToolsTests` 加：launch `DebugTarget.exe drill 0` → 断点 `Drill` 入口 → 停点 → `debug_object "root"` 返回含 `Next` 项；`debug_object "root" depth=3` 含 `Name`/`Value` 深两层且正常返回（环被 `<cyclic>` 截住不死循环）；`debug_object "root.Value"`（标量）返回中文「不是对象/数组」提示；非 Stopped 前置校验。Run 定向 + 宿主全量 + Client。`git commit -m "feat: 新增 debug_object 对象结构下钻工具（D1，同步 README/CHANGELOG）"`

---

## 收尾

- [ ] Release build + Engine/宿主全量单测 + Client 端到端 + `-dbg` CLI 手测 drill 停点。
- [ ] 核对 `src/DotNetDebuggerMcp/TODO.md` D1 状态；`docs/planning/specs/README.md` 收录 D1 spec 行。

## Self-Review（writing-plans 内审）

- **Spec 覆盖**：受控递归 depth（Task1 ExpandNode）、`<cyclic>` 防环（Task1 visited 地址集合）、每层预算（Task1 Step4 沿用 MaxChildren=32）、$exception/根支持（ResolvePathValue 复用）、标量/字符串/null 中文提示（Task1 Step2 测试 + 工具兜底）、分工 README（Task2）。
- **占位符**：无 TBD；唯一实现期查证点（ClrDebug 引用地址属性名/数组展开细节）已注明「以本地源码为准」并给 fallback 语义。
- **类型一致**：`ReadObjectAtPathAsync` Task1 定义 Task2 用；`MaxDrillDepth` Task1 定义 Task2 用；`RenderVariable` 渲染嵌套 children 树（DebugValue.Children 递归）。
- **风险点已标**：环检测「沿路径集合 + finally remove」语义（兄弟同对象不误判）；深度/预算双护栏；debug_target 链故意成环供 e2e 验证不死循环。
