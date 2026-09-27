# 03 · 语义桥（Windows.Media.Ocr · UIA 文本树 · Set-of-Marks · 旧帧护栏 · 与 ui_* 共用）

> 一句话：**OCR 不是主承重梁，UIA 才是；OCR 只补「UIA 覆盖不到的像素文字」。**

> **来源等级**：主体 A（源码核实：PowerToys/FlaUI/OmniParser/Windows-MCP/本项目代码）+ B（官方文档：Microsoft Learn/arXiv）；个别条目标注 C（低星来源/未 clone 项目待核）。生成日期 2026-09-27。

## ① 结论与推荐（先给答案）

1. **`Windows.Media.Ocr` 可用**（非打包 .NET 桌面，TFM 自带投影、**零新增 NuGet**），但当成「能力探测 + 优雅降级」的可选增强。语言包不随包分发，未装时 `TryCreateFrom*` **返回 null 不抛**。
2. **OCR 要跑在原生/放大图上再按 k 映射**（不能跑在已压到 ≤2000px 的输出图上，否则小字丢失）。
3. **`detail=text`（UIA 文本树）应当是第一等公民**，但**与现有 `ui_find` 重叠**——建议 screenshot 保持「视觉 + 标注」，文本走 `ui_find`，或 `annotate` 直接复用 `ui_find` 数据（见决策点 8）。
4. **SoM 元素来源用 UIA（可交互控件），不用 ML 检测器；编号 = `ui_find` 的 index**，天然闭环回 `ui_action index=N`。OCR 词只作「可见文字」补充（**不进可交互编号**——本项目已移除物理输入，OCR 词无可执行入口）。
5. **旧帧护栏落在「元素缓存的代际一致性」**，不是坐标（本项目动作为 UIA 语义）。
6. **共用身份最优解 = 根本只有一套**：让 screenshot 元素采集走 `UiElementLocator.Find`、写同一 `_lastFind`、返回同一 index + 代际号。

## ② 事实与 API

### WinRT 投影前置
- `net*-windows10.0.x` TFM 自动引 `Microsoft.Windows.SDK.NET.Ref` → `Windows.Media.Ocr` 直接 `using`。
- **PowerToys 铁证**：`PowerOCR.csproj:20` `WindowsPackageType=None`（非打包）却用完整 OCR；`PowerOCR.Core.csproj:19-21` 只引 `System.Drawing.Common`，TFM `net10.0-windows10.0.26100.0`；`PowerOCR.Core.UnitTests` 是纯控制台 exe 直接用 `OcrEngine.MaxImageDimension`。**本项目 TFM `net10.0-windows10.0.22621.0` 满足，无需新增包。**
- `Microsoft.Windows.CsWinRT` 仅在投影自定义 C++/WinRT 组件时需要；OS 自带类型走 SDK 投影。PowerOCR 引它是为 WinUI3/AOT。

### OcrEngine 静态面
| 成员 | 用法 | 坑 |
|---|---|---|
| `AvailableRecognizerLanguages` | 列出已装 OCR 语言 | 依赖 `C:\Windows\OCR`；系统盘非 C: 时查不到（PowerToys #20388 需拷到 `C:\Windows\OCR`） |
| `IsLanguageSupported(Language)` | language matching（zh-CN→zh-Hans-CN） | 精确标签用 `AvailableRecognizerLanguages` 的 `LanguageTag` |
| `TryCreateFromLanguage(Language)` | 创建引擎 | **不支持返回 null**（PowerOCR 在此抛 `InvalidOperationException`） |
| `TryCreateFromUserProfileLanguages()` | 按用户语言取首个 | **无语言包返回 null**；PowerOCR 现状**未用**它（`WindowsOcrRecognizer.cs:25` 只用 `TryCreateFromLanguage`） |
| `MaxImageDimension` | uint，W/H 超限需缩放 | 官方不给数值；**运行时读，不要硬编码** |
| `engine.RecognizerLanguage` | 实际生效语言（回显诊断） | — |

