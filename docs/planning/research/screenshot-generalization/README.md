# screenshot 通用化调研汇总（2026-09-27）

> 目标：把现有 `screenshot` MCP 工具（Windows-only，mode=window/screen/region）通用化为「agent 视觉基座」。
> 范围（用户拍板「三件都做」）：**① 寻址通用 · ② 图像经济 · ③ 语义桥**。
> 本目录是该轮调研的**权威存档**（逐份报告 + 交叉结论 + 决策点 + 仓库取舍）。本轮只做「来源可信度重建」——把原基于 0–32 星项目的结论重钉到高星/官方来源、新增 `11`–`15` 源码/规范深挖，并给每份文档加「来源等级」标注。**未改任何产品代码，未 git commit。**

## 来源等级图例

- **A｜源码核实**：本地 clone + 逐条 `文件:行号`（可复现、可回溯）。
- **B｜官方文档/官方仓库**：Microsoft Learn、MCP 官方规范、官方样例/官方参考实现。
- **C｜网页/README 待核（或低星来源）**：未在本地源码核实；仅旁证，**不作定稿依据**。

## 报告清单

| 文件 | 内容 | 来源等级 |
|---|---|---|
| `01-addressing.md` | 寻址通用（多显示器/前台/客户区/hwnd/元素级/坐标空间/旧帧护栏） | 主体 **A**（FlaUI/Windows-MCP/ShareX/PowerToys/本项目源码）+ **B**（Microsoft Learn/WinRT）；个别条目 **C** |
| `02-image-economy.md` | 图像经济（WebP 选型/灰度/双轴降采样/diff/by-ref/默认值） | 主体 **A**（ImageSharp/Windows-MCP/chrome-devtools-mcp/本项目源码）+ **B**（MCP 规范/Anthropic Vision/dotnet-runtime issue）；个别条目 **C** |
| `03-semantic-bridge.md` | 语义桥（`Windows.Media.Ocr`/UIA 文本树/Set-of-Marks/旧帧护栏/与 `ui_*` 共用） | 主体 **A**（PowerToys/FlaUI/OmniParser/Windows-MCP/本项目源码）+ **B**（Microsoft Learn/arXiv）；个别条目 **C** |
| `04-prior-art.md` | 业界范式对比（高星/官方来源 → 可借鉴 + 参数面草案） | **A**（`11`/`12`/`13`/`14`/`15`+`06`–`10` 源码核实）+ **B**（MCP spec/Anthropic computer-use/Microsoft Learn）；低星入文末附录、不作依据 |
| `05-repo-integration.md` | 仓库集成点与约束（精确路径/铁律/测试/打包） | **只读梳理**（本项目代码，未标 A/B） |
| `06-source-flaui.md` | FlaUI 源码深挖（元素截图/几何/控件树/文本/DPI/Patterns），3.1k★ | **A** 源码核实 |
| `07-source-sharex.md` | ShareX 源码深挖（多显示器/窗口捕获/编码/标注/历史/健壮性），39.8k★ | **A** 源码核实 |
| `08-source-powertoys.md` | PowerToys 源码深挖（PowerOCR OCR 全链路 + 窗口枚举 + 多显示器），139k★ | **A** 源码核实 |
| `09-source-imagesharp.md` | ImageSharp 源码深挖（WebP API/许可/体积/像素导入），8k★ | **A** 源码核实 |
| `10-source-omniparser.md` | OmniParser 源码深挖（SoM 去重/合并/编号/标签摆放纯算法），25.5k★ | **A** 源码核实 |
| `11-source-windows-mcp.md` | Windows-MCP 源码深挖（工具形态/寻址/返回/双轴降采样/语义树/无代际反例），7.3k★ | **A** 源码核实 |
| `12-source-mcp-resources.md` | MCP 官方规范（image content/`resource_link`/resources capability）+ `csharp-sdk v2.2.0` + Anthropic computer-use 官方参考 + browser-use（116k★） | **A** 源码核实 + **B** 官方规范/官网锚点 |
| `13-source-browser-mcp.md` | 浏览器 MCP 截图参数设计（chrome-devtools-mcp 52.6k★ / playwright MCP 37.6k★） | **A** 源码核实 |
| `14-source-capture-tools.md` | 截图工具与 UFO 源码深挖（ScreenToGif 27.7k★ / flameshot 30.9k★ / UFO 9.8k★） | **A** 源码核实 |
| `15-eval-openai-screenshot-skill.md` | OpenAI Codex `screenshot` skill 评估（openai/skills 27.7k★） | **B**｜官方仓库网页读取（未本地 clone） |

