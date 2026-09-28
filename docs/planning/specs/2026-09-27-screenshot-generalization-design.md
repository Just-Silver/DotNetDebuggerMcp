# screenshot 设计 spec（agent 视觉基座：寻址与坐标模型，2026-09-27）

> **目标**：把原 Windows-only 的 `screenshot`（`mode=window|screen|region`）升级为可复用的「agent 视觉**寻址**基座」——**① 寻址通用**（多显示器 / 前台 / 客户区 / hwnd / UIA 元素）+ **统一坐标模型**（`origin + frameId`）；并把截图与 UI 自动化能力**抽成独立可复用库**，MCP 宿主只做薄暴露。
> **状态**：**已实施**（寻址扩展 / 库化重构 / 坐标模型 / 独立工具目录 `Tools/Screenshot/` / 两个只读发现工具）。
> **范围收缩（用户裁定 2026-09-28）**：原 spec 的另两条主线 **② 图像经济**（1568 长边上限、双轴降采样、灰度、无变化检测、图像格式选型）与 **③ 语义桥**（OCR / Set-of-Marks 标注）**整条撤销**，配套调研与实施计划一并从仓库移除。理由：**图像尺寸处理交模型侧**——模型收到图片必然自行处理一次，服务器再缩一次会让「我们标注的坐标元数据」与「模型真正看到的图」错位；截图据此保持 **固定 PNG、原生 1:1、不做任何缩放**。
> **前序**：`2026-09-22-screenshot-tool-design.md`（已实现：单工具三模式 + WGC/GDI + 双轨）。本 spec **解冻其 §8** 的「多显示器 `display` / `includeCursor` / 元素级截图 / region 的 `origin` 坐标系」四项。

---

## 0. 采用决策

| # | 决策 | 采用值 |
|---|---|---|
| A1 | 架构 | 拆 `SharpSight.Capture`（捕获 + 图像管线，**不含 FlaUI**）+ `SharpSight.UiAutomation`（FlaUI/UIA 元素模型与定位）；宿主薄暴露 |
| A2 | 命名 | 套件 `SharpSight`（NuGet 实测可用） |
| A3 | 分发 | 同仓库 `src/` 分项目，各自独立 NuGet |
| D1 | 工具形状 | **单工具多参数**（不拆多工具） |
| D2 | 默认返回 | **内联 image content**；base64 后 ≥2MB 或指定 `filePath` → 落盘只回路径（无 image 块） |
| D4 | region 坐标系 | `regionSpace=screen`（虚拟屏图像素）默认；越界=裁交集 + 头部注明；完全屏外=中文错误 |
| D5 | 元素级截图 | 做：**先取元素所属顶层窗口的帧**，再把元素矩形从虚拟屏物理像素换算为帧内坐标裁剪（**不对屏幕直接 BitBlt 裁元素**，否则截到遮挡物） |
| D9 | 元素身份 | 与 `ui_find` **共用一套**（同一 find 缓存 + 代际号 `frameId`） |
| — | 缩放 | **不做**（用户裁定 2026-09-28）：固定 PNG、原生 1:1，尺寸处理交模型侧 |
| — | 语义桥 / 图像经济 | **撤销**（同上）：OCR / SoM 标注 / 灰度 / 无变化检测 / 图像格式选型均不在本设计内 |

---

## 1. 背景与现状

- `screenshot` 是**独立工具**（不入缓存/不过 ToolPipeline、不写 AgentView），返回 `Task<CallToolResult>`（图片类铁律例外），抓取链 **WGC → PrintWindow → BitBlt** 已落地并有单测。
- 能力边界（旧 spec §8 冻结项）：无多显示器选择、无 `includeCursor`、无元素级截图、region 固定「screen 图像素空间」。
- 消费者：宿主 `Tools/Screenshot/ScreenshotTool.cs`（唯一）。
- 相关既有能力：`ui_find`/`ui_action`/`ui_input`/`ui_get`/`ui_wait`（FlaUI UIA3、语义动作、元素缓存 + 重解析，已迁入 `SharpSight.UiAutomation`）。
- 痛点（本 spec 要解决的）：可见性只能看「窗口/屏/区域」三档，无法**精确寻址**（副屏/前台/客户区/hwnd/元素），且截图与元素编号不闭环（`element` 引用与 `ui_find` 异源会漂移）。

## 2. 范围（做 / 不做）

