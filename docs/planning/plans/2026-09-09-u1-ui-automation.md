# 实施计划 · U1 UI 自动化主动触发业务操作（ui_find/ui_invoke/ui_wait/ui_scroll）

> **For agentic workers:** REQUIRED SUB-SKILL: 用 superpowers:executing-plans 逐 Task 实施（或 subagent-driven-development 逐 Task 派发）。Step 用 `- [ ]` 追踪。

**Goal:** 新增宿主 `UiAutomationService`（FlaUI 封装）+ `ui_find`/`ui_invoke(action)`/`ui_wait`/`ui_scroll` 四工具，agent 驱动任意 .NET UI 目标（WPF/WinForms/…）的控件定位与**多键操作**（左键/右键/双击）与**滚动**，并与 debug_* 编排成「点按钮→断点命中→看现场」闭环。ui_find 命中元素附**务实语义标注**（反查 Name/Text 同名成员候选）。

**Architecture:** 全部在宿主层：FlaUI 引用姿势照抄 UIInspect.MCP（PackageDownload+HintPath——PackAsTool 拒绝 platform-qualified TFM）；`UiAutomationService` 单自动化实例 + 跨进程 UIA 5s 超时护栏 + 全操作串行锁 + Find 结果缓存（index 复用）+ 点击分派（Invoke/物理左/右/双击）+ `Mouse.Scroll` 滚轮；`UiSemanticResolver` 用 PEReader 对主模块做成员名反查（缓存倒排索引）；四工具薄包装。Engine/Session 零改动。

**Tech Stack:** C# net10.0、FlaUI.Core/UIA3 5.0.0（net8.0-windows7.0 二进制）、FlaUI `Mouse.RightClick/DoubleClick/Scroll`（main 源码已核实）、kernel32 SetForegroundWindow、ModelContextProtocol.Server。

**Spec:** `docs/planning/specs/2026-09-08-u1-ui-automation.md`（2026-09-09 拍板：不需会话 / AgentActionLog+文档护栏 / 务实成员反查自动标注 v1 / U1 先行 / 追加：action 左·右·双击 + ui_scroll 滚轮 = 四件套）。
**参考源码（动手前必读）：** `../../Externals/DebuggerExternals/UIInspect.MCP/`（`FlaUiAutomationBackend.cs`、`FlaUiAutomationSession.cs`、`UIInspect.MCP.Windows.csproj` 的 FlaUI 引用段）。

## Global Constraints（根 AGENTS.md + 宿主 AGENTS.md 铁律）

- MCP 工具参数带默认值、`[Description]` 中文注明默认值、`CancellationToken cancellationToken = default`、返回 `Task<string>`、错误中文不抛异常。
- 新工具落地必须：同 commit 改根 `README.md`（工具表+参数+用法+风险）+ `CHANGELOG.md` 新开/补 `[Unreleased]` + **同批补 V4 语料断言**（D21 政策）。
- 宿主 TFM 保持 net10.0（PackAsTool）；FlaUI 引用用 PackageDownload+HintPath，不引入 platform-qualified TFM。
- UIA 跨进程调用一律带超时护栏（Connection/Transaction 5s + 外层 WaitAsync 兜底）；UiAutomationService 内部对全部操作串行化（SemaphoreSlim(1,1)）——MCP server 工具可能并发调用，FlaUI 自动化实例不并发访问。
- 副作用护栏：ui_invoke/ui_scroll（ui_wait 只读轮询）全程打 `AgentActionLog`；Description 明示产线副作用与「物理右键/双击/滚轮可能触发系统级行为」；不做 Consent（v2）。
- 测试目标（新 WinForms sample）为 net10.0-windows 小工程，由 `generate-testdata.ps1` 构建产出到 `tests/TestData/UiSampleApp/`；**UiSampleApp 不进 git**（与 DebugTarget 同 gitignore 模式）。

---

### Task 0: 工作区/分支准备

- [ ] **Step 1**: 与用户确认实施分支策略。
- [ ] **Step 2**: `generate-testdata.ps1` 就绪 + Release build 基线绿；读 UIInspect.MCP 三个源码文件做前置参考。

---

### Task 1: FlaUI 依赖落地（引用姿势照抄 UIInspect.MCP）

**Files:**
- Modify: `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`（FlaUI 引用段）