## 一、三件事的核心结论（速查）

### ① 寻址

- **不需要 WinAppSDK，不需要重写抓取链**：显示器/hwnd/前台/客户区都是 Win32 枚举 + 矩形运算（**B**｜Microsoft Learn；`01`）。
- 显示器首选 **GDI BitBlt 该显示器矩形**（复用现有 `CaptureScreen` clip）；WGC `CreateForMonitor` 仅 best-effort（部分 Win11/RDP 稳定 `E_INVALIDARG`）。高星桌面同类同样走「枚举 + 矩形」：ScreenToGif 用 `EnumDisplayMonitors`/`GetMonitorInfo`/`MonitorFromWindow`，ShareX 用虚拟屏矩形（**A**；`14`②.1、`07`①B）。
- 前台窗口 = `GetForegroundWindow` + `GetWindowThreadProcessId`（**B**）；OpenAI 官方 skill 的 `-ActiveWindow` 同源（**B**；`15`②）。
- 客户区 = `GetClientRect` + `ClientToScreen`；**整窗去阴影默认改用 `DWMWA_EXTENDED_FRAME_BOUNDS`**（`GetWindowRect` 含 7px 隐形 resize 边+阴影，且被 DPI 虚拟化，而扩展边框是物理像素）。高星同类：ShareX「优先 `EXTENDED_FRAME_BOUNDS`、失败回退 `GetWindowRect`」、PowerToys 窗口枚举同款（**A**；`07`①B#3、`08`①#3）；OpenAI skill 直用 `GetWindowRect`（含阴影、未处理 DPR）为反证（**B**；`15`④#4）。
- hwnd 用 **string** 传（避开 64 位 JSON 精度）；OpenAI skill 的 `-WindowHandle`（int）为直寻址先例（**B**；`15`②）。
- 元素级 = 宿主 FlaUI 取 `BoundingRectangle` + 所属顶层 hwnd → **Engine 从窗口帧裁剪**（不能对屏幕直接 BitBlt 裁元素，会被遮挡）。反证：FlaUI `Capture.Element` 就是屏上 BitBlt（会截到遮挡物）（**A**；`06`①）；正例：UFO `PrintWindow(PW_RENDERFULLCONTENT)` 可抓被遮挡/离屏窗口（**A**；`14`⑥.1）。浏览器 MCP 的稳定元素引用 `uid`/`aria-ref` 为身份语义参照（**A**；`13`⑥）。
- 坐标模型统一 **`origin + scale`**（`screen = origin + image/scale`）——Windows-MCP 降采样后回传 `Coordinate Scale = round(1/scale,6)`（**A**；`11`④.3/⑥.1），chrome-devtools-mcp 同款思路但降采样做在**源头**（CDP `clip.scale`，**A**；`13`③）；region 用轻量 `frameId` 护栏。
- 旧帧护栏必须补：Windows-MCP **无** capture/snapshot id、无代际号、无时间戳，`label` 只是当前列表下标（**A** 反例；`11`⑥.2）；chrome/pw 两家高星则「旧 ref 直接拒绝 + 教学式重新采集」（**A**；`13`⑥）。
- **必须先 spike**：WGC 窗口帧几何 = `GetWindowRect` 还是 `EXTENDED_FRAME_BOUNDS`（决定裁剪偏移）。

### ② 图像经济