**做**：寻址通用（§4）；统一坐标模型（§5）；库化重构（§3）；宿主薄暴露（§3.4）；铁律同步（§9）；测试（§10）。
**不做（已撤销）**：图像经济（缩放上限/双轴降采样/灰度/无变化检测/格式选型）与语义桥（OCR/SoM 标注）——见文首「范围收缩」；by-ref `resource_link` 亦不做（内联 image 是 MCP 规范一等公民、零 capability 前置）。

## 3. 架构

### 3.1 项目布局与依赖方向

```
src/
  SharpSight.Capture/        # 能力库：捕获 + 图像管线（无 FlaUI、无宿主依赖）
  SharpSight.UiAutomation/   # 能力库：FlaUI/UIA 元素模型 + 定位 + 交互（无 Capture/宿主依赖）
  DotNetDebugger.Engine/     # 迁出 Capture/ 后：仅 ClrDebug + DbgShim（去掉 System.Drawing.Common）
  DotNetDebugger.Session/    # 不变（Engine + Decompiler）
  DotNetDebugger.Web/        # 不变（Session + Decompiler）
  DotNetDebuggerMcp/         # 宿主：引 Capture + UiAutomation + Engine + Session + Decompiler，薄暴露 MCP
  DotNetDebuggerMcp.Client/  # 不变
```

- **依赖方向**：两个 `SharpSight.*` 是**零宿主依赖**能力库；`Engine`/`Session`/`Web` **不反向依赖**它们；宿主是唯一组合点。三者**均不得**引用 `DotNetDebuggerMcp`。
- **`SharpSight.Capture` 依赖**：`System.Drawing.Common`（GDI 抓取 / 裁剪 / PNG 编码）。**无 FlaUI**；**不引入任何第三方图像编码库**（输出固定 PNG）。
- **`SharpSight.UiAutomation` 依赖**：`FlaUI.UIA3`（+ `FlaUI.Core`）。**无 Capture/Engine**。
- **组合关系**：元素级截图 = 宿主用 `SharpSight.UiAutomation` 取元素 `BoundingRectangle` + 所属顶层 hwnd → 交给 `SharpSight.Capture` 从**窗口帧**裁剪；两库互不引用，由宿主接线（避免循环依赖）。

### 3.2 Engine 迁出 `Capture/`

- 物理移动 `src/DotNetDebugger.Engine/Capture/*` → `src/SharpSight.Capture/`（`ScreenCapture`/`WgcCapture`/`GdiCapture`/`ImagePipeline`/`CaptureModels`）。**已落地**。
- `Engine` 去掉 `System.Drawing.Common` 包引用（**已核实**：引擎其它处无 `System.Drawing` 使用）。
- **`WgcCapture.cs` 的手写 COM/WinRT interop（旧 spec 标「勿动」）整体原样迁移，不在本设计内改动**。
- TFM 保持 `net10.0-windows10.0.22621.0`（两新库同 TFM）；`DotNetDebuggerMcp.slnx` 增加两个项目。

### 3.3 库公开面

**`SharpSight.Capture`**
```csharp
// 抓取门面（HSM）
DisplayInfo[] EnumerateDisplays();                 // index/szDevice/primary/bounds/workArea/dpi/scale
CaptureResult CaptureScreen(CaptureOptions o);     // 虚拟屏 / clip（region）
CaptureResult CaptureDisplay(int index, CaptureOptions o);
CaptureResult CaptureWindow(IntPtr hwnd, CaptureOptions o);   // WGC→PrintWindow→BitBlt
CaptureResult CaptureElement(IntPtr topLevelHwnd, Rectangle elementRectPx, CaptureOptions o);
WindowHandleInfo[] EnumerateVisibleWindows();      // 供 screenshot_windows（不限 .NET 进程）

// 图像管线（唯一编码归属；**不缩放**）
CaptureResult Process(Bitmap source, string? title, string sourceName, ...);  // 纯黑采样 + 固定 PNG 编码
```
- `CaptureResult` 字段：`Image`（PNG 字节）、`Width/Height`（=抓取区**原生**像素）、`WindowTitle`、`Source`（WGC/PrintWindow/BitBlt）、`WasAllBlack`、`ClippedToScreen`、`OriginX/OriginY`（虚拟屏物理像素）、`FrameId`、`DisplayIndex`、`IsClientArea`。