- [ ] **Step 1: 按 UIInspect.MCP 实文引入引用段并验证编译**

按线上实读的 `UIInspect.MCP.Windows.csproj`（main，TFM 同为 net10.0）逐条照抄：
```xml
<PackageDownload Include="FlaUI.Core" Version="[$(FlaUIVersion)]" />
<PackageDownload Include="FlaUI.UIA3" Version="[$(FlaUIVersion)]" />
<PackageReference Include="Interop.UIAutomationClient" />
<PackageReference Include="System.Management" />
<Reference Include="FlaUI.Core" HintPath="$(NuGetPackageRoot)flaui.core\$(FlaUIVersion)\lib\net8.0-windows7.0\FlaUI.Core.dll" Private="true" />
<Reference Include="FlaUI.UIA3" HintPath="$(NuGetPackageRoot)flaui.uia3\$(FlaUIVersion)\lib\net8.0-windows7.0\FlaUI.UIA3.dll" Private="true" />
```
`FlaUIVersion` 属性 = `5.0.0`。**原理**（csproj 注释，勿删）：PackAsTool 拒绝 platform-qualified TFM → 宿主保持 net10.0；net10.0 无法经 PackageReference 消费 net8.0-windows7.0 资产 → PackageDownload 把 FlaUI 当 path-only 输入、HintPath 显式绑定兼容程序集，避开 AssetTargetFallback/NU1701。`Private="true"` 保证 dll 随 tool 输出。

**Task 0 Step 2 / Task 1 前置（2026-09-09 审查修正）**：本地 `../../Externals/DebuggerExternals/` **无 UIInspect.MCP**——引用段与 FlaUI API 已按线上 main 源码核实并内联在本计划；如需参考实现细节，线上取 `ChrisPulman/UIInspect.MCP` main 的 `FlaUiAutomationBackend.cs`/`FlaUiAutomationSession.cs`/`UIInspect.MCP.Windows.csproj`（显式 fallback 来源）。若 `Interop.UIAutomationClient`/`System.Management` 两个 `PackageReference` 在 net10.0（非 windows TFM）报 NU1701/无兼容资产，改 PackageDownload+HintPath 同法兜底。

验证：`dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` 绿；输出目录含 `FlaUI.Core.dll`/`FlaUI.UIA3.dll`/`Interop.UIAutomationClient.dll`。`git commit -m "build: 宿主引入 FlaUI 5.0（PackageDownload+HintPath，U1）"`

---

### Task 2: UiAutomationService + UiSemanticResolver（宿主组件）

**Files:**
- Create: `src/DotNetDebuggerMcp/Services/UiAutomationService.cs`
- Create: `src/DotNetDebuggerMcp/Services/UiSemanticResolver.cs`
- Test: `tests/DotNetDebuggerMcp.Tests/UiAutomationServiceTests.cs`（可纯单测部分：语义倒排索引构建/查询；UIA 实调用走 e2e）

**Interfaces:**
- Produces（`UiAutomationService`，internal，单例 `Instance` 或经宿主 Services 静态挂）：
  - `Task<IReadOnlyList<UiElementInfo>> FindAsync(string process, string title, string text, string type, string automationId, int limit, CancellationToken ct)`
  - `Task<UiActionResult> InvokeAsync(string process, int index, string name, string type, string action, CancellationToken ct)`（action ∈ click/rightClick/doubleClick）
  - `Task<UiActionResult> ScrollAsync(string process, int index, string name, string type, string direction, int lines, CancellationToken ct)`
  - `Task<UiWaitResult> WaitAsync(string process, string text, string type, string textChangedFrom, string textChangedTo, int timeoutSeconds, CancellationToken ct)`
  - 模型（同文件）：`UiElementInfo(int Index, string Name, string Type, string AutoId, string Rect, string CanInvoke, string? Semantic)`、`UiActionResult(bool Ok, string Message)`（Message 注明**实际动作**：Invoke/物理左键/物理右键/双击/滚轮方向·行数）、`UiWaitResult(string Outcome, string Message)`（Outcome ∈ 出现/已变化/超时/失败）
- `UiSemanticResolver`（internal）：
  - `IReadOnlyList<string>? Lookup(string assemblyPath, string elementName)`（返回 `类型全名.成员` 候选；assemblyPath 无/不可读/无匹配 → null）

