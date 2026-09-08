# Spec · U1 UI 自动化主动触发业务操作（FlaUI）

> 状态：**计划中（草案）**——FlaUI 用法与蓝本实现已查证（UIInspect.MCP 源码实读），供后续实施直接参照。实施前置：宿主 TODO「UI 自动化主动触发业务操作」立项 + 工具形态拍板。
> 关联：宿主 TODO U1/UI 自动化条目；调试侧联动依赖 W1/V1（复验闭环是 U1 的验收载体）；会话模型复用现有 `DebugSessionManager` 单活动会话。
> 参考实现（已实读源码）：`ChrisPulman/UIInspect.MCP`（MIT）——`FlaUiAutomationBackend.cs`/`FlaUiAutomationSession.cs`；`sbroenne/mcp-windows`（MIT，91★，工具面命名参照）。

## 1. 背景与目标

`debug_run_to` 只能停「进程自然执行到」的目标；func-eval 对 async/UI/网络业务方法会死锁（I 项已关）。要让 agent 自主驱动 UI 业务流（如 CoreMes 点"手动/自动"切换），必须让 agent 能**找到控件、语义点击、并在业务代码里停下**。

目标：在现有 debug_* MCP 工具面旁，新增一套 **`ui_*` 工具**，agent 用它驱动任意 .NET App（WPF/WinForms/WinUI/UWP/MAUI/Avalonia 11+，即"整个 .NET 生态 UI 技术栈"）的 UI 操作，并能与 `debug_breakpoint_set`/`debug_run_to`/`debug_continue` 编排成"点按钮→断点命中→看现场"闭环。

无视觉前提：**模型不"看"截图**，工具输出控件**文本清单**（Name/Type/Rect/Patterns），模型从文本挑目标。

非目标：非 .NET 目标（Qt/Web/Electron——覆盖矩阵见 TODO，Avalonia <11、自绘控件树空壳场景降级坐标）；WebView2 内部 DOM（Chromium UIA 尚在演进，Web 调试走 WebDriver/Playwright 是正路）；Consent/Audit 完整版（v1 从简，见 §4.4）。

## 2. FlaUI 事实（已实读 UIInspect.MCP 源码 + FlaUI API 文档）

### 2.1 包与版本
- NuGet：`FlaUI.Core` + `FlaUI.UIA3`。**当前 stable 5.0.0**（2025-02-25，二进制仅发 `net8.0-windows7.0`）；`net10.0-windows` 目标在 GitHub main（包版本 6.0.0）未发 release。
- **引用姿势**：net10 工程引 net8 程序集向上兼容；**包分发照抄 UIInspect.MCP**——`PackageDownload` + `HintPath` 手动绑定 FlaUI 5.0 dll（其 `UIInspect.MCP.Windows.csproj` 注释写明了 PackAsTool 拒绝 platform-qualified TFM 的坑与解法）。
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

**关键洞察**：U1 不是"裸 UIA"，而是把 **反编译读到的 Command 绑定/代码语义** 标到 UIA 元素上——UIA 树只告诉 agent"有按钮叫'手动'"，反编译告诉它"点了会切自动"。这是 DotNetDebuggerMcp 相对裸 UIA MCP 的差异点，spec 里必须写死。

## 4. 工具契约与分层设计

### 4.1 工具面（2026-09-08 定案：5 个，v1 做 3 个）

| 工具 | 参数 | 返回 | 对标 |
|---|---|---|---|
| `ui_find` | 进程名/pid 或窗口标题；text/automationId/type（可组合）；limit | 控件文本清单（index/Name/Type/AutoId/Rect/可操作类型），**附带反编译语义标注**（若命中模块可反编译） | sbroenne UIFind + 我们独有语义 |
| `ui_invoke` | index 或 name+type；目标窗口 | 操作结果 + 可选的"状态已变"二次确认 | UIInspect Invoke/Click |
| `ui_wait` | text=期望出现的控件文本；type；textChangedFrom/To；timeoutSeconds | 命中（控件已出现/文本已变）或超时提示 | UIInspect/DebugMCP wait 语义 |
| `ui_input`（v1.5） | index/name；text | 输入结果 | ValuePattern.Enter |
| `ui_pick`（v1.5） | 无（进入拾取模式，人类 hover+快捷键） | 命中元素文本清单 | Snipaste/FlaUInspect；**无视觉 agent 兜底锚定**（找不到时人类指认一次） |

> **v1 最小闭环 = `ui_find` + `ui_invoke` + `ui_wait`**（定位→点击→状态确认跑通 CoreMes 切换）；`ui_input`/`ui_pick` 列 v1.5（压到最少，pick 引入人类介入面）。
> **V4 衔接**：ui_* 新工具落地时同批补语料断言（见 `2026-09-08-v4-copy-guard.md`）。
> **README 同步**：ui_* 属新增 MCP 工具，落地同 commit 改根 README。