**`SharpSight.UiAutomation`**：宿主 `Services/Ui/*` 已迁入并保持公开面（`UiElementLocator`、`UiModels`、`UiPatternCapabilities`、`UiAutomationService`、`UiEventWaiter`、`UiSemanticResolver`），另有：
```csharp
int FrameGeneration { get; }                        // 单调递增代际号
FindResult FindForCapture(FindRequest req);         // 与 ui_find 共用缓存；返回 index/name/type/rect/patterns
Rectangle GetBoundingRectangle(int index, int frameId);  // 带代际校验；失配 → StaleFrameException
```

### 3.4 宿主薄暴露（关键约束）

- 宿主**只做**：MCP 参数绑定/校验、编排（定位→抓取→头部组装）、落盘与 `CallToolResult` 组装、中文错误。
- 宿主**不得**含像素运算、坐标换算、编码细节——全部下沉 `SharpSight.*`（沿用旧 spec「职责唯一归属」原则，改为归属库）。
- 工具仍**不入缓存、不过 ToolPipeline、不写 AgentView**。

## 4. 工具面契约（`screenshot`，agent 可见）

### 4.1 参数表（全部带默认值）

**寻址**
| 参数 | 类型/默认 | 语义 |
|---|---|---|
| `mode` | `string = "auto"` | `auto`（按其它参数推断）/ `screen` / `display` / `window` / `foreground` / `region` / `element` |
| `display` | `string = ""` | `mode=display`：`1`、`2`…（1 基）或 `primary`/`left`/`right`；空=主屏 |
| `processId` | `int = 0` | window/element：按 pid（0=未提供；优先于 `windowTitle`） |
| `windowTitle` | `string = ""` | window：标题子串；`@active` 表示前台窗口 |
| `hwnd` | `string = ""` | **十进制字符串**（避开 64 位 JSON 精度）；需为可见顶层主窗 |
| `clientArea` | `bool = false` | window：截客户区而非整窗 |
| `region` | `string = ""` | `"x,y,w,h"`（**虚拟屏图像素空间**，原点左上）；`mode=region` 必填、`mode=screen` 可选作局部裁剪 |
| `element` | `string = ""` | `mode=element`：UIA 元素引用（index 或控件名/AutomationId 子串）；见 §7.1 |
| `frameId` | `int = 0` | 旧帧护栏；0=不校验 |
| `timeoutSeconds` | `int = 5` | window/foreground：等窗口出现（clamp 0-30） |