### Bitmap/byte[] → SoftwareBitmap（最易踩坑）
- 路径 A（本项目最顺，已有编码后 `byte[]`）：
  `byte[] → InMemoryRandomAccessStream → DataWriter.WriteBytes/StoreAsync → Seek(0) → BitmapDecoder.CreateAsync → GetSoftwareBitmapAsync(BitmapPixelFormat.Bgra8, BitmapAlphaMode.Ignore) → RecognizeAsync`。
- 路径 B（PowerToys，从 `Bitmap`）：`bitmap.Save(stream, ImageFormat.Bmp)` → `stream.AsRandomAccessStream()` → `BitmapDecoder` → `GetSoftwareBitmapAsync()`。
- **坑**：
  - **alpha=0 踩雷**：`PrintWindow` 产物常整幅 alpha=0；默认 `Premultiplied` 解码会乘成透明/全黑 → **必须 `BitmapAlphaMode.Ignore`**（或 Straight），且最好先铺白/底色。
  - 透明底 OCR 极不友好（返回空）。
  - `SoftwareBitmap` 要 Dispose。
  - `RecognizeAsync` 参数是未压缩 `SoftwareBitmap`，不是文件字节。

### OcrResult 结构（坐标来源）
- `Text`（整幅）、`Lines`、`TextAngle`（**非 null 时 BoundingRect 按旋转图算**）、`OcrLine.Text/Words`、`OcrWord.Text/BoundingRect`（**相对输入图左上、像素**）。
- **坑**：`OcrLine` **没有 BoundingRect**（行框要 Union 词框，PowerToys `MapLine` 用 `Aggregate(Union)`，见 `08-source-powertoys.md`）；**无 word-level confidence 字段**——这是 `Windows.Media.Ocr` 的 API 事实：官方文档 `OcrWord`/`OcrLine` 成员表只有 `Text`/`BoundingRect`/`Words`，**不提供任何置信度**（等级 B，微软官方 API 文档）。本项目据此**不做置信度阈值**。
- 【等级 C｜低星来源，未核实】曾有低星项目（`desktop-touch-mcp`，<32★、未源码核实）提出用 `lineWordCount/lineCharCount` 密度启发式、并排除 CJK 行来「猜置信度」。本项目**不采纳**该启发式（无源码核实、无官方依据）；如确需区分可信词，改用「词框面积/字符数」等本项目可自证规则再议。

### 语言包
| 问题 | 事实 |
|---|---|
| 默认装了什么 | 与系统/键盘/显示语言绑定；**通常 en-US 有，其它语言多数机器没有** |
| 未安装行为 | `TryCreateFrom*` 返回 null（无异常、无回退模型、不可由代码安装） |
| 查询 | `Get-WindowsCapability -Online \| ? Name -Like 'Language.OCR*'` |
| 安装（管理员） | `Add-WindowsCapability -Online -Name "Language.OCR~~~zh-CN~0.0.1.0"` |
| 系统盘非 C: | 拷 `Windows\OCR` 到 `C:\Windows\OCR` |
| 精简镜像 | servercore/nanoserver 下 `OcrEngine` **TypeLoadException**（WinRT 类型未注册） |

### 线程/异步/取消
- `OcrEngine` 标注 `MarshalingBehavior(Agile)` + `ThreadingModel(Both)` → **MTA 线程池直接用**（与 `WgcCapture` 模型一致）。
- `RecognizeAsync` 可 `await`/`.AsTask(ct)`；**取消只停等待、不中断底层计算**（符合「超时=放弃等待」）。
- CPU 密集数百 ms~秒级 → **务必串行**。

