# screenshot 通用化设计 spec（agent 视觉基座，2026-09-27）

> **目标**：把现有 Windows-only 的 `screenshot`（`mode=window|screen|region`）升级为可复用的「agent 视觉基座」，覆盖三条主线：**① 寻址通用 · ② 图像经济 · ③ 语义桥**；并把截图与 UI 自动化能力**抽成独立可复用库**，MCP 宿主只做薄暴露。
> **范围**：三件都做（用户拍板）；配「最不妥协」目标（高星/官方来源重建后的调研结论，见 `docs/planning/research/screenshot-generalization/`）。
> **状态**：**草案，待用户复核。** §0 的架构/命名/分发与 D1–D11 均为**本轮采用的推荐值**（用户此前经阻塞式问答界面所选，明确表示「随便点的」，故一律按「未逐条明文确认」处理，复核时可直接改）。
> **前序**：`2026-09-22-screenshot-tool-design.md`（已实现，单工具三模式 + WGC/GDI + 双轨）。本 spec **解冻其 §8** 的「多显示器 `display` / `includeCursor` / 元素级截图 / region 的 `origin` 坐标系」，并对架构做库化重构；旧 spec 相应条目加 **Superseded（部分）** 标注，不重写其正文（仓库惯例）。

---

## 0. 决策状态（本轮采用值，待用户复核）

| # | 决策 | 本轮采用值 | 依据 |
|---|---|---|---|
| A1 | 架构 | **方案 C**：拆 `SharpSight.Capture`（捕获+图像管线+OCR+标注，**不含 FlaUI**） + `SharpSight.UiAutomation`（FlaUI/UIA 元素模型与定位）；宿主薄暴露 | 用户「截图能力单独成库」要求；`05` 集成点 |
| A2 | 命名 | 套件 `SharpSight` | NuGet 实测 `SharpSight.Capture`/`SharpSight.UiAutomation` 可用 |
| A3 | 分发 | 同仓库 `src/` 分项目，各自独立 NuGet（`SharpSight.Capture` / `SharpSight.UiAutomation`） | 同上 |
| D1 | 工具形状 | **单工具多参数**（非拆多工具） | `13`（chrome 单工具 5 参数、playwright 单工具）；`15`（参数互斥显式报错） |
| D2 | 默认返回 | **内联 image content**；≥2MB 或指定 `filePath` 落盘 | `12`②（图像是规范一等公民、零 capability 前置）；`12`③（by-ref 必新增 resources 面） |
| D3 | 缩放默认 | `maxDimension=1568`（可覆盖） | `12`⑤.2（官方 vision 长边 ≤1568 / tile ≤1568） |
| D4 | region 坐标系 | **兼容**：无 `origin` 时走旧口径（screen 图像素），有则新公式 | `11`⑥.1 + `13`③ |
| D5 | 元素级截图 | **做** | `06`①（屏 BitBlt 遮挡坑）+ `14`⑥.1（`PrintWindow` 正例） |
| D6 | OCR | **做**，默认关、显式 `ocr=true` | `08`①②（非打包可用 + PowerOCR 配方） |
| D7 | 上限野心 | UIA/OCR/SoM（**不追 ML**） | `10`④ + `14`⑧ |
| D8 | `detail=text` | **不加**（文本走 `ui_find`；`annotate` 复用其数据） | `13`⑤（结构优先）+ `03`①#3（避免重叠） |
| D9 | 元素身份 | 与 `ui_find` **共用一套**（同一 `_lastFind` + 代际号 `frameId`） | `13`⑥（稳定 id + stale 报错）；`11`⑥.2（无代际号反例） |
| D10 | OCR 默认开关 | **默认关** | `03`②（语言包缺失是常态） |
| D11 | `maxMarks` 上限 | 默认 **120**（区间 100~150） | `11`⑨#7（元素预算 + truncated） |

> 若复核要调整任一条：**直接给编号 + 目标值**，本 spec 对应章节随之改。

---

## 1. 背景与现状

- 现有 `screenshot` 是**独立工具**（不入缓存/不过 ToolPipeline、不写 AgentView），`Task<CallToolResult>`（图片类铁律例外），WGC→PrintWindow→BitBlt 回退链已落地并有三轮审查 + 单测。
- 能力边界（旧 spec §8 冻结）：无多显示器选择、无 `includeCursor`、无 `delay`、无元素级截图、region 固定「screen 图像素空间」。
- 消费者：宿主 `Tools/Screenshot/ScreenshotTool.cs`（唯一）。
- 相关既有能力：宿主 `Services/Ui/*`（U1A：`ui_find`/`ui_action`/`ui_input`/`ui_get`/`ui_wait`，FlaUI UIA3、语义动作、元素缓存 + 重解析）。
- 痛点：可见性只能看「窗口/屏/区域」三档，无法**精确寻址**（副屏/前台/客户区/hwnd/元素）、无法**控制图像成本**（只有 png/jpeg + 单一 2000 上限）、没有**语义通道**（读文本须另调 `ui_find`，且截图与元素编号不闭环）。

