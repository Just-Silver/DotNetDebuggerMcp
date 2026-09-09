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
  - `DebugSession.ReadObjectAtPathAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments, int depth, int limit = 32, CancellationToken ct = default)` → `Task<DebugValue>`（终值为对象/数组时含嵌套 children 树；标量/字符串/null 抛中文 `InvalidOperationException`「…不是对象/数组…」）。

- [ ] **Step 1: DebugTarget 追加 drill 样本（链 + 环）**

`generate-testdata.ps1` 的 `$dbgSrc` 末尾追加（新类型，token 追加不漂移）：
```csharp
// D1 对象深读样本：三层链 + 自引用环 + 数组下钻（drill 模式入口锚点 Drill(drillRoot, arr)）
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
    Drill(a, new[] { 3, 1, 4 });
    return;
}
public static void Drill(DrillNode root, int[] arr)
{
    Console.WriteLine("[DebugTarget] drill " + root.Name + " arr0=" + arr[0]);
    Thread.Sleep(2000); // 让调试器从容设断点/观察
}
```
重跑脚本（UTF16LE 写回同前）。确认 `Generated DebugTarget.exe`。

- [ ] **Step 2: 写失败测试（Engine）**

`ReadObjectDrillTests.cs`（骨架复刻 `EvaluatePathTests`：attach **`drill 5`** → 断点 `Drill` 入口 offset 0 → `WaitForHit`。**注意时序**：`drill 0` 无延迟，attach 窗口过后 `Drill` 入口已执行过、断点不再命中——沿用 bag 模式 `delay 5s`）：
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

// 数组：arr 根 depth=1 → 3 个元素
var arr1 = await session.ReadObjectAtPathAsync(tid, "arr", [], 1, ct);
Assert.NotNull(arr1.Children);
Assert.Equal(3, arr1.Children!.Count);

// 环：沿 Next 链递归找 <cyclic> 占位（根的 Display 是「N 字段」，不会含 cyclic）
var deep = await session.ReadObjectAtPathAsync(tid, "root", [], 6, ct);
Assert.True(ContainsCyclic(deep));

// 错误语义：标量/字符串/null 非对象目标（spec §4.1 同文案）
var scalarErr = await Assert.ThrowsAsync<InvalidOperationException>(() =>
    session.ReadObjectAtPathAsync(tid, "root", [new PathSegment.Field("Value")], 1, ct));
Assert.Contains("不是对象/数组", scalarErr.Message);
var nullErr = await Assert.ThrowsAsync<InvalidOperationException>(() =>
    session.ReadObjectAtPathAsync(tid, "nullRoot", [], 1, ct)); // Drill 传入 null 的场景见样本注释——或用环终点
Assert.Contains("不是对象/数组", nullErr.Message);

static bool ContainsCyclic(DebugValue v) =>
    v.Display == "<cyclic>" || (v.Children?.Any(c => ContainsCyclic(c.Value)) ?? false);
```
Run，Expected: FAIL（类型/方法不存在）。注：DebugTarget 链成环后 `Next.Next.Next` 回到 a，expander 遇已访问地址返回 `<cyclic>`；null 场景样本若不便造则删 nullErr 用例、以字符串/标量错误覆盖 spec 语义。

- [ ] **Step 3: 实现受控递归展开器**

`DebugEngineCore`：
```csharp
/// <summary>debug_object 展开深度上限（防失控长链；同 DebugMCP maxDepth）。</summary>
public const int MaxDrillDepth = 6;

public Task<DebugValue> ReadObjectAtPathAsync(int threadId, string rootName, IReadOnlyList<PathSegment> segments,
    int depth, int limit = 32, CancellationToken ct = default)
    => PostAsyncResult(() => ReadObjectAtPath(threadId, rootName, segments, depth, limit), ct);

