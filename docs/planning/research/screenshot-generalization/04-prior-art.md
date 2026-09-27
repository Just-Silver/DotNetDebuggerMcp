# 04 · 业界范式对比（高星/官方来源 → 可借鉴 + 参数面草案）

> **来源等级**：全部 A（源码核实：11/12/13/14/15 + 06–10）/ B（官方规范：MCP spec、Anthropic computer-use、Microsoft Learn）。低星来源集中在文末附录且标注「不作为依据」。生成日期 2026-09-27。

本文件取代旧版「基于 0–32 星项目」的业界对比。横向总览只保留**高星/官方**来源，每条附本目录文档号；来自源码的结论附 `file:line`。原 5 个低星项目（`desktop-touch-mcp` / `windows-computer-use-mcp` / `gui-mcp` / `mcp-screenshot-server` / `ubuntu-desktop-control-mcp`）全部移入文末附录，**正文不再把它们当依据**。

## 事实判断：桌面截图方向的「高星 MCP 同类」稀缺

桌面截图方向**缺少高星 MCP 同类**。最接近的高星同类是 **Windows-MCP（7.3k★，`11`）** 与**浏览器 MCP（chrome-devtools-mcp 52.6k★ / playwright MCP 37.6k★，`13`）**；桌面侧其余高星/官方来源（ShareX 39.8k、ScreenToGif 27.7k、flameshot 30.9k、UFO 9.8k、OmniParser 25.5k、FlaUI 3.1k、PowerToys 139k、ImageSharp 8k、OpenAI Codex screenshot skill 27.7k）或是**采集/标注工具**、或是**库**、或是**单 OS 技能**，并非同类 MCP。因此本项目参数面以**四者为可信外部基线**：**Windows-MCP（`11`）＋ 浏览器 MCP（`13`）＋ MCP 官方规范（`12`）＋ Anthropic computer-use 官方参考（`12`）**，辅以官方/高星源码（`06`–`10`、`14`、`15`）。

## ① 横向总览（行只保留高星/官方来源）