### 尺寸/预处理（抄 PowerToys 经验值）
- **放大 1.5×**；1.5× 超 `MaxImageDimension` 退回 1.0×，再按 `min(1,Max/W,Max/H)` 缩。
- **最小画布 80×80**（内容 64 + 两侧各 8 padding；短边 <64 触发）；高清用 `HighQualityBicubic`+`PixelOffsetMode.HighQuality`；先 `Clear(GetPixel(0,0))` 铺底。
- 坐标映射三件套 `ScaleX/ScaleY + OffsetX/OffsetY`（`PreparedBitmap`）。
- **本项目坐标链多一段**：`原生物理像素 →(k)→ 输出图 →(OCR scale+pad)→ OCR 图`。**强烈建议对「未缩放的 native 图」跑 OCR**，再统一乘 k 画 marks，少一层误差。

### UIA 文本树（`detail=text` / annotate 原料）
- 现成：`UiElementLocator.Find`（`FindAllDescendants` → Name/ControlType/AutomationId/BoundingRectangle/patterns → `UiElementInfo`）；`UiPatternCapabilities.Probe` 是现成「可交互」判据。
- 文本取值链：多数控件在 `Name`；Edit/Document → `ValuePattern.Value`（优先）→ `TextPattern.DocumentRange.GetText(-1)`（**取大块，一次跨进程**）→ 兜底；过 `SensitiveValueRedactor`。
- **性能坑**：`FindAllDescendants()` 在桌面根极慢（FlaUI #501；项目 `UiElementLocator.FindWindow` 已记录桌面全量 ~7.5s → 改 `FindFirstChild`）。**文本树枚举只能限定在已定位窗口内**。FlaUI #614：`FindAllDescendants()` 偶发不完整。
- **稀疏/退化**：Chromium/Electron（内容树在渲染进程，需 `--force-renderer-accessibility`）、WinUI3 自绘 → 只剩窗口壳；虚拟化列表复用 `TryRealizeVirtualized`。
- **可交互/可滚动判定的高星同类基线（`11-source-windows-mcp.md`，等级 A）**：Windows-MCP（7.3k★）用「控件类型白名单（`tree/config.py:1-27`）+ Legacy `AccessibleRole` 白名单（`:29-63`）+ 可聚焦启发 + 可见（`area>0` 且非 `IsOffscreen`）+ `IsEnabled` + 浏览器/Image/Group 等特殊分支」判 interactive（`tree/service.py:490-555`），并单列可滚动元素（非交互/信息类型 + `ScrollPattern.VerticallyScrollable`，`:429-480`）。本项目 `UiPatternCapabilities.Probe` 是 pattern 维度判据，**建议两维取并**：白名单补 `Probe` 覆盖不到的「可聚焦但无 pattern」控件，`Probe` 补白名单漏掉的 pattern 型控件。

### Set-of-Marks（原理 + 可落地手法）
- SoM（arXiv 2310.11441《Set-of-Mark Prompting Unleashes Extraordinary Visual Grounding in GPT-4V》，**论文**；本仓库原 `microsoft/SoM` 本地克隆已移除，故仅引论文、不再引仓库链接）：分区叠唯一可读记号，模型输出记号 id，经 `region↔mark↔text` 绑回像素区域；加框（mask+box+id）比只加 mask+id 好。
- OmniParser 工程手法（`10-source-omniparser.md`，等级 A）：检测框+OCR 合并；**重叠>90% 去重保留更小的框**；OCR 被图标框完全包含则并文本；标签与其它框最小化重叠。
- **本项目规则建议**：
  - 候选：① UIA 元素（有任一可交互 pattern，或 ControlType ∈ {Button,Edit,CheckBox,RadioButton,ComboBox,Hyperlink,ListItem,MenuItem,TabItem,TreeItem,Slider,Spinner,SplitButton}）；② OCR 词（仅"信息"）。
  - 过滤：`IsEnabled`、`IsOffscreen=false`、bounded 非空、与截图区相交、面积有上下限（去全窗容器与 1px 噪声）。
  - 去重/聚类：嵌套只留最内层可交互；同身份按 `Ordinal` 各自编号；IoU 去重保留更小。
  - 编号顺序：按阅读顺序（先 y 后 x 带容差）比"小区域优先"可预测。
  - 徽标画框左上角外侧（越界内移），带对比底色。
  - 坐标空间：一切以「返回图像素空间」为准；label→元素映射随 `frameId` 返回。
  - 遮挡：UIA 不给，建议不建模（`IsOffscreen`+owner z-order 粗过滤）。