private DebugValue ReadObjectAtPath(int threadId, string rootName, IReadOnlyList<PathSegment> segments, int depth, int limit)
{
    if (_process is null) throw new InvalidOperationException("无被调试进程。");
    var d = Math.Clamp(depth, 1, MaxDrillDepth);
    var lim = Math.Clamp(limit, 1, 128);
    var thread = FindThread(threadId) ?? throw new InvalidOperationException($"找不到线程 threadId={threadId}。");
    var raw = ResolvePathValue(thread, rootName, segments);   // P6 定位（含 $exception/字段/下标）
    if (raw is not EvalValue.Raw r)
        throw new InvalidOperationException("路径终值不可展开（字符串索引单字符等合成标量）。");
    var value = ValidateExpandable(r.Value, rootName, segments);  // 终值必须是对象/数组（2026-09-09 审查修正）
    var visited = new HashSet<long>();                        // 引用地址集合：防环
    return value is CorDebugObjectValue obj ? ExpandObject(obj, d, visited, lim)
         : value is CorDebugArrayValue arr ? ExpandArray(arr, d, visited, lim)
         : throw new InvalidOperationException("该路径不是对象/数组（当前为标量/字符串/null），请给对象或数组路径。");
}

/// <summary>解引用并校验：null 引用 / 标量 / 字符串 → 中文报错；返回可展开对象/数组。</summary>
private static CorDebugValue ValidateExpandable(CorDebugValue target, string rootName, IReadOnlyList<PathSegment> segments)
{
    if (target is CorDebugReferenceValue rr && rr.IsNull)
        throw new InvalidOperationException($"路径 {Describe(rootName, segments)} 为 null——请给对象或数组路径。");
    var deref = target is CorDebugReferenceValue r2 ? r2.Dereference() : target;
    return deref switch
    {
        CorDebugObjectValue or CorDebugArrayValue => deref!,
        CorDebugStringValue => throw new InvalidOperationException(
            $"路径 {Describe(rootName, segments)} 是字符串——请给对象或数组路径（或取其字段/索引）。"),
        _ => throw new InvalidOperationException(
            $"路径 {Describe(rootName, segments)} 不是对象/数组（当前为标量）——请给对象或数组路径。"),
    };
}

private DebugValue ExpandObject(CorDebugObjectValue obj, int depth, HashSet<long> visited, int limit)
{
    var fields = EnumerateInstanceFields(obj);
    var shown = fields.Take(limit).ToList();
    var children = new List<DebugVariable>();
    foreach (var (cls, name, token) in shown)
    {
        try
        {
            var fv = obj.GetFieldValue(cls.Raw, new mdFieldDef((uint)token));
            children.Add(new DebugVariable(name, -1,
                depth <= 1 ? ReadValue(fv) : ExpandNode(fv, depth - 1, visited, limit), IsArgument: false));
        }
        catch (Exception ex) { children.Add(new DebugVariable(name, -1, DebugValue.Summary("error", $"<读取失败:{ex.Message}>"), IsArgument: false)); }
    }
    var display = fields.Count > shown.Count ? $"字段 {fields.Count} 个（前 {shown.Count}）" : $"{children.Count} 字段";
    return DebugValue.Object(display, children);
}

private DebugValue ExpandArray(CorDebugArrayValue arr, int depth, HashSet<long> visited, int limit)
{
    // rank=1 线性取前 limit 元素；元素为引用/对象时沿 ExpandNode 继续（depth-1）；标量元素浅读
    if (arr.Rank != 1)
        return DebugValue.Summary("array", $"多维数组 Rank={arr.Rank} v1 不展开（可用 debug_evaluate [i] 直读）");
    var children = new List<DebugVariable>();
    var total = arr.Count;
    for (var i = 0; i < total && children.Count < limit; i++)
    {
        try
        {
            var el = arr.GetElementAtPosition(i);
            children.Add(new DebugVariable($"[{i}]", -1,
                depth <= 1 ? ReadValue(el) : ExpandNode(el, depth - 1, visited, limit), IsArgument: false));
        }
        catch (Exception ex) { children.Add(new DebugVariable($"[{i}]", -1, DebugValue.Summary("error", $"<读取失败:{ex.Message}>"), IsArgument: false)); }
    }
    var display = total > children.Count ? $"数组 {total} 项（前 {children.Count}）" : $"数组 {total} 项";
    return DebugValue.Object(display, children);
}

