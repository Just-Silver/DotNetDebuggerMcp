# 02 · 图像经济 / 返回（WebP · 灰度 · 双轴降采样 · diff · by-ref · 默认值）

> **来源等级**：主体 A（源码核实：ImageSharp/Windows-MCP/chrome-devtools-mcp/本项目代码）+ B（官方文档：MCP 规范/Anthropic Vision/dotnet-runtime issue）；个别条目标注 C（低星来源/客户端行为待核）。生成日期 2026-09-27。

## ① 结论与推荐（先给答案）

| 议题 | 推荐 | 理由 |
|---|---|---|
| WebP 编码 | **`SixLabors.ImageSharp 3.1.12`**（首选）；SkiaSharp 备选 | GDI+ 不能编 WebP；ImageSharp 纯托管零原生资产、WebP 参数最全、OSS 免密钥；**不要 v4.x**（Release 强制 `sixlabors.lic`） |
| 灰度 | **可选参数，默认关** | 文本界面省 20–40%（WebP 有损已丢色度，非 50%）；红/绿状态色会丢失 |
| 双轴上限 | `maxDimension` → `(maxWidth,maxHeight)`，`k=min(1, maxW/W, maxH/H)` | 改动极小、兼容现有 kBase 语义 |
| 默认尺寸 | 2000 → **1568** | 对齐 Claude 标准档视觉上限（超限被服务端缩） |
| dotByDot+scale | **不加独立 dotByDot**；仅在头部补显 `scale` | 现有 region 已是「返回图像素空间 + Engine 反算」，比 dotByDot 更不易错 |
| diffMode | **一期只做「无变化检测」**（哈希/memcmp），变化图二期 | 高价值低风险；真 P 帧需新增状态机；业界无高星参考实现可抄（见 `14`） |
| resource_link | **不做默认，默认继续内联** | 规范内联零 capability 前置；by-ref 需补 `resources/read`；客户端是否 fetch 待核实（等级 C） |
| 格式/质量默认 | format 默认 **png**，webp 为显式选项，quality 默认 **80** | png 客户端通用、文字无损 |

**最省事**：本次只做两件低风险高收益——(1) ImageSharp 加 `webp`；(2) 默认 2000→1568 + 双轴上限。其余（灰度/diff/by-ref）做可选或二期。

## ② 事实与 API

### WebP 编码
- **GDI+ 不支持**：`ImageCodecInfo.GetImageEncoders()` 只回 BMP/JPEG/GIF/TIFF/PNG；`ImageFormat.Webp` 常量存在但 `Save` 找不到编码器**静默回退 PNG**（dotnet/runtime#70418、#76237、SO 75988248）。现有 `ImagePipeline.Encode` 的 `default: throw` 是对的。
- **ImageSharp 3.1.12**（选定）：
  - `WebpEncoder`：`FileFormat`（**只有 `Lossy`/`Lossless`**，默认 null→Lossy）、`Quality`（0–100，默认 75）、`Method`（`WebpEncodingMethod` 0=最快/`Default=Level4=4`/6=最好）、`UseAlphaCompression`（默认 true）、`EntropyPasses`(1–10,默认1)、`SpatialNoiseShaping`(0–100,默认50)、`FilterStrength`(0–100,默认60)、`NearLossless`(bool)+`NearLosslessQuality`(默认100)。
  - 源码 `src/SixLabors.ImageSharp/Formats/Webp/WebpEncoder.cs:23-79`；`FileFormat` 枚举**没有** NearLossless（`WebpFileFormatType.cs:9-20`）。
  - 夹取：quality 只夹上界（`Vp8Encoder.cs:130`）→ 调用方自行 `Clamp(0,100)`。
  - 尺寸上限 `MaxDimension=16383`（`WebpEncoderCore.cs:135`）。
  - 默认注册，开箱即用；`SaveAsWebp`（`Formats/_Generated/ImageExtensions.Save.cs:1154`）。
  - 从 `Bitmap.LockBits` 喂：**`Bgra32` 与 `Format32bppArgb` 二进制兼容**（`Bgra32.cs:14,25-40`）；`Image.LoadPixelData<Bgra32>(ReadOnlySpan<byte>, w, h, rowStride)`（会拷贝，`Image.LoadPixelData.cs:79`）；零拷贝用 `Image.WrapMemory`（需保持内存有效）。`rowStride` 须能被 `sizeof(TPixel)` 整除（4 的倍数恒满足）。