| 来源（文档号） | 寻址 | 返回形态 | token 控制 | 语义桥 | 坐标空间 | 安全 or 备注 |
|---|---|---|---|---|---|---|
| **Windows-MCP**（`11`，7.3k★） | `display`（0 基活动显示器数组）/ `region`（虚拟桌面像素 xyxy），region 优先；**无 hwnd/窗口标题/前台寻址**（`11`③①；`tools/snapshot.py:44-45,90-91`、`desktop/service.py:1110-1212`） | MCP `image` content（PNG），文本块在前、图片块在后；**不落盘、无 resource_link、无大小阈值回退**（`11`④.1；`tools/_snapshot_helpers.py:13,236-239`） | 双轴独立比例取 min，硬上限 1920×1080 + 环境变量 `WINDOWS_MCP_SCREENSHOT_SCALE∈[0.1,1.0]`；**仅 PNG**，无 webp/jpeg/灰度/quality/diff（`11`④.2/④.3；`_snapshot_helpers.py:21,24-34,91`、`desktop/service.py:241-259`） | UIA 树 → 语义树 + 可交互/可滚动元素清单；0 基整数 `label`=列表下标，图形编号与 `label` 对齐；**无 OCR**（`11`⑤；`tree/views.py:185-209`、`desktop/service.py:651-663,1273-1304`） | 虚拟桌面物理像素；降采样时回传 `Coordinate Scale = 1/scale` 要求 agent 乘回（`11`⑥.1；`_snapshot_helpers.py:186-210`） | HTTP Bearer/OAuth + IP allowlist + TrustedHost + SSRF 防护；工具可裁剪；**截图无擦除/模糊、密码 value 未脱敏**；**无旧帧护栏**（无 capture/snapshot id）（`11`⑧⑥.2；`infrastructure/auth.py:23-79`、`security.py:25-65`、`tree/service.py:601-609`、`desktop/service.py:273-290`） |
| **chrome-devtools-mcp**（`13`，52.6k★） | `uid`（a11y 文本快照元素 id，形如 `"{snapshotId}_{counter}"`，跨快照复用）；整页/视口（`13`②⑥；`src/tools/screenshot.ts:159-164`、`src/TextSnapshot.ts:78-87`） | 内联 image content 优先；显式 `filePath` 或 `≥2,000,000B` 落盘只回路径（`13`②；`screenshot.ts:269-287`） | `format` png/jpeg/webp（默认 png）、`quality`（仅 jpeg/webp）；**尺寸不做工具参数**，只有服务端 flag `--screenshot-max-width/height`，源头 CDP `clip.scale` 保比降采样（双轴取 min）（`13`②③；`screenshot.ts:145-158`、`mcp-options.ts:232-263`、`screenshot.ts:93-124,223-233`） | `take_snapshot` a11y 文本树 + `uid`；工具描述原文「**Prefer taking a snapshot over taking a screenshot**」；截图无编号（`13`⑤⑥；`src/tools/snapshot.ts:14-16`、`screenshot.ts:138`） | 视口 CSS 像素；DPR 乘入物理像素上限（修复 `ebf58f2`）（`13`③；`screenshot.ts:98-106`） | MCP roots + 恒含 OS temp；路径规范化前缀校验；写文件 `O_NOFOLLOW`+`0600`；「Reference over Value」（`13`⑦；`McpContext.ts:243-324,678-703`、`design-principles.md:12`） |
| **playwright MCP**（`13`，37.6k★） | `target`（页面快照 `aria-ref`，形如 `e1`/`f2e3`）或唯一选择器；整页/元素（`13`④⑥；`backend/snapshot.ts:23,27`、`backend/tab.ts:536-548`） | **总是写盘** outputDir 并附文本链接；**未显式给 `filename` 时再附内联图**（`13`④.2；`backend/screenshot.ts:75-85`） | `scale=css\|device`（默认 css）；`--image-responses=allow\|omit\|only`（默认 allow）；`--output-max-size` 按 mtime 淘汰旧文件；JPEG 质量硬编码 90（`13`④；`backend/screenshot.ts:33,65`、`mcp/config.d.ts:236-239`、`backend/response.ts:236-265`） | `browser_snapshot` a11y 树 + `aria-ref`；工具描述原文「**this is better than screenshot**」；截图不编号（`13`⑤⑥；`backend/snapshot.ts:40`、`backend/screenshot.ts:53`） | 视口 CSS 像素（`scale=device` 含 DPR）（`13`④.1；`backend/screenshot.ts:33`） | 限制在 `outputDir ∪ cwd`；拒绝系统目录；**自称非安全边界**（`13`⑦；`backend/context.ts:494-510`、`mcp/config.d.ts:259-265`） |
| **MCP 官方规范**（`12`，B） | 不涉及（协议层无截图寻址概念） | tool result 中图像是**一等公民**：`CallToolResult.content: ContentBlock[]`，`ImageContent{type,data,mimeType}`，**零 server capability 前置**；`resource_link` 为可选内容块（`ResourceLink` 必填 `uri`+`name`），**规范未强制服务端实现 `resources/read`**（`12`②；`schema.ts:1809-1838,2305-2306,2340-2361`、`tools.mdx:404-439,451-471`） | 规范**未对 data 长度/像素做协议级约束**（上限属 provider/vision 侧）；一切 content 支持可选 `annotations`（`12`②.1；`tools.mdx:410-416`） | 无（协议层无） | 无（协议层无） | 服务端声明 `resources` capability 的条件：SDK 在 resources 相关 handler/`ResourceCollection`/显式 capability **全为 null 时直接不声明**；by-ref 落地必然要新增 `resources/list` + `resources/read`（`12`②.3③.3；`McpServerImpl.cs:1208-1215`、`resources.mdx:41,74-81`） |
| **Anthropic computer-use**（`12`，B/官方参考） | 整屏；`zoom.region` 细读（`12`⑤.1；`computer.py:475-509`） | tool_result **一律内联 image 块**（`media_type: image/png`），**无 resource_link/by-ref**（`12`⑤.1；`loop.py:363-372`） | 应用侧先缩到 API 不会二次缩放的尺寸（28×28 tile、长边 ≤1568px、tile ≤1568）；推荐 XGA/WXGA 上限；LANCZOS + JPEG（quality=75）；图像历史按 interval pruning 保 prompt cache（`12`⑤.1⑤.2；`image.py:1-15,38-102`、`constants.py:114-130`、`README.md:167-173,350-422`） | 靠 `zoom` 细读；**无文本树**（`12`⑤.1） | 模型坐标以「截图（缩放后）像素」为准（`screenshot_size` 即坐标帧）；Retina DPR 需折算（`12`⑤.1⑤.2；`computer.py:102-111,244-250,255-275`） | 官方参考场景为 Docker+X11 / 本地 macOS 最小权限；每图约 **1,500 input tokens**、30 轮后图像历史约 45k tokens 且每次重发（`12`⑤.1⑤.2；`README.md:350-422`） |
| **browser-use**（`12`，116k★） | 每步截图 + 可交互元素编号 `[k]`（`selector_map` 键）同时出现在 DOM 文本与截图编号框（`12`⑥；`agent/service.py:1094-1099`、`dom/serializer/serializer.py:1030-1033`、`python_highlights.py:383-399`） | 送 LLM 前缩放 + **PNG base64 data URL** 内联（`12`⑥.2；`agent/prompts.py:442-474`） | `llm_screenshot_size`（默认 None，云服务默认 1400×850）LANCZOS 缩放；**无变化检测/无感知哈希**（`12`⑥.1⑥.2；`session.py:429-432`、`prompts.py:375-401`、`beta/service.py:4320-4325`） | **双重编号同源**：DOM 文本 `*[k]` 与截图编号框 `k` 同为 `selector_map` 键（`12`⑥.3；`serializer.py:1030-1033`、`python_highlights.py:383-399`） | LLM 尺寸 → viewport 回算 `actual = llm / llm_size * original_viewport`（`12`⑥.2；`tools/service.py:611-626`） | 截图前先 `remove_highlights()` 避免高亮入图；状态摘要缓存 + 动作后失效（`12`⑥.1；`screenshot_watchdog.py:55-62`、`session.py:1241-1243`） |
| **OpenAI Codex screenshot skill**（`15`，27.7k★，官方 skill） | `-Region`（x,y,w,h）/ `-ActiveWindow`（`GetForegroundWindow`）/ `-WindowHandle`（hwnd）；三参数互斥冲突抛错；Windows 只有虚拟桌面单图（`15`②③） | **保存到磁盘并回传路径**（「Always report the saved file path」），**无内联图像**（`15`①） | 固定 png（Windows 可 jpg/bmp），**无 quality/无降采样**（`15`②③） | 无（无 OCR/UIA/SoM）（`15`③） | 无坐标元数据回传（`15`③） | 「工具优先」原则（专用工具 > OS 抓取兜底）；保存位置优先级：显式 path > OS 默认截图目录 > 临时目录（`15`④） |
| **ShareX**（`07`，39.8k★） | 虚拟屏 `SystemInformation.VirtualScreen`；窗口矩形优先 `DWMWA_EXTENDED_FRAME_BOUNDS` 回退 `GetWindowRect`；元素级=Win32 子窗口（`EnumChildWindows`），**无 UIA**（`07`①B；`CaptureHelpers.cs:35-38,326-346`、`WindowsList.cs:65-69,171-191`） | 落盘文件（历史 SQLite），非 MCP（`07`②；`HistoryManagerSQLite.cs:64-85`） | `memcmp` 逐字节比图做「无变化检测」；有损质量回退到满足体积（`SaveJPEGAutoQuality`）；PNG 位深自适应；**原生不支持 WebP**（`07`①A；`ImageHelpers.cs:1050-1069,2904-2942,3028-3059`） | `Windows.Media.Ocr`（系统内置）；标注数据驱动（`NumberAnnotation` 编号球）；**无 UIA 语义桥**（`07`①C；`OCRHelper.cs:36-106`、`NumberAnnotation.cs:34-240`） | 物理像素、虚拟屏含负坐标（`07`①B；`CaptureHelpers.cs:103-125`） | 窗口白名单过滤链（cloaked/工具窗/忽略类名/被包含去重）；历史无去重/无容量上限（不是可借模型）（`07`①B②；`WindowsList.cs:37-41,65-69,79-99,140-150`） |
| **ScreenToGif**（`14`，27.7k★） | `EnumDisplayMonitors`+`GetMonitorInfo` 枚举；`MonitorFromWindow/Point`；**未找到 PrintWindow**（窗口捕获=桌面 DC BitBlt 窗口矩形）（`14`②.1②.3；`Monitor.cs:95-106,184-198`、`Capture.cs:89-122`） | 帧默认落盘 `{n}.png`；导出 GIF/APNG/WebP(FFmpeg)/视频等（`14`②.5；`ImageCapture.cs:67,171`、`ExportFormats.cs:18`） | DXGI dirty/move rects 丢无变化帧（非逐像素 diff）；WebP 经 FFmpeg（quality 75）；**录制导向、非单次抓取**（`14`②.5；`DirectChangedImageCapture.cs:40-62`、`FfmpegWebpPreset.cs:15`） | 无（录制工具，无 UIA/OCR） | 每屏 `GetDpiForMonitor(Effective)`，`Scale=Dpi/96`（`14`②.2；`Monitor.cs:71-88`、model `:21-23`） | 用 DXGI Desktop Duplication（**非** WinRT WGC）；被遮挡窗口无专门处理（`14`②.4②.6；`DirectImageCapture.cs:24,44-46`、`Capture.cs:97-103`） |
| **flameshot**（`14`，30.9k★） | 先抓整桌/指定屏 → 显示器选择 → 区域选择；多显示器按物理像素偏移拼大画布（`14`③.1③.2；`screengrabber.cpp:303-347,781-856`） | 保存为文件（Qt `QImageWriter`，默认 png）（`14`③.5；`screenshotsaver.cpp:99-102`） | JPEG 质量默认 75；**webp 未硬编码**（取决于 Qt 构建插件）（`14`③.5；`confighandler.cpp:138`） | 无（标注工具，非语义桥） | 非 Windows 平台除 DPR；Windows 按各屏 DPR 累加物理宽高（`14`③.3；`screengrabber.cpp:413-427,700-728`） | **Linux 优先**项目，Windows 侧只用 Qt 通用抓屏（无 DesktopDuplication/PrintWindow）（`14`③.1；`screengrabber.cpp:189-229`） |
| **UFO**（`14`，9.8k★，微软） | UIA 控件（`IsEnabled`/`IsOffscreen`/`IsControlElement`/ControlType 白名单，限 500）；元素 ID 为 1 起始字符串（`14`⑤.2；`inspector.py:334-370,266,461-463`、`ui_mcp_server.py:762,788`） | 截图落盘 PNG（`compress_level` 可配）；SoM 标注图（`14`⑥.1⑥.2；`screenshot.py:33,1209-1220`） | `rescale_image` LANCZOS + letterbox 塞固定画布；PNG 压缩级可配（默认 1）（`14`⑥.2；`screenshot.py:45-67,33`） | SoM = 每控件类型一色 + 半透明色块 + 数字/字母徽标；截图与 UIA 绝对矩形减窗口矩形对齐（`14`⑥.3；`screenshot.py:361-377,693-757`） | 绝对屏幕矩形减窗口矩形得窗口相对坐标，另出 0..1 归一化（`14`⑥.3；`screenshot.py:379-398`） | `change_detector` 是任务星座**结构** diff（非图像 diff）；`control_filter` 为 NLP 死代码、全仓无调用方（`14`④⑤；`change_detector.py:27-184`、`control_filter.py:164-272`） |
| **OmniParser**（`10`，25.5k★，微软） | 图标检测框 + OCR 文本框；内部归一化 [0,1]；点击点=框中心（`10`①；`utils.py:417-496`） | base64 PNG + `label_coordinates` + 元素清单（`10`②；`utils.py:496`） | 无（算法层不做尺寸经济） | SoM 编号（0 基连续）+ 标签摆放 4 锚点最小重叠（`10`①③；`utils.py:449-451,480`、`box_annotator.py:189-262`） | 归一化 `bbox` xyxy / `label_coordinates` xywh（`10`①；`utils.py:492-493`） | 过滤极弱（仅面积>0），需本项目自加严过滤；ML 检测器/字幕模型不采纳（`10`①④；`utils.py:444-445`） |