private DebugValue ExpandNode(CorDebugValue value, int depth, HashSet<long> visited, int limit)
{
    // 不再无条件先浅读（避免每层重复展开）；default 叶（标量/字符串）按需浅读
    switch (value)
    {
        case CorDebugReferenceValue r when r.IsNull:
            return DebugValue.Summary("null", "null");
        case CorDebugReferenceValue r:
        {
            var address = r.Value;                                   // 被引用对象地址（ClrDebug 属性名以本地源码为准）
            if (!visited.Add(address)) return DebugValue.Summary("cyclic", "<cyclic>"); // 环：占位不再下钻
            try
            {
                var deref = r.Dereference() ?? throw new InvalidOperationException("解引用失败。");
                return deref switch
                {
                    CorDebugObjectValue obj => ExpandObject(obj, depth, visited, limit),
                    CorDebugArrayValue a => ExpandArray(a, depth, visited, limit),
                    CorDebugStringValue s => DebugValue.Scalar($"\"{s.GetString(s.Length)}\""), // 字符串不再下钻
                    _ => ReadValue(deref),
                };
            }
            finally { visited.Remove(address); }   // 沿路径集合：兄弟分支同地址不算环
        }
        case CorDebugObjectValue obj: return ExpandObject(obj, depth, visited, limit);  // 装箱结构对象
        case CorDebugArrayValue arr: return ExpandArray(arr, depth, visited, limit);
        default: return ReadValue(value);          // 标量叶：不展开
    }
}

private static string Describe(string root, IReadOnlyList<PathSegment> segments) =>
    root + string.Concat(segments.Select(s => s switch { PathSegment.Field f => "." + f.Name, PathSegment.Index i => $"[{i.Position}]", _ => "" }));
```
> 实现注（动手前先查再写）：① **前置小重构**：`DebugEngineCore` 现无 `ResolvePathValue`/`FindThread`——把 `ReadPathValue`（`:771`）的「根解析+逐段求值 → `EvalValue`」抽成私有 `ResolvePathValue(CorDebugThread, rootName, segments)`、线程查找提为 `FindThread(threadId)`（评审修正，勿依赖不存在的名字）；② `ReadValue`/`EnumerateInstanceFields`/`ReadFieldTokens` 现成复用；③ `CorDebugReferenceValue` 读地址属性名以本地 ClrDebug 源码为准；④ 环检测 = 引用地址 + 沿路径集合（finally remove）——兄弟同对象不误判环、祖孙链成环才 `<cyclic>`。

- [ ] **Step 4: limit 语义（动态每层，2026-09-09 审查修正）**

limit 为**真实参数**：`ReadObjectAtPathAsync(…, depth, limit=32)` 与 `ExpandObject/ExpandArray/ExpandNode` 逐层透传（Step 3 代码已含），宿主钳制 1-128；超限 display 提示「共 N 个（前 M）」。无 no-op /「固定 32」描述。

- [ ] **Step 5: 验证 + 提交**

Run Engine `ReadObjectDrillTests` + 既有 `EvaluatePathTests`（读不回归）+ `StateReadTests`。`git commit -m "feat: Engine 受控递归对象展开 ReadObjectAtPathAsync（depth+limit+防环 <cyclic>，D1）"`

---

### Task 2: 宿主 — `debug_object` 工具 + README/CHANGELOG + e2e

**Files:**
- Create: `src/DotNetDebuggerMcp/Tools/Debugger/DebugObjectTool.cs`
- Modify: `README.md`、`CHANGELOG.md`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`

**Interfaces:**
- Consumes: `DebugInspectTool.TryRequireStopped`；`ExpressionParser.Parse`（path → `PathNode`，**`$exception` 前缀特判见 Step 1**）；`active.Buffer.StoppedThreadId`；`active.Session.ReadObjectAtPathAsync`；`DebugInspectTool.RenderVariable`（children 渲染）；`DebugSessionService.Manager.Actions.Log`。
- Produces: MCP 工具 `debug_object(path, depth=2, limit=32, threadId=0)` → `Task<string>`。

- [ ] **Step 1: 实现工具（含 `$exception` 前缀特判）**