- **SkiaSharp**（落选）：`SKWebpEncoderOptions` 只有 `fCompression(Lossy/Lossless)+fQuality(float)`（`Generated/SKWebpEncoderOptions.generated.cs:15-20`），无 method/entropy/sns/filter；win-x64 `libSkiaSharp.dll` **13.4MB**（`SkiaSharp.NativeAssets.Win32` nupkg 89.9MB 含三 RID），与 PackAsTool/NETSDK1146 hack 冲突。
- **Magick.NET**：Apache-2.0 但原生库数十 MB，过度设计。

### ImageSharp 许可（关键）
- **Six Labors Split License**（`LICENSE`）：Apache-2.0 **或** 商业双许可。
- 命中 Apache-2.0 的情形（`LICENSE:33-40`）：消费方本身 OSS / 作为传递依赖 / 直接依赖且年营收<100 万美元 / 非营利。**本项目 MIT OSS → 命中第①条，免费**。
- 版本分界：`v2.1.13` 纯 Apache；**`v3.0.0` 起** Split License；**v3.1.12 与 v4.1.2 LICENSE 文本逐字相同**。
- **v4 的真正差异 = 构建期强制密钥**：v4 包带 `build/SixLabors.ImageSharp.targets` + `SixLabors.Licensing.dll`，`SixLabors_ValidateLicense` target 在 `CoreCompile` 前跑；**Debug 只报错不中断，Release 直接失败**。v3.1.12 包只有空 `props`，**无 targets、无密钥**。
- 官方：密钥从 v4.0 起强制；OSS 可免费申请社区密钥但**不得入库**、按年轮换。
- **结论**：用 **3.1.12**，零构建摩擦；**不要引 `SixLabors.ImageSharp.Drawing`**（会拖回 v4 密钥链 + 多余包）。

### ImageSharp 体积/依赖
| | v3.1.12 | v4.1.2 |
|---|---|---|
| TFM（发布包） | `lib/net6.0` | `lib/net8.0` |
| 依赖 | **无** | `System.IO.Hashing 8.0.0` |
| nupkg | 1.01 MB | 1.30 MB |
| 主 dll | 2.10 MB | 2.60 MB |
| 原生 | **0** | 0 |

本项目 Engine TFM `net10.0-windows10.0.22621.0` 消费 `lib/net6.0` 兼容。

### 灰度 / 降色
- System.Drawing：`ImageAttributes.SetColorMatrix` 灰度矩阵（0.299/0.587/0.114）。
- ImageSharp：`Mutate(x=>x.Grayscale())` 或 `CloneAs<L8>()`（核心包内）。
- 收益务实估计 **20–40%**；WebP 有损 YUV420 已丢色度。可读性：文字/边缘基本无损，彩色状态指示丢失 → 只做 opt-in。

### 双轴上限
- chrome-devtools-mcp（**A 源码核实**，见 `13-source-browser-mcp.md`）：服务端 flag `--screenshot-max-width/height` → `computeDownscaleClip`：`widthScale=min(1,maxW/(w*DPR))`、`heightScale=min(1,maxH/(h*DPR))`、`scale=min(widthScale,heightScale)`，`scale>=1` 不缩放；在**源头**用 CDP `clip.scale` 降采样（`screenshot.ts:93-124,223-233`）。DPR 必须乘入物理像素（修复 commit `ebf58f2`，`screenshot.ts:98-106`）。
- 本项目：`k=min(1, maxDim/max(kBase.W,kBase.H))` → `k=min(1, maxW/kBase.W, maxH/kBase.H)`，kBase 语义不变。已 DPI Aware 物理像素，无 chrome-devtools 的 HiDPI 坑。

