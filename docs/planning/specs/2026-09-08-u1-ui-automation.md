# Spec · U1 UI 自动化主动触发业务操作（FlaUI）

> **⚠️ Superseded（2026-09-10）**：本 spec 正文保留作历史依据，但其**工具面（`ui_invoke` / `ui_scroll` 四件套）已被
>  `docs/planning/specs/2026-09-10-u1a-uia-only-ui-automation.md`（U1A）取代**——U1A 弃用一切物理输入、改语义动词
> `ui_action`/`ui_input`/`ui_get` + 事件化 `ui_wait`。U1 的**连接/语义标注/无视觉清单**等设计继续有效（U1A 沿用）。
> 实施 U1A 时请勿按本 spec 的工具参数/物理鼠标措辞复核。

> 状态：**已立项**（2026-09-09 拍板）——① ui_* **不需活动 debug 会话**（支持先操作 UI 再 attach）；② 副作用护栏 = AgentActionLog + Description 明示（Consent 列 v2）；③ 自动语义标注 **v1 做 = 务实成员反查**（不解析 XAML/BAML，ui_find 命中后用现有 Decompiler 元数据反查与 Name/Text 同名成员候选，无候选提示手动 decompile）；④ U1 先行（V1 复验闭环待 W1/U1/V3 落地后再计划）。**2026-09-09 追加：v1 = ui_find + ui_invoke(action=click/rightClick/doubleClick) + ui_wait + ui_scroll(滚轮) 四件套**（FlaUI main 源码已核实 Mouse.RightClick/DoubleClick/Scroll）。实施计划见 `docs/planning/plans/2026-09-09-u1-ui-automation.md`，规格冻结。
> 关联：宿主 TODO U1/UI 自动化条目；调试侧联动依赖 W1/V1（复验闭环是 U1 的验收载体）；会话模型复用现有 `DebugSessionManager` 单活动会话。
> 参考实现（已实读源码）：`ChrisPulman/UIInspect.MCP`（MIT）——`FlaUiAutomationBackend.cs`/`FlaUiAutomationSession.cs`；`sbroenne/mcp-windows`（MIT，91★，工具面命名参照）。

## 1. 背景与目标

`debug_run_to` 只能停「进程自然执行到」的目标；func-eval 对 async/UI/网络业务方法会死锁（I 项已关）。要让 agent 自主驱动 UI 业务流（如 CoreMes 点"手动/自动"切换），必须让 agent 能**找到控件、语义点击、并在业务代码里停下**。

目标：在现有 debug_* MCP 工具面旁，新增一套 **`ui_*` 工具**，agent 用它驱动任意 .NET App（WPF/WinForms/WinUI/UWP/MAUI/Avalonia 11+，即"整个 .NET 生态 UI 技术栈"）的 UI 操作，并能与 `debug_breakpoint_set`/`debug_run_to`/`debug_continue` 编排成"点按钮→断点命中→看现场"闭环。

无视觉前提：**模型不"看"截图**，工具输出控件**文本清单**（Name/Type/Rect/Patterns），模型从文本挑目标。

非目标：非 .NET 目标（Qt/Web/Electron——覆盖矩阵见 TODO，Avalonia <11、自绘控件树空壳场景降级坐标）；WebView2 内部 DOM（Chromium UIA 尚在演进，Web 调试走 WebDriver/Playwright 是正路）；Consent/Audit 完整版（v1 从简，见 §4.4）。

## 2. FlaUI 事实（已实读 UIInspect.MCP 源码 + FlaUI API 文档）

### 2.1 包与版本
- NuGet：`FlaUI.Core` + `FlaUI.UIA3`。**当前 stable 5.0.0**（2025-02 正式发布，打包完整）——nuspec 实读含三档 lib：`.NETFramework4.8` / `net6.0-windows7.0` / `net8.0-windows7.0`；**没有 `net10.0-windows` 档**（该档在 GitHub main / 6.0.0-dev 未发 release）。净影响：net10 宿主用 `net8.0-windows7.0` 的 dll 向上兼容跑，不必等 net10 档。
- **引用姿势（为什么绕 PackageDownload+HintPath）**：本宿主是 **PackAsTool 的 dotnet tool，必须保持非 platform-qualified TFM（net10.0）**；而 net10.0 经普通 `PackageReference` 无法消费 `net8.0-windows7.0` 资产（平台不匹配，只能靠 AssetTargetFallback/压制 NU1701），又不能把宿主改成 `net10.0-windows`（PackAsTool 拒绝）。照抄 UIInspect.MCP：`PackageDownload` FlaUI.Core/FlaUI.UIA3 + `<Reference HintPath="$(NuGetPackageRoot)…\lib\net8.0-windows7.0\*.dll" Private="true" />`，另显式 `PackageReference Interop.UIAutomationClient` + `System.Management`（csproj 注释实读确认）。
- 客户端 TFM 只决定"你的 server 跑在哪"，**不决定能控哪些目标**——UIA 与被控 App 的 .NET 版本无关。

