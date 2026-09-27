# 13 · 浏览器 MCP 截图参数设计（chrome-devtools-mcp 52.6k★ / playwright-mcp 37.6k★ 源码核实）

> **来源等级：A 源码核实**（本地 clone `Externals/chrome-devtools-mcp` @ `ae0aaef`；`Externals/playwright` @ `b9a34ac77`，MCP 实现位于 `packages/playwright-core/src/tools/backend/`；行号按此 clone）。生成日期 2026-09-27。
> 本轮修正了 `04-prior-art.md` 中对两家的「二手印象」——全部参数/阈值/默认值均落到本地 clone 的 `文件:行号`（每条均已按本地 clone 逐条复核）。
> 未改任何产品代码，未 commit。

**两个前置事实（与任务书的出入，先说清楚）：**

1. **playwright 的截图工具实现不在 `packages/playwright-core/src/tools/mcp/`**。该 `mcp/` 目录只放配置解析（`config.ts`/`config.d.ts`）与传输/扩展（`program.ts`/`browserModel.ts`/`cdpRelay*.ts`）。真正的工具实现在 **`packages/playwright-core/src/tools/backend/`**（`screenshot.ts`/`snapshot.ts`/`response.ts`/`tab.ts`…），MCP 与 CLI 共用该 backend。任务书点名的 `browserModel.ts` 是「v2 relay 的 chrome tab ↔ CDP session 映射模型」，**与截图无关**（`packages/playwright-core/src/tools/mcp/browserModel.ts:17-34` 的类注释即证据）。
2. **截图工具是「元素/整页 a11y 快照」生态的一部分**，不是独立能力：两家都把「文本结构快照」当一等公民，截图是补充（见 ⑤）。

---

## ① 结论速览（表格）

| 维度 | chrome-devtools-mcp（ChromeDevTools） | playwright-mcp（microsoft/playwright） | 对本项目（桌面视觉基座）启示 |
|---|---|---|---|
| 工具名 | `take_screenshot`（`screenshot.ts:137`） | `browser_take_screenshot`（`backend/screenshot.ts:51`） | 保持单工具多参数 |
| 工具参数 | `format` / `quality` / `uid` / `fullPage` / `filePath`（**仅 5 个**，`screenshot.ts:144-177`） | `element` / `target` / `type` / `filename` / `fullPage` / `scale`（`backend/screenshot.ts:29-34`） | 参数宜少而正交 |
| 尺寸控制 | **不做工具参数**；只有服务端 flag `--screenshot-max-width/height`（`mcp-options.ts:232-263`） | 工具参数 `scale=css\|device`（默认 css，`backend/screenshot.ts:33`） | 尺寸是「环境/配置」而非每次调用参数 |
| 降采样位置 | **源头**：CDP `clip.scale`（`screenshot.ts:223-233`） | **源头**：Playwright `screenshot({scale})`（`backend/screenshot.ts:66`） | 源头缩放优于后处理 |
| DPR | 显式乘入：`maxW/(w*DPR)`（`screenshot.ts:98-105`），**修复史 ebf58f2**（见 ③） | `scale=device` 即含 DPR（测试 400×300@2x→800×600，`tests/mcp/screenshot.spec.ts:410-442`） | 双轴上限必须按物理像素算 |
| 返回形态 | 内联 image content 优先；`filePath` 或 ≥2,000,000B 则落盘只回路径（`screenshot.ts:269-287`） | **总是写盘**（`outputDir`）+ 无显式 filename 时**再**附内联图（`backend/screenshot.ts:75-85`） | 双通道：文件 + 内联可兼得 |
| 落盘阈值 | **`screenshot.length >= 2_000_000`** 字节（`screenshot.ts:276`） | 无字节阈值；靠 `--output-max-size` 淘汰旧文件（`backend/response.ts:236-265`） | 阈值语义要写死并可见 |
| 图像开关 | 无（`--slim` 只砍工具集，`mcp-options.ts:264-268`） | `--image-responses=allow\|omit\|only`（默认 allow，`mcp/config.d.ts:236-239`） | 值得抄的客户端能力开关 |
| 输出目录 | 无 `--output-dir`；`filePath` 或 OS temp（`McpContext.ts:705-712`） | `--output-dir`（默认 `<cwd>/.playwright-mcp` 或 temp，`backend/context.ts:477-484`） | 输出目录可配 + 自动命名 |
| 语义桥 | `take_snapshot` a11y 文本树 + `uid`；"**Prefer taking a snapshot over taking a screenshot**"（`snapshot.ts:14-16`） | `browser_snapshot` a11y 树 + `aria-ref`；"**this is better than screenshot**"（`backend/snapshot.ts:40`） | 「结构优先」是两家原文共识 |
| 元素寻址 | `uid`（形如 `"{snapshotId}_{counter}"`，`TextSnapshot.ts:85`；跨快照复用映射 `:80-87`） | `target`（形如 `e1`/`f2e3`，`aria-ref=<target>`，`backend/tab.ts:539,548`） | 稳定 id + 代际复用 |
| 旧引用护栏 | `idToNode` 未命中 → `Element uid "…" not found on page …`（`McpPage.ts:694-697`）；元素失效 → `no longer exists on the page`（`:705`） | `aria-ref` 解析失败 → `Ref … not found in the current page snapshot. Try capturing new snapshot.`（`backend/tab.ts:554`） | **必须**有可操作的 stale 报错 |
| SoM 编号叠加 | **无**（本地 clone 全仓检索 `set-of-marks`/`setOfMarks`/`numbered overlay` 均 0 命中） | **无** | 两家不画编号；本项目 SoM 是自有增量 |
| 安全 | MCP roots + 恒含 OS temp；路径规范化前缀校验；写文件 `O_NOFOLLOW`+`0600`（`McpContext.ts:243-324,678-703`） | 限制在 `outputDir ∪ cwd`；拒绝系统目录；**自称非安全边界**（`backend/context.ts:494-510`、`mcp/config.d.ts:259-265`） | 路径护栏抄两边 |