- **高星同类交叉验证（`11-source-windows-mcp.md`，等级 A）**：Windows-MCP 的 SoM 式标注与本文规则一致——① **编号 = 元素列表的 0 基整数下标**，图上数字（`i = enumerate`）与 `Click(label=i)` 严格对齐（`desktop/service.py:651-663,1303-1304`）；② 候选同样走白名单 + 可见 + `IsEnabled` 过滤（`tree/service.py:490-555`）。差异：其 **scrollable 元素不参与编号绘制**、且**无代际号**（见「旧帧护栏」小节），本项目应补 `maxMarks` 上限与代际号，并把「编号=index、闭回 `ui_action index=N`」写成明确契约。
- **另可借鉴（`11` ⑨，等级 A）**：① **参考线/网格叠加**（`desktop/service.py:1264-1271`，低成本、利于空间推理；**不抄**其「宽高必须同时给」的过紧约束，本项目允许单轴）；② **元素预算 + 截断显式标记**（`tree/budget.py:19,22-52,71-91`、`tree/views.py:89-95,191-192`——默认上限 500 可配、耗尽即置 `truncated` 并在响应标注），对应本文 `maxMarks`；③ **密码字段标记**（`tree/service.py:592-599` 打 `[password]` 并跳过逐词框提取），但**不抄其 value 未脱敏**（同一分支仍把 `LegacyIAccessibleValue` 写进 `metadata['value']`，`:600-602`）。
- **可直接移植的纯算法见 `10-source-omniparser.md`**。

### 旧帧护栏（高星/源码先例 + 反例）
- **`13-source-browser-mcp.md`（等级 A，源码核实）**：两家高星浏览器 MCP 都实现了「稳定 id + 换代失效 + 可操作报错」，可直接对照——
  - chrome-devtools-mcp（52.6k★）：`uid` 形如 `"{snapshotId}_{counter}"`，同一 a11y 节点**跨快照复用**旧 id（`TextSnapshot.ts:78-87`）；uid 未命中报 `Element uid "…" not found on page …`（`McpPage.ts:694-697`），元素失效报 `no longer exists on the page`（`:701-717`），另一处措辞带行动指令「**Please take a new snapshot**」（`:804`）。
  - playwright MCP（37.6k★）：`aria-ref` 解析失败报 `Ref … not found in the current page snapshot. **Try capturing new snapshot.**`（`tab.ts:547-555`）；回归测试用旧 ref 点击断言报错（`core.spec.ts:252-269`）。
  - 结论：**「旧 ref 直接拒绝 + 教学式重新采集」是两家高星共识**，不是低星项目的孤例；且两家**均不画编号 SoM**（见 `13` ⑥），编号 SoM 是本项目增量。
- **`11-source-windows-mcp.md`（等级 A）作为反例**：Windows-MCP **无 capture/snapshot id、无 generation、无时间戳、无内容哈希**（`desktop/views.py:65-80`；`desktop/service.py:273-290`），`label` 仅「当前 `desktop_state` 列表下标」；一次 `Screenshot`（内部固定 `use_ui_tree=False`）会把树清成空占位，导致先前记下的 `label` 解析 `IndexError`（`desktop/service.py:191-203,651-663`）。**这正是本项目必须补「帧归属校验/代际号」的反证依据。**
- 【等级 C｜未 clone 项目】`vercel-labs/agent-browser` 的 snapshot-ref（`Stale ref: e2 was minted by an earlier snapshot... Take a new snapshot`）为**早期线索，未经源码核实**；其结论已被上面两家高星实现独立印证，故此处仅作旁证、**不作定稿依据**。
- **转译**：每次采集产生单调 `frameId`；元素清单记录在对应帧缓存；动作携带的 `frameId` ≠ 当前 → **拒绝 + 教学提示**（「编号 N 来自旧画面（frame 12，当前 15），请重新 screenshot/ui_find」）。