## 2. 范围（做 / 不做）

**做**：三目标全量（§4–§7）；库化重构（§3）；宿主薄暴露（§3.4）；铁律同步（§9）；测试（§10）。
**分批**：阶段一为低风险高收益（双轴 1568、寻址扩展、库化）；阶段二为语义桥与成本优化（OCR/SoM/diff/by-ref）——见 §14。

## 3. 架构（方案 C）

### 3.1 项目布局与依赖方向

```
src/
  SharpSight.Capture/        # 新库：捕获 + 图像管线 + OCR + 标注（无 FlaUI、无宿主依赖）
  SharpSight.UiAutomation/   # 新库：FlaUI/UIA 元素模型 + 定位 + 交互（无 Capture/宿主依赖）
  DotNetDebugger.Engine/     # 迁出 Capture/ 后：仅 ClrDebug + DbgShim（去掉 System.Drawing.Common）
  DotNetDebugger.Session/    # 不变（Engine + Decompiler）
  DotNetDebugger.Web/        # 不变（Session + Decompiler）
  DotNetDebuggerMcp/         # 宿主：引 Capture + UiAutomation + Engine + Session + Decompiler，薄暴露 MCP
  DotNetDebuggerMcp.Client/  # 不变
```

- **依赖方向**：`SharpSight.Capture` 与 `SharpSight.UiAutomation` 是**零宿主依赖**能力库；`Engine`/`Session`/`Web` **不反向依赖**它们；宿主是唯一组合点。三者**均不得**引用 `DotNetDebuggerMcp`。
- **`SharpSight.Capture` 依赖**：`System.Drawing.Common`（GDI/PNG 编码/裁剪/标注）、TFM 自带 WinRT 投影（`Windows.Media.Ocr`）。**无 FlaUI**；**不引入任何第三方图像编码库**（输出固定 PNG，见 §6.1）。
- **`SharpSight.UiAutomation` 依赖**：`FlaUI.UIA3`（+ `FlaUI.Core`）。**无 Capture/Engine**。
- **组合关系**：元素级截图 = 宿主用 `SharpSight.UiAutomation` 取 `BoundingRectangle` + 所属顶层 hwnd → 交给 `SharpSight.Capture` 从**窗口帧**裁剪；两库互不引用，由宿主接线（避免循环依赖）。

### 3.2 Engine 迁出 `Capture/`

- 物理移动 `src/DotNetDebugger.Engine/Capture/*` → `src/SharpSight.Capture/`（`ScreenCapture`/`WgcCapture`/`GdiCapture`/`ImagePipeline`/`CaptureModels`）。
- `Engine` 去掉 `System.Drawing.Common` 包引用（§11 待实测：确认引擎其它处无 `System.Drawing` 使用）。
- **`WgcCapture.cs` 的手写 COM/WinRT interop（旧 spec 标「勿动」）整体原样迁移，不在本设计内改动**。
- TFM 保持 `net10.0-windows10.0.22621.0`（两新库同 TFM）；`DotNetDebuggerMcp.slnx` 增加两个项目。

### 3.3 库公开面（草案）

**`SharpSight.Capture`**
```csharp
// 抓取门面
DisplayInfo[] EnumerateDisplays();                       // index/szDevice/primary/bounds/workArea/dpi/scale
CaptureResult CaptureScreen(CaptureRequest req);         // 虚拟屏 / clip
CaptureResult CaptureDisplay(CaptureRequest req);        // 指定显示器（index 或 primary/left/right）
CaptureResult CaptureWindow(CaptureRequest req);         // hwnd；WGC→PrintWindow→BitBlt
CaptureResult CaptureElement(CaptureRequest req);        // 宿主给「顶层窗口 hwnd + 元素物理矩形」→ 从窗口帧裁剪

// 图像管线（唯一编码/缩放/裁剪归属）
EncodedImage Process(Image source, ImageOptions opts);   // 双轴缩放(maxWidth,maxHeight)、PNG 编码、grayscale
byte[] Annotate(Image source, Mark[] marks, AnnotateOptions opts); // SoM：框+编号徽标（高对比底、字号自适应）

// OCR
OcrReport? TryRecognize(byte[] pngBytes, string language);      // 探测 + null 降级
string[] AvailableOcrLanguages { get; }

// 变化检测（无变化即复用上一帧结论）
bool IsUnchanged(string targetKey, ReadOnlySpan<byte> image, TimeSpan ttl);
```
- `CaptureResult` 扩展（相对现状）：`OriginX/OriginY`、`Scale`、`FrameId`、`DisplayIndex`、`Rect`（虚拟屏物理像素）、`IsClientArea`、`WasAllBlack`、`ClippedToScreen`、`Source`（WGC/PrintWindow/BitBlt）。