---

## ② chrome-devtools-mcp `screenshot` 参数面（逐参数）

**源码：`src/tools/screenshot.ts`（`definePageTool` 工厂，`screenshot.ts:126`）。** `pageId` 由 `pageIdRouting` 自动注入（`ToolHandler.ts:211-220`），不在工具 schema 内。

### 工具入参（`screenshot.ts:144-177`）——只有 5 个

| 参数 | 类型/默认 | 说明（原文要点） | 行号 |
|---|---|---|---|
| `format` | `enum('png','jpeg','webp')`，默认 `screenshotFormat ?? 'png'` | 输出格式；默认值可被服务端 `--screenshotFormat` 覆盖 | `screenshot.ts:145-150`、`134` |
| `quality` | `number` 0–100，**optional** | 仅 JPEG/WebP 生效；PNG 忽略；未给则回落 `screenshotQuality` flag | `screenshot.ts:151-158`、`193-196` |
| `uid` | `string` optional | 元素 uid（来自页面文本快照）；省略=整页截图 | `screenshot.ts:159-164` |
| `fullPage` | `boolean` optional | 截整页而非视口；**与 `uid` 互斥** | `screenshot.ts:165-170`、`183-185` |
| `filePath` | `string` optional | 绝对路径或相对 CWD；给了就写盘而不内联 | `screenshot.ts:171-176` |

- `annotations.readOnlyHint: false`，理由注释明写 **"Not read-only due to filePath param."**（`screenshot.ts:141-142`）。
- `blockedByDialog: true`（有 JS dialog 时拒绝执行，`screenshot.ts:178`）；`verifyFilesSchema: {filePath: true}`（写文件前走路径校验，`screenshot.ts:179-181`）。

### 服务端 flag（不是工具参数）

| flag | 默认 | 说明 | 行号 |
|---|---|---|---|
| `--screenshotFormat` | 无（保留 `png`） | 覆盖工具默认格式；"JPEG and WebP are ~3-5x smaller than PNG" | `mcp-options.ts:210-215` |
| `--screenshotQuality` | 无（Puppeteer 默认） | JPEG/WebP 默认压缩质量 0–100 | `mcp-options.ts:216-231` |
| `--screenshotMaxWidth` | 无（不缩放） | 超过则**保比降采样** | `mcp-options.ts:232-247` |
| `--screenshotMaxHeight` | 无（不缩放） | 可与 maxWidth 并用，**较小 scale 获胜** | `mcp-options.ts:248-263` |

> 关键：**`width`/`height`/`maxWidth`/`maxHeight`/`clip` 都不是工具参数**。`clip` 是内部计算的中间量（`ScreenshotClip`，`screenshot.ts:93-124`），调用方不可见。

### 返回形态与落盘阈值（`screenshot.ts:249-288`）

判定顺序（互斥三选一）：

1. **给了 `filePath`** → `context.saveFile(...)`，文本行 `Saved screenshot to <canonical path>.`，**不附加 image**（`screenshot.ts:269-275`）。
2. **否则若 `screenshot.length >= 2_000_000`** → `context.saveTemporaryFile(...)` 写到 OS temp，文本行 `Saved screenshot to <filepath>.`，**不附加 image**（`screenshot.ts:276-281`）。
3. **否则** → `response.attachImage({mimeType:'image/<format>', data: base64})`（`screenshot.ts:282-287`）。

- **是 2,000,000 字节**：`screenshot.length >= 2_000_000`（`screenshot.ts:276`），`screenshot` 是 `Uint8Array` 编码后字节数。
- 返回体是 `content: [text, ...images]`——**文本行永远在，image 可选**（`McpResponse.ts:1562-1576`；`attachImage` 定义 `:484-486`）。
- 大图落盘的回归断言：`tests/tools/screenshot.test.ts:139-174`（`full page` 大图 → `response.images.length === 0`，第二行匹配 `/Saved screenshot to.*\.png/`）。

---

## ③ 降采样与 DPR 处理（含修复史）

### 算法（`src/tools/screenshot.ts`）