- **WebP 不能用 GDI+**（`Save(ImageFormat.Webp)` 静默退化为 PNG）。
- **选型结论：`SixLabors.ImageSharp 3.1.12`**——纯托管、零原生资产、WebP 参数最全、OSS 免密钥；**不要 v4.x**（Release 构建强制 `sixlabors.lic`）、**不要 SkiaSharp**（win-x64 原生 dll 13.4MB，与 PackAsTool/NETSDK1146 hack 冲突）（**A**；`09`①）。
- 双轴上限 `(maxWidth,maxHeight)`，`k=min(1, maxW/W, maxH/H)`，且 **W/H 为物理像素**——chrome `computeDownscaleClip` 把 DPR 乘入（修复史 `ebf58f2`）、Windows-MCP 双轴独立比例取 min、且两者都主张**在源头降采样**（**A**；`13`③、`11`④.3）。
- **默认尺寸 2000 → 建议 1568**（对齐官方 vision 上限：28×28 tile、长边 ≤1568、tile ≤1568；超限被服务端二次 resize 会导致坐标漂移）（**B**｜Anthropic best-practices；`12`⑤.2）。
- **不加独立 dotByDot**（与现有 region 坐标空间冲突）；仅在头部补显 `scale`（`11` 的 `Coordinate Scale` 与 `13` 的 `clip.scale` 都刻意不引入第二套坐标话术）。
- 灰度默认关（WebP 有损已丢色度，收益 20–40% 而非 50%，且状态色丢失）。
- diff **一期只做「无变化检测」**：高星对照 ShareX 用 `memcmp` 逐字节比图（**A**；`07`①A#1）；ScreenToGif 用 DXGI dirty/move rects 丢无变化帧、**非**逐像素 diff（**A**；`14`②.5）；UFO 全仓**无图像 diff**（其 `change_detector` 是任务星座结构 diff）（**A**；`14`④）——真 P 帧二期。
- **by-ref `resource_link` 不做默认**：内联 image content 是 MCP 规范一等公民、**零 capability 前置**；`resource_link` **未强制**服务端实现 `resources/read`，且 SDK 在无 resources 面时**不声明** capability → by-ref 落地 = 一次「新增 resources 能力族」的完整改动（**B+A**；`12`②③）。官方 computer-use 与 browser-use **全部内联**（**A/B**；`12`⑤⑥）。客户端是否 fetch/渲染属【等级 **C**】；默认继续内联、超限落盘。
- 业界默认值对照：Windows-MCP **仅 PNG** + 硬上限 1920×1080（`11`④.2/④.3）、chrome `format` png/jpeg/webp（默认 png）+ 编码后 `>=2,000,000B` 落盘只回路径（`13`②）、playwright **总是写盘 + 未显式给 filename 时再内联**、`--output-max-size` 按 mtime 淘汰（`13`④）、OpenAI skill 固定 png、**落盘回路径**（`15`②③）（均 **A/B**）。

### ③ 语义桥

- **UIA 是主承重，OCR 是补充**。`Windows.Media.Ocr` 在非打包 .NET 10 桌面**实践可用**（PowerToys 非打包铁证 `WindowsPackageType=None`；ShareX 亦用系统内置 `OcrEngine` 零第三方依赖），TFM 自带投影、**零新增 NuGet**（**A**；`08`①、`07`①C#1）。
- 语言包**不随包分发**且 `TryCreateFrom*` 未装返回 **null 不抛**——必须 null 检查 + 优雅降级（缺失是常态）（**A**；`08`②、`03`②）。
- 两个必踩坑：**alpha=0**（PrintWindow 产物必须 `BitmapAlphaMode.Ignore`）；**OCR 要跑在原生/放大图上再按 k 映射**（PowerToys：1.5× 放大、最小 80×80 画布；`MaxImageDimension` 运行时读、不硬编码）（**A**；`08`②、`03`②）。
- **SoM 元素来源用 UIA（不用 ML 检测器），编号 = `ui_find` index** → 图上的 `[7]` 与 `ui_action index=7` 物理同源。高星/官方对照：browser-use **双重编号同源**（DOM 文本 `*[k]` 与截图编号框同一 `selector_map` 键，**A**；`12`⑥.3）；UFO SoM = 每控件类型一色 + 半透明色块 + 数字徽标、查询阶段 UIA 条件过滤（**A**；`14`⑤⑥）；OmniParser 纯算法（去重保留更小框阈值 0.7、OCR 并入 0.80、标签 4 锚点最小重叠，**A**；`10`①）；Windows-MCP 编号 = 元素列表 0 基下标（**A**；`11`⑤.5）；flameshot 编号徽标「圆形泡泡 + 对比色自动判定 + 字号自适应」（**A**；`14`③.4）。
- **编号 SoM 是本项目增量**：chrome-devtools-mcp 与 playwright MCP 两家高星**均不往图上画编号**（本地全仓检索 0 命中）（**A**；`13`⑥）。
- 旧帧护栏语义 = **元素缓存的代际一致性**（本项目动作为 UIA 语义，非按坐标点击）：反例 `11`⑥.2（无代际号 → 旧 `label` `IndexError`）、正面范式 `13`⑥（stale-ref 教学式拒绝）。
- **核心架构建议：不要两套身份，只留一套**——screenshot 元素采集走 `UiElementLocator.Find`、写同一个 `_lastFind`、返回同一 index + 代际号（`01`③、`03`①#6）。

