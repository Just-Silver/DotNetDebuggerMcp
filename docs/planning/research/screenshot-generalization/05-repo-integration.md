# 05 · 仓库集成点与约束（精确路径 + 铁律 + 测试 + 打包）

> 只读梳理（未改动任何文件）。用于评估「通用化」改动会卷入哪些文件/约束/测试。

## ① 关键文件清单（路径:行 — 作用）

### A. 现有 screenshot 工具（宿主）
| 位置 | 作用 |
|---|---|
| `src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs:22-23` | `[McpServerToolType] class DebugScreenshotTool`（独立工具，不入缓存/不过 ToolPipeline/不写 AgentView） |
| 同上 `:26-30` | `[Description]`（三模式语义与边界；通用化必改） |
| 同上 `:31-40` | `Screenshot(...)`：`mode/processId/windowTitle/region/format/quality/timeoutSeconds/filePath` + 末位 `CancellationToken` |
| 同上 `:45-66` | 参数校验（mode/format 白名单、quality/timeout clamp、region 解析） |
| 同上 `:72-89` | 抓取分派：window→`LocateWindowAsync`+`CaptureWindow`；screen/region→`CaptureScreen` |
| 同上 `:92-110` | 头部（目标/尺寸/来源/备注） |
| 同上 `:113-136` | 双轨：`b64Len=((len+2)/3*4)`；`wantFile=filePath非空||b64Len>=InlineImageBase64Bytes`；落盘失败仍附块；`ImageContentBlock.FromBytes` |
| 同上 `:138-149` | catch：取消/CaptureException 透传/兜底不抛 |
| 同上 `:156-184` | `LocateWindowAsync`：pid→标题→活动会话 pid 兜底；50ms 轮询 |
| 同上 `:186-198` | `ResolveScreenshotPath`（测试用）；默认 `screenshot-{ts}-{mode}[-{pid}].{ext}` |
| 同上 `:200-201` | `TextOnly`（错误纯文本、不设 IsError） |

### B. Engine 截图栈
| 位置 | 作用 |
|---|---|
| `src/DotNetDebugger.Engine/Capture/ScreenCapture.cs:14` | 门面（工具线程同步调用，与 ICorDebug/泵零交互） |
| `:35-40` | `EnsureDpi()`：首次设 PMv2（幂等，容忍 ACCESS_DENIED） |
| `:49-86` | `FindMainWindow(pid, titleSubstring)`：EnumWindows 可见顶层主窗，pid 优先/标题兜底择一 |
| `:89-98` | `GetWindowInfo(hwnd)` |
| `:107-129` | `CaptureScreen(Rectangle? clipInImageSpace, maxDimension, format, quality)`：GDI BitBlt 虚拟屏 |
| `:137-153` | `ResolveRegionClip(...)`：图像空间求交→round→夹紧；空交集抛 `CaptureException`（纯函数可单测） |
| `:161-190` | `CaptureWindow(hwnd,...)`：WGC→PrintWindow（纯黑继续回退）→BitBlt（IsIconic 跳过） |
| `CaptureModels.cs:16-17` | `WindowHandleInfo(Hwnd,Title,Rect,IsIconic,Pid,MatchCount)` |
| `CaptureModels.cs:29-31` | `CaptureResult(Image,Width,Height,NativeWidth,NativeHeight,WindowTitle,Source,WasAllBlack,ClippedToScreen=false)` |
| `CaptureModels.cs:35-38` | `CaptureException` |
| `ImagePipeline.cs:15-27` | `Process(source,kBase,maxDimension,format,quality,windowTitle,sourceName,clippedToScreen)` |
| `:51-72` | `IsAllBlack`（LockBits 指针逐像素，A 通道不看） |
| `:74-94` | `Encode`：png / jpeg+quality；其它抛 `ArgumentException` |
| `WgcCapture.cs:18-27` | `TryCaptureWindow`：`IsSupported()` 探测，失败/异常返回 null |
| `:20` | `FirstFrameTimeout=500ms` |
| `:118-235` | `WgcInterop`（手写 COM/WinRT，5 处坑固化）——**不改** |
| `GdiCapture.cs:28-48/:51-74/:77-95/:98-105` | `TryPrintWindow`/`TryBitBltWindow`/`CaptureScreenBits`/`VirtualScreenRect`(SM 76-79) |