### 2.2 核心 API 形态（从 UIInspect.MCP 源码提炼）
```csharp
// 建自动化（带超时护栏——UIA 跨进程调用可能挂起）
using var automation = new UIA3Automation {
    ConnectionTimeout = TimeSpan.FromSeconds(5),
    TransactionTimeout = TimeSpan.FromSeconds(5),
};

// 根定位：进程 → 顶层窗口（无视觉的"锚"）
var root = automation.GetDesktop()
    .FindFirstChild(cf => cf.ByControlType(ControlType.Window).And(cf.ByProcessId(pid)))
    ?? throw ...;
// 或经窗口句柄
var root = automation.FromHandle(new(windowHandle));

// 元素查找：条件工厂（ChainableCondition）
var btn = window.FindFirstDescendant(cf => cf.ByText("手动").And(cf.ByControlType(ControlType.Button)));
// 收集清单（ui_find 核心）：
var all = window.FindAllDescendants(cf => cf.ByControlType(ControlType.Button));
// ByText / ByAutomationId / ByName / ByClassName / ByControlType / ByProcessId / ByFrameworkId，可 And/Or/Not

// 操作优先级：InvokePattern（语义点击）> Click（坐标点击）
element.Patterns.Invoke.PatternOrDefault?.Invoke();   // 支持 Invoke 时
element.Click();                                       // 兜底（真实鼠标）
element.AsTextBox()?.Enter("文本");                    // ValuePattern 输入
element.AsToggleButton()?.Toggle();

// 能力探测（ui_find 输出 "可操作类型" 的依据）
bool canInvoke = element.Patterns.Invoke.IsSupported;

// 多键/滚轮（v1 已按 FlaUI main 源码核实）：右键/双击无 UIA pattern，走物理鼠标
element.RightClick(); element.DoubleClick();
Mouse.RightClick(point); Mouse.DoubleClick(point);
Mouse.Scroll(lines);          // 物理滚轮（WheelDelta*lines，上/下由正负号定）
Mouse.HorizontalScroll(lines);
```

### 2.3 控件属性（ui_find 清单字段来源）
`element.Name` / `Properties.AutomationId` / `Properties.ControlType` / `Properties.ClassName` / `Properties.FrameworkId` / `Properties.BoundingRectangle`（FlaUI 用 `System.Drawing.Rectangle`）/ `Properties.IsEnabled` / `Properties.ProcessId` / `Properties.NativeWindowHandle`。

### 2.4 CoreMes 实证（2026-09-08 UIA 探针）
切换按钮 = `ControlType.Button + Name="手动"`（Name 绑定 State 文本，随状态变"自动"）；AutoId 空；Rect=(14,60 150x40)；同名字有 TextBlock 子节点 → **必须 Name+ControlType 组合消歧**（探针脚本 `C:\Users\13178\AppData\Local\Temp\opencode\uia_probe.ps1`）。

## 3. 目标工作流（agent 视角，含语义闭环）

```
① 静态读语义（现有反编译能力，U1 差异化核心）
   decompile MainView.xaml 区域 / MainViewModel
   → 得知按钮 Content=State、Command=SwitchAutoStateCommand → 切换手自动
② ui_find 窗口=芯子自动装配 文本=手动 类型=Button
   → [5] Button Name=手动 AutoId= Rect=(14,60 150x40) Invoke✓  ← 语义标注：Command=SwitchAutoStateCommand
③ debug_breakpoint_set/run_to 在 SwitchState 设临时断点并放行（已有能力）
④ ui_invoke 5          → 真实点击 → 业务代码自然执行
⑤ debug_wait/state     → 断点命中 → debug_stack/variables 观察
⑥ （复验）再次 ui_find 验证按钮文本 手动→自动（状态变更确认，V1 闭环）
```

**关键洞察**：U1 不是"裸 UIA"，而是把 **反编译读到的代码语义** 标到 UIA 元素上——UIA 树只告诉 agent"有按钮叫'手动'"，反编译告诉它"点了会切自动"。这是 DotNetDebuggerMcp 相对裸 UIA MCP 的差异点。

> **v1 自动语义标注形态（2026-09-09 拍板：务实成员反查）**：不解析 XAML/BAML 还原 Command 绑定（工作量上一个量级，留 v1.5）。ui_find 命中元素后，若被控进程主模块可反编译（磁盘 dll 存在），宿主用现有 Decompiler 元数据能力（`MemberResolver` 同款：类型全名 + 子串忽略大小写）按控件 Name/Text **反查同名成员候选**（Command 属性 / 方法 / 事件处理器 / 字段）——找到则在返回行附 `语义候选: {TypeName}.{member}（可用 decompile_member 看实现）`；无候选给「无线索，可用 decompile_member <Type> <Name> 手动查」。命中率弱于 XAML 绑定还原但零新反编译设施。