### 三点共识（可作本项目取向的外部依据）

1. **结构/引用优先、像素补充**：chrome-devtools-mcp 与 playwright MCP 的工具描述原文都劝退「以截图作为可操作依据」（`13`⑤；`snapshot.ts:14-16`、`backend/snapshot.ts:40`、`backend/screenshot.ts:53`）。
2. **内容优先于形式**：官方 computer-use 与 browser-use 的坐标空间都是「模型实际看到的截图尺寸」，并按官方 vision 预算（1568）预缩放，避免服务端二次 resize 造成坐标漂移（`12`⑤.2⑥.2）。
3. **编号 SoM 是高频增量**：chrome/pw 两家**均无**编号叠加（`13`⑥ 本地检索 0 命中）；能对照的高星实现是 UFO 的 UIA 条件过滤 + SoM 标注（`14`⑥）与 browser-use 的「双重编号同源」（`12`⑥.3）。

## ② 可借鉴清单（按性价比；标注服务 ①寻址 / ②图像经济 / ③语义桥）

| # | 借鉴点 | 服务 | 来源（文档号 + file:line） |
|---|---|---|---|
| 1 | **稳定元素 id + 跨快照复用 + 失效可操作报错** | ①③ | `13` chrome `TextSnapshot.ts:78-87`、`McpPage.ts:694-717`；pw `backend/tab.ts:539-555` |
| 2 | **统一寻址语义：`display`（0 基活动显示器数组）/ `region`（虚拟桌面像素，region 优先）** | ① | `11` `tools/snapshot.py:44-45,90-91`、`desktop/service.py:1110-1212` |
| 3 | **双轴保比降采样 `k=min(1,maxW/W,maxH/H)`，W/H 为物理像素** | ② | `13` chrome `screenshot.ts:98-106`；`11` `desktop/service.py:241-259` |
| 4 | **降采样后回传 scale 换算元数据（`Coordinate Scale=1/scale`）** | ①② | `11` `tools/_snapshot_helpers.py:186-210` |
| 5 | **整页/视口/元素三态 + 不兼容组合显式报错** | ①③ | `13` chrome `screenshot.ts:165-170,183-185`；pw `backend/screenshot.ts:32,59-60` |
| 6 | **硬字节阈值落盘 + 只回路径（`>=2,000,000B`）** | ② | `13` chrome `screenshot.ts:276-287` |
| 7 | **文件 + 内联双通道（显式给 filename 时只回链接）** | ② | `13` pw `backend/screenshot.ts:75-85` |
| 8 | **`image-responses=allow\|omit\|only` 客户端能力开关（默认 allow）** | ② | `13` pw `mcp/config.d.ts:236-239`、`backend/response.ts:221-227` |
| 9 | **输出目录可配 + 自动命名 + 体积按 mtime 淘汰** | ② | `13` pw `backend/context.ts:477-492`、`backend/response.ts:236-265` |
| 10 | **「结构优先、截图仅作视觉校验」写进工具描述** | ③ | `13` chrome `src/tools/snapshot.ts:14-16`；pw `backend/snapshot.ts:40`、`backend/screenshot.ts:53` |
| 11 | **SoM 双重编号同源（DOM 文本 `[k]` 与截图编号框同一 `selector_map` 键）** | ①②③ | `12` browser-use `dom/serializer/serializer.py:1030-1033`、`browser/python_highlights.py:383-399` |
| 12 | **可交互控件类型白名单 + UIA 查询阶段过滤** | ③ | `14` UFO `inspector.py:334-370`、`system.yaml:28`；`11` `tree/config.py:1-27`、`tree/service.py:490-555` |
| 13 | **元素预算 + 截断显式标记（truncated）** | ③ | `11` `tree/budget.py:19,71-91`、`tree/views.py:89-95` |
| 14 | **SoM = 每控件类型一色 + 半透明色块 + 数字徽标** | ①②③ | `14` UFO `screenshot.py:693-750`、`config/ufo/system.yaml:34-47` |
| 15 | **SoM 重叠去重保留更小框 + OCR↔元素包含合并（0.80）** | ③ | `10` `util/utils.py:259-267,283-288,269-273,294-316` |
| 16 | **标签 4 锚点最小重叠摆放（上左→左外→右外→上右）** | ③ | `10` `util/box_annotator.py:189-262` |
| 17 | **编号徽标：圆形泡泡 + 对比色自动判定 + 字号自适应收缩** | ③ | `14` flameshot `src/tools/circlecount/circlecounttool.cpp:100-182` |
| 18 | **高亮笔 = Multiply + opacity 0.35（透出底图）** | ②③ | `14` flameshot `src/tools/marker/markertool.cpp:58-61` |
| 19 | **`Windows.Media.Ocr` 封装（非打包 .NET 可用）+ 1.5×/最小 80×80 padding** | ③ | `08` PowerToys `PowerOCR.csproj:20`、`BitmapPreprocessor.cs:13-14`、`TextExtractorService.cs:74-100` |
| 20 | **截图后端自动链 + 静默失败降级（含全黑检测）** | ②稳健性 | `11` `desktop/screenshot.py:280-285,343-375`；`14` UFO `screenshot.py:207-287` |
| 21 | **`memcmp` 无变化检测 + 质量回退到满足体积 + PNG 位深自适应** | ② | `07` ShareX `ImageHelpers.cs:1050-1069,2904-2942,3028-3059` |
| 22 | **视觉尺寸对齐官方 vision 预算（长边 ≤1568 / tile ≤1568）** | ② | `12` Anthropic best-practices `image.py:1-15`、`constants.py:124-130` |
| 23 | **绝对→窗口相对坐标换算 + 归一化（0..1）** | ①③ | `14` UFO `screenshot.py:361-377,379-398,798-824` |
| 24 | **窗口白名单过滤链 + `DWMWA_EXTENDED_FRAME_BOUNDS` 去阴影** | ① | `07` ShareX `CaptureHelpers.cs:326-346`、`WindowsList.cs:65-69`；`08` PowerToys `WindowHelper.cs:224-243` |