源盒 `getSourceBox`（`:25-91`）返回 `{x,y,width,height,devicePixelRatio}`（`SourceBox`，`:21-23`）：

- 元素：`element.boundingBox()` + DPR（有模拟视口用 `viewport.deviceScaleFactor ?? 1`，否则 `window.devicePixelRatio`）（`:30-38`）。
- 整页：`max(documentElement.scrollWidth, body.scrollWidth)` × 同高，DPR=`window.devicePixelRatio`（`:40-61`）。
- 视口：`viewport.width/height` + `deviceScaleFactor ?? 1`（`:63-72`）。
- 无模拟视口回落：`window.innerWidth/innerHeight` + `window.devicePixelRatio`（`:73-90`）。

降采样 `computeDownscaleClip`（`:93-124`）：

```
widthScale  = maxWidth  !== undefined ? min(1, maxWidth  / (box.width  * box.devicePixelRatio)) : 1   // :98-101
heightScale = maxHeight !== undefined ? min(1, maxHeight / (box.height * box.devicePixelRatio)) : 1   // :102-105
scale = min(widthScale, heightScale)                                                                  // :106
if (scale >= 1) return undefined;                                                                     // :107-109
// 亚像素退化保护
if (round(box.width*DPR*scale) < 1 || round(box.height*DPR*scale) < 1) return undefined;              // :110-116
return { x, y, width, height, scale };                                                                // :117-123
```

- **在源头降采样**：`page.screenshot({ ..., clip })`，注释明写「`clip` lets the CDP scale param downscale the capture … rely on Puppeteer's default of `captureBeyondViewport=true`」（`:223-233`）。元素截图与整页截图都能借同一条 CDP clip 缩放。
- 未设 max 时走原始分支（元素 `element.screenshot` / 整页 `page.screenshot({fullPage})`），即**零改动直出**（`:234-247`）。

### DPR 修复史

- 修复 commit：**`ebf58f2f4aa8f1dfbbae38e440fde4e5fef7deef`（2026-08-21，`fix: respect screenshot bounds on HiDPI displays (#2536)`，Fixes #2531）**。
- 改动内容（`git show ebf58f2 -- src/tools/screenshot.ts`）：
  - 旧式 `maxWidth / box.width` → 新式 `maxWidth / (box.width * box.devicePixelRatio)`；
  - 旧式 `maxHeight / box.height` → 新式 `maxHeight / (box.height * box.devicePixelRatio)`；
  - 新增 `SourceBox.devicePixelRatio`、`getSourceBox` 取 DPR、亚像素判定乘 DPR。
- commit message 原文归因：「Screenshot source boxes and CDP clip dimensions are expressed in CSS pixels, but the returned bitmap is scaled by the page's device pixel ratio. The previous calculation compared the CSS width directly with `screenshotMaxWidth`, so **a 2x page could return an image twice the configured bound**.」
- 回归测试：`tests/tools/screenshot.test.ts:400-437`（`--force-device-scale-factor=2` 下断言宽度=100，高度≈按比例换算），另 `:375-398`（无 DPR 基本缩放）、`:476-499`（双轴取小）、`:523-556`（整页）、`:501-521`（不超限不缩放）。
- 引入该功能的上游 commit：`55c8a541d4f842056db6bc843e54117b07bf06c1`（2026-06-17，`feat(screenshot): add CLI options to cap screenshot size at the source (#1823)`）。

> 教训直译：**「用户设的上限」必须约束「送去给模型的位图字节尺寸」，也就是 `CSS尺寸 × DPR`**，否则 HiDPI 上悄悄翻倍。

---

## ④ playwright MCP 图像策略（output-dir / image-responses / max-size / scale）

**源码：本地 clone `Externals/playwright` @ `b9a34ac77`，路径 `packages/playwright-core/src/tools/backend/*` 与 `packages/playwright-core/src/tools/mcp/*`（行号按此 clone 实测，见 ⑩）。**

### 4.1 工具入参（`backend/screenshot.ts:29-34`）

| 参数 | 类型/默认 | 语义 | 行号 |
|---|---|---|---|
| `element` | `string` optional | 人类可读元素描述（仅用于描述/权限） | `backend/snapshot.ts:25-28` |
| `target` | `string` optional | **「Exact target element reference from the page snapshot, or a unique element selector」** | `backend/snapshot.ts:23,27` |
| `type` | `enum('png','jpeg','webp')` optional | 未给则按 `filename` 扩展名推断，否则 `png` | `backend/screenshot.ts:30,36-46,62` |
| `filename` | `string` optional | 相对名解析到 workspace root；未给则自动 `page-{timestamp}.{ext}` 落到 output dir | `backend/screenshot.ts:31` |
| `fullPage` | `boolean` optional | 整页；**不可与元素截图同用** | `backend/screenshot.ts:32,59-60` |
| `scale` | `enum('css','device')`，**默认 `css`** | `css`=按 CSS 像素（更小、跨设备一致）；`device`=设备像素（更大，含 DPR） | `backend/screenshot.ts:33` |