### dotByDot + scale
- Windows-MCP（**A 源码核实**，见 `11-source-windows-mcp.md`）：截图被降采样时以文本元数据回传 `Screenshot Coordinate Scale = round(1/scale,6)`，要求 agent 把图像像素乘回得到虚拟桌面坐标（`_snapshot_helpers.py:186-210`）。
- chrome-devtools-mcp（**A 源码核实**，见 `13-source-browser-mcp.md`）：降采样统一走 CDP `clip.scale`，不引入第二套坐标话术（`screenshot.ts:93-124`）。
- **本项目不建议加独立 dotByDot**（两套坐标系会混乱）；建议头部显式打印 `scale`，沿用现有「返回图像素空间 + Engine 反算」。真正该学 dotByDot 的前提是将来加「按屏幕坐标点击」。

### diffMode
- 【等级 C｜低星来源，未核实，需实测】desktop-touch-mcp（0–32 星、未源码核实）据称用 LockBits 两帧 → **8×8 分块 SAD**、块均值 > 噪声阈值(16) 计变化块、64 位 dHash 滚动校验、`<2%` 无变化。**该来源（含其性能数字、I/P 帧与 TTL 90s 缓冲）不作为定稿依据**；若确有需要，进 spec 前须先源码核实或实测。
- 业界现状（**A 源码核实**，见 `14-source-capture-tools.md`）：
  - UFO：**无图像 diff**——`change_detector` 是 TaskConstellation 结构 diff（非像素），全仓 `ImageChops/ImageStat/ssim/hash` 未找到。
  - ScreenToGif：用 **DXGI dirty/move rects** 丢无变化帧，**不是逐像素 diff**（`DirectChangedImageCapture.cs:40-62,69-94`）。
- 工程判断（依据本项目现状 + `14`）：diff 复杂度与收益不成正比、无高星参考可抄，**一期只做「无变化检测」**——编码前原图或编码后字节哈希（现有 `IsAllBlack` 已 LockBits 扫描，可顺带算 hash），相同返回文本「画面无变化」。**不要**一上来传差分像素图（真 P 帧需新增状态机）。

### by-ref resource_link
- **MCP 官方规范（B，见 `12-source-mcp-resources.md`）**：`CallToolResult.content` 是 `ContentBlock[]`，`ImageContent{type,data,mimeType}` 是**一等公民**、**零 capability 前置**（`schema.ts:1809-1838,2305-2306,2340-2361`；`tools.mdx:404-439`）。`resource_link` 是可选内容块（`ResourceLinkBlock` 必填 `uri`+`name`），**规范未强制服务端实现 `resources/read`**（`tools.mdx:451-471`）；但语义上它只是「可 fetch 的 URI」，不实现 `resources/read` 即死链，且不保证出现在 `resources/list`。
- **SDK 行为（A 源码核实，见 `12`）**：`McpServerImpl.ConfigureResources` 在 resources 相关 handler / `ResourceCollection` / 显式 capability **全为 null 时直接不声明 `resources` capability**（`McpServerImpl.cs:1208-1215`）。**本仓库当前无任何 resources 面**（仅 `.WithToolsFromAssembly()`），故 `resource_link` 落地 = 一次「新增 resources 能力族」的完整改动。
- **客户端支持度（风险）**：【等级 C｜本轮未核实客户端源码】有资料称 Claude Desktop **拒绝** `TextContent+ResourceLink` 混合（modelcontextprotocol#1638）、Claude Code **不 fetch 不渲染** ref 与 embedded blob（claude-code#53453）、Claude Desktop 不把 `EmbeddedResource` 呈现为附件（claude-ai-mcp#287）；支持方（Copilot Studio、ChatGPT 等）亦同属未核实。**以上均来自低星网页/issue，不作为定稿依据；要定 by-ref 须先做客户端源码核实。**
- **评估**：协议上默认内联零风险（一等公民），官方 computer-use 参考全内联；by-ref 默认**不可行**（需先补 resources 面 + 客户端行为核实）；仅当显式 `byRef=true`/`filePath` 时返回并同时实现 `resources/read`；文案提示「看不到图改用内联」。现有「>2MB 落盘 + 返回路径」对自带 fs 的 agent 已可用。