## ③ 落地建议与代价

保持分层：**坐标换算/图像处理唯一在 Engine；UIA 元素采集在宿主 `Services/Ui`**。

| 层 | 改动 |
|---|---|
| `Engine/Capture/CaptureModels.cs` | `CaptureResult` 增 `OriginX/OriginY`、`Scale` |
| `Engine/Capture/ImagePipeline.cs` | 新增 `Annotate(bitmap, marks[])`（高亮+边框+编号徽标，System.Drawing） |
| `Engine/Capture/` 新增 `Ocr/WindowsOcr.cs` | OCR 封装（探测/null 降级/缩放/padding/返回 `{text, words[{text,rect}]}`） |
| `Mcp/Services/Ui/UiElementLocator.cs` | `Find` 返回**结构化几何**（现拼成 `Rect` 字符串）+ `IsEnabled/IsOffscreen` + `GenerationId`；新增 `FindForCapture()` 复用同一 `_lastFind` |
| `Mcp/Tools/Debugger/DebugScreenshotTool.cs` | 新增 `detail`/`annotate`/`ocr`/`ocrLanguage`/`maxMarks`；annotate 时元素清单写进文本头部 |
| 文案常量 | 收敛 SoM/OCR 提示到常量类 |

**接口草案（agent 可见）**：
```
screenshot(..., detail="image", annotate=false, ocr=false, ocrLanguage="", maxMarks=40)
```
头部示意：
```
目标:   窗口 "xxx" (pid=1234)
帧:     frame=15  原点=(120,80)  缩放=67%  (原生 3000x1800 → 2000x1200)
标注:   12 个可交互编号（编号=ui_find index，可直接 ui_action index=N）
OCR:    zh-Hans-CN（可用: en-US, zh-Hans-CN）；MaxImageDimension=10000
---
[1] Button "保存" Rect=...
可见文字（OCR，仅供参考）: "…"
```

**关键架构建议（最核心）**：截图可交互元素清单**直接由 `UiElementLocator` 采集并写 `_lastFind`**（返回结构化几何而非字符串），编号=index。于是 SoM 的 `[7]` ↔ `ui_action index=7` 天然一致，不可能漂移；代价是引入代际号 + 旧代际教学式拒绝。OCR 词另起 `visibleText[]`，不并入编号空间。

**可交互判定与元素预算（并入 `11-source-windows-mcp.md` 的高星同类）**：
- 「可交互」判据取 **`UiPatternCapabilities.Probe`（pattern 维度）∪ Windows-MCP 白名单/状态（role 白名单 + 可聚焦 + 可见 + `IsEnabled`，`tree/service.py:490-555`）**；可滚动元素单列（`ScrollPattern.VerticallyScrollable`），不并入可交互编号或明确分区。
- 元素上限（`maxMarks`）超出时**显式标注「已截断（N/M）」并提示改用 `ui_find` 缩小**——对标 `11` 的 `truncated` 标记（`tree/budget.py:71-91`、`tree/views.py:89-95`），避免 agent 误判「元素不存在」。
- 密码控件在清单里标 `password` 并**丢弃 value 文本**（走既有 `SensitiveValueRedactor`）；**不照抄** Windows-MCP 打 `[password]` 却仍写 value 的做法（`tree/service.py:600-602`）。
- **可选增强**：`annotate` 可叠加参考线/网格（借鉴 `11` 的 `grid_lines`，`desktop/service.py:1264-1271`）；本项目允许单轴，不抄「宽高必须同时给」的约束。

**代价**：WinRT OCR 封装 ~150–250 LOC；SoM 绘制 ~150–250 LOC；元素采集复用/代际 ~100–200 LOC；工具面 ~150 LOC。**总约 1–2 人日量级**（不含语言包/CI 真机验证）。

## ④ 风险 / 开放问题