- JPEG 质量**硬编码 90**（`backend/screenshot.ts:65`：`quality: fileType === 'jpeg' ? 90 : undefined`）。
- 不兼容组合是**显式报错**：`fullPage cannot be used with element screenshots.`（`backend/screenshot.ts:59-60`；测试 `tests/mcp/screenshot.spec.ts:444-466`）。

### 4.2 落盘 + 内联双通道（`backend/screenshot.ts:75-85`）

- **总是写文件**：`response.resolveClientOutputFile(...)` 解析路径并 `addFileResult`（含 `[title](relativeName)` 文本链接）（`:75-83`；`backend/response.ts:92-102,129-132`）。
- **仅在未显式给 `filename` 时**再注册内联图：`if (!params.filename) await response.registerImageResult(data, fileType)`（`backend/screenshot.ts:84-85`）。
  - 推论：**给了 `filename` 就只回文本链接**（`tests/mcp/screenshot.spec.ts:222-247` 断言只有 `text`）。
- `scale=device` 测试：400×300 视口 + `deviceScaleFactor:2` → css=400×300、device=800×600（`tests/mcp/screenshot.spec.ts:410-442`）。

### 4.3 `--output-dir` / 自动命名 / `--output-max-size`

- `outputDir` 解析：显式 config → `path.resolve(outputDir)`；否则 `<cwd>/.playwright-mcp`（skillMode 下 `.playwright-cli`）；cwd 是系统目录或不可写则退到 `os.tmpdir()`（`backend/context.ts:477-484`）。
- **显式 `filename` 不被 outputDir 影响**，而是相对 workspace root（`mcp/config.d.ts:162-167`、`backend/response.ts:104-106`、`backend/context.ts:470-475`）。
- 自动命名格式：`page-{ISO时间戳}.{ext}`（测试 `tests/mcp/screenshot.spec.ts:96`）。
- `--output-max-size`：**没有默认值**（`defaultConfig` 未设，`mcp/config.ts:93-105`；`mcp/config.d.ts:169-172` "Threshold for evicting old output files, in bytes"）。每次响应序列化前执行 `_enforceOutputBudget`：递归统计 output dir 全部文件，超限则**按 mtime 从旧到新删除**，跳过本次刚写的文件（`backend/response.ts:236-265`）。测试 `tests/mcp/output-max-size.spec.ts:22-71`（含"单个超大文件会把其它全淘汰但仍写入"）。

### 4.4 `--image-responses=allow|omit|only`（默认 allow）

- 类型定义：`imageResponses?: 'allow'|'omit'|'only'`，**默认 `allow`**；`only` 表示「带图的响应只含图片部分、不含文本部分」（`mcp/config.d.ts:235-239`）。
- 序列化逻辑（`backend/response.ts:221-227`）：
  - `omit` → `images = []`（丢弃全部 image part）；
  - `imagesOnly = imageResponses==='only' && images.length>0 && !isError` → content 只取 images；
  - 否则 content = `[text, ...images]`。
- **何时 omit 图像**：① 全局 `--image-responses=omit`；② 调 `browser_take_screenshot` 时显式传了 `filename` → 不注册内联图（`backend/screenshot.ts:84-85`）。测试：`tests/mcp/screenshot.spec.ts:274-304`（omit 只回 text）、`:306-342`（only 只回 image；但带 `filename` 时回文本链接，走 text 分支）。
- 另有 `--file-paths=relative|absolute`（默认 relative）控制工具结果里路径渲染（`mcp/config.d.ts:241-244`；`backend/response.ts:81-90`）。

### 4.5 `--snapshot-boxes` / `--snapshot-mode`

- `snapshot.boxes`：在快照里给每个元素附 `[box=x,y,width,height]`，坐标为**视口相对 CSS 像素**（`mcp/config.d.ts:246-257`；`backend/snapshot.ts:45`；CLI flag `--snapshot-boxes`，`mcp/config.ts:75,391`）。
- `snapshot.mode`：`full|none`；MCP 默认 `full`（CLI daemon 显式设 `'full'`，`mcp/config.ts:178`；`backend/response.ts:159-162`）。
- 测试 `tests/mcp/snapshot-mode.spec.ts:21-123`（full/none/boxes 各自行为，boxes 断言 `[box=100,50,80,40]`）。

---

## ⑤ 结构优先哲学（原文引用）

### chrome-devtools-mcp

`docs/design-principles.md`（全文仅 12 行）关键两条：

- `:7` **"Token-Optimized**: Return semantic summaries. \"LCP was 3.2s\" is better than 50k lines of JSON. **Files are the right location for large amounts of data.**"
- `:12` **"Reference over Value**: for heavy assets (screenshots, traces, videos), **return a file path or resource URI, never the raw data stream.** Some MCP clients support a built-in handling of heavy assets e.g. directly displaying images. This _could_ be an exception."
- `:8` "**Small, Deterministic Blocks**: Give agents composable tools (Click, Screenshot), not magic buttons."