- [ ] **Step 1: UiSemanticResolver（先做纯逻辑，可单测）**

用 PEReader（仓库既有模式：`SymbolNameResolver.ReadArgNames`）扫主模块（**DebugTarget 等 apphost 的元数据在** `DebugTarget.dll`，exe 是原生 PE 无元数据——单测素材用 dll）全部 TypeDefinitions，收集 字段/属性/方法/事件 的名字；对 `elementName` 做 **子串忽略大小写**匹配（`member.Contains(elementName, OrdinalIgnoreCase)`，对齐 spec §5「子串忽略大小写」；2026-09-09 审查修正），产出候选 `{TypeFullName}.{Member}`；按 assemblyPath 做进程内缓存（`ConcurrentDictionary<string, 倒排索引>`，assembly 文件路径为键——metadata 只读一次）。查询无匹配或模块不可读返回 null。
单测：对 `DebugTarget.dll`（`TestPaths.DebugTargetExe` 换扩展名 `.dll`）反查 `WorkBag` → 候选含 `Program.WorkBag`（子串/忽略大小写）；未知名 `NoSuchMember` → null；重复查询命中缓存（不重复读盘）。

- [ ] **Step 2: UiAutomationService 核心骨架**

```csharp
internal sealed class UiAutomationService
{
    private readonly SemaphoreSlim _gate = new(1, 1);
    private UIA3Automation? _automation;
    private List<(int Pid, UiElementInfo Info, AutomationElement El)> _lastFind = new(); // index 复用（ui_invoke index>=0）
    private int _lastFindPid;

    private async Task<UIA3Automation> AutomationAsync(CancellationToken ct)
    {
        await _gate.WaitAsync(ct);
        _automation ??= new UIA3Automation
        {
            ConnectionTimeout = TimeSpan.FromSeconds(5),
            TransactionTimeout = TimeSpan.FromSeconds(5),
        };
        return _automation;
    }
    // FindAsync/InvokeAsync/ScrollAsync/WaitAsync 均在 _gate 内串行；全部 UIA 调用外层包
    // await Task.WhenAny(op, Task.Delay(5s)) 兜底 + WaitAsync(ct)（OperationCanceled 转中文提示）
}
```
窗口定位：按 `process`（pid 或进程名子串）`automation.GetDesktop().FindAllDescendants(cf => cf.ByControlType(ControlType.Window).And(cf.ByProcessId(pid)))`；`title` 非空再 `ByName(title)` 精确过滤；命中多窗口取第一个 + 提示。元素查找：窗口 `FindAllDescendants(ChainableCondition)`，type/text/automationId 组合 `And`；输出清单字段见 spec §2.3（`element.Name`、`Properties.AutomationId`、`ControlType`、`ClassName`、`FrameworkId`、`BoundingRectangle`、`IsEnabled`、`ProcessId`、`NativeWindowHandle`、`Patterns.Invoke.IsSupported`）。find 缓存 `_lastFind`（pid、元素 + `UiElementInfo`），供 `ui_invoke index`/`ui_scroll index`。

语义标注：find 收集到元素后，对每个元素 Name/AutomationId 去重 → `UiSemanticResolver.Lookup(元数据模块路径, name)`。**元数据模块路径取法（2026-09-09 审查修正）**：`Process.GetProcessById(pid).MainModule?.FileName` 是 apphost 原生 exe（无 CLI 元数据）——先取主模块路径；若扩展名是 `.exe`，则同目录**同名 `.dll`**（`Path.ChangeExtension(path, ".dll")`）存在就用 dll（现代 .NET 应用 exe+dll+runtimeconfig 布局）；dll 不存在再降级回主模块/异常降级 null。命中候选取首个附到 `Semantic` 字段（候选 >3 时附前 3 + “…”）。

- [ ] **Step 3: InvokeAsync（action 分派）/ ScrollAsync / WaitAsync**