| # | 风险 | 处置 |
|---|---|---|
| 1 | 语言包未装（常态） | 强制 null 检查；回退链：显式参数→当前输入法→`TryCreateFromUserProfileLanguages()`→`en-US`→`AvailableRecognizerLanguages[0]`→全无则只关 OCR 保留 UIA SoM + 提示安装命令 |
| 2 | 官方文档说需 package identity，实践可用 | 视为**实测决定**：启动期探测（`AvailableRecognizerLanguages` 不抛 + `TryCreateFromUserProfileLanguages()` 非 null），失败永久降级 |
| 3 | 无桌面/服务/Session 0/精简容器 | 截图本已给「可能无桌面」；OCR 在 servercore/nanoserver `TypeLoadException` → 整体不可用，明确告知不重试 |
| 4 | 系统盘非 C: | 头部提示检查 `Windows\OCR` |
| 5 | 小字/小图读不出（<~50px 高） | OCR 用原生/放大图；抄 1.5× + 64px padding；头部标注所用缩放 |
| 6 | `TextAngle != null` | 截图通常 0；非 null 标注「框可能偏」或只返回 `Text` |
| 7 | 无置信度（API 无该字段） | 不采用未核实的密度启发式【等级 C】；OCR 词只作「可见文字」提示，进 `visibleText[]`、不入可交互编号 |
| 8 | Chromium/Electron/WinUI3 文本树稀疏 | 明确告知「疑似 Chromium，文本树可能不完整」；SoM 退化为窗口壳+OCR |
| 9 | WGC 帧与 `GetWindowRect` 口径差几像素 | 同帧交叉校验 UIA rect；偏差记入诊断头部 |
| 10 | UIA 坐标 DPI 虚拟化 | 进程 PerMonitorV2 + `ui_get what=rect` 对账 |
| 11 | OCR/标注图 >2MB | 沿用落盘双轨；`detail=text` 作省 token 首选 |
| 12 | OCR 数百 ms~秒、取消不停计算 | 串行化；头部给耗时；超时=放弃等待 |
| 13 | `_lastFind` 被 ui_find/screenshot 交替覆写 | 代际号 + 教学式拒绝；或截图采集用独立缓存按 `frameId` 选源 |
| 14 | SoM 编号爆炸 | `maxMarks` 上限 + 筛选；超限提示用 ui_find 缩小 |
| 15 | CI 无头/无 GPU | 探测失败即 Skip；OCR 单测用固定 PNG + 语言缺失 `Assert.Skip` |
| 16 | 密码控件 value 泄漏进元素清单 | 清单标 `password` + **丢弃 value 文本**（走 `SensitiveValueRedactor`）；注意 `11` 反例（Windows-MCP 打 `[password]` 仍写 value）不可照抄 |
| 17 | 元素截断后编号不完整、agent 误判「元素不存在」 | `maxMarks` 超限**显式标注「已截断（N/M）」** + 提示改用 `ui_find` 缩小（对标 `11` 的 `truncated`） |

**待实测**：① 非打包 exe 下 `TryCreateFromUserProfileLanguages()` 是否恒可用；② `zh-Hans-CN` 语言包在位/降级提示；③ WGC 帧几何偏差；④ FlaUI #614 是否复现；⑤ `detail=text` vs `image` 实测 token 增益。

## 附：来源
desktop-touch-mcp `tools/win-ocr/*`；PowerToys `PowerOCR.Core/{Ocr/WindowsOcrRecognizer.cs, Imaging/BitmapPreprocessor.cs, Services/TextExtractorService.cs, Ocr/OcrTextFormatter.cs, Ocr/OcrRect.cs}`、`PowerOCR.csproj`、Text Extractor 文档与 #20388；Microsoft Learn `OcrEngine`/`OcrWord.BoundingRect`/`MaxImageDimension`；SoM arXiv 2310.11441 + microsoft/SoM；OmniParser arXiv 2408.00203；FlaUI #362/#501/#614；Chromium accessibility；vercel-labs/agent-browser snapshot-refs；MS Q&A（Docker OCR、小图 OCR）。