## ③ 建议参数面草案（本项目；每条标注依据来源）

**寻址**
- `mode=auto|screen|display|window|foreground|region|element`（默认 auto，按其它参数推断）——依据：`13`（chrome 整页/视口/元素三态，`screenshot.ts:165-170`）、`15`（Windows `-Region`/`-ActiveWindow`/`-WindowHandle` 三互斥）、`11`（`display`/`region` 双寻址）。
- `display=int|primary|left|right`——依据：`11`（0 基活动显示器数组，`tools/snapshot.py:44-45`）；语义命名参照 `14`（逐屏枚举 + 每屏 DPI，`Monitor.cs:71-88,95-106`）。
- `windowTitle`（含 `@active`）——依据：`15`（`-ActiveWindow`= `GetForegroundWindow` 脚本）；`07`（标题「精确→模糊」两级查找，`NativeMethods_Helpers.cs:609-625`）。
- `hwnd`（string，避开 64 位 JSON 精度）——依据：`15`（`-WindowHandle`）；`01`（B，`IsWindow`/`GetAncestor(GA_ROOT)` 校验）。
- `element`（UIA ref）——依据：`06`（FlaUI `BoundingRectangle`）；`13`（chrome `uid` / pw `aria-ref` 稳定 id 与 stale 报错）。
- `region` + `regionSpace`（默认 screen，兼容现有）——依据：`11`（`region`=虚拟桌面像素 xyxy，**优先于 display**，`desktop/service.py:1139-1166`）。
- `frameId`（旧帧护栏）——依据：`11` 反例（无代际号，`desktop/service.py:273-290`）＋ `13` stale-ref 报错范式（`McpPage.ts:694-717`、`backend/tab.ts:547-555`）。