工具描述里的原文（`src/tools/snapshot.ts:14-16`，同一句进 `docs/tool-reference.md:467-469`）：

> "Take a text snapshot of the target page based on the a11y tree. The snapshot lists page elements along with a unique identifier (uid). Always use the latest snapshot. **Prefer taking a snapshot over taking a screenshot.** The snapshot indicates the element selected in the DevTools Elements panel (if any)."

`take_screenshot` 描述极简（`screenshot.ts:138`）："Take a screenshot of the page or element."——**主语是 page/element，不承诺可据此操作**。

### playwright MCP

工具描述（`backend/snapshot.ts:40`）：

> `description: 'Capture accessibility snapshot of the current page, **this is better than screenshot**'`

截图工具描述里主动劝退（`backend/screenshot.ts:53`）：

> "Take a screenshot of the current page. **You can't perform actions based on the screenshot, use browser_snapshot for actions.**"

官方文档（`docs/src/getting-started-mcp.md`）：

- `:8` "…enabling LLMs to interact with web pages using **structured accessibility snapshots**. It works with … any other MCP client — **no vision models required**."
- `:75` "The assistant will use Playwright MCP tools to … interact with elements — **all through structured accessibility snapshots rather than screenshots**."
- `:81` "Playwright MCP **operates on the page's accessibility tree, not pixels.** When a tool runs, it returns a structured snapshot showing the page elements, their roles, and text content. **The LLM uses element references from these snapshots to interact with the page.**"
- `:91` "The LLM reads this snapshot and uses `ref=e5` to type into the textbox or `ref=e10` to check the checkbox."
- `:99` 截图定位为「**for visual verification**」（视觉校验，而非操作依据）。

> 对本项目「语义桥优先」的外部权威依据：**两家高星都把『结构/引用』设为默认路径，把『像素』设为补充**，并把这条写进工具描述（agent 第一眼看到的地方），而不只是文档。

---

## ⑥ 元素寻址 / uid / stale-ref 护栏

### chrome-devtools-mcp：`uid`

- uid 生成（`src/TextSnapshot.ts:73-110`）：遍历 a11y 节点树时按 `"${snapshotId}_${idCounter++}"` 生成（`:85`）；但**若 `uniqueBackendId = loadersId_backendNodeId` 已存在映射，就复用旧 id**（`:78-87`）；每次快照重建 `idToNode` 映射（`:108`），并清理本轮未见到的映射（`:141-146`）。即 **uid 是跨快照稳定的（对同一 DOM 节点）**。
- uid 使用：`getElementByUid(uid)` 从 `idToNode.get(uid)` 取节点 → `node.elementHandle()`（`src/McpPage.ts:688-699`）。
- **stale 护栏**：
  - 无快照：`No snapshot found for page … Use take_snapshot to capture one.`（`McpPage.ts:689-692`）；
  - uid 未命中：`Element uid "…" not found on page …`（`McpPage.ts:694-697`）；
  - 元素已失效：`Element with uid … no longer exists on the page.`（`McpPage.ts:701-717`）；
  - CSS 路径另有一句带"行动指令"的措辞：`Element with uid "…" was detached or no longer exists on the page. **Please take a new snapshot with take_snapshot.**`（`McpPage.ts:804`）。
- 截图与元素绑定：`take_screenshot` 传 `uid` → `getElementByUid` → 截该元素；**截图本身不编号**。

### playwright MCP：`target` / `aria-ref`

- 快照生成：`captureSnapshot` 调 `page.ariaSnapshot({ mode:'ai', depth, boxes })` / `ariaSnapshotJSON(...)`（`backend/tab.ts:434-457`）。快照文本形如 `- button "Button 1" [ref=e2]`（测试 `tests/mcp/core.spec.ts:247-249`）。
- 寻址解析（`backend/tab.ts:536-558`）：
  - 若非 ref 形状 → 当选择器/JS locator 处理，找不到 → `"…" does not match any elements.`（`:539-545`）；
  - 若匹配 **`/^(f\d+)?e\d+$/`**（支持 `eN` 与 iframe 前缀 `fNeM`）→ `page.locator(`aria-ref=${target}`)`（`:539,548`）。
- **stale 护栏**（`:547-555`）：`locator.normalize()` 失败 → `Ref ${target} not found in the current page snapshot. **Try capturing new snapshot.**`
  - 回归测试：`tests/mcp/core.spec.ts:252-269`（用旧 ref e3 点击 → 报错 isError）、`:357-393`（`browser_snapshot` 传 `e999` → 同一报错）。
- 截图元素绑定：`browser_take_screenshot` 的 `target` 走同一 `targetLocator`（`backend/screenshot.ts:71-73`），**截图不编号**。

### SoM（Set-of-Marks）结论

- **两家都不在截图上叠加编号/标记**。对本地 chrome-devtools-mcp clone 全仓检索（`src/`+`docs/`+`tests/`）：`set-of-marks` / `setOfMarks` / `numbered overlay` / `marker overlay` 全部 **0 命中**；playwright 的 `boxes` 只是快照文本里的 `[box=...]`（`backend/snapshot.ts:45`），也不往图上画。
- 结论：**编号 SoM 是本项目的增量能力（复用 `ui_find` index + 代际号），不是从这两家可抄的实现**；能从它们抄的是「稳定 id 语义 + stale 报错文案 + id 跨快照复用规则」。