`InvokeAsync`：index>=0 时取 `_lastFind[index]`（pid 不匹配则清缓存重新查并提示）；否则按 name/type 即时唯一命中（歧义 → 返回候选清单提示用 index）。**action 分派**（FlaUI main 源码已核实）：
```csharp
static void ActOn(UiElement el, string action) => action switch
{
    "click" when el.Patterns.Invoke.IsSupported => el.Patterns.Invoke.PatternOrDefault?.Invoke(), // Invoke 语义优先
    _ => PerformPhysicalMouse(el, action), // 统一物理鼠标：先 SetForegroundWindow/还原最小化，再 GetClickablePoint（失败取 BoundingRectangle 中心）
};
// click 兜底 = Mouse.LeftClick(pt)；rightClick = Mouse.RightClick(pt)；doubleClick = Mouse.DoubleClick(pt)
```
返回 Message 注明实际动作：`已 Invoke` / `已物理左键点击` / `已物理右键点击（可能弹系统菜单）` / `已物理双击`。失败回滚提示（无 clickable point/窗口不可激活等中文原因）。
`ScrollAsync`：目标元素解析同 InvokeAsync；**v1 物理滚轮** `Mouse.Scroll(direction == "up" ? lines : -lines)`（**方向修正 2026-09-09**：FlaUI `MouseTests` 实证 `Scroll(+n)` 上滚、`Scroll(-n)` 下滚——`MOUSEEVENTF_WHEEL` 正负约定；原计划写反）于元素中心（缺省=窗口内首个可滚动区/窗口工作区中心）；先 SetForegroundWindow；返回「已向上/向下滚动 N 行 @ 控件 X（物理滚轮）」。滚动不改变元素树——提示 agent 用 `ui_find` 复查目标控件。
`WaitAsync`：轮询间隔 200ms（仍属 UIA 查询带 5s/次护栏），到 `timeoutSeconds`（默认 30）止：`text` 模式 = 窗口内出现该 text 元素即命中；`textChangedFrom/To` = 首个匹配控件 `Name` 从 From 变 To；超时返回「超时未命中（当前仍:…）」不报错。

- [ ] **Step 4: 单测 + 提交**

纯单测覆盖 UiSemanticResolver 正/负/缓存路径；UiAutomationService 的并发串行/超时护栏以 e2e 覆盖（Task 4）。`git commit -m "feat: UiAutomationService + UiSemanticResolver（FlaUI 封装+语义反查，U1）"`

---

### Task 3: UI 测试目标 UiSampleApp（generate-testdata.ps1 产出）

**Files:**
- Create: `tests/TestData/UiSampleApp/UiSampleApp.csproj`（net10.0-windows、UseWindowsForms、OutputType WinExe）
- Create: `tests/TestData/UiSampleApp/Program.cs`（主窗口 Title=`UiSample`：按钮 `手动`/`自动`（Text 随 State 切换）；按钮支持**右键**（右击切换另一状态 Label）；`ListBox` 100 项供**滚动**测试；一个输入框；进程自报窗口标题 `UiSample`）
- Modify: `tests/TestData/generate-testdata.ps1`（DebugTarget 段后加 UiSampleApp 构建+拷贝；编码 UTF16LE 写回）
- Modify: `.gitignore`（`tests/TestData/UiSampleApp/bin`、`obj`、产出 exe/dll——若不把源码目录全忽略则忽略产出）

- [ ] **Step 1: 建工程**

最小 WinForms（自绘/标准控件皆可）：主窗口 Title=`UiSample`；按钮 A 文本绑定 `_state`（初始「手动」点击切换为「自动」），按钮 A **右键**切换另一 Label 文本（验证右击）；**ListBox 100 项**——单击/滚动锚点，**双击某项** → 计数 Label `双击:0` 变 `双击:1`（double-click 锚点，2026-09-09 审查修正）；按钮 B `计数`点击 +1 显示在 Label；TextBox + 按钮 `输入`。断点观察用：按钮 Click 处理程序 `private void OnToggleState(...)`、`OnToggleStateRightClick(...)`、`OnListDoubleClick(...)`。

- [ ] **Step 2: generate-testdata.ps1 接入**

在脚本 DebugTarget 段后追加：`dotnet build tests/TestData/UiSampleApp` → 拷贝 `UiSampleApp.exe/.dll/runtimeconfig.json` 到 `tests/TestData/UiSampleApp/`。跑脚本验证产出；`git commit -m "test: UiSampleApp UI 测试目标（WinForms，U1）"`

---

### Task 4: ui_find / ui_invoke(action) / ui_wait / ui_scroll 工具 + README/CHANGELOG