**图像经济**
- `maxDimension`（默认 1568）——依据：`12`（官方 vision 长边 1568 / tile 1568，`image.py:1-15`）。
- `maxWidth`/`maxHeight`（双轴，取 min）——依据：`13`（chrome 双轴 `k=min(1,maxW/(w*DPR),maxH/(h*DPR))`，`screenshot.ts:98-106`）；`11`（双轴 min，`desktop/service.py:241-259`）。
- `format=png|jpeg|webp`（默认 png）+ `quality`（默认 80）——依据：`13`（chrome `format`/`quality`，`screenshot.ts:145-158`）；WebP 编码依据 `09`（ImageSharp 3.1.12 `WebpEncoder`）。
- `grayscale`（默认关）——依据：`09`⑥（ImageSharp `Grayscale`）；收益/取舍依据 `02`（工程判断）。
- `filePath`——依据：`13`②（chrome `filePath` 落盘只回路径，`screenshot.ts:269-275`）；保存位置优先级参照 `15`④。
- `imageResponses=allow|omit|only`（可选，默认 allow）——依据：`13`④.4（pw，`mcp/config.d.ts:236-239`）。
- `includeCursor`（默认关）——依据：`11`⑩#6（Windows-MCP 光标高亮环为响应内嵌、建议默认关）。