**返回与采集**
| 参数 | 类型/默认 | 语义 |
|---|---|---|
| `includeCursor` | `bool = false` | 叠加光标（WGC 内建开关；GDI 源手动叠加，失败在来源行注明） |
| `filePath` | `string = ""` | 非空=强制落盘该路径（**相对路径以临时目录 `%TEMP%\DotNetDebuggerMcp\screenshots\` 为基准**，绝对路径按原样；均不写当前工作目录）；落盘时只回文本 + 路径，**不再附 image 块** |

> **不提供** `format`/`quality`（固定 PNG）、**任何缩放参数**（`maxDimension`/`maxWidth`/`maxHeight` 已移除）、`delay`。

### 4.2 寻址语义（钉死）

- `mode=auto` 推断优先级：`element` → `hwnd` → `windowTitle`/`processId` → `region` → `display` → 否则 `screen`。
- `mode=foreground`：`GetForegroundWindow` + `GetWindowThreadProcessId`（锁屏/安全桌面返回 NULL → 中文原因）。
- `mode=window`：整窗默认取 **`DWMWA_EXTENDED_FRAME_BOUNDS`**（去阴影、物理像素），失败回退 `GetWindowRect`；`clientArea=true` 走 `GetClientRect`+`ClientToScreen`。定位规则沿用旧 spec（hwnd 优先、pid 次之、标题兜底、空则活动会话 pid 兜底）。**pid 命中多个可见根窗时按「非工具窗（`WS_EX_TOOLWINDOW`）→ 有标题 → 面积最大」择优**（2026-09-28 修：Z 序最前常是 1×1 缩略图/任务栏类助手窗，会截出 1×1 黑图却貌似成功）；**标题检索保持 Z 序最前**。
- `mode=display`：`EnumDisplayMonitors` 枚举 → `primary`/`left`/`right` 解析；**1 基**对外编号，头部回显设备名 + 矩形，避免枚举序歧义。
- `mode=screen`：虚拟屏（`SM_X/Y/CX/CYVIRTUALSCREEN`，原点可负），GDI BitBlt。
- `mode=region`：图像素空间求交；越界=裁交集 + 头部注明（`已裁至屏幕交集`）；完全屏外=中文错误。
- `mode=element`：见 §7.1。
- **不兼容组合显式报错**（不静默丢参）：`clientArea` 与 `element`；`region` 与 `window`/`display` 等。

### 4.3 返回契约与头部

```
成功 base64 后 < 2MB 且无 filePath: content[0]=文本头部；content[1]=image 块
成功 base64 后 ≥ 2MB 或指定 filePath: content[0]=文本头部 +「已落盘: <绝对路径>」，无 image 块（落盘失败→仍附块）
失败: 纯文本中文提示，不抛异常
```
头部（示意，纯文本，字段按模式裁剪）：
```
目标:   显示器 2 "\\.\DISPLAY2" (1920,0 1920x1080)   ← 或 窗口 "标题" (pid=…) / 前台窗口 / 屏幕 / 屏幕区域 / 元素 Button "保存"
尺寸:   3440x1440                                   ← 图像=抓取区原生像素（不做缩放）
原点:   (0,0)                                        ← 抓取矩形左上在虚拟屏物理像素的坐标
选择:   命中 13 个可见窗口 → 已选 hwnd=27201368 2006x984 "test - 文件资源管理器"   ← 仅多命中时（事实行）
备注:   目标窗口已最小化——截图为占位/残影画面，非真实界面                        ← 仅最小化/无标题/极小窗/参数越界时
帧:     15                                           ← 仅 element 模式（代际护栏）
来源:   WGC                                          ← WGC | PrintWindow | BitBlt（+「（光标未叠加）」备注）
备注:   画面为纯黑（目标可能未渲染）                  ← 仅全黑时
---
```
> `原点` 即 §5 的 `origin`；`帧` 即 `frameId`。`选择:` 给出**实际选中的 hwnd/尺寸**（选错也能被 agent 一眼看出）；
> `备注:` 为客观事实行（无标题/极小窗/最小化/参数越界）。全部为纯文本行，不含行号。

### 4.4 与 `ui_*` 的边界

- **读文本用 `ui_find`**（结构化清单），不靠截图识字。
- `element` 引用的元素序号**与 `ui_find` 同源**（同一缓存、同一 index、同一 `frameId`）——`screenshot element=N` 与 `ui_action index=N` 指向同一控件。

## 5. 坐标模型（`origin + frameId`）

- 换算：`screen_x = origin_x + image_x`，`screen_y = origin_y + image_y`（图像为抓取区**原生 1:1**，故无 scale 项；曾有的 `缩放/scale` 随「不做缩放」一并移除）。
- `origin` = 抓取矩形左上角在**虚拟屏物理像素**空间的坐标（显示器/窗口/元素/region 各自填）。
- `frameId`：每次元素采集产出的单调递增代际号（由 `SharpSight.UiAutomation` 维护）；`ui_action`/`ui_get`/`screenshot element` 携带的 `frameId` ≠ 当前 → **拒绝 + 教学提示**（「编号 N 来自旧画面（帧 12，当前 15），请重新 screenshot/ui_find」）。
- region 口径：`regionSpace=screen` 且以「mode=screen 返回图像」的像素为坐标基准——因图像不缩放，该空间与虚拟屏物理像素仅差一个 `origin` 平移（图像 (0,0) 对应虚拟屏左上 `origin`）。

## 6. 图像管线

### 6.1 格式与编码
- **输出固定 PNG**（无损、文字清晰），**唯一格式**——不提供 `format`/`quality`。**不引入任何第三方图像编码库**（PNG 由 `System.Drawing.Common` 编码）。
- **不做任何缩放**（用户裁定 2026-09-28）：图像恒为抓取区原生像素 1:1，尺寸处理交模型侧；因此不存在「服务器缩一次 + 模型侧再处理」的双重处理与坐标口径错位。

### 6.2 落盘（传输口径，非图像处理）
- base64 后 **≥2MB** 或 `filePath` 非空 → 落盘，只回文本 + 绝对路径（客户端需自行读取该文件）；阈值常量 `AppConfig.InlineImageBase64Bytes`（测试可注入）。

### 6.3 光标（`includeCursor`）
- 默认关。WGC 路径用 `GraphicsCaptureSession.IsCursorCaptureEnabled`（Win10 1903+，`ApiInformation` 探测）；GDI 路径手动叠加（`GetCursorInfo`+`GetIconInfo`），失败则在来源行注明「（光标未叠加）」。

## 7. 元素级截图与代际护栏

### 7.1 元素级截图与元素身份（D5/D9）
- 元素来源：`SharpSight.UiAutomation.UiElementLocator.FindForCapture`（同 `ui_find` 一套身份：pid/窗口 → `FindAllDescendants` → 过滤 → 能力探测 → ordinal）。
- 遮挡安全：**先拿元素所属顶层窗口的帧**（WGC/PrintWindow），再把元素矩形从虚拟屏物理像素换算为帧内坐标裁剪。
- 元素可能属**另一顶层窗口**（ComboBox 弹层/popup/tooltip）→ 用元素自身 `GetAncestor(GA_ROOT)` 的 hwnd 取帧；**元素无独立 HWND 时（XAML/UWP/WPF/Electron/Web 控件，`NativeWindowHandle=0`）回退元素所属顶层窗口帧**（2026-09-28 修：此前一律报 `hwnd=0` 失败，等于只有 Win32/WinForms 控件可截）；裁剪与窗口帧求交，部分越界 → `ClippedToScreen=true`（头部注明「已裁至窗口帧」）。
- 离屏：`IsOffscreen=true` 过滤（与 `ui_find` 同一过滤，index 同源）。
- `element` 为空时**显式报错**，绝不静默截取首个元素。

### 7.2 代际一致性（护栏）
- 语义 = **元素缓存的代际一致性**（本项目动作为 UIA 语义，非按坐标点击）。
- `SharpSight.UiAutomation` 维护单调 `FrameGeneration`；每次采集（`ui_find` 或 `screenshot element`）产出一帧；元素清单与 `frameId` 绑定。
- 消费侧（`ui_action`/`ui_input`/`ui_get`/`screenshot`）带旧 `frameId` → 拒绝 + 教学提示。

## 8. 仓库集成与改动清单（均已落地）

| 项 | 动作 |
|---|---|
| **新增** `src/SharpSight.Capture/`、`src/SharpSight.UiAutomation/` | 两个零宿主依赖能力库（§3.3），`DotNetDebuggerMcp.slnx` 已登记 |
| `src/DotNetDebugger.Engine/Capture/*` | 已迁出至 `SharpSight.Capture`；Engine 已去 `System.Drawing.Common` |
| `src/DotNetDebuggerMcp/Services/Ui/*` | 已迁出至 `SharpSight.UiAutomation`；宿主改为引用 |
| `Tools/Debugger/UiTools.cs` | `ui_find` 输出含 `帧: frameId=N`；`ui_action`/`ui_input`/`ui_get` 增可选 `frameId`（0=不校验，向后兼容） |
| `Tools/Screenshot/`（`ScreenshotTool` / `ScreenshotDisplaysTool` / `ScreenshotWindowsTool`） | 独立截图工具面：寻址扩展 + 头部 `原点/帧`；两个只读发现工具补齐寻址参数取值来源（显示器清单 / 可见窗口清单） |
| `Configuration/AppConfig.cs` | 保留 `InlineImageBase64Bytes`/`ScreenshotsDir`；**已移除** `ScreenshotMaxDimension`（缩放撤销） |
| 各 `AGENTS.md` / 根 `README.md` / 握手 `AppText.HandshakeFeatureIntro` / `CHANGELOG.md` | 工具面变更同步（同 commit） |

## 9. 铁律同步（同 commit）

1. **根 `README.md` + 握手简介 + 回归断言**：改 MCP 工具必须同步；断言 `DotNetDebuggerMcpCmdTests.HandshakeFeatureIntro_覆盖全部能力族触发条件` 需逐族补（多显示器/前台/元素级/发现工具），漏一族即失败。`ui_*` 因新增 `frameId` 也属工具面变更。
2. **工具 `[Description]` 是 agent 唯一说明**：参数/默认值/行为一变就改；`AgentCopyGuardTests` 锁定关键契约片段。
3. **图片类例外**：`screenshot` 保持 `Task<CallToolResult>`、错误纯文本、不设 `IsError`。
4. **stdout 纪律**：不新增 stdout 输出；不得改动 stderr 日志配置。
5. **库元数据**：`SharpSight.*` 若独立发布需各自 `PackageId`/`Version`/`Description`/`README`（Windows-only 注明）——发布未做，见 §11。

## 10. 测试计划

- **`SharpSight.Capture` 单测**：显示器枚举；`CaptureDisplay` 各选择器（含 R21 回归：以显示器矩形为基而非虚拟屏）；窗口定位/几何（DWM 边框 vs `GetWindowRect`）；`CaptureElement` 裁剪；region 求交/屏外文案（纯函数恒跑）；PNG 产物合法；纯黑检测；光标叠加；**输出恒原生 1:1（无缩放）**；窗口枚举过滤。
- **`SharpSight.UiAutomation` 单测**：迁移既有 U1A 测试；`FindForCapture` 身份与 `ui_find` 一致；`FrameGeneration` 单调；旧帧拒绝。
- **宿主工具单测**：参数校验全表中文文案；返回结构（头部 + image 块）；落盘/2MB seam；`mode=element` 端到端（UiSampleApp）；发现工具输出契约。
- **GUI 来源**：复用 `tests/TestData/UiSampleApp`。
- **CI**：无头/无 GPU → WGC 探测失败即 `Assert.Skip`；沿用 Engine `Parallelization(None)` 与宿主 `[Collection("AppServices")]` 纪律。

## 11. 待办 / 待实测

1. **`SharpSight.*` 独立 NuGet 发布**：pack/publish 流程 + windows TFM 依赖链验证（**未做**；当前为仓库内 ProjectReference）。
2. **FlaUI 偶发不完整**：`FindAllDescendants()` 在目标场景下是否复现（持续观察）。
3. **多屏 `display=left/right` 真机验证**：本机为单显示器，邻居解析逻辑只有纯函数单测覆盖（宿主 TODO 已记）。

## 12. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 库化重构波及 Engine/Session/Web/host，回归面大 | 已按「物理迁移 + 命名空间替换」保行为不变落地；全量 build + 单测回归 |
| `SharpSight.*` windows TFM 被非 Windows TFM 引用（NU1201） | 宿主本就 windows TFM；Web/Session 不引两库 |
| 只有 PNG 一种格式 | 已定（用户裁定 2026-09-28）；不缩放，体积由「超限落盘」承担；若将来确需其它格式，按「可免费升级 + 许可无门 + 无原生资产」三条标准重新选型 |
| WGC 帧几何不明导致元素裁剪偏移 | 已实测：WGC 首帧几何 == `DWMWA_EXTENDED_FRAME_BOUNDS`（与 `GetWindowRect` 差阴影/不可见边框），故 WGC 源 origin=扩展边框左上、GDI 回退源 origin=窗口矩形左上 |
| pid 多窗导致截错窗口（1×1 助手窗）/ 无 HWND 元素截不到 | **已修（2026-09-28）**：pid 检索按「非工具窗→有标题→面积最大」择优 + 头部 `选择:` 事实行；element 无 HWND 时回退所属顶层窗口帧 |
| **安全：截图可能含敏感信息 / 屏幕内容是不可信输入** | 本工具**不做**像素级脱敏；README/握手注明「截图会原样采集可见内容」；agent 侧应把屏幕内容视为**不可信数据**（防提示注入） |

## 13. 明确不做（YAGNI / 冻结）

- **缩放与图像经济**：任何尺寸上限/双轴降采样/灰度/无变化检测/JPEG/WebP（用户裁定 2026-09-28 整条撤销）。
- **语义桥**：OCR、Set-of-Marks 标注（同上撤销）。
- by-ref `resource_link`（内联 image 是规范一等公民）；`detail=text`；`delay`。
- mp4 录制 / `play` 输入 DSL；NCC 模板匹配；ML 像素检测器（OmniParser 式）。
- 跨平台 provider（X11/Wayland/portal）；HTTP 多租户鉴权；遥测。
- 物理输入注入（本项目动作走 UIA 语义）；截图结果的像素级脱敏；截图结果入反编译缓存 / 写 `AgentView` / Web 展示回放（Web 冻结）。

## 14. 实施状态

- **已实施（2026-09-27/28）**：库化重构（`SharpSight.Capture` + `SharpSight.UiAutomation`）；寻址扩展（`display`/`foreground`/`hwnd`/`clientArea`/`element` + `origin+frameId`）；独立工具目录与两个只读发现工具；`includeCursor`；**移除缩放**（固定 PNG、原生 1:1）；文档/握手/回归同步。
- **已撤销（不再计划）**：语义桥（OCR/SoM）与图像经济（缩放/灰度/diff/格式）——如需重启，另立 spec（不得复活「服务器侧缩放」：尺寸处理交模型侧是已定边界）。