**Files:**
- Create: `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs`（四个 `[McpServerTool]`）
- Modify: `README.md`（工具表 + 参数 + 用法 + 副作用/无视觉定位说明）
- Modify: `CHANGELOG.md`
- Test: `tests/DotNetDebuggerMcp.Tests/DebugUiToolsTests.cs`（真实起 UiSampleApp 的 e2e）

**Interfaces:**
- Consumes: `UiAutomationService`；`DebugSessionService.Manager.Actions.Log`（副作用轨迹）。

- [ ] **Step 1: 工具实现（要点）**

```csharp
// 通用前置：process 解析（"1234"=pid 否则进程名子串 → pid；找不到中文提示）；不要求活动 debug 会话。
[Description("按进程/窗口条件查找 UI 控件清单（无视觉——返回文本清单供选择，含 index/Name/Type/AutoId/Rect/可操作类型与语义候选）。进程需已运行且是 .NET UI 应用（WPF/WinForms/…）。")]
public static async Task<string> UiFind(
    [Description("目标进程：pid 或进程名（必填），如 CoreMes、1234。")] string process,
    [Description("窗口标题精确匹配；空 = 该进程首个顶层窗口。")] string title = "",
    [Description("控件文本/名称（Name，子串忽略大小写）。")] string text = "",
    [Description("控件类型（如 Button/TextBlock/TextBox/Window）。")] string type = "",
    [Description("AutomationId 精确匹配（可为空——WPF 常不设）。")] string automationId = "",
    [Description("返回条数上限（默认 50）。")] int limit = 50,
    CancellationToken cancellationToken = default)

[Description("对 UI 控件执行点击（真实操作，有产线副作用）。action：click（默认，Invoke 优先）/ rightClick / doubleClick（后两者物理鼠标——可能触发系统级行为如系统上下文菜单）。index=上次 ui_find 结果序号；或给 name/type 即时唯一定位（歧义返回候选请用 index）。")]
public static async Task<string> UiInvoke(
    [Description("目标进程：pid 或进程名（必填）。")] string process,
    [Description("上次 ui_find 返回序号（>=0 优先于 name 定位）。")] int index = -1,
    [Description("控件名/文本（子串忽略大小写）。")] string name = "",
    [Description("控件类型。")] string type = "",
    [Description("动作：click（默认）/ rightClick / doubleClick。")] string action = "click",
    CancellationToken cancellationToken = default)

[Description("滚动 UI 容器（长列表/文本定位；真实滚轮，有产线副作用）。目标=容器元素（List/DataGrid/TextBox 等，index 或 name/type 定位，缺省=窗口内首个可滚区）；v1 物理滚轮，direction=up/down，lines=行数。滚动后请用 ui_find 复查目标控件。")]
public static async Task<string> UiScroll(
    [Description("目标进程：pid 或进程名（必填）。")] string process,
    [Description("上次 ui_find 返回序号（>=0 优先于 name 定位）；-1 = 窗口内首个可滚区。")] int index = -1,
    [Description("容器名/文本（子串忽略大小写）。")] string name = "",
    [Description("容器类型。")] string type = "",
    [Description("滚动方向：up / down。")] string direction = "down",
    [Description("滚动行数（默认 3）。")] int lines = 3,
    CancellationToken cancellationToken = default)

[Description("等待 UI 状态变化/控件出现（轮询，超时返回当前状态不报错）。text=期望出现的控件文本；textChangedFrom/textChangedTo=控件文本从 X 变 Y（点击后的状态确认）。")]
public static async Task<string> UiWait(/* process, text, type, textChangedFrom, textChangedTo, timeoutSeconds=30 */)
```
每个工具成功/失败都 `Actions.Log("ui_find"/"ui_invoke"/"ui_scroll"/"ui_wait", args, result)`；action/direction 非法值 → 中文提示（可选值列全）。

- [ ] **Step 2: e2e 失败测试 → 验证**