### C. AppConfig 截图常量
| 位置 | 常量 | 值 |
|---|---|---|
| `Configuration/AppConfig.cs:36` | `ScreenshotMaxDimension` | 2000 |
| `:41` | `InlineImageBase64Bytes` | 2*1024*1024 |
| `:43-49` | `ScreenshotsDir` | `%LOCALAPPDATA%\DotNetDebuggerMcp\screenshots` |

> ⚠️ **实现与计划不一致**：计划曾规划 `InlineImageLimitBytes` 测试 seam，但实现**直接引用 `AppConfig.InlineImageBase64Bytes`**（`:114`），**无 seam**；`ScreenshotToolTests.cs:14` 注释已过期。要单测 2MB/默认目录分支须重新引入 seam（常量仍留 AppConfig）。

### D. UI 自动化可复用面
| 位置 | 作用 |
|---|---|
| `Services/Ui/UiModels.cs:10-17` | `UiElementInfo(Index,Name,Type,AutoId,Rect,Patterns,Semantic)`；`Rect` 是字符串 `"X,Y WxH"`（由 `el.BoundingRectangle` 物理像素格式化，空→"不可见/无几何"） |
| `:32-35` | `UiException` |
| `:39-86` | `UiPatternCapabilities`（10 项 + `Describe()` + `Probe(element)`） |
| `UiElementLocator.cs:17` | `TargetDescriptor(AutoId,Name,ControlType,Ordinal)` |
| `:48-55` | `SameIdentity`/`OrdinalKey`（`\u001F` 分隔） |
| `:58-129` | `Find(...)`：进程/窗口解析+全后代遍历+过滤+能力探测+语义标注+ordinal；写 `_lastFind` |
| `:132-142` | `ResolveForAction(automation,pid,index,name,type)` |
| `:145-150` | `ResolveTopWindow` |
| `:298-310` | `GetCachedEntry`（index 缓存 + pid 校验） |
| `:329-360` | `ResolveProcess`（pid / 进程名子串） |
| `:370-385` | `FindWindow`：`FindFirstChild(ControlType.Window+pid[+Name])`（不用 FindAllChildren，避免 7.5s） |
| `UiAutomationService.cs:21-23` | `DefaultTimeoutSeconds=5`/Min1/Max300 |
| `:320-337` | `RunGateAsync`+`ResolveTimeout` |
| `:339-354` | `UiaBoundAsync` 超时护栏 |
| `UiEventWaiter.cs:21-52` | Subscribe StructureChanged+PropertyChanged |
| `Tools/Debugger/UiTools.cs:18` | `[SupportedOSPlatform] [McpServerToolType] UiTools`（工具面范式） |
| `Services/UiSemanticResolver.cs` | 语义候选反查 |

## ② 会卷入的约束 / 文档 / 测试

### 铁律（定义位置）
- 参数带默认值、`[Description]` 中文注明默认值、必填标「（必填）」→ 根 `AGENTS.md:32`、宿主 `AGENTS.md:24`。
- 每个工具带 `CancellationToken cancellationToken = default` → 根 `:33`、宿主 `:25`。
- 工具返回 `Task<string>`、错误中文不抛；**图片类例外 screenshot 返回 `CallToolResult`**（错误仍纯文本、不设 IsError）→ 根 `:34`、宿主 `:26`、spec §3.3、`DebugScreenshotTool.cs:18-19,136,200-201`。
- stdout 只走 MCP、日志走 stderr（严禁改）→ 根 `:35`；护栏 `McpSessionConcurrencyTests`。
- 跨层字面量必须常量化（AppText/CacheSignatures/…）→ 根 `:38`。
- **改 MCP 工具须同步根 `README.md` + 握手 `HandshakeFeatureIntro` + 扩回归断言** → 根 `:37`、宿主 `:38`、`decisions.md:8`。
- 版本号三处同步（csproj/.mcp/server.json×2/CHANGELOG）→ 根 `:36`。