## 二、决策点（待用户拍板）

> ⚠️ **状态：待用户复核 / 调整，未定稿。** 用户此前表示「要调整其中若干条」，但**尚未给出具体条目**。下表保留原 11 条决策与「调研倾向」，并把「依据」从旧的低星来源改挂到本轮高星/官方/源码证据上；**「调研倾向」列仅供参考，未获用户确认前不得当作已定**。

| # | 分歧 | 选项 | 调研倾向 | 依据（本轮来源） |
|---|---|---|---|---|
| 1 | 工具形状 | 单工具加 `detail` 分档 ｜ 拆专用工具 | 单工具多档（业界共识） | `13`（chrome 单工具仅 5 参数 `screenshot.ts:144-177`；pw 单工具 `backend/screenshot.ts:29-34`）；`15`（参数互斥显式报错） |
| 2 | 默认返回 | 内联图（稳） ｜ by-ref（省） | **内联**（by-ref 客户端支持碎片化） | `12`②（图像为规范一等公民、零 capability 前置）；`12`⑤（Anthropic 官方全内联）；`12`③.3（by-ref 必新增 resources 面） |
| 3 | 缩放默认 | 保持 2000 ｜ 改 1568 | **1568**（可参数覆盖） | `12`⑤.2（官方 vision 长边 1568 / tile 1568）；`13`③（双轴降采样须按物理像素含 DPR） |
| 4 | region 坐标系 | 保持 screen 图像素（不破坏） ｜ 切 `origin+scale` | 兼容：无 origin 走旧口径，有则新公式 | `11`⑥.1（`Coordinate Scale=1/scale`）；`13`③（源头 `clip.scale`） |
| 5 | 元素级截图 | 做（需解冻 spec §8） ｜ 只做高亮 | 做（跨层，中等代价） | `06`①（FlaUI `Capture.Element` 屏 BitBlt 的遮挡坑是必须绕过的原因）；`14`⑥.1（UFO `PrintWindow` 正例）；`13`⑥（稳定 id 语义） |
| 6 | OCR | 做（依赖语言包，需降级） ｜ 只做 UIA 树 | 做，但默认关、显式 `ocr=true` | `08`①②（非打包可用 + PowerOCR 黄金配方）；`07`①C#1（ShareX `OcrEngine` 先例） |
| 7 | 上限野心 | UIA/OCR/SoM（工程可控） ｜ OmniParser 式像素解析（要 ML） | **前者**（ML 排除） | `10`④（ML 检测器/字幕模型不采纳，仅借纯算法）；`14`⑧（UFO ML 过滤不采纳） |
| 8 | `screenshot` 要不要 `detail=text` | 加（与 `ui_find` 重叠） ｜ 不加，`annotate` 复用 `ui_find` | 避免功能重叠：**screenshot 保持视觉+标注，文本走 `ui_find`** | `13`⑤（chrome/pw 工具描述原文「结构优先」）；`03`①#3（与 `ui_find` 重叠） |
| 9 | `_lastFind` 共享 ｜ 独立缓存 | 共享（编号天然一致，需代际号） ｜ 独立（解耦但漂移） | 共享 + 代际号 | `13`⑥（chrome `uid` 跨快照复用 + stale 报错）；`11`⑥.2（无代际号反例）；`03`①#6 |
| 10 | OCR 默认开关 | 默认开 ｜ 默认关 | **默认关**（语言包缺失是常态） | `03`②（未装 `TryCreateFrom*` 返回 null）；`08`②（PowerOCR 只用 `TryCreateFromLanguage`、未用 profile 语言） |
| 11 | `maxMarks` 上限 | 默认值 | 100~150（防编号爆炸） | `11`⑨#7（元素预算默认 500 可配 + `truncated` 显式标记，`tree/budget.py:19,71-91`）；`14`⑤.2（UFO 上限 500 + `name!=""` 后置过滤） |