### 4.2 分层（对齐仓库边界纪律）
- **新组件（宿主层）**：`UiAutomationService`——FlaUI 封装 + 窗口/元素缓存 + 与 `DebugSessionManager` 的联动。放宿主是因为 U1 强耦合"反编译语义标注"（Decompiler）+ "debug 会话编排"（Session），而它俩在宿主工具面交汇。若未来要独立库再抽。
- Engine/Session **零改动**（U1 纯进程外 UIA，不碰 ICorDebug）。

### 4.3 关键实现注意（防坑）
- **UIA 超时护栏**：所有跨进程 UIA 调用包 5s 超时（UIInspect `UiaOperationGuard` 模式）；目标无响应时不能挂死 MCP 工具。
- **线程**：FlaUI UIA3 需 COM 初始化（MTA/STA 均可，但**单自动化实例单线程访问**）；宿主现 MCP 工具是 async——ui_* 内需确保 UIA 调用不并发（复用/类比 Engine"单线程泵"纪律或简单 lock）。
- **窗口激活**：`ui_invoke` 的 Click 兜底前 `SetForegroundWindow`；最小化窗口先还原。
- **坐标兜底**：InvokePattern 不可用（自绘控件）→ `BoundingRectangle` 中心 + `Click()`（FlaUI 内部 SendInput 类）。
- **副作用护栏**：产线软件点击有真实后果——`ui_invoke` 返回前**要求 agent 在参数/确认里声明动作类别**？还是仅文档提示？见 §6 取舍。

### 4.4 Consent/安全（借鉴 UIInspect.MCP，v1 从简）
UIInspect.MCP 有完整安全层（Consent 用户授权 + Audit 审计 + RateLimit）。本仓库定位 agent 主导，建议 v1 简化：
- `ui_invoke`/`ui_input` 属于"会改变目标状态"的操作——至少打 `AgentActionLog`（已有轨迹，Web/复盘可查）；
- 完整用户确认弹窗（Consent）列 v2（与产品"agent 主导、人类不开 Web 也能用"定位需权衡）；
- Audit/RateLimit 视需要后置。

## 5. 复用与依赖
- **反编译管道**（语义标注）：`decompile`/`decompile_member`/`call_chain` 已有——U1 反查 Command 绑定时 agent 已能手动做；工具内自动标注是增强项，可 v1 不做自动、靠 agent 两步查（先 decompile 再 ui_find）。**简化建议**：v1 的 ui_find 不带自动语义标注，靠 agent 工作流（文档引导）实现闭环——少一个 Decompiler 集成点。
- **调试会话**：与 debug_* 编排是宿主侧纯串联，无新会话模型。
- 依赖 FlaUI 包（新 NuGet 引用）+ `System.Drawing` 矩形（FlaUI BoundingRectangle）。

## 6. 待拍板（立项时决策；工具面已定案见 §4.1，以下为剩余取舍）
1. ~~工具数量/命名~~（**已定案 2026-09-08**：5 个，v1 做 find/invoke/wait 三件套，见 §4.1）
2. **ui_* 是否需要活动 debug 会话**：独立可用 vs 要求先 attach/launch（影响"先操作 UI 后 attach"场景）。**倾向不需要**（支持"先操作 UI 到某状态再 attach"）。
3. **副作用护栏形态**：仅 AgentActionLog vs 需 agent 声明动作类别 vs v2 用户确认。
4. **自动语义标注**：v1 不做（靠 agent 两步）还是做（ui_find 集成 Decompiler 反查 Command）？**倾向 v1 不做**（少一个 Decompiler 集成点）。
5. **拾取（ui_pick）是否 v1.5**：已定案列 v1.5（引入人类介入面，压到最少）；v1 全自动定位失败的兜底策略另议。
6. **与 V1 复验闭环的关系**：U1 是 V1 的触发源之一——立项顺序建议 U1 先于 V1。

## 7. 验证方案
- **对 CoreMes 实测**（真实 WPF）：ui_find 找到切换按钮 → ui_invoke → 进程行为变化（配合/不配合 debug 会话两种）。
- **跨框架 sample**：参考 UIInspect.MCP 的五 sample（WPF/WinUI/Avalonia/WinForms/MAUI）——建一个小 WinForms + 一个小 WPF 测试目标各一，验证框架无关。
- **无头/异常**：目标未启动/无窗口/无响应/树空壳 → 中文诊断。
- **与 debug 编排 e2e**：launch DebugTarget → 停点 → ui 操作（DebugTarget 无 UI，需专用测试目标带按钮）。

## 8. 工作量与风险
- 新组件 + 4 工具 + 2 测试目标：中-大（估计与 P3 同级或略高）。
- 风险集中在**跨进程 UIA 时序/超时**与 **产线副作用**；实现层无 ICorDebug 复杂度（不碰引擎）。
- 最大不确定性：FlaUI 5.0 在 net10 引用的打包姿势（已由 UIInspect.MCP csproj 实证可解，照抄即可）。