### 握手与文档同步链
| 位置 | 现状 |
|---|---|
| `Configuration/AppText.cs:40-53` | `HandshakeFeatureIntro`；**screenshot bullet 在 `:50`**；`:51` 兜底前缀句含 screenshot |
| `README.md:147-151` | screenshot 章节 + 工具行 |
| `CHANGELOG.md:31` | 2.0.0 段含 screenshot；**当前无 `[Unreleased]` 段**（最新 `## [2.1.0] - 2026-09-27` 已发布）→ 本次需新建 |
| `docs/planning/specs/README.md:23` / `docs/planning/README.md:41` | screenshot spec 状态「已实现」/ 地图登记 |
| `docs/planning/specs/2026-09-22-screenshot-tool-design.md` | 全文（§3 契约/§4 Engine/§5 输出/§8 不做/§9 风险）——通用化应新立或修订 spec |

### 握手覆盖度回归测试
- `tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcpCmdTests.cs:116-133`：`HandshakeFeatureIntro_覆盖全部能力族触发条件`，逐族 `Assert.Contains`——反编译(:121)/动态调试(:122)/debug_run_to(:123)/debug_set(:124)/debug_evaluate(:125)/debug_verify(:126)/ui_find(:127)/ui_action(:128)/**screenshot(:129)**/web_open(:130)/`## 何时使用`(:131)/`DoesNotContain("## 工具一览")`(:132)。新增能力族须逐族补断言。

### 测试与端到端
| 位置 | 作用 |
|---|---|
| `tests/DotNetDebuggerMcp.Tests/ScreenshotToolTests.cs:17` | `[Collection("AppServices")]` |
| `:30-52` | Launch/Kill UiSampleApp（`UseShellExecute=true` 排空纪律） |
| `:58-75` | `ScreenAvailableAsync`/`SkipIfScreenUnavailableAsync`（锁屏/无头 Skip） |
| `:79-127` | 参数校验逐条文案 |
| `:132-275` | window pid→PNG 块、jpeg、screen 块、强制落盘无块、`ResolveScreenshotPath` |
| `Engine.Tests/CaptureTestHelpers.cs:9-34` | `WaitFound` 50ms 轮询 |
| `CaptureWindowFindTests.cs:10-39` | FindMainWindow 三态 |
| `CaptureScreenTests.cs:14-117` | screen/region（Skip）+ `ResolveRegionClip` 纯函数（恒跑） |
| `CaptureWindowGdiTests.cs:10-27` | PrintWindow 非纯黑 + 尺寸 ±2 |
| `CaptureWindowWgcTests.cs:10-33` | `IsSupported()` 不支即 Skip；支持则 `Source=="WGC"` 且非纯黑 |
| `ImagePipelineTests.cs:12-85` | 缩放守恒/不缩放/JPEG/PngMagic/纯黑/未知格式抛 |
| `tests/TestData/UiSampleApp/Program.cs:51-52` | 标题 `"UiSample"`、ClientSize 760x520（测试契约，勿改） |
| `tests/TestData/generate-testdata.ps1:594-606` | 构建 UiSampleApp；`:401-592` DebugTarget；`:340-396` TestSamples |
| `src/DotNetDebuggerMcp.Client/AGENTS.md:3` | e2e **不含 debug_*/cache_stats**；screenshot 豁免 Client e2e（spec §3.3:76） |