---

## ⑦ 安全与输出限制

### chrome-devtools-mcp

- **workspace 限制**：`McpContext.validatePath`（`src/McpContext.ts:243-324`）：
  - `resolveCanonicalPath` 失败 → `Access denied: Cannot resolve base path for …`（`:258-268`）；
  - 未配置 roots 且 `--allow-unrestricted-paths` → 放行（`:270-277`）；
  - 否则要求规范化路径等于某 root 或在其下（`:282-321`），否则 `Access denied: path … is not within any of the configured workspace roots.`（`:317-320`）。
- **roots 恒含 temp**：`roots()` 总是追加 `os.tmpdir()`（`McpContext.ts:229-237`）——注释说明「即使客户端没协商 roots，也不能跳过校验」（`:278-282`）。
- **写文件加固**：`#writeFile` 用 `O_WRONLY|O_CREAT|O_TRUNC|O_NOFOLLOW` + `mode 0o600`（`McpContext.ts:678-703`；`O_NOFOLLOW` 防止符号链接跟随）。
- `saveFile` 会**补/改扩展名**（`ensureExtension`，`McpContext.ts:326-337,714-725`）。
- CLI 描述：`--filesystemRoot/--workspace`（可多次，默认 OS temp）；`--allowUnrestrictedPaths`（默认 false，仅对可信本地客户端开启）（`docs/configuration.md:225-233`）。
- 内容暴露声明：`04-prior-art.md` 提到的 "Reference over Value"（`design-principles.md:12`）即是「重资产用路径而非裸流」的声明。

### playwright MCP

- **限制在 `outputDir ∪ cwd`**：`checkFile`（`backend/context.ts:494-510`）——`origin==='code'`、`allowUnrestrictedFileAccess`、`skillMode` 三者之一则放行，否则对 output/workspace/目标分别 `resolveSymlinks` 后要求目标在二者之一内，否则 `File access denied: … is outside allowed roots. Allowed roots: …`（`:508-509`）。
- **拒绝系统目录**：`validateOutputDir` → `--output-dir cannot point to a system directory: …`（`mcp/config.ts:151-156`；在 `:146`、`:204` 两处调用）。
- **自称非安全边界**（`mcp/config.d.ts:259-265`）原文：
  > "allowUnrestrictedFileAccess acts as a **guardrail** to prevent the LLM from accidentally wandering outside its intended workspace. It is a **convenience defense** … **not a secure boundary**; a deliberate attempt to reach other directories can be easily worked around, so always rely on client-level permissions for true security."
- 输出体积限制：`--output-max-size`（淘汰策略，见 4.3）；无「单文件字节阈值内联」机制。
- 其它相关：`--secrets` 用于把敏感明文替换出响应，但注释同样声明「**a convenience and not a security feature**」（`mcp/config.d.ts:155-160`）。

---

## ⑧ 可借鉴清单（按性价比；标注服务 ①寻址通用 / ②图像经济 / ③语义桥）