**语义桥**
- `detail=auto|text|image|som|ocr`——依据：`13`⑤（chrome/pw「结构优先」，`snapshot.ts:14-16`、`backend/snapshot.ts:40`）；`som` 为本项目增量（`13`⑥ 两家均无编号叠加，对齐 `14` UFO + `10` OmniParser）；`ocr` 依据 `08`（PowerToys OCR 全链路）。
- `annotate`（SoM 编号叠加）——依据：`14`（UFO 半透明色块 + 数字徽标，`screenshot.py:693-750`）、`14`⑦#15（flameshot 编号徽标）；FlaUI 侧扩展点 `06`（`ApplyOverlays`）。
- `ocr` + `ocrLanguage`——依据：`08`（`TryCreateFromLanguage`、语言回退链）；`03`。
- `ocrFallback=auto|always|never`——依据：`08`④#1（语言包缺失→中文提示、不抛不空返回）。
- `maxMarks`（默认 100~150）——依据：`11`⑨#7（元素预算 + `truncated` 标记，`tree/budget.py:71-91`）；上限与截断语义参照 `10`③R9。

**明确不做**
- mp4 录制 / play 输入 DSL——依据 `14`（ScreenToGif 录制/帧管线/编码不采纳）。
- NCC 模板匹配——无高星依据（原 `gui-mcp` 为低星，见附录）；且与 UIA 重叠（`06`）。
- 跨平台 provider（X11/Wayland/portal）——依据 `14`（flameshot Linux 优先，不采纳）。
- HTTP 多租户鉴权——依据 `11`⑧（Windows-MCP HTTP 传输的形态）；本项目 stdio 单机，不引入。
- 默认内联高分辨率像素——依据 `13`⑦（「Reference over Value」，`design-principles.md:12`；但注意 `12`② 判定内联为零 capability 前置，本项目默认内联、超限落盘）。
- 把屏幕内容当可信指令——通用安全原则；隔离场景依据 `12`⑤.1（官方参考为 Docker+X11 / 本地最小权限）。提示注入条目**【等级 C】，本轮未在 11–15 逐条核实**，不作定稿依据。

## ④ 坑与教训（仅保留可由官方/高星源码支撑的）