### 打包与 TFM
| 位置 | 现状 |
|---|---|
| `DotNetDebuggerMcp.csproj:4` | `net10.0-windows10.0.22621.0` |
| `:11-13` | `IsPackable` + `PackAsTool` + `PackageType=McpServer` |
| `:21` | `<Version>2.1.0</Version>` |
| `:19`/`:39` | `PackageReadmeFile=README.md`，打包根 README |
| `:48-55` | FlaUI 直引段（5.0.0） |
| `:69-78` | **PackAsTool NETSDK1146 两段 MSBuild hack**（`HackBeforePackToolValidation` 清 TargetPlatformIdentifier → `HackAfterPackToolValidation` 恢复） |
| `DotNetDebugger.Engine.csproj:4` | `net10.0-windows10.0.22621.0`；`:8` `AllowUnsafeBlocks` |
| `:17-24` | ClrDebug 0.4.2 + DbgShim.win-x64 + System.Drawing.Common 10.0.12 |
| `.mcp/server.json:5,:10` | version 两处 2.1.0 |

## ③ 可复用接口点
- **寻址**：`UiElementLocator.ResolveProcess`（pid/进程名）vs screenshot 的 pid+title+会话兜底；两套顶层窗口定位（`ScreenCapture.FindMainWindow` Win32 vs `UiElementLocator.FindWindow` UIA）结果可映射；`element Rect`（UIA 物理像素）与 `WindowHandleInfo.Rect`（GetWindowRect）同坐标系 → 元素级/语义桥天然连接点；`TargetDescriptor/SameIdentity/OrdinalKey` 是现成身份模型。
- **图像经济**：三常量 + `ResolveScreenshotPath`；编码/缩放/纯黑唯一在 `ImagePipeline`；双轨判据 `b64Len`；头部约定。
- **语义桥**：`UiElementInfo`+`UiSemanticResolver`+`UiPatternCapabilities.Probe`；`UiTools` 工具面范式（Fail/Done、三段 catch）；`UiAutomationService` gate+超时。
- **输出约定**：`AppText`/`CacheSignatures`/`MetadataNaming.FormatToken`/`OutputFormatter.MemberLine`/`SectionBuilder.EmptyPlaceholder`；screenshot 错误文案目前**内联**未集中，通用化建议收拢。

## ④ 风险与注意
1. **PackAsTool hack 脆弱**：`_PackToolValidation` 是内部 target；升 SDK 后必须重验 pack+install+run。
2. **TFM 全链 windows-only**；改 TFM 连带根 `opencode.json`、各 AGENTS.md 的 `bin/Debug/net10.0-windows10.0.22621.0` 路径。
3. **Web 整体冻结**（`Web/TODO.md:7`，spec §1）：不得为 Web 加展示/回放；screenshot 不写 AgentView/Actions。
4. **测试串行纪律**：Engine `Parallelization(None)` 勿删；宿主 `[Collection("AppServices")]`；新增测试默认落 CI `core` 分片。
5. **锁屏/无头**：真实 BitBlt 会失败，测试 `Assert.Skip`，不要断言确定像素。
6. **CHANGELOG 无 `[Unreleased]`**：需新建段；发布时 CI 从 CHANGELOG 提正文。
7. **图像经济无测试 seam**：见 §①C。
8. **四套坐标口径**：窗口 rect（含边框阴影）/客户区/元素 rect/screen 图像素空间——须钉死；建议复用 `ResolveRegionClip` 的 k 换算与 `ClippedToScreen`。
9. **spec §8 冻结项**：多显示器、`includeCursor`、`delay`、**元素级截图**、`region` 的 `origin` 坐标系——本次可能触碰，须先修订/新立 spec 解冻。
10. **WGC interop 勿轻改**（`WgcCapture.cs:118-235`）；扩展只在 `ScreenCapture`/`ImagePipeline` 层。
11. **`_screenAvailable` 静态缓存跨用例**：新增截图测试勿污染。