### 默认值（业界经验）
- chrome-devtools-mcp（**A**，见 `13`）：工具默认 `png`；`quality` 仅 jpeg/webp；`--screenshot-max-width/height` 默认不设；编码后 `>= 2_000_000` 字节落盘只回路径（`screenshot.ts:276`）。
- playwright（MCP，**A**，见 `13`）：默认按 `filename` 扩展推断否则 `png`；`--output-max-size` 默认不设、按 mtime 淘汰旧文件（`response.ts:236-265`）。
- Windows-MCP（**A**，见 `11`）：**仅 PNG**；双轴 min 降采样，**硬上限 1920×1080** + 环境变量 `WINDOWS_MCP_SCREENSHOT_SCALE∈[0.1,1.0]`（`_snapshot_helpers.py:21,24-34,91`）。
- 【等级 C｜低星来源，未核实，需实测】desktop-touch-mcp 的 `maxDimension=768` / `webpQuality=60`（0–32 星、未源码核实）：**不作默认值依据**。
- **视觉 token 真相（B｜Anthropic 官方文档，见 `12` §5.2）**：Claude 标准档长边上限 **1568px/~1.15MP**（28×28 tile、tile 数 ≤1568），高分辨率档 2576px；图像 token 由**尺寸**决定而非字节数 → 格式/质量主要影响传输负载与是否触发 2MB 落盘；超限会被服务端二次 resize 导致坐标漂移。
- **建议**：`maxDimension` 2000→**1568**（双轴）；format 默认 **png**，webp 显式、quality 默认 80（文本可读）。

## ③ 落地建议与代价

**阶段一（低风险）**
1. 默认尺寸 2000→1568（改 `AppConfig.ScreenshotMaxDimension`）。
2. 双轴上限：`ImagePipeline.Process` 改 `(maxWidth,maxHeight)`。
3. 头部补 `scale`。
4. **WebP**：Engine 加 `SixLabors.ImageSharp 3.1.12`；`Encode` 加 `case "webp"`（`LockBits`→`LoadPixelData<Bgra32>`→`SaveAsWebp`）；工具 format 白名单 + ext + `image/webp`。**必须实测发布产物**（确认纯托管、无原生资产进 tool 包）。
5. 文档/握手/回归同步（铁律）。

**阶段二（可选）**：灰度参数；无变化检测（TTL 哈希缓存）；by-ref（需 `resources/read`，收益受客户端限制）。

**不建议**：GDI+ WebP、独立 dotByDot、默认 resource_link / 默认灰度 / 默认 webp、Magick.NET、ImageSharp v4 / ImageSharp.Drawing。

## ④ 开放问题
1. ImageSharp 3.1.12 是否需为 `net6.0` → `net10` 加 shim（预期不需要）。
2. webp 在目标 agent 客户端（opencode 等）是否渲染？未确认前默认 png 保守。
3. diff 缓存归属（截图当前不入缓存、不写 Actions，D9）——独立 TTL 缓存是否违和。
4. 默认 1568 是否过小（Claude 4.7+ 2576）；是否提供参数覆盖。
5. 尺寸参数三个数字 vs 一个，agent 友好度权衡。

## 附：来源