## 4. 工具契约与分层设计

### 4.1 工具面（2026-09-08 定案 5 个；2026-09-09 扩展：v1 = find/invoke(action)/wait/scroll 四件套）

| 工具 | 参数 | 返回 | 对标 |
|---|---|---|---|
| `ui_find` | 进程名/pid 或窗口标题；text/automationId/type（可组合）；limit | 控件文本清单（index/Name/Type/AutoId/Rect/可操作类型），**附带反编译语义标注**（若命中模块可反编译） | sbroenne UIFind + 我们独有语义 |
| `ui_invoke` | index 或 name+type；**action=click(默认)/rightClick/doubleClick**；目标窗口 | 操作结果 + 可选的"状态已变"二次确认 | UIInspect Invoke/Click + Mouse |
| `ui_wait` | text=期望出现的控件文本；type；textChangedFrom/To；timeoutSeconds | 命中（控件已出现/文本已变）或超时提示 | UIInspect/DebugMCP wait 语义 |
| `ui_scroll`（v1） | 容器 index/name/type（缺省=窗口内首个可滚区）；direction=up/down；lines=3 | 滚动结果（v1 物理滚轮 `Mouse.Scroll`；ScrollPattern 自动滚动 v1.5） | 自定义 |
| `ui_input`（v1.5） | index/name；text | 输入结果 | ValuePattern.Enter |
| `ui_pick`（v1.5） | 无（进入拾取模式，人类 hover+快捷键） | 命中元素文本清单 | Snipaste/FlaUInspect；**无视觉 agent 兜底锚定**（找不到时人类指认一次） |

> **v1 最小闭环 = `ui_find` + `ui_invoke`(action) + `ui_wait` + `ui_scroll` 四件套**——右键菜单/双击树项/长列表滚动定位是自动化场景必需（2026-09-09 拍板）；`ui_input`/`ui_pick` 列 v1.5（压到最少，pick 引入人类介入面）。横向滚动与「滚到某控件可见」（ScrollItemPattern）列 v1.5。
> **V4 衔接**：ui_* 新工具落地时同批补语料断言（见 `2026-09-08-v4-copy-guard.md`）。
> **README 同步**：ui_* 属新增 MCP 工具，落地同 commit 改根 README。

### 4.2 分层（对齐仓库边界纪律）
- **新组件（宿主层）**：`UiAutomationService`——FlaUI 封装 + 窗口/元素缓存 + **语义标注反查**（ui_find 命中后用宿主反编译元数据能力 `MemberResolver` 同款反查 Name/Text 同名成员）+ 与 `DebugSessionManager` 的联动。放宿主是因为 U1 强耦合"反编译语义标注"（Decompiler）+ "debug 会话编排"（Session），而它俩在宿主工具面交汇。若未来要独立库再抽。
- Engine/Session **零改动**（U1 纯进程外 UIA，不碰 ICorDebug）。

### 4.3 关键实现注意（防坑）
- **UIA 超时护栏**：所有跨进程 UIA 调用包 5s 超时（UIInspect `UiaOperationGuard` 模式）；目标无响应时不能挂死 MCP 工具。
- **线程**：FlaUI UIA3 需 COM 初始化（MTA/STA 均可，但**单自动化实例单线程访问**）；宿主现 MCP 工具是 async——ui_* 内需确保 UIA 调用不并发（复用/类比 Engine"单线程泵"纪律或简单 lock）。
- **窗口激活**：`ui_invoke`/`ui_scroll` 操作前 `SetForegroundWindow`；最小化窗口先还原。
- **点击分派（action 语义）**：`click` = InvokePattern 优先（语义点击）、坐标 `element.Click()` 兜底；`rightClick`/`doubleClick` **无 UIA pattern，一律物理鼠标**（`Mouse.RightClick/DoubleClick(clickablePoint)`）——返回文案注明「物理右键/双击，可能触发系统级行为（如系统上下文菜单）」，agent 据此判断是否接受副作用。
- **坐标兜底**：InvokePattern 不可用（自绘控件）→ `GetClickablePoint()`/`BoundingRectangle` 中心 + 对应 Mouse 动作。
- **滚动语义**：`ui_scroll` v1 = 物理滚轮，作用于**定位到的容器元素中心**（无目标元素时用窗口工作区内可滚区域中心）；滚动后元素树不变——agent 应再 `ui_find` 确认目标控件文本/可见性（`IsOffscreen` 属性可辅助判断）。
- **副作用护栏**：产线软件点击/滚轮有真实后果——全部操作打 AgentActionLog + Description 明示；见 §6 拍板。