**`SharpSight.UiAutomation`**：把宿主 `Services/Ui/*` 迁入并保持现有公开面（`UiElementLocator.Find/ResolveForAction`、`UiModels`、`UiPatternCapabilities`、`UiAutomationService`、`UiEventWaiter`、`UiSemanticResolver`），新增：
```csharp
int FrameGeneration { get; }                    // 单调递增代际号
FindResult FindForCapture(FindRequest req);     // 与 ui_find 共用 _lastFind；返回 index/name/type/rect/patterns
Rect GetBoundingRectangle(int index, int frameId);  // 带代际校验；失配 → StaleFrameException
```

### 3.4 宿主薄暴露（关键约束）

- 宿主**只做**：MCP 参数绑定/校验、编排（定位→抓取→处理→标注/OCR）、头部文本组装、落盘与 `CallToolResult` 组装、中文错误。
- 宿主**不得**含像素运算、坐标换算、编码、OCR 细节——全部下沉 `SharpSight.*`（沿用旧 spec「职责唯一归属 Engine」原则，改为归属库）。
- 工具仍**不入缓存、不过 ToolPipeline、不写 AgentView**（旧 spec D9 延续）。

## 4. 工具面契约（`screenshot`，agent 可见）

### 4.1 参数表（全部带默认值）

**寻址**
| 参数 | 类型/默认 | 语义 |
|---|---|---|
| `mode` | `string = "auto"` | `auto`（按其它参数推断）/ `screen` / `display` / `window` / `foreground` / `region` / `element` |
| `display` | `string = ""` | `mode=display`：`1`、`2`…（1 基）或 `primary`/`left`/`right`；空=主屏 |
| `processId` | `int = 0` | window/element：按 pid（0=未提供；优先于 `windowTitle`） |
| `windowTitle` | `string = ""` | window：标题子串；`@active` 表示前台窗口 |
| `hwnd` | `string = ""` | **十进制字符串**（避开 64 位 JSON 精度）；`IsWindow`+`IsWindowVisible`+`GetAncestor(GA_ROOT)==h` 校验 |
| `clientArea` | `bool = false` | window：截客户区（`GetClientRect`+`ClientToScreen`）而非整窗 |
| `region` | `string = ""` | `"x,y,w,h"`；`mode=region` 必填 |
| `regionSpace` | `string = "screen"` | `screen`（默认，兼容旧口径）/ `window`（相对目标窗口左上，**二期**） |
| `element` | `string = ""` | `mode=element`：UIA 元素引用（`index` 或 `name`+`type`）；见 §7.1 |
| `frameId` | `int = 0` | element/region 可选旧帧护栏；0=不校验 |
| `timeoutSeconds` | `int = 5` | window/foreground：等窗口出现（clamp 0-30） |