1. **坐标漂移/旧帧是头号失败**：Windows-MCP **无** capture/snapshot id、无 generation、无时间戳（`11`⑥.2；`desktop/service.py:273-290`），`label` 只是当前列表下标；一次 `Screenshot`（内部 `use_ui_tree=False`）会把树清成空占位，先前记下的 `label` 解析即 `IndexError`（`desktop/service.py:191-203,651-663`）。**这是本项目必须补 `frameId`/代际号的反例依据**；正面范式见 `13`（chrome/pw stale-ref 拒绝 + 教学式重新采集）。
2. **DPI / 多显示器负坐标**：浏览器侧 DPR 必须乘入物理上限，否则 HiDPI 上悄悄翻倍（`13` 修复 `ebf58f2`，`screenshot.ts:98-106`）；桌面侧虚拟屏原点可为负，裁剪须减虚拟屏 left/top（`11`⑤.3；`desktop/screenshot.py:35-48`；`07` `CaptureHelpers.cs:103-125`）。
3. **全黑/被遮挡抓取**：Windows-MCP 的结构校验**允许纯黑帧**（只查尺寸>0 且可 `load()`），靠进程级后端降级链兜底（`11`④.4；`desktop/screenshot.py:280-285,298-319`）；UFO 则做「全黑图 → RDP 断开检测」并逐级回退（`14`⑥.1；`screenshot.py:207-287`）。→ 本项目应**回传抓取来源/回退原因**，且不把黑图直接断言为「内容受保护」。
4. **客户端不读 `resource_link`**：规范里 `resource_link` 只是「可订阅/fetch 的 URI」，**未强制服务端实现 `resources/read`**，且不保证出现在 `resources/list`（`12`②.2；`tools.mdx:451-471`、`schema.ts:1710-1712`）；SDK 在无任何 resources 面时**不声明 capability**（`12`③.3；`McpServerImpl.cs:1208-1215`）。客户端是否 fetch/渲染**【等级 C】**（`12`§7.2，未核实客户端源码）。→ 默认内联，by-ref 只在补齐 `resources/list`+`resources/read` 后作为可选。
5. **截图 token 爆炸**：官方 computer-use 每图约 1,500 input tokens，30 轮后约 45k 且每次重发（`12`⑤.2；`README.md:350-422`）；browser-use **每步都截图且无变化检测**（`12`⑥.1；`screenshot_watchdog.py:27-88`）。手段=预缩放 + 图像历史 pruning（`12`⑤.2）；桌面侧再加 `maxDimension` + 落盘。
6. **过度降采样压糊小字**：官方给出 `zoom` 细读逃生门（`12`⑤.1；`computer.py:475-509`）；PowerToys OCR 强制 1.5× 放大 + 最小 80×80 画布（`08`③；`BitmapPreprocessor.cs:13-14`）；playwright 提供 `scale=device` 物理像素版本（`13`⑧#12；`backend/screenshot.ts:33`）。
7. **为读文本而截图**：两家高星浏览器 MCP 的工具描述原文都优先结构树（`13`⑤）。本项目「结构/UIA 优先、像素兜底」即源自此。
8. **UIA 对 Chromium/WinUI3 近乎无效**：文本树稀疏/只剩窗口壳（`03`；FlaUI/Chromium accessibility）；Windows-MCP 的浏览器 DOM/IA2 兜底为可选参考（`11`⑨#11；`tree/service.py:698-712,900-941`）。
9. **寻址歧义：同名/部分标题命中错窗口**：ShareX 用「精确标题→进程 `MainWindowTitle.Contains`」两级查找（`07`①B#6；`NativeMethods_Helpers.cs:609-625`）；本项目优先 hwnd + 操作前身份校验（`01` B）。
10. **安全面：截图泄露敏感信息**：Windows-MCP 对密码控件只打 `[password]` 标记，但**同一分支仍把 value 写进 `metadata['value']`、且截图不做任何遮挡**（`11`⑧⑩#4；`tree/service.py:592-609`）；playwright 明确自述路径护栏**不是安全边界**（`13`⑦；`mcp/config.d.ts:259-265`）。
11. **输入与截图耦合陷阱 / 不兼容组合**：浏览器两家对 `fullPage`+元素等不兼容组合**显式报错**而非静默丢参（`13`⑧#9；`screenshot.ts:183-185`、`backend/screenshot.ts:59-60`）。本项目应沿用「显式报错」。
12. **模型图片上限因 provider/模型而异**：官方 vision 编码器为 28×28 tile、长边 ≤1568、tile ≤1568（`12`⑤.2），规范则**未设协议级上限**（`12`②.1）。→ 上限属 provider 侧，需可参数覆盖。

### 【等级 C】（低星来源独有，仅旁证，不作定稿依据）