### 4.4 Consent/安全（借鉴 UIInspect.MCP，v1 从简）
UIInspect.MCP 有完整安全层（Consent 用户授权 + Audit 审计 + RateLimit）。本仓库定位 agent 主导，建议 v1 简化：
- `ui_invoke`/`ui_scroll`（`ui_input` v1.5）属于"会改变目标状态"的操作——至少打 `AgentActionLog`（已有轨迹，Web/复盘可查）；
- 完整用户确认弹窗（Consent）列 v2（与产品"agent 主导、人类不开 Web 也能用"定位需权衡）；
- Audit/RateLimit 视需要后置。

## 5. 复用与依赖
- **反编译管道（语义标注，v1 = 务实成员反查）**：ui_find 命中元素后，宿主对被控进程主模块做**旁路元数据反查**——复用 `MemberResolver.FindMembers`（类型全名 + 子串忽略大小写）按控件 Name/Text 找同名成员候选（Command 属性/方法/事件处理器/字段），找到即标注、无候选提示手动 decompile。**不解析 XAML/BAML**（绑定还原留 v1.5）；不占反编译缓存/不分页（旁路查询，量小）。agent 仍可先 decompile 再 ui_find 两步闭环，自动标注是增强不是依赖。
- **调试会话**：与 debug_* 编排是宿主侧纯串联，无新会话模型。
- 依赖 FlaUI 包（新 NuGet 引用）+ `System.Drawing` 矩形（FlaUI BoundingRectangle）。

## 6. 拍板记录（2026-09-09 用户拍板）

1. ~~工具数量/命名~~（已定案 2026-09-08：5 个，v1 做 find/invoke/wait 三件套，见 §4.1）
2. **会话依赖**：ui_* **不需活动 debug 会话**——独立可用，支持「先操作 UI 到某状态再 attach」。
3. **副作用护栏**：v1 = 全 ui_* 操作打 `AgentActionLog` + `[Description]` 明示产线副作用风险 + ui_wait 二次确认引导；不做强制动作类别声明；完整用户确认（Consent）列 v2。
4. **自动语义标注**：v1 **做 = 务实成员反查**（见 §3 注与 §5）——不解析 XAML/BAML，反查 Name/Text 同名成员候选；XAML 绑定还原 v1.5。
5. **ui_pick v1.5**（已定案）；v1 全自动定位失败兜底 = ui_find 宽松条件 + 输出可辨识清单供 agent 调参。
6. **与 V1 顺序**：U1 先行（本 spec 冻结 + 实施计划）；V1 复验闭环待 W1/U1/V3 落地后再计划。
7. **点击操作维度（2026-09-09 追加拍板）**：只左键无法覆盖自动化场景（右键菜单/双击树项/列表）。`ui_invoke` 加 **action = click(默认)/rightClick/doubleClick**——click 仍 Invoke 优先；rightClick/doubleClick 无 UIA pattern，**一律物理鼠标**并明示副作用。
8. **滚动（2026-09-09 追加拍板）**：新 `ui_scroll(容器定位, direction=up/down, lines=3)`，v1 **物理滚轮 `Mouse.Scroll`**（覆盖自绘/无 ScrollPattern 场景；FlaUI main 源码已核实）；ScrollPattern 自动滚动/横向/「滚到控件可见」列 v1.5。
9. **v1 范围（2026-09-09 确认）**：`ui_find + ui_invoke(action) + ui_wait + ui_scroll` **四件套**；v1.5 = ui_input/ui_pick/横向滚动/自动滚动。

## 7. 验证方案
- **对 CoreMes 实测**（真实 WPF）：ui_find 找到切换按钮 → ui_invoke → 进程行为变化（配合/不配合 debug 会话两种）。
- **跨框架 sample**：参考 UIInspect.MCP 的五 sample（WPF/WinUI/Avalonia/WinForms/MAUI）——建一个小 WinForms + 一个小 WPF 测试目标各一，验证框架无关。
- **无头/异常**：目标未启动/无窗口/无响应/树空壳 → 中文诊断。
- **与 debug 编排 e2e**：launch DebugTarget → 停点 → ui 操作（DebugTarget 无 UI，需专用测试目标带按钮）。

## 8. 工作量与风险
- 新组件 + 4 工具 + 2 测试目标：中-大（估计与 P3 同级或略高）。
- 风险集中在**跨进程 UIA 时序/超时**与 **产线副作用**；实现层无 ICorDebug 复杂度（不碰引擎）。
- 最大不确定性：FlaUI 5.0 在 net10 引用的打包姿势（已由 UIInspect.MCP csproj 实证可解，照抄即可）。