| # | 借鉴点 | 来源（file:line） | 服务 | 理由 |
|---|---|---|---|---|
| 1 | **稳定元素 id + 跨快照复用 + 失效可操作报错** | chrome `TextSnapshot.ts:78-87`、`McpPage.ts:694-717`；pw `backend/tab.ts:539-555` | ①③ | 本项目 `_lastFind` 代际号的正统对照；报错必须含"重新获取"动作 |
| 2 | **双轴保比降采样，`k=min(1, maxW/W, maxH/H)`，且 W/H 为物理像素** | chrome `screenshot.ts:98-106`（含 DPR） | ② | 与 `02-image-economy.md` 的 k 一致；DPR 教训见 ③ |
| 3 | **硬字节阈值落盘 + 只回路径**（chrome `>=2_000_000`） | chrome `screenshot.ts:276-287` | ② | 本项目已有「图片过大落盘」，可把阈值写死成常量并回显 |
| 4 | **文件 + 内联双通道，显式给文件名时只回链接** | pw `backend/screenshot.ts:75-85`、`backend/response.ts:129-141` | ② | 兼顾「agent 看图」与「省 token/留档」 |
| 5 | **`image-responses=allow\|omit\|only` 客户端能力开关（默认 allow）** | pw `mcp/config.d.ts:236-239`、`backend/response.ts:221-227` | ② | 客户端不支持图片时一键 omit；`only` 需谨慎（丢文本路径） |
| 6 | **输出目录可配 + 自动命名 + 体积淘汰（按 mtime 删旧、跳过本次刚写）** | pw `backend/context.ts:477-492`、`backend/response.ts:236-265`、`mcp/config.d.ts:162-172` | ② | 本项目可给截图目录加上限与自动清理 |
| 7 | **路径渲染 `relative\|absolute` 可配（默认 relative）** | pw `mcp/config.d.ts:241-244`、`backend/response.ts:81-90` | ② | 与项目「头部信息块」路径展示一致化 |
| 8 | **工具描述里直接写"结构优先/截图仅供校验"** | chrome `snapshot.ts:14-16`；pw `backend/snapshot.ts:40`、`backend/screenshot.ts:53` | ③ | agent 只读工具目录，这句是行为引导的关键位 |
| 9 | **不兼容参数显式报错而非静默丢参** | chrome `screenshot.ts:183-185`；pw `backend/screenshot.ts:59-60` | ①③ | 对齐 `01-addressing.md` 对坐标空间的谨慎 |
| 10 | **写文件加固 `O_NOFOLLOW` + `0600` + 扩展名强制** | chrome `McpContext.ts:678-703,326-337` | 安全 | 防符号链接/权限泄露 |
| 11 | **`fullPage` / 视口 / 元素三态，元素与整页互斥** | chrome `screenshot.ts:165-170`；pw `backend/screenshot.ts:32,59-60` | ① | 本项目对应整屏/窗口/元素三态 |
| 12 | **`scale=css\|device` 把"物理像素可读版本"做成显式开关** | pw `backend/screenshot.ts:33` | ② | 小字/代码压糊时的逃生门（与 `dotByDot` 同类） |
| 13 | **`--snapshot-boxes` 给结构项附 box** | pw `backend/snapshot.ts:45`、`tests/mcp/snapshot-mode.spec.ts:99-123` | ③ | 本项目 SoM 的 box 可来自 UIA 矩形，语义对齐 |
| 14 | **"Reference over Value" 作为默认返回哲学** | chrome `design-principles.md:12` | ② | 但注意 `04/README` 已裁定：本项目默认内联（客户端不 fetch ref） |
| 15 | **`--output-max-size` 的"淘汰而非拒绝写入"策略** | pw `backend/response.ts:253-264` | ② | 比"超限报错"更不易打断调试流 |

---

## ⑨ 不采纳清单（本项目浏览器外的桌面场景）

| 不采纳 | 来源/原因 |
|---|---|
| `fullPage`（滚动全页） | 纯 Web 概念（`screenshot.ts:165-170`）；桌面用整屏/全窗口/内容高度 |
| `aria-ref` / accessibility tree 寻址 | Web a11y 专有（`backend/tab.ts:548`）；桌面元素身份走 **UIA**（与 `ui_find`/`10`、`06-source-flaui.md` 一致） |
| `scale=css\|device` 语义 | Web CSS 像素 vs 设备像素（`backend/screenshot.ts:33`）；桌面已有 `origin + scale`（`01-addressing.md`）与 DPI 感知口径，不引入第三套坐标话术 |
| `pageId` 路由 / `--slim` 工具集 | 多标签页运行时会话模型（`ToolHandler.ts:211-220`、`mcp-options.ts:264-268`）；桌面无 page 概念 |
| `--isolated` / user-data-dir / `--browser-url` | 浏览器进程/用户配置专属（`advanced-usage.md:27-41,101-150`） |
| `image-responses=only`（丢弃全部文本） | 会让 agent 失去路径/元信息（`backend/response.ts:223`）；本项目默认内联，保留头部信息块 |
| 把截图作为「可操作依据」 | 两家工具描述都明确反对（`backend/screenshot.ts:53`、`snapshot.ts:14-16`）；本项目动作为 UIA 语义，坐标点击非主路径 |
| SoM 编号叠加（当作可从两家抄） | 两家**均无**此实现（见 ⑥ 末尾本地检索 0 命中）；本项目 SoM 需自建（`10-source-omniparser.md`、`03-semantic-bridge.md`） |
| `--output-dir` 自动命名 `page-{timestamp}` | 命名绑定 "page" 语义（`backend/screenshot.ts:31`）；桌面宜用 window/monitor 语义化前缀 |
| playwright `filename` 相对 workspace root 的解析规则 | 其"workspace root"= MCP client cwd（`backend/context.ts:470-475`），与桌面宿主的输出目录策略不同，只借思路不抄规则 |

---

## ⑩ 来源

### 来源等级与本地 clone

- **chrome-devtools-mcp**：本地 clone `E:\Code\Projects\Externals\chrome-devtools-mcp`（origin `https://github.com/ChromeDevTools/chrome-devtools-mcp.git`），**commit `ae0aaef884c41445d83f86f099ef211f4584b791`（2026-09-25，`fix: reject --blockedUrlPattern/--allowedUrlPattern hostname regexp groups (#2796)`）**；行号按此 commit 在本机实测。该 clone 的 `git status` 唯一改动为子模块 `third_party/devtools-frontend`（与 `src/` 无关）。
- **playwright（MCP 实现）**：本地 clone `E:\Code\Projects\Externals\playwright`（origin `https://github.com/microsoft/playwright.git`，即 `microsoft/playwright` monorepo），**commit `b9a34ac7783a1b6c2e1dfff0c08ac744c048fa59`**；行号按此 commit 在本机实测。工具实现在 `packages/playwright-core/src/tools/backend/`（`screenshot.ts`/`snapshot.ts`/`response.ts`/`config.ts`/`tools.ts`/`browserBackend.ts`…），配置解析在 `packages/playwright-core/src/tools/mcp/`（`config.ts`/`config.d.ts`）。本 clone 内唯一的 `config.d.ts` 位于 `packages/playwright-core/src/tools/mcp/config.d.ts`，仓库根目录没有 `config.d.ts`。
- 排除路径（未采信）：`packages/playwright-core/src/tools/mcp/browserModel.ts` 是 v2 relay 的 tab↔session 模型，**非截图工具**。