- **本项目代码（A）**：`src/DotNetDebugger.Engine/.../ImagePipeline.cs`、`src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs`、`AppConfig.cs`。
- **ImageSharp（A）**：本地 clone `Externals/ImageSharp`（tag `v3.1.12`/`v4.1.2`）+ NuGet 实拉包；详见 `09-source-imagesharp.md`（`WebpEncoder.cs:23-79`、`LICENSE:33-40`、`Bgra32.cs:14,25-40`、`Image.LoadPixelData.cs:79`）。
- **SkiaSharp（A，落选对照）**：`Externals/SkiaSharp`；`SKWebpEncoderOptions.generated.cs:15-20`、`SkiaSharp.NativeAssets.Win32` 包体。
- **GDI+ 不能编 WebP（B）**：dotnet/runtime#70418、#76237。
- **Windows-MCP（A）**：本地 clone `Externals/Windows-MCP` @ `59e77f6`，详见 `11-source-windows-mcp.md`（`_snapshot_helpers.py:21,24-34,91,186-210`）。
- **chrome-devtools-mcp / playwright（A）**：本地 clone `E:\Code\Projects\Externals\chrome-devtools-mcp` @ `ae0aaef8`（`screenshot.ts:93-124,276`、修复 `ebf58f2`）；playwright 经远端 `b9a34ac`（`backend/screenshot.ts`、`response.ts`）；详见 `13-source-browser-mcp.md`。
- **MCP 规范与 csharp-sdk（B + A）**：spec 仓库 `Externals/modelcontextprotocol`（`schema/2026-07-28/schema.ts`、`server/tools.mdx`）+ csharp-sdk tag `v2.2.0`（`McpServerImpl.cs:1208-1215`）；详见 `12-source-mcp-resources.md`。
- **Anthropic computer-use（B）**：`Externals/claude-quickstarts`（`computer_use/image.py:1-15`、`constants.py:124-130`）；详见 `12` §5.2。
- **截图工具与 UFO（A）**：`Externals/ScreenToGif`/`flameshot`/`UFO`，详见 `14-source-capture-tools.md`（UFO 无图像 diff、ScreenToGif DXGI dirty rects）。
- **【等级 C｜低星来源，未核实，不作为依据】**：`desktop-touch-mcp`（8×8 SAD/dHash/`maxDimension=768`/`webpQuality=60`）、`playwright-mcp` 旧 README、`windows-computer-use-mcp`；MCP 客户端是否 fetch/渲染 `resource_link` 的行为描述。

## 来源等级

> 判定口径：**A** = 本地 clone / 本项目源码逐行核实；**B** = 官方规范 / 官方文档；**C** = 低星来源或本轮未核实的客户端行为（需实测/待核）。

| 结论区 | 等级 | 代表证据 |
|---|---|---|
| WebP 编码 API / 许可 / 体积 / 像素导入 | **A** | ImageSharp `v3.1.12`/`v4.1.2` 源码（`WebpEncoder.cs:23-79`、`LICENSE:33-40`、`Bgra32.cs:14,25-40`），见 `09-source-imagesharp.md` |
| GDI+ 不能编 WebP | **B** | dotnet/runtime#70418、#76237（官方 issue） |
| 双轴降采样 / DPR / 落盘阈值 | **A** | chrome-devtools-mcp 源码 `screenshot.ts:93-124,276`、修复 `ebf58f2`，见 `13` |
| dotByDot+scale 坐标回传 | **A** | Windows-MCP `_snapshot_helpers.py:186-210`（见 `11`）；chrome-devtools `clip.scale`（见 `13`） |
| 视觉 token / 1568 上限 | **B** | Anthropic computer-use best-practices `image.py:1-15`、`constants.py:124-130`（官方文档/代码，见 `12` §5.2） |
| 默认值（png / 1920×1080 / 2MB / `--output-max-size`） | **A** | `13`（chrome/pw）、`11`（Windows-MCP） |
| by-ref `resource_link` / `ImageContent` 一等公民 / SDK capability 门槛 | **B + A** | MCP spec `schema.ts`/`tools.mdx`（B）+ csharp-sdk `McpServerImpl.cs:1208-1215`（A），见 `12` |
| diff 业界现状（UFO 无图像 diff / ScreenToGif DXGI dirty rects） | **A** | `14-source-capture-tools.md` |
| desktop-touch-mcp（8×8 SAD / dHash / 768 / 60） | **C** | 0–32 星、未源码核实；仅标注，不作为依据 |
| MCP 客户端是否 fetch/渲染 `resource_link` | **C** | 本轮未核实客户端源码（issue 引用见 `12` §7.2） |
| 本项目加不加 dotByDot / 一期只做无变化检测 | **A（本项目代码）+ 工程判断** | 依据本项目现状（`ImagePipeline.cs` 等）与 `11`/`13`，无外部定稿依据 |