**图像经济**
| 参数 | 类型/默认 | 语义 |
|---|---|---|
| `maxDimension` | `int = 1568` | 单值双轴上限（0=不缩放） |
| `maxWidth` | `int = 0` | 双轴上限（非 0 时覆盖，与 `maxHeight` 取 min 语义） |
| `maxHeight` | `int = 0` | 同上 |
| `grayscale` | `bool = false` | 灰度（默认关） |
| `diff` | `bool = false` | 无变化检测：与上一帧比对，未变化只回文本 |
| `includeCursor` | `bool = false` | 叠加光标（**解冻**旧 spec §8） |
| `filePath` | `string = ""` | 非空=强制落盘该路径（**相对路径以临时目录 `%TEMP%\DotNetDebuggerMcp\screenshots\` 为基准**，绝对路径按原样；均不写当前工作目录） |

**语义桥**
| 参数 | 类型/默认 | 语义 |
|---|---|---|
| `annotate` | `bool = false` | SoM 编号叠加（元素来自 UIA，编号=`ui_find` index） |
| `ocr` | `bool = false` | OCR 文本（默认关） |
| `ocrLanguage` | `string = ""` | 空=按回退链（见 §7.2） |
| `ocrFallback` | `string = "auto"` | `auto` \| `always` \| `never` |
| `maxMarks` | `int = 120` | SoM 编号上限（clamp 1-200） |

> **不提供** `delay`（YAGNI）、`detail`（见 D8）、`byRef`（by-ref 二期，见 §6.6）。

### 4.2 寻址语义（钉死）

- `mode=auto` 推断优先级：`element` 非空→element；`hwnd` 非空→window；`windowTitle`/`processId` 非空→window；`region` 非空→region；`display` 非空→display；否则 screen。
- `mode=foreground`：`GetForegroundWindow` + `GetWindowThreadProcessId`（锁屏/安全桌面返回 NULL → 中文原因）。
- `mode=window`：整窗默认取 **`DWMWA_EXTENDED_FRAME_BOUNDS`**（去阴影、物理像素），失败回退 `GetWindowRect`；`clientArea=true` 走 `GetClientRect`+`ClientToScreen`。定位规则沿用旧 spec（pid 优先、标题兜底、空则活动会话 pid 兜底）。
- `mode=display`：`EnumDisplayMonitors` 枚举 → 按 `szDevice` 与 `MONITORINFOF_PRIMARY` 解析 `primary`；`left`/`right` = 主屏矩形左/右侧（按中心 x 排序取相邻）。**1 基**对外编号，头部回显 0 基 index + 设备名 + 矩形，避免枚举序歧义。
- `mode=screen`：虚拟屏（`SM_X/Y/CX/CYVIRTUALSCREEN`，原点可负），GDI BitBlt。
- `mode=region`：`regionSpace=screen` 默认；越界=裁交集 + 头部注明；完全在屏外=中文错误。
- `mode=element`：见 §7.1。
- **不兼容组合显式报错**（对齐 `13`）：如 `clientArea` 与 `element` 同时给→报错；`region` 与 `window`/`display` 同时给→报错。**输出恒为 PNG**（无 `format`/`quality` 参数）。

### 4.3 返回契约与头部（沿用旧 spec 双轨，扩展头字段）

```
成功 <2MB(base64 后): content[0]=文本头部；content[1]=image 块
成功 ≥2MB/指定 filePath: content[0]=文本头部 +「已落盘: <绝对路径>」，无 image 块（落盘失败→仍附块）
失败: 纯文本中文提示，不抛异常
```
头部（示意，纯文本，字段按模式裁剪）：
```
目标:   显示器 2 "\\.\DISPLAY2" (1920,0 1920x1080)   ← 或 窗口 "标题" (pid=…) / 前台窗口 / 屏幕 / 屏幕区域 / 元素 Button "保存"
尺寸:   原生 3840x2160 → 1568x882 (41%)   缩放=41%   原点=(0,0)   帧=15
来源:   WGC
标注:   12 个可交互编号（编号=ui_find index，可直接 ui_action index=N）
OCR:    zh-Hans-CN（可用: en-US, zh-Hans-CN）  MaxImageDimension=10000
变化:   与上一帧一致（diff 命中，未附图）        ← 仅 diff 命中时
备注:   画面为纯黑（目标可能未渲染）            ← 仅全黑时
---
```
> `原点`/`缩放` 即 §5 的 `origin+scale`；`帧` 即 `frameId`。全部为纯文本行，不含行号。

### 4.4 与 `ui_*` 的边界（D8）

- **不加 `detail=text`**：读文本用 `ui_find`（已有结构化输出）。
- `annotate` 的编号**直接复用 `ui_find` 的元素清单**（同一 `_lastFind`、同一 index、同一 `frameId`），使图上 `[7]` ↔ `ui_action index=7` 物理同源。
- OCR 词**不并入编号空间**（只作「可见文字」附注，因本项目无物理输入，OCR 词无可执行入口）。

## 5. 坐标模型（`origin + scale + frameId`）

- 统一表达：`screen_x = origin_x + image_x / scale`，`screen_y = origin_y + image_y / scale`。
- `origin` = 抓取矩形左上角在**虚拟屏物理像素**空间的坐标（显示器/窗口/元素/region 各自填）；`scale` = 图像像素 / 原生物理像素（现有 `k=output/native` 即 scale）。
- `scale=1` 且 `origin=(0,0)` 时可省略显示（screen 全屏常见）。
- 头部恒给 `原点/缩放`（`screenshot` 是**唯一设置可点击/可操作坐标系**的入口——本项目的操作由 `ui_*` 语义承载，故只作**视觉标注一致性**用途）。
- `frameId`：每次元素采集产出的单调递增代际号（由 `SharpSight.UiAutomation` 维护）；`ui_action`/`ui_get` 携带的 `frameId` ≠ 当前 → **拒绝 + 教学提示**（「编号 N 来自旧画面（帧 12，当前 15），请重新 screenshot/ui_find」）。
- region 旧口径兼容：`regionSpace="screen"` 且未给 `origin` → 完全沿用旧换算（屏幕图像素空间），保证既有调用方零迁移。

## 6. 图像管线（经济）

### 6.1 格式与编码
- **输出固定 PNG**（无损、文字清晰），**唯一格式**——不提供 `format`/`quality` 参数，且**不引入任何第三方图像编码库**（PNG 由 `System.Drawing.Common` 编码）。
- **理由（用户裁定 2026-09-28）**：第三方 WebP 编码库要么**免费路径不可升级**（`SixLabors.ImageSharp`：v3 无密钥但属旧线、v4+ 强制许可证密钥），要么带来体积/打包面（SkiaSharp 原生库 ~13MB）——均不满足「面向未来、可升级、依赖洁净」。体积收益的大头与格式无关，由**双轴降采样**（§6.2）与**超限落盘**（本节末条）承担。
- 编码后 `≥2MB`（base64 前以 base64 后长度判定，沿用现状常量 `InlineImageBase64Bytes`）或 `filePath` 非空 → 落盘只回路径。

### 6.2 双轴降采样
- `k = min(1, maxW/W, maxH/H)`，`W/H` 为**原生物理像素**（DPR 已含，本项目进程 PMv2）。
- `maxDimension` 展开为 `maxW=maxH=maxDimension`；显式 `maxWidth`/`maxHeight` 优先。
- 默认 `1568`（官方 vision 长边上限；超限会被服务端二次 resize 导致坐标漂移）。
- 在**源头**降采样（抓取后立即缩，再编码），并恒回传 `缩放`。

### 6.3 灰度
- 默认关；显式 `grayscale=true` 才转灰（`System.Drawing` `ImageAttributes.SetColorMatrix` 灰度矩阵，0.299/0.587/0.114）。色状态会丢失。

### 6.4 无变化检测（`diff`）
- 一期只做「**无变化 → 不附图、只回文本**」：对**编码前原图**做 64 位感知哈希/分块比对（TTL 内同一 `targetKey`）。
- `targetKey` = 模式 + 目标标识（display index / hwnd / region 矩形 / clientArea 标志）。
- 命中（未变化）→ `content[0]` 头部加「变化: 与上一帧一致」，**无 image 块**；未命中 → 正常返回并更新缓存。
- 不做真 P 帧差分图（二期，评估后定）。

### 6.5 光标（`includeCursor`）
- 默认关。WGC 路径用 `GraphicsCaptureSession.IsCursorCaptureEnabled`（Win10 1903+，`ApiInformation` 探测）；GDI 路径需手动叠加光标（`GetCursorInfo`+`GetIconInfo` 绘制），失败则忽略并在头部注明「光标未叠加」。

### 6.6 by-ref（`resource_link`）
- **默认不做**。理由（`12`）：内联 image content 是 MCP 规范一等公民、**零 capability 前置**；`resource_link` **未强制**服务端实现 `resources/read`，且 SDK 在无 resources 面时**不声明** capability → 落地 by-ref = 一次「新增 `resources/list`+`resources/read` 能力族」的完整改动，收益又受客户端实现限制。
- 本仓库当前**无任何 resources 面**（`.WithToolsFromAssembly()` 之外零注册）。
- 二期若做：显式 `byRef=true` 才返回 `resource_link`，并补齐 `resources/read`；文案提示「看不到图改用内联」。
- **可选（二期）服务端开关** `imageResponses=allow|omit|only`（playwright 范式，`13`④.4）：`omit`=只回文本头部、不附图（纯结构检查场景省 token）。

## 7. 语义桥

### 7.1 元素级截图与元素身份（D5/D9）
- 元素来源：`SharpSight.UiAutomation.UiElementLocator.FindForCapture`（同 `ui_find` 一套身份：pid/窗口 → `FindAllDescendants` → 过滤 → 能力探测 → ordinal）。
- 遮挡安全：**先拿元素所属顶层窗口的帧**（WGC/PrintWindow），再把元素矩形从虚拟屏物理像素换算为帧内坐标裁剪（**不能对屏幕直接 BitBlt 裁元素**）。
- 元素可能属**另一顶层窗口**（ComboBox 弹层/popup/tooltip）→ 用元素自身 `GetAncestor(GA_ROOT)` 的 hwnd 取帧；跨显示器则与虚拟屏求交。
- 离屏/虚拟化：`IsOffscreen=false` 过滤 + `TryRealizeVirtualized`/`ScrollIntoView`。
- `frameId` 校验：`element` 引用的 `frameId` 与当前差 → 拒绝 + 教学提示。

### 7.2 OCR（D6/D10）
- `Windows.Media.Ocr`（非打包 .NET 可用，`08`①；TFM 自带投影，**零新增 NuGet**）。
- 语言回退链：显式 `ocrLanguage` → 当前输入法 → `TryCreateFromUserProfileLanguages()` → `en-US` → `AvailableRecognizerLanguages[0]` → 全无则关 OCR + 提示安装命令。
- **必须 null 检查 + 优雅降级**（未装返回 null 不抛）；`TryCreateFromLanguage` 不支持也返回 null（不抛）。
- 预处理：对**未缩放的 native 图**跑 OCR，再统一乘 `k` 画 marks（少一层误差）；抄 PowerToys：1.5× 放大、最小 80×80 画布、`BitmapAlphaMode.Ignore`（PrintWindow 产物 alpha=0）。
- `ocr=false`（默认）时**不加载 OCR**；`ocr=always` 时语言缺失 → 中文提示 + 参数保留（不静默成功）。

### 7.3 Set-of-Marks（`annotate`）
- 元素来源 = UIA（§7.1），**不用 ML 检测器**；编号 = `ui_find` index（0 基）。
- 筛选：`IsEnabled` + `IsOffscreen=false` + 非空 bounded + 与截图区相交 + 面积上下限（去全窗容器与 1px 噪声）；嵌套只留最内层可交互。
- 编号顺序：按阅读顺序（先 y 后 x 带容差），比「小区域优先」可预测。
- 绘制（`SharpSight.Capture.Annotate`）：高对比纯色框 + 编号徽标（对比色自动判定、字号随图缩放、越界内移）；不画随机色、不内嵌光标环（默认）。
- `maxMarks` 上限（默认 120）→ 超出只标前 N + 文本注明「已截断，共 M 个可交互元素」。
- OCR 词只作附注（`visibleText[]`），不进编号空间。

### 7.4 代际一致性（护栏）
- 语义 = **元素缓存的代际一致性**（本项目动作为 UIA 语义，非按坐标点击）。
- `SharpSight.UiAutomation` 维护单调 `FrameGeneration`；每次采集（`ui_find` 或 `screenshot` 元素采集）产出一帧；元素清单与 `frameId` 绑定。
- 消费侧（`ui_action`/`ui_input`/`ui_get`/`screenshot element`）带旧 `frameId` → 拒绝 + 教学提示。

## 8. 仓库集成与改动清单

| 项 | 动作 |
|---|---|
| **新增** `src/SharpSight.Capture/` | 新库（§3.3）；TFM `net10.0-windows10.0.22621.0`；依赖 System.Drawing.Common（**无第三方图像库**） |
| **新增** `src/SharpSight.UiAutomation/` | 新库（§3.3）；依赖 FlaUI.UIA3 |
| `src/DotNetDebugger.Engine/Capture/*` | **迁出**至 `SharpSight.Capture`；Engine 去掉 System.Drawing.Common（§11 待实测） |
| `src/DotNetDebuggerMcp/Services/Ui/*` | **迁出**至 `SharpSight.UiAutomation`；宿主改为引用 |
| `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs` | `ui_find` 输出增 `frameId`；`ui_action`/`ui_input`/`ui_get` 增**可选 `frameId`**（`int = 0`，0=不校验，**向后兼容**）——旧帧引用拒绝 + 教学提示（§7.4）。**这是 `ui_*` 工具面变更 → 触发 §9 的 README/握手/回归同步** |
| `src/DotNetDebuggerMcp/Tools/Screenshot/ScreenshotTool.cs` | 扩展参数/头部/编排；改引用新库命名空间（原 `Tools/Debugger/DebugScreenshotTool.cs`，2026-09-28 独立成 `Tools/Screenshot/`） |
| `DotNetDebuggerMcp.slnx` | 增两个项目 |
| `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` | 增对两新库的 ProjectReference；TFM/PackAsTool hack 不变 |
| `Configuration/AppConfig.cs` | 新增 `ScreenshotMaxDimension=1568`（原 2000）、`ScreenshotMaxMarks=120`、`OcrDefaultLanguage=""`；保留 `InlineImageBase64Bytes`/`ScreenshotsDir` |
| 各 `AGENTS.md` | 根：依赖方向句加两库；Engine：去 System.Drawing.Common + Capture/ 迁出说明；宿主：库引用与工具面；Web：不变 |
| `docs/planning/specs/2026-09-22-screenshot-tool-design.md` | §8 冻结项（多显示器/`includeCursor`/元素级/`origin`）加 **Superseded（部分）** 指向本 spec；正文不重写 |
| `docs/planning/specs/README.md` / `docs/planning/README.md` | 登记本 spec |
| `README.md`（根，打包进 NuGet） | screenshot 章节重写（三目标参数 + Windows-only + 新库说明） |
| 握手 `AppText.HandshakeFeatureIntro` | screenshot 触发条件同步（多显示器/元素级/OCR/标注） |
| `CHANGELOG.md` | **新建 `[Unreleased]` 段**（当前无）：记 screenshot 通用化 + 新库（使用者可见） |
| 版本三处同步 | 发布时（csproj `<Version>` / `.mcp/server.json`×2 / CHANGELOG 段转换）；本设计不动 |

## 9. 铁律同步（同 commit）

1. **根 `README.md` + 握手简介 + 回归断言**：改 MCP 工具必须同步。断言 `DotNetDebuggerMcpCmdTests.HandshakeFeatureIntro_覆盖全部能力族触发条件` 需**逐族补**（新增：多显示器/前台/元素级/OCR/标注 触发词），漏一族即失败。**注意 `ui_*` 也因新增 `frameId` 变更工具面（§8），`ui_*` 的 README/握手/`[Description]` 需一并更新。**
2. **`CHANGELOG.md` 无 `[Unreleased]`** → 新建段（面向包使用者）。
3. **新增跨层字面量常量化**：OCR/SoM/落盘提示收敛到常量类（`AppText` 或新 `CaptureText`）。
4. **图片类例外**：`screenshot` 保持 `Task<CallToolResult>`、错误纯文本、不设 `IsError`。
5. **stdout 纪律**：不新增 stdout 输出；不得改动 stderr 日志配置。
6. **新库 NuGet 元数据**：`SharpSight.Capture`/`SharpSight.UiAutomation` 各自 `PackageId`/`Version`/`Description`/`README`（独立发布）；Windows-only 注明。

## 10. 测试计划

- **`SharpSight.Capture` 单测**：显示器枚举；`CaptureDisplay` 各选择器；DWM 边框 vs `GetWindowRect` 偏差；双轴缩放守恒；PNG 产物合法；灰度；`IsUnchanged` TTL；`Annotate` 编号/截断/越界；OCR 探测与 null 降级（语言缺失 `Assert.Skip`）。
- **`SharpSight.UiAutomation` 单测**：迁移既有 U1A 测试；`FindForCapture` 身份与 `ui_find` 一致；`FrameGeneration` 单调；旧帧拒绝。
- **宿主工具单测**：参数校验全表中文文案；返回结构（头部 + image 块）；落盘/2MB seam（旧 spec §5.1 的 seam 缺失需补，见 §11）；`diff` 命中无图；`annotate`/`ocr` 头部；`mode=element` 端到端（UiSampleApp）。
- **GUI 来源**：复用 `tests/TestData/UiSampleApp`（不改生成脚本）。
- **CI**：无头/无 GPU → WGC/OCR 探测失败即 `Assert.Skip`；沿用 Engine `Parallelization(None)` 与宿主 `[Collection("AppServices")]` 纪律。
- **手工验收**：副屏/前台/客户区/hwnd/元素各截一张；OCR 语言包缺失表现；标注图编号与 `ui_action index` 闭环。

## 11. 待实测 Spike（实现前置）

1. **PNG 产物与工具包干净**：确认发布产物未引入任何第三方图像库的原生资产（PNG 走 `System.Drawing.Common`）。
2. **OCR**：本机非打包 exe 下 `TryCreateFromUserProfileLanguages()` 是否可用；`zh-Hans-CN` 语言包在位/缺失降级提示。
3. **WGC 窗口帧几何**：`CreateForWindow` 帧对应 `GetWindowRect` 还是 `EXTENDED_FRAME_BOUNDS`（决定元素裁剪偏移与整窗去阴影口径）。
4. **元素裁剪正确性**：ComboBox 弹层/popup 跨顶层窗口场景下，用元素自身顶层 hwnd 取帧是否覆盖。
5. **Engine 去 System.Drawing.Common**：全仓确认引擎其它处无 `System.Drawing` 使用。
6. **FlaUI #614**：`FindAllDescendants()` 偶发不完整是否在本项目目标复现。
7. **测试 seam**：2MB/默认目录分支的测试可替换 seam（旧 spec §5.1 记录实现未留 seam）。
8. **双库打包**：`SharpSight.*` 独立 pack/publish 流程与宿主引用（windows TFM 依赖链）验证。

## 12. 风险与缓解

| 风险 | 缓解 |
|---|---|
| 库化重构波及 Engine/Session/Web/host，回归面大 | 先做「物理迁移 + 命名空间替换」保行为不变（阶段一），再叠功能；全量 build + 单测回归 |
| `SharpSight.*` windows TFM 被非 Windows TFM 引用（NU1201） | 宿主本就 windows TFM；Web/Session 不引两库 |
| 只有 PNG 一种格式（无 jpeg/webp） | 已定（用户裁定 2026-09-28）：体积靠双轴降采样 + 超限落盘；若将来确需其它格式，按「可免费升级 + 许可无门 + 无原生资产」三条标准重新选型 |
| OCR 语言包缺失是常态 | 默认关 + null 降级 + 中文安装提示（§7.2） |
| WGC 帧几何不明导致元素裁剪偏移 | §11.3 spike 先定；spike 前元素级走 `PrintWindow` 帧（其几何=窗口矩形） |
| `diff` 缓存与「截图不入缓存」原则冲突 | 独立小 TTL（`ChangeTracker`），与反编译缓存/`IsErrorResult` 无关；文档说明 |
| **安全：截图可能含敏感信息 / 屏幕内容是不可信输入** | 本工具**不做**像素级脱敏（参照 `11` 反例：密码字段 value 未脱敏）；README/握手注明「截图会原样采集可见内容」；agent 侧应把屏幕内容视为**不可信数据**（防提示注入）。脱敏留待后续独立设计 |
| 双库独立发布增加维护面 | 阶段一先本地 ProjectReference，NuGet 独立发布在阶段二（§14） |

## 13. 明确不做（YAGNI / 冻结）

- mp4 录制 / `play` 输入 DSL；NCC 模板匹配；ML 像素检测器（OmniParser 式）。
- 跨平台 provider（X11/Wayland/portal）；HTTP 多租户鉴权；遥测。
- **默认** by-ref `resource_link`（二期显式开关）；**默认** 灰度；`delay`。
- **其它图片格式（jpeg/webp）一律不做**：`screenshot` 输出固定 PNG，不提供 `format`/`quality` 参数。
- `detail=text`（D8）；物理输入注入（本项目动作走 UIA 语义）。
- 截图结果的**像素级敏感信息脱敏**（本设计不涉及；仅在文档提示可见内容原样采集）。
- 截图结果入反编译缓存 / 写 `AgentView` / Web 展示回放（Web 冻结）。

## 14. 分批实施

- **阶段一（低风险高收益）**：库化迁移（Capture→`SharpSight.Capture`、Ui→`SharpSight.UiAutomation`，行为不变）；寻址扩展（display/foreground/hwnd/clientArea/element + `origin+scale+frameId`）；双轴降采样 + 默认 1568；`includeCursor`；头部/文档/握手/回归同步。
- **阶段二（语义桥与成本）**：`annotate`（SoM）/`ocr`/`maxMarks`；`diff` 无变化检测；`regionSpace=window`；`SharpSight.*` 独立 NuGet 发布；by-ref 评估。

## 15. 来源（本设计依据）

调研目录 `docs/planning/research/screenshot-generalization/`（15 份，均带来源等级）：
- 三目标：`01-addressing.md` / `02-image-economy.md` / `03-semantic-bridge.md`。
- 业界范式（高星/官方重建）：`04-prior-art.md`。
- 集成点：`05-repo-integration.md`。
- 源码核实（A）：`06` FlaUI · `07` ShareX · `08` PowerToys · `09` ImageSharp · `10` OmniParser · `11` Windows-MCP · `12` MCP 规范+computer-use 官方+browser-use · `13` chrome-devtools-mcp+playwright · `14` ScreenToGif+flameshot+UFO。
- 官方 skill 评估（B）：`15-eval-openai-screenshot-skill.md`。
- 索引与决策表：`README.md`。