- `capture_id` 旧帧护栏、`dotByDot`、8×8 分块 diff/`dHash`/`<2%` 无变化、`webpQuality=60`——均出自低星且未源码核实（原 `desktop-touch-mcp`）。
  - 旧帧护栏 → 已改引 **`11` 缺失反例 + `13` stale-ref**（见坑 1）。
  - `dotByDot` → 已改引 **`11` `Coordinate Scale` + `13` 源头 `clip.scale`**（见坑 2、可借鉴 #3/#4）。
  - 字符串 diff → 已改引 **`14`（UFO 无图像 diff / ScreenToGif DXGI dirty rects）**，并以 **`07` `memcmp` 无变化检测** 为高星对照（见可借鉴 #21）。

## 附：已排除的低星来源（不作为依据）

> 以下 5 个项目均为 0–32 星、且**未做源码核实**；正文不再引用，仅存档说明其被哪个高星/官方来源取代。

| 低星来源 | 星数 | 核实状态 | 被何高星/官方来源取代 |
|---|---|---|---|
| `desktop-touch-mcp` | 22★ | **未核实** | `Coordinate Scale`/双轴降采样 → **Windows-MCP（`11`，7.3k★）**；源头 `clip.scale` → **chrome-devtools-mcp（`13`，52.6k★）**；`detail` 分档 → **chrome/pw「结构优先」（`13`⑤）**；`dotByDot`/`webpQuality=60`/8×8 diff → **无高星依据**，仅存为【等级 C】 |
| `windows-computer-use-mcp` | 0★ | **未核实** | `capture_id` 旧帧护栏 → **Windows-MCP 缺失反例（`11`⑥.2）+ chrome/pw stale-ref（`13`⑥）**；统一 `target` 寻址 → **`15`（region/activeWindow/hwnd）+ `13`（uid/aria-ref）** |
| `gui-mcp` | 0★ | **未核实** | NCC 模板匹配 → **无高星依据**，且与 UIA 重叠（`06`）；元素寻址 → **FlaUI（`06`）+ chrome/pw 稳定 id（`13`）** |
| `mcp-screenshot-server` | 32★ | **未核实** | 「四层安全」→ **chrome 路径护栏（`13`⑦；`McpContext.ts:243-324,678-703`）+ pw outputDir 限制（`13`⑦；`backend/context.ts:494-510`）** |
| `ubuntu-desktop-control-mcp` | 6★ | **未核实** | AT-SPI/百分比坐标（仅 Linux/X11）→ **UFO（`14`，9.8k★）UIA 条件过滤 + SoM 标注**；Windows 场景无关 |

## 附：来源

**本目录文档（A · 源码核实；`15` 为 B｜官方仓库网页读取）**
- `11-source-windows-mcp.md`（Windows-MCP 7,332★，MIT，commit `59e77f6`）
- `12-source-mcp-resources.md`（MCP 官方规范 + csharp-sdk `v2.2.0` + Anthropic computer-use 官方参考 + browser-use 116k★）
- `13-source-browser-mcp.md`（chrome-devtools-mcp 52.6k★ `ae0aaef` + playwright MCP 37.6k★ `b9a34ac77`）
- `14-source-capture-tools.md`（ScreenToGif 27.7k★ + flameshot 30.9k★ + UFO 9.8k★）
- `15-eval-openai-screenshot-skill.md`（openai/skills 27.7k★，官方 skill `49f948f`，网页读取）
- `06-source-flaui.md`（FlaUI 3.1k★ `fd7cc64`）
- `07-source-sharex.md`（ShareX 39.8k★ `cf5a6fe`）
- `08-source-powertoys.md`（PowerToys 139k★，微软官方 `62e60ca858`）
- `09-source-imagesharp.md`（ImageSharp 8k★ `v3.1.12`/`v4.1.2`）
- `10-source-omniparser.md`（OmniParser 25.5k★ `3540212`，微软）

**官方规范/文档（B）**
- MCP 官方规范：`modelcontextprotocol` 仓库 `docs/specification/2026-07-28/server/{tools,resources}.mdx`、`schema/2026-07-28/schema.ts`；官网锚点 `https://modelcontextprotocol.io/specification/2026-07-28/server/tools`（`#image-content`/`#resource-links`/`#embedded-resources`）。
- Anthropic computer-use 官方参考：`claude-quickstarts` `computer-use-demo/`、`computer-use-best-practices/`、`browser-use-demo/`。
- Microsoft Learn：Win32（`EnumDisplayMonitors`/`GetMonitorInfo`/`MonitorFromWindow`/`GetForegroundWindow`/`GetClientRect`/`DwmGetWindowAttribute`/`GetDpiForMonitor`）、WinRT（`OcrEngine`/`IGraphicsCaptureItemInterop::CreateForMonitor`）。

**已排除的低星来源**：见 `## 附：已排除的低星来源（不作为依据）`（`desktop-touch-mcp`、`windows-computer-use-mcp`、`gui-mcp`、`mcp-screenshot-server`、`ubuntu-desktop-control-mcp`），**不再列为主来源**。