### chrome-devtools-mcp 引用清单

- `src/tools/screenshot.ts`：`19,21-23,25-91,93-124,126-135,137-142,144-177,178-181,182-288`
- `src/McpResponse.ts`：`88-102,484-486,504-531,1562-1576`
- `src/McpContext.ts`：`229-237,243-324,326-337,678-703,705-712,714-725`
- `src/ToolHandler.ts`：`74-92,121-150,211-220`
- `src/McpPage.ts`：`688-699,701-717,804`
- `src/TextSnapshot.ts`：`64-88,108,141-146`
- `src/config/mcp-options.ts`：`210-215,216-231,232-247,248-263,264-268`
- `docs/design-principles.md`：`7,8,12`
- `docs/tool-reference.md`：`450-461,465-475`
- `docs/configuration.md`：`198-213,225-233`
- `docs/advanced-usage.md`：`27-41,101-150`
- `tests/tools/screenshot.test.ts`：`139-174,256-288,375-398,400-437,476-499,501-521,523-556`
- git 历史：`ebf58f2f4aa8f1dfbbae38e440fde4e5fef7deef`（2026-08-21，DPR 修复 #2536）、`55c8a541d4f842056db6bc843e54117b07bf06c1`（2026-06-17，cap size #1823）
- 本地全仓检索（SoM 负结论）：对本地 clone 的 `src/`、`docs/`、`tests/` 检索 `set-of-marks` / `setOfMarks` / `numbered overlay` / `marker overlay`，全部 **0 命中**。

### playwright 引用清单（本地 clone commit `b9a34ac77`）

- `packages/playwright-core/src/tools/backend/screenshot.ts`：`27,29-34,36-46,48-56,58-60,62-69,71-85,89-91`
- `packages/playwright-core/src/tools/backend/snapshot.ts`：`23,25-28,30-33,35-56,58-63,100-129`
- `packages/playwright-core/src/tools/backend/tool.ts`：`25-56,62-83`
- `packages/playwright-core/src/tools/backend/response.ts`：`52-79,81-106,112-141,159-170,172-234,236-265,267-325,401-468`
- `packages/playwright-core/src/tools/backend/context.ts`：`42,47,52-53,223-229,470-492,494-510`
- `packages/playwright-core/src/tools/backend/tab.ts`：`225,434-457,531-558,577-579`
- `packages/playwright-core/src/tools/mcp/config.d.ts`：`33-103,105-137,139-172,174-199,201-233,235-244,246-257,259-271`
- `packages/playwright-core/src/tools/mcp/config.ts`：`35-86,93-105,116-119,140-156,158-214,375-415,417-459`
- `packages/playwright-core/src/tools/mcp/browserModel.ts`：`17-34`（仅作"非截图工具"排除证据）
- `docs/src/getting-started-mcp.md`：`8,75,79-91,99`（本地 clone 内文档）
- `tests/mcp/screenshot.spec.ts`：`45-75,77-97,99-140,142-177,179-219,222-247,249-272,274-304,306-342,344-371,373-408,410-442,444-466,468-499`
- `tests/mcp/snapshot-mode.spec.ts`：`21-54,56-97,99-123,125-143`
- `tests/mcp/output-max-size.spec.ts`：`22-54,56-71`
- `tests/mcp/core.spec.ts`：`247-269,357-393`

### 与既有调研的关系

- 本文**取代/校准** `04-prior-art.md` 中 chrome-devtools-mcp、playwright-mcp 两行的二手描述：
  - chrome「≥2,000,000B 落盘」「源头 max-width/height」——**核实无误**（`screenshot.ts:276`、`mcp-options.ts:232-263`）。
  - playwright「默认落 `--output-dir`」——**需修正**：截图**总是**写 outputDir，且**还会额外内联一张图**，除非显式给了 `filename`（`backend/screenshot.ts:75-85`）。
  - playwright「`scale=css|device`」——**是工具参数**（默认 `css`），**不是 CLI flag**（`backend/screenshot.ts:33`）。
  - playwright「`--snapshot-boxes` 附 box」——**核实无误**，且 box 是**视口相对 CSS 像素**（`backend/snapshot.ts:45`）。
- 本文不重复 `06/07/08/09/10/11` 对桌面实现的深挖；SoM 算法仍以 `10-source-omniparser.md`、`03-semantic-bridge.md` 为准。