## 三、待实测验证项（建议进 spec 的验证节）

1. 本机非打包 exe 下 `OcrEngine.TryCreateFromUserProfileLanguages()` 是否恒可用（对照 `08`②：PowerOCR 未用它、只用 `TryCreateFromLanguage`）。
2. `zh-Hans-CN` 语言包在本机/CI 是否安装；降级提示是否够诊断（`08`②、`03`②）。
3. WGC 窗口帧与 `GetWindowRect`/`EXTENDED_FRAME_BOUNDS` 的实际像素偏差（正例 `07`①B#3/`08`①#3 用扩展边框；反证 `15`④#4 直用 `GetWindowRect`）。
4. `FindAllDescendants()` 偶发不完整（FlaUI #614）在本项目目标上是否复现（`06`①#4）。
5. `detail=text` 相对 `image` 的实测 token 增益（若做）（参照 `12`⑤.2：官方每图约 1,500 input tokens）。
6. ImageSharp 3.1.12 webp 分支产物合法（`RIFF....WEBP`）+ 体积对比（`09`①②）。

## 四、调研仓库取舍（本轮克隆）

> 说明：调研与源码结论已全部落盘本目录，**源码仅作核对**；可在**对应实现完成前**按需保留。下表按当前磁盘实况（`E:\Code\Projects\Externals`）核对。

| 仓库 | 大小 | 处置 | 理由 |
|---|---|---|---|
| **FlaUI** | 1.5 MB | **保留** | 是当前 NuGet 依赖；元素寻址/SoM 落地时可能要再核对 |
| **PowerToys** | 197 MB | **保留到 OCR 实现完成** | OCR「黄金配方」（PowerOCR）+ 窗口枚举过滤器清单，实现时可能要抄代码（MIT，注意署名） |
| **ShareX** | 27 MB | 已抽干 | 结论已抽干；本项目**不复用其代码**（架构不同：它无 UIA、无 WGC） |
| **ImageSharp** | 539 MB | **实现后删** | 选型已定（3.1.12）；只需 NuGet 包，不需源码 |
| **OmniParser** | 21 MB | 已抽干 | 纯算法已完整抽到 `10-source-omniparser.md` |
| **ScreenToGif** | 12 MB | **保留** | 多显示器枚举/DPI/窗口捕获结论已入 `14`，实现前可再核对 |
| **UFO** | 69 MB | **保留** | UIA 条件过滤 + SoM 标注高星同类，实现前可再核对 |
| **csharp-sdk** | 6 MB | **保留** | MCP `resource_link`/resources capability 结论的源码出处（`12`） |
| **playwright** | 65.7 MB | **保留**（monorepo） | MCP 截图实现在 `packages/playwright-core/src/tools/backend/`（`13`） |
| **chrome-devtools-mcp** | ≈908 MB | **保留** | 参数面/双轴降采样/落盘阈值的一手出处（`13`）；体积最大 |
| **Windows-MCP** | 2.9 MB | **保留** | 唯一高星桌面同类 MCP（`11`），反例与可借鉴清单的一手出处 |
| **modelcontextprotocol** | 54 MB | **保留** | MCP 官方规范源码（`12`②③） |
| **claude-quickstarts** | 8.2 MB | **保留** | Anthropic computer-use 官方参考（`12`⑤） |
| **browser-use** | 8.5 MB | **保留** | SoM 双重编号同源最强范式（`12`⑥） |
| **flameshot** | 23 MB | **保留** | 编号徽标/多屏拼接手法（`14`③） |
| **servers** | 1.5 MB | **保留** | MCP 官方 servers 样例备查 |

**已移除**（本轮清理，源码结论均已落盘）：