`DebugUiToolsTests`（真实起 UiSampleApp，进程独立——不依赖 debug 会话）：
1. `ui_find process=UiSampleApp type=Button` → 返回含「手动」按钮行（含 index/Invoke✓）+ 语义候选（`UiSampleApp.*` 命中 `OnToggleState` 相关同名成员，若实现名匹配则标注，否则仅主窗口类型行）。
2. 左键闭环：`ui_invoke process=UiSampleApp index=0`（或 name=手动 type=Button）→ 返回含「已 Invoke/已物理左键」；`ui_wait textChangedFrom=手动 textChangedTo=自动` → 命中（状态已切）。
3. 右键：`ui_invoke … action=rightClick` 目标右键按钮 → 返回含「物理右键」；`ui_wait` 验证右击 Label 文本变化。
4. 双击：`ui_invoke … action=doubleClick` 双击 ListBox 某项 → 返回含「物理双击」；`ui_wait textChangedFrom=双击:0 textChangedTo=双击:1` 命中（UiSampleApp OnListDoubleClick 生效）。
5. 滚动：`ui_scroll process=UiSampleApp name=listBox direction=down lines=5` → 返回含「向下滚动 5 行」。**滚动生效方向验证（2026-09-09 审查修正）**：返回文案抓不住方向反转——实现期做一次自检：若目标容器支持 `ScrollPattern`，用 `VerticalScrollPercent` 前后对比断言（`down` 后 percent 增大）；WinForms ListBox 等无 ScrollPattern 的容器无法可靠断言，标注「环境相关，手工/后续补」；**不**用「树里能看到末尾项」断言（非虚拟化 ListBox 滚动前后 UIA 树都在）。
6. 错误面：非法 action/direction 中文提示；不存在的 name/歧义 → 候选清单；process 不存在 → 中文提示；target 无响应（UiSampleApp 卡 5s 模式可后补）→ 超时护栏返回不挂死。
7. 与 debug 编排冒烟：`debug_launch` UiSampleApp → 断点设 `OnToggleState` → `ui_invoke` 点按钮 → debug_wait 命中（可验证 UIA 与调试同进程协作；若时序不稳则该用例单列为可选，先保 1-6 绿）。
Run 定向 + 宿主全量。README/CHANGELOG 同步。`git commit -m "feat: 新增 ui_find/ui_invoke(action)/ui_wait/ui_scroll UI 自动化工具（FlaUI + 语义反查，U1，同步 README/CHANGELOG）"`

---

## 收尾

- [ ] Release build + 宿主全量 + Client + CoreMes 手测（可选外部验证：切手自动按钮闭环）。
- [ ] **V4 同批补**：AgentCopyGuardTests ContractData 加 ui_find（「控件清单」/「无视觉」）、ui_invoke（「副作用」/「rightClick」）、ui_wait（「超时返回当前状态」）、ui_scroll（「滚轮」）Description 片段。
- [ ] 核对 `src/DotNetDebuggerMcp/TODO.md` U1 状态（拍板+计划→实施完成后再勾）；`docs/planning/specs/README.md` 收录 U1 spec 行（本次立项完成，实施后改状态）。
- [ ] V1 复验闭环（已计划 `2026-09-09-v1-verify-loop.md`）：执行依赖 W1/U1/V3 就绪——U1 落地是其触发源之一（D22④/D24 顺序变更）。

## Self-Review（writing-plans 内审）

- **Spec 覆盖**：四件套工具（Task4 action + ui_scroll）、不需会话、AgentActionLog + 物理右键/双击/滚轮风险、右键/双击/滚动分派与 Mouse API（方向已修正 up=+）、语义标注（apphost→同名 dll 元数据 + 子串匹配，审查修正）、超时护栏/串行锁、FlaUI 打包（含线上 fallback 注）、测试目标含右键/双击/滚动元素（Task3）、V4/README/CHANGELOG（Task4/收尾）。
- **占位符**：无 TBD；FlaUI API 按线上 main 核实并内联；滚动生效方向为环境相关自检（无 ScrollPattern 容器无法可靠断言，标注）。
- **类型一致**：`UiElementInfo`/`UiActionResult`/`UiWaitResult`/`FindAsync`/`InvokeAsync(action)`/`ScrollAsync`/`WaitAsync` Task2 定义 Task4 用；`UiSemanticResolver.Lookup(assemblyPath, elementName)` 子串匹配 Task2 定义 Task2 自用；`UiSampleApp` Task3 产出 Task4 e2e 起。
- **风险点已标**：UIA 跨进程时序/超时（双层护栏）；MCP 并发 → 串行锁；元素缓存 index 失效；PackAsTool/platform TFM 冲突；产线副作用与物理右键/双击/滚轮系统级行为；滚动不改变元素树；语义标注 dll 兜底路径。