`ExpressionParser` 分词器不接受 `$` 开头（`$exception` 根只能由引擎直传 rootName 到达）——工具层对 `$exception` / `$exception.…` 前缀特判：根固定 `$exception`，剩余段串经 `ExpressionParser.Parse` 复用其 Segments（2026-09-09 审查修正）：
```csharp
// 解析 path → (root, segments)；支持 $exception 伪根（spec 拍板 #2）
private static (string Root, IReadOnlyList<PathSegment> Segments)? ParseObjectPath(string path)
{
    if (path == "$exception") return ("$exception", []);
    if (path.StartsWith("$exception.", StringComparison.Ordinal))
    {
        var rest = path["$exception.".Length..];
        return ExpressionParser.Parse(rest) is PathNode p ? ("$exception", p.Segments) : null;
    }
    return ExpressionParser.Parse(path) is PathNode p ? (p.Root, p.Segments) : null;
}
```
工具方法：
```csharp
[McpServerTool]
[Description("查看对象/数组的下一级结构（受控递归下钻，进程需停）：path 用 P6 文法定位对象（根=栈顶帧局部/参数，支持 $exception 伪根），depth 递归层数（默认 2，上限 6），limit 每层字段/元素上限（默认 32，范围 1-128），同路径环输出 <cyclic>。返回 children 递归清单。与 debug_evaluate 分工：debug_evaluate 取标量值，debug_object 探索对象结构。")]
public static async Task<string> DebugObject(
    [Description("对象路径（必填），如 order.Customer、$exception.InnerException（根为栈顶帧局部/参数名）。")] string path,
    [Description("递归深度（默认 2，范围 1-6）。")] int depth = 2,
    [Description("每层字段/元素上限（默认 32，范围 1-128）。")] int limit = 32,
    [Description("线程 id；缺省 0 = 用最近停点线程。")] int threadId = 0,
    CancellationToken cancellationToken = default)
{
    if (string.IsNullOrWhiteSpace(path)) return "缺少 path（必填）。对象路径如 order.Customer、$exception。";
    if (!DebugInspectTool.TryRequireStopped(out var active, out var error)) return error;
    var tid = threadId > 0 ? threadId : active.Buffer.StoppedThreadId;
    if (tid <= 0) return "无停点线程可读（先 debug_continue 运行至断点停下）。";
    try
    {
        var target = ParseObjectPath(path);
        if (target is null) return $"path「{path}」不是有效路径（应为变量名/字段/下标链）。";
        var d = Math.Clamp(depth, 1, DebugEngineCore.MaxDrillDepth);
        var lim = Math.Clamp(limit, 1, 128);
        var value = await active.Session.ReadObjectAtPathAsync(tid, target.Value.Root, target.Value.Segments, d, lim, cancellationToken);
        DebugSessionService.Manager.Actions.Log("debug_object", $"{path} depth={d} limit={lim}", "ok");
        if (value.Children is not { Count: > 0 } children)
            return $"对象 {path}（depth={d}）：{value.Display}（空对象/空数组，无 children）。"; // 标量/字符串/null 已由引擎抛错
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

- **Spec 覆盖**：受控递归 depth（Task1 ExpandObject/Array/Node）、`<cyclic>` 防环（visited 地址集合 + finally remove）、每层 limit 动态透传 1-128（Task1 Step3/4 + Task2）、$exception 伪根（Task2 ParseObjectPath 前缀特判）、标量/字符串/null 终值中文提示（Task1 ValidateExpandable + 工具兜底）、分工 README（Task2）。
- **占位符**：无 TBD；ClrDebug 引用地址属性名/解引用 API 以本地源码为准（已给 fallback 语义）；数组展开已写实（ExpandArray）。
- **类型一致**：`ReadObjectAtPathAsync(…, depth, limit=32)` Task1 定义 Task2 用；`MaxDrillDepth` Task1 定义 Task2 用；`ParseObjectPath` Task2 定义 Task2 用；`RenderVariable` 渲染嵌套 children 树（DebugValue.Children 递归）。
- **风险点已标**：环检测「沿路径集合 + finally remove」语义；深度/limit 双护栏；`drill 0` 时序（Engine 测试用 `drill 5`）；`ReadPathValue` 需先小重构出 `ResolvePathValue`/`FindThread`。