- `SkiaSharp`——WebP 选型落选（`02`/`09`）。
- `SoM`——已被 OmniParser 覆盖（`03` 改引论文）。
- `Windows-universal-samples`——OCR 样例被 PowerToys 覆盖（`08`）。
- `Windows.UI.Composition-Win32-Samples`——现有 WGC interop 已工作且「勿动」，无需求（`01`）。
- `playwright-mcp`——本地源码已迁至 `microsoft/playwright` monorepo（`13`①前置事实）。

> 顺带提示（非本轮）：`helix-toolkit` 单仓 **6.9 GB**，与本任务无关。

## 参考来源（外部）

> 以高星/官方来源为主；低星来源见文末「已排除」段，不作依据。

- **Windows-MCP**（`CursorTouch/Windows-MCP`，7.3k★，MIT）——唯一高星桌面同类 MCP（`11`）。
- **chrome-devtools-mcp**（`ChromeDevTools/chrome-devtools-mcp`，52.6k★，`ae0aaef`）（`13`）。
- **playwright MCP**（`microsoft/playwright` monorepo，37.6k★，MCP 实现在 `packages/playwright-core/src/tools/backend/`）（`13`）。
- **openai/skills**（Codex `screenshot` skill，27.7k★，`49f948f`，官方）（`15`）。
- **ShareX**（39.8k★，`cf5a6fe`）（`07`）。
- **ScreenToGif**（27.7k★，`a4d0a67`）（`14`）。
- **flameshot**（30.9k★，`2d47806`）（`14`）。
- **UFO**（微软，`microsoft/UFO`，9.8k★，`e2a0312`）（`14`）。
- **OmniParser**（微软，25.5k★，`3540212`）（`10`）。
- **PowerToys**（微软官方，139k★，`62e60ca858`）（`08`）。
- **FlaUI**（3.1k★，`fd7cc64`）（`06`）。
- **ImageSharp**（Six Labors，8k★，`v3.1.12`/`v4.1.2`）（`09`）。
- **browser-use**（116k★，`4cbe921`）（`12`⑥）。
- **MCP 官方规范**：`modelcontextprotocol` 仓库 `docs/specification/2026-07-28/server/{tools,resources}.mdx`、`schema/2026-07-28/schema.ts`；官网 `https://modelcontextprotocol.io/specification/2026-07-28/server/tools`（`#image-content`/`#resource-links`/`#embedded-resources`）；`csharp-sdk v2.2.0`（`12`）。
- **Anthropic computer-use 官方参考**：`claude-quickstarts` 的 `computer-use-demo/`、`computer-use-best-practices/`、`browser-use-demo/`（`12`⑤）。
- **Microsoft Learn**：Win32（`EnumDisplayMonitors`/`GetMonitorInfo`/`MonitorFromWindow`/`GetForegroundWindow`/`GetClientRect`/`ClientToScreen`/`DwmGetWindowAttribute`/`GetDpiForMonitor`）、WinRT（`OcrEngine`/`IGraphicsCaptureItemInterop::CreateForMonitor`）。

### 已排除的低星来源（不作为依据）

以下 5 个项目均为 0–32 星、**未做源码核实**，已在 `04-prior-art.md` 文末附录存档（说明被哪个高星/官方来源取代），**不再列为主来源、正文不再引用**：

| 低星来源 | 星数 | 状态 |
|---|---|---|
| `desktop-touch-mcp` | 22★ | 已存档、降级为【等级 C】，结论改引 `11`（Coordinate Scale/双轴降采样）与 `13`（`clip.scale`） |
| `windows-computer-use-mcp` | 0★ | 已存档，`capture_id` 旧帧护栏改引 `11`⑥.2 + `13`⑥ |
| `gui-mcp` | 0★ | 已存档，NCC 模板匹配无高星依据且与 UIA 重叠（`06`） |
| `mcp-screenshot-server` | 32★ | 已存档，「四层安全」改引 `13`⑦（chrome 路径护栏 + pw outputDir 限制） |
| `ubuntu-desktop-control-mcp` | 6★ | 已存档，AT-SPI/百分比坐标仅 Linux，Windows 场景无关（`14`） |
