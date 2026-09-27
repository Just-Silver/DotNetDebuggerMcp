# 12 · MCP 资源/图像返回 与 computer-use 官方参考（源码+规范核实）

> 来源等级：A 源码核实（本地 clone，逐条 `file:line`）＋ B 官方规范（spec 仓库 / 官网锚点）。生成日期 2026-09-27。
> 本文件所有 `file:line` 均按下方 ⑧ 记录的 clone commit 实测行号（`read` / `Select-String` 双重核对）。凡规范/源码中没有的能力，一律写「未找到」，不做经验补全。
> 本轮**未修改任何产品代码、未 git commit**；客户端行为（Claude Desktop / Claude Code 等是否 fetch `resources/read`、是否渲染 `resource_link`）本轮**未做源码核实，等级 C**，见 ⑦。

---

## ① 结论速览（表格）

| 维度 | 结论 | 证据 |
|---|---|---|
| `ModelContextProtocol` 2.2.0 实现的协议版本 | 支持 **5 个协议版本**：`2024-11-05` / `2025-03-26` / `2025-06-18` / `2025-11-25` / `2026-07-28`；**默认偏好最新 `2026-07-28`**；走 `initialize` 握手的最高版本为 `2025-11-25`（`2026-07-28` 起改为 per-request `_meta`，移除 initialize 握手与 HTTP session） | csharp-sdk `src/Common/McpProtocolVersions.cs:17,25,28,31,34,39,50,58`（tag `v2.2.0`） |
| 图像作为 tool result 返回 | 规范**一等公民**：`CallToolResult.content` 是 `ContentBlock[]`，`ImageContent` = `{type:"image", data(base64), mimeType}`（+可选 annotations/_meta）。**无需任何 server capability** | spec `schema/2026-07-28/schema.ts:1809-1838,2305-2306,2340-2361`；`docs/specification/2026-07-28/server/tools.mdx:404-439` |
| `resource_link` | 规范可选内容块，`ResourceLink extends Resource`，**必填 `uri` + `name` + `type:"resource_link"`**；它只是「链接」，客户端要取内容须自行 `resources/read` | spec `schema.ts:1719-1721`（+ `:1441-1474` Resource、`:954-969` BaseMetadata）；`tools.mdx:451-471` |
| 返回 `resource_link` 是否**强制**服务端实现 `resources/read` | **规范未强制**。规范只说「resource_link 提供可订阅/可 fetch 的 URI」「tool 返回的 resource_link 不保证出现在 `resources/list`」；**只有 `EmbeddedResource` 才写了 `SHOULD implement the resources capability`**。但**实现上**：服务端不注册 handler 就不声明 `resources` capability（SDK 行为），客户端 `resources/read` 必然失败 → by-ref 落地**必然要新增 resources handler** | spec `tools.mdx:453-454,468-471,475-476`；csharp-sdk `McpServerImpl.cs:1208-1213` |
| 服务端声明 `resources` capability 的条件（SDK） | `ConfigureResources` 中：**任一** resources 相关 handler（list/listTemplates/read/subscribe/unsubscribe）或 `ResourceCollection` 或显式 `Capabilities.Resources` **存在**才声明；**全为 null 直接 return（不声明）** | csharp-sdk `McpServerImpl.cs:105,1198,1208-1215`；`ServerCapabilities.cs:54-55` |
| 本仓库现状 | **未注册任何 resources handler**（无 `ConfigureResources`/`ResourceCollection`/`WithResources`）；MCP 装配仅 `.WithToolsFromAssembly()` → 当前**不声明 `resources` capability**。现有 `screenshot` 工具用 `ImageContentBlock.FromBytes` 内联返回 `CallToolResult` | 本仓 `DotNetDebuggerMcpCmd.cs:383-387`；`Tools/Debugger/DebugScreenshotTool.cs:132-136,200-201` |
| Anthropic 官方 computer-use（demo + best-practices） | **一律内联**：截图 base64 进 `tool_result` 的 `image` 块（`media_type: image/png`），**无 `resource_link`/by-ref**；缩放为「先缩到 API 不会二次缩放的尺寸」，坐标空间 = 截图（模型看到的）像素 | `computer-use-demo/.../loop.py:342-382`、`tools/computer.py:228-302`；`computer-use-best-practices/.../image.py:1-102` |
| browser-use（116k★） | 截图**每步都拍**（无变化检测/无感知哈希），整份 `BrowserStateSummary` 有缓存与失效；送 LLM 前按 `llm_screenshot_size`（默认 None，云服务默认 1400×850）LANCZOS 缩放 + PNG base64 data URL；可交互元素编号 `[k]` **同时**出现在 DOM 文本与**画在截图上的编号框**（`selector_map` 键同源） | `browser_use/browser/watchdogs/screenshot_watchdog.py:27-88`；`browser/session.py:1595-1614,430-432`；`agent/prompts.py:375-401,442-474`；`browser/python_highlights.py:383-399`；`dom/serializer/serializer.py:1030-1033` |

---

## ② MCP 规范：image content / resource_link / resources capability（含规范原文路径）

> 以下均以 spec 仓库最新版 `docs/specification/2026-07-28/`（= SDK 2.2.0 支持的最新协议版本）为准；机器可读定义在 `schema/2026-07-28/schema.ts`（另有 `schema.json`）。
> 官网对应锚点（B 级，便于引用）：`https://modelcontextprotocol.io/specification/2026-07-28/server/tools#image-content` 、`...#resource-links` 、`...#embedded-resources` ；`https://modelcontextprotocol.io/specification/2026-07-28/server/resources#capabilities` 。

### 2.1 tool result 中返回图像（image content block）——规范强制

- **结果容器**（`CallToolResult`）：`content: ContentBlock[]`（非结构化内容）、`structuredContent?`、`isError?`。规范原文路径 `schema/2026-07-28/schema.ts:1809-1838`；说明文档 `docs/specification/2026-07-28/server/tools.mdx:404-416`（「Unstructured content is returned in the `content` field … can contain multiple content items of different types」）。
- **ContentBlock 联合类型**：`TextContent | ImageContent | AudioContent | ResourceLink | EmbeddedResource` —— `schema.ts:2305-2306`。
- **ImageContent 正式定义**（`schema.ts:2340-2361`）：
  - `type: "image"`（必填，字面量）
  - `data: string`（必填，base64 编码图像数据，`@format byte`）
  - `mimeType: string`（必填，「Different providers may support different image types」）
  - `annotations?: Annotations`、`_meta?: MetaObject`（可选）
- **文档示例**（`tools.mdx:427-439`）：`{"type":"image","data":"base64-encoded-data","mimeType":"image/png","annotations":{"audience":["user"],"priority":0.9}}`。
- **约束**：规范**未对 data 长度/像素上限做协议级约束**（上限属 provider/vision 侧，见 ⑤）。规范只声明所有 content 类型都支持可选 `annotations`（`tools.mdx:410-416`）——`audience: ["user"|"assistant"]`、`priority: 0..1`、`lastModified`（`resources.mdx:336-342`）。
- **是否要求 capability**：图像是 tool result 的内联内容，**无任何 server capability 前置**（规范在 tools 一节直接给出，未挂 capabilities 条件）。

### 2.2 `resource_link` / `EmbeddedResource` 定义、字段、与 resources 的关系

- **`ResourceLink`**：`export interface ResourceLink extends Resource { type: "resource_link" }`（`schema.ts:1719-1721`）。因此字段继承 `Resource`（`schema.ts:1441-1474`）+ `BaseMetadata`（`schema.ts:954-969`，**`name: string` 必填**）+ `Icons`（`schema.ts:934-947`）：
  - **必填**：`type: "resource_link"`、`uri: string`、`name: string`
  - **可选**：`title`、`description`、`mimeType`、`annotations`、`size`、`icons`、`_meta`
  - 关键语义（`schema.ts:1710-1712` / `tools.mdx:451-471`）：「A resource that the server is capable of reading」；「tool **MAY** return links to Resources … a URI that can be subscribed to or fetched by the client」；**「Resource links returned by tools are not guaranteed to appear in the results of a `resources/list` request」**。
- **`EmbeddedResource`**：`{ type: "resource", resource: TextResourceContents | BlobResourceContents, annotations?, _meta? }`（`schema.ts:1734-1744`）。即**内容内联进结果**（`TextResourceContents.text` 或 `BlobResourceContents.blob`=base64，`schema.ts:1514-1555`），渲染方式由客户端决定（`schema.ts:1723-1727`）。
- **与 `resources/list` / `resources/read` 的关系**：
  - `resources/list` 返回 `Resource[]`（`resources.mdx:85-132`、`schema.ts:1441-1474`）；`resources/read` 返回 `{ contents: (TextResourceContents | BlobResourceContents)[] }`（`resources.mdx:134-179`、`schema.ts:1229-1230`）。
  - `resource_link` 只携带 `uri` 等元数据，**内容需客户端发 `resources/read` 取**；且规范明说不保证该 uri 出现在 `resources/list`（`tools.mdx:468-471`）。
  - `EmbeddedResource` 内容已在结果里，**不依赖 `resources/read`**；但规范写：**「Servers that use embedded resources **SHOULD** implement the `resources` capability:」**（`tools.mdx:475-476`）。

### 2.3 服务端声明 `resources` capability 的条件

- 规范（`docs/specification/2026-07-28/server/resources.mdx`）：
  - **`resources.mdx:41`**：「Servers that support resources **MUST** declare the `resources` capability」。
  - **`resources.mdx:54-72`**：capability 子字段 `listChanged?`、`subscribe?` 皆可选，可单独/同时/都不带（两者都不带时写 `"resources": {}`）。
  - **`resources.mdx:74-81`**：声明后 **MUST** 响应 `resources/list`（集合可空、可变，但 **MUST NOT** 因连接或同连接其它请求而变）。
  - schema 侧：`ServerCapabilities.resources?: { subscribe?: boolean; listChanged?: boolean }`（`schema.ts:831-855`）。
- **对「返回 `resource_link` 是否要求服务端实现 `resources/read`」的规范裁定**：
  - **规范没有**任何条文要求「返回 resource_link 的服务端必须实现 resources/read」——全 spec 目录检索 `resource_link` 仅出现在 tools/prompts 的示例与 schema 定义处，无 capability 约束（`grep "resource_link" docs/**/*.mdx` 命中 `tools.mdx` / `prompts.mdx` 各版本，均无强制句）。
  - 规范**反向提示**：resource_link 的用途就是「让客户端订阅或 fetch」（`tools.mdx:453-454`），所以**不实现 `resources/read` 时该 link 对客户端是死链**；而 `resources/read` 属于 `resources` capability 的职责，声明 capability 又必须能响应 `resources/list`（`resources.mdx:74`）。**综上：规范不强制，但语义上 by-ref 必须成对提供 `resources/list` + `resources/read`。**
  - 对照：真正的强制/建议只在 `EmbeddedResource` 处（`SHOULD implement the resources capability`，`tools.mdx:476`）。

---

## ③ csharp-sdk 2.2.0 API 与协议版本

> 下表行号均取 **tag `v2.2.0`**（commit `6fa3825973949a9c4f0cd8af344e15a8db09dc35`）。工作树 HEAD（`c40ee04…`）比 2.2.0 新，行号可能漂移，故一律用 `git show v2.2.0:<path>` 实测。

### 3.1 协议版本常量（回答问题「2.2.0 实现哪个协议版本」）

`src/Common/McpProtocolVersions.cs`（v2.2.0）：

| 行 | 内容 |
|---|---|
| `:17` | `public const string July2026ProtocolVersion = "2026-07-28";`（**最新，客户端默认偏好**；该版移除 `initialize` 握手与 `Mcp-Session-Id`） |
| `:25` | `November2025ProtocolVersion = "2025-11-25"`（**仍支持 HTTP session 的最后一版**；初始化/恢复的默认版本） |
| `:28` | `June2025ProtocolVersion = "2025-06-18"` |
| `:31` | `March2025ProtocolVersion = "2025-03-26"` |
| `:34` | `November2024ProtocolVersion = "2024-11-05"` |
| `:39` | `InitializeHandshakeProtocolVersions = [2024-11-05, 2025-03-26, 2025-06-18, 2025-11-25]` |
| `:50` | `PerRequestMetadataProtocolVersions = [2026-07-28]` |
| `:58` | `SupportedProtocolVersions = 二者合并` |

**结论**：`ModelContextProtocol` 2.2.0 **支持 `2024-11-05` ~ `2026-07-28` 共 5 个版本**，默认/最新为 **`2026-07-28`**（`Client.McpClientOptions.cs:55-59`：默认 `null` 表示偏好最新 `2026-07-28`，可回退到 initialize 握手；设成 `2026-07-28` 可禁回退）。

**对应 spec 仓库路径**（以 `2026-07-28` 为准）：

| 文档 | 路径 |
|---|---|
| server/resources | `E:\Code\Projects\Externals\modelcontextprotocol\docs\specification\2026-07-28\server\resources.mdx` |
| server/tools | `...\docs\specification\2026-07-28\server\tools.mdx` |
| basic/index | `...\docs\specification\2026-07-28\basic\index.mdx` |
| 机器可读 schema | `...\schema\2026-07-28\schema.ts` / `schema.json` / `schema.mdx` |

> 对照：`2025-06-18`（当前客户端实际部署最广的版本之一）同结构存在：`docs/specification/2025-06-18/server/{resources,tools}.mdx`、`docs/specification/2025-06-18/basic/index.mdx`。

### 3.2 内容块类型成员与必填项（`src/ModelContextProtocol.Core/Protocol/ContentBlock.cs`, v2.2.0）

| 类型 | 行 | 字段/说明 | 必填 |
|---|---|---|---|
| `abstract class ContentBlock` | `:31` | `Type`(抽象, `:47-48`)、`Annotations?`(`:57-58`)、`Meta(JsonObject)?`(`:66-67`)；`[JsonConverter(typeof(Converter))]`(`:30`) | — |
| `Converter : JsonConverter<ContentBlock>` | `:77` | 多态读写，按 `type` 分派 | — |
| `ImageContentBlock` | `:430` | `Type="image"`(`:466`)；`Data`(`:474-475`, `required ReadOnlyMemory<byte>`, base64 UTF-8 字节，`JsonPropertyName("data")`)；`DecodedData`(`:503-504`, `[JsonIgnore]`)；`MimeType`(`:523-524`, `required string`) | `Data`、`MimeType` |
| `ImageContentBlock.FromBytes` | `:446-451` | 从**未编码字节**构造（内部懒 base64 编码）；`mimeType` 空白抛 `ArgumentException` | — |
| `EmbeddedResourceBlock` | `:651` | `Type="resource"`(`:654`)；`Resource`(`:666-667`, `required ResourceContents`) | `Resource` |
| `ResourceLinkBlock` | `:678` | `Type="resource_link"`(`:681`)；`Uri`(`:686-688`, required)、`Name`(`:693-694`, required)、`Title?`(`:704-705`)、`Description?`(`:723-724`)、`MimeType?`(`:739-740`)、`Size(long)?`(`:748-749`)、`Icons(IList<Icon>)?`(`:757-758`) | `Uri`、`Name` |

> 注意 SDK 的 `ImageContentBlock.Data` 语义是 **base64 文本字节**（不是原始像素）；直接赋 `Data` 需自己 base64；推荐 `FromBytes(原始字节, mime)`（`:441-451`）。本仓 `DebugScreenshotTool.cs:135` 用的正是 `FromBytes`。

### 3.3 何时声明 `resources` capability（`McpServerImpl.cs`, v2.2.0）

- 构造时统一调用：`McpServerImpl.cs:105` → `ConfigureResources(options);`，定义在 `:1198`。
- **声明门槛**（`:1208-1215`）：
  ```
  if (listResourcesHandler is null && listResourceTemplatesHandler is null && readResourceHandler is null &&
      subscribeHandler is null && unsubscribeHandler is null && resources is null &&
      resourcesCapability is null)
      return;                          // ← 全 null：直接 return，不声明 resources capability
  ServerCapabilities.Resources = new();
  ```
- 一旦声明，SDK 会对缺省 handler 补「空实现」（`:1217-1225`：list 返回空、read 抛 `Unknown resource URI`）；`:1336-1337` 落 `ListChanged`/`Subscribe`；`:1339-1367` 注册 `resources/list`、`resources/templates/list`、`resources/read`、`resources/subscribe`、`resources/unsubscribe`（**这些方法是否可被调用取决于该 handler 是否存在**，但 capability 已声明）。
- `ServerCapabilities.Resources` 属性：`Protocol/ServerCapabilities.cs:54-55`（`[JsonPropertyName("resources")] public ResourcesCapability? Resources`）。

### 3.4 服务端返回 image content 的 API 与序列化路径

- **API 形状**：工具返回 `CallToolResult`，其 `Content` 为 `IList<ContentBlock>` —— `Protocol/CallToolResult.cs:32,37-38`（`structuredContent` `:43-44`、`isError` `:65-66`）。
- **`[McpServerTool]` 返回值桥接**（`Server/AIFunctionMcpServerTool.cs`，v2.2.0）：
  - `:296` `Content = [aiContent.ToContentBlock()]`
  - `:309` 字符串 → `TextContentBlock`
  - `:313` 直接返回 `ContentBlock` 则原样
  - `:321` 返回 `IEnumerable<ContentBlock>`
  - `:703-709` 集合路径 `contentList.Add(item.ToContentBlock())`
  - 即 `Microsoft.Extensions.AI` 的 `DataContent`（官方 sample `samples/EverythingServer/Tools/TinyImageTool.cs:11-15` 返回 `new DataContent(MCP_TINY_IMAGE)`）会被 `ToContentBlock()` 转成 `ImageContentBlock`。
- **序列化路径**：`CallToolResult.Content` 经 `McpJsonUtilities.JsonContext` + `ContentBlock.Converter`（`ContentBlock.cs:30,77`）序列化为 `{"type":"image","data":...,"mimeType":...}`，与 ② 规范 JSON 一致。
- **官方样例**：`samples/EverythingServer/Tools/TinyImageTool.cs:10-18`（工具 `getTinyImage` 返回 `[TextContent, DataContent(data:image/png;base64,…), TextContent]`）。

---

## ④ 本仓库现状（是否已有 resources）

- **未注册任何 resources handler**。全 `src/**/*.cs` 检索 `ConfigureResources` / `ResourceCollection` / `ListResourcesHandler` / `ReadResourceHandler` / `McpServerResource` / `WithResources` / `McpServerResource` / `WithPrompts` / `McpServerPrompt` —— **全部 0 命中**。
- MCP 装配只有工具面：`src/DotNetDebuggerMcp/DotNetDebuggerMcpCmd.cs:383-387`：
  ```csharp
  builder.Services.AddMcpServer(o => { o.ServerInstructions = serverInstructions; })
      .WithStdioServerTransport().WithToolsFromAssembly();
  ```
  → 按 ③.3 的 SDK 门槛，**当前会话不声明 `resources` capability**。
- **现有 screenshot 返回形态**（内联图片块，无双轨）：
  - `src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs:132` `new List<ContentBlock> { new TextContentBlock { Text = header } }`
  - `:133-135` `if (attachImage) content.Add(ImageContentBlock.FromBytes(result.Image, $"image/{format}"));`
  - `:136` `return new CallToolResult { Content = content };`
  - `:120-124` ≥2MB 或显式 `filePath` 时**落盘并在文本头写路径**（`已落盘: …`），此时不附图片块；错误路径 `:200-201` 仅文本。
  - 参数/行为契约见 root `AGENTS.md` 与 spec `docs/planning/specs/2026-09-22-screenshot-tool-design.md` §3.3。

---

## ⑤ Anthropic computer-use 官方参考（截图生命周期/缩放/坐标）

> 两个官方目录：`computer-use-demo/`（Docker+X11 桌面，**Anthropic 官方 quickstart**）与 `computer-use-best-practices/`（macOS 本地版，含 vision 尺寸参考算法）。

### 5.1 `computer-use-demo`：截图如何产生 / 缩放 / 作为 tool_result 返回

- **截图产生**：`computer_use_demo/tools/computer.py:228-242` `screenshot()` → `_capture()`（`:257-265`：优先 `gnome-screenshot`，回退 `scrot`）→ **再 `convert {path} -resize {x}x{y}! {path}` 缩放**（`:232-236`，`!` 强制精确尺寸）→ 读文件 base64（`:239-241`）。返回 `ToolResult`（`tools/base.py:23-30`：`output`/`error`/`base64_image`/`system`）。
- **缩放尺寸/上限**（`computer.py:56-62`）：`MAX_SCALING_TARGETS = { XGA:1024×768, WXGA:1280×800, FWXGA:1366×768 }`；`scale_coordinates`（`:279-302`）按长宽比误差 `< 0.02` 选档、只在目标 `< self.width` 时缩放；`_scaling_enabled = True`（`:100`）。
- **坐标空间定义**：`ComputerToolOptions.display_width_px/height_px = scale_coordinates(COMPUTER, self.width, self.height)`（`:102-111`），即**模型发出的坐标以「截图（缩放后）像素」为准**；`screenshot_size()`（`:244-250`）明确注释「This is the coordinate frame the model emits coordinates in」。API→物理按 `/factor`（`:296-300`），并做越界校验（`:297-298`）。
- **作为 tool_result 返回**：`computer_use_demo/loop.py:342-382` `_make_api_tool_result` → 文本块 + **图片块** `{"type":"image","source":{"type":"base64","media_type":"image/png","data":base64_image}}`（`:363-372`），整体 `{"type":"tool_result", "content":[...], "tool_use_id":...}`（`:374-382`）。**无 `resource_link`/by-ref 形态。**
- **超大屏**：README `computer-use-demo/README.md:167-173`：**不推荐发送高于 XGA/WXGA 的截图**（会触发 API 侧二次 resize，降低精度/变慢）；推荐 XGA 1024×768；更高分辨率**先缩到 XGA**再按比例映射坐标回原始分辨率；更低分辨率/小设备则**加黑边 padding 到 1024×768**。
- **是否有 `zoom`**：有。`ComputerTool20251124`（`computer.py:428-462`）`options.enable_zoom = True`（`:431-433`）；`zoom(region)`（`:475-509`）：在**物理分辨率**抓取上按 region 裁剪、再 `-resize {frame_w}x{frame_h}`（保比例 fit）输出；注释明确 **zoom 图本身不是坐标空间，后续坐标仍用整屏截图像素**（`:482-483,452-453`）。`ComputerToolset20260801` 把 `zoom` 列为常驻成员（`:517-537,558-559`）。
- **截图历史裁剪**：`loop.py:244-290` `_maybe_filter_to_n_most_recent_images`——只保留最近 N 张 tool_result 图片、按 `min_removal_threshold` 成块删除以保护 prompt cache（说明截图为「递减价值」）。

### 5.2 `computer-use-best-practices`：官方截图/vision/坐标建议（逐条）

- **视觉编码上限**（`computer_use/image.py:1-15` docstring）：「The API's vision encoder tiles images into 28x28 patches and caps both the long edge (1568 px) and the total tile count (1568).」若超限，服务端会**再缩一次**，模型随后在「我们没见过的坐标空间」出坐标，导致**系统性点击漂移（16:10 MacBook 上约 14%）**。→ 做法：**预先缩到同时满足两约束的最大尺寸，并记住该尺寸反算坐标**；`target_image_size` 是「API 参考算法的直接移植」（`:14`）。
- **目标尺寸算法**（`image.py:38-75`）：`target_image_size(w,h)` 长边 ≤ `max_edge_px` 且 tile 数 ≤ `max_tokens`，二分保比例；已合规则原样返回。
- **编码**（`image.py:78-102`）：`resize_and_encode` → 若需缩放用 **LANCZOS**（`:91`）、转 RGB（`:92-93`）、**JPEG**（`:95`），低于 `min_screenshot_bytes` 抛 `ScreenshotTooSmall`（`:97-101`）。
- **阈值常量**（`constants.py:114-130`）：`jpeg_quality=75`（`:116`，注释对齐桌面 App Swift 编码器）、`min_screenshot_bytes=1024`（`:120`）、`px_per_token=28`、`max_edge_px=1568`、`max_tokens=1568`（`:125-127`）、`browser_viewport=(1456,819)`（`:130`，选择「截图天然落在 vision 预算内、无需 resize」）。
- **坐标换算**（`computer_use/tools/computer.py:255-275`）：`_scale_to_screen = round(coord * screen / sent)` 并 clamp（`:255-262`）、`_scale_to_image` 反向（`:264-268`）；工具输出坐标用**模型自己发出的 image-space 数值**回显并附 `WxH` 后缀（`:270-275`）。`take_screenshot`（`:277-294`）先处理 Retina（物理像素截图 → 逻辑像素），再 `resize_and_encode`，`meta` 记 `sent_size`/`screen_size`。
- **系统提示的坐标纪律**（`constants.py:336-344`）：「Coordinates you emit must refer to the most recent screenshot you were shown.」「batch 内坐标都指 batch 调用前那张截图。」
- **zoom**（`:428-453`）：`zoom` 用**最近一次截图的坐标空间**裁剪物理截图 → 再 `resize_and_encode(crop, min_bytes=0)`，输出提示「Subsequent coordinates still refer to the full `{sent_w}x{sent_h}` screenshot, not this crop.」（`:451-453`）。
- **本地化/坐标可视化**（`README.md:198-206`）：`dev_ui/localization_demo` 演示「resize → 模型给 resized 空间 (x,y) → 用 `x * orig_w / sent_w` 反算原始图」的完整管道；`README.md:40-42` 把「正确截图尺寸 → 模型看到的就是你发的像素 → 点击坐标才准」列为核心特性。
- **图像经济**（`README.md:350-353`）：每张截图约 **1,500 input tokens**；30 轮后图像历史约 45k tokens 且每次重发；配合 prompt caching + image pruning（`:355-422`，`cfg` 默认 `image_prune_strategy="interval"`、`image_prune_min=3`、`image_prune_interval=40`，`constants.py:148-157`）。

### 5.3 `browser-use-demo`（官方 browser 版）：截图与元素编号/ref 如何绑定

- **截图**：`browser_use_demo/tools/browser.py:352-372` `_take_screenshot` → Playwright `page.screenshot(path, full_page=False)` → 读文件 base64 → `ToolResult(base64_image=…)`。`zoom` 截图在 `:374-395`（带 clip 区域）；navigate 后自动附截图（`:408-411`）。
- **元素编号/ref**：**ref 挂在 DOM 文本树上，不画在截图上**。`_read_page`（`browser.py:814-849`）执行 `browser_dom_script.js` 返回 YAML 树；`browser_dom_script.js:357-384` 为每个元素分配 `ref_<n>`（全局 `window.__claudeElementMap` + `WeakRef`，`:370-372`）并写成 `- role "name" [ref=ref_N] id=… href=…`（`:384-394`）。
- **绑定方式**：点击等动作**可选** `coordinate` 或 `ref`（`browser.py:63-68`），`ref` 优先（`:763` 注释「refs are more reliable」），`ref` 经 `browser_element_script.js` 在页面里解析元素位置再点击（`:523-543`）。
- **坐标**：`tools/coordinate_scaling.py:1-33` 内建「Claude 视觉基准尺寸」表（`(16,9):1456×819`、`(1,1):1092×1092`、`(4,3):1268×951` …），`scale_coordinates`（`:106-153`）把模型坐标映射到真实 viewport，带 `0.02` 比例容差与 `1.2×` 阈值兜底；`browser.py:416-445` 调用它。
- **截图与编号的关系**：截图的定位是「**视觉确认**，不是读内容」（`browser.py:163-165`：「screenshot: Take a visual screenshot (only for visual confirmation, not for reading content)」）；结构/内容走 `read_page`/`get_page_text`。**没有把 ref 编号绘制到截图上的机制**（与 browser-use 库不同，见 ⑥）。

---

## ⑥ browser-use 截图与元素绑定

> clone `E:\Code\Projects\Externals\browser-use`，Python 版 computer-use agent。

### 6.1 截图生命周期：何时截 / 缓存 / 变化检测

- **何时截**：Agent **每个 step 都取一次带截图的浏览器状态**——`browser_use/agent/service.py:1094-1099` `get_browser_state_summary(include_screenshot=True…)`，注释「Always take screenshots for all steps」。
- **底层捕获**：`browser_use/browser/watchdogs/screenshot_watchdog.py:27-88` `on_ScreenshotEvent`：
  - 校验焦点 target 是 page/tab，否则回退任意 page（`:40-51`）
  - **截图前先 `remove_highlights()`**，避免高亮框出现在图里（`:55-62`）
  - CDP `Page.captureScreenshot`，`format: "png"`、`captureBeyondViewport=full_page`，可选 `clip`（`:64-83`）
  - **本 watchdog 不做缓存、不做变化检测**——纯按需抓取。
- **缓存**：缓存的是**整份浏览器状态摘要**，不是单张图。`browser/session.py:1595-1614` `get_browser_state_summary(cached=...)`：命中缓存且「有交互元素」时直接返回；但**若需截图而缓存里没有截图，则强制重取**（`:1605-1608`）；0 交互元素也重取（`:1612-1614`）。缓存写入在 `dom_watchdog.py:488-506`（`self.browser_session._cached_browser_state_summary = browser_state`）；失效在 `session.py:1241-1243`（focus 变化清空）+ `default_action_watchdog.py:523-525,556-564`（动作后清 DOM 缓存）。
- **变化检测**：**未找到**任何感知哈希/逐像素 diff/截图内容哈希（`grep perceptual|image_hash|screenshot_hash|changed_pixels` 在 `browser_use/browser/**` 0 命中）。等价机制只有上面的「状态摘要缓存 + 动作后失效」。
- 状态构建：`dom_watchdog.py:381-388` 并行启动 DOM 构建 + 干净截图任务；`:404-419` 截图有剩余时间预算；`:488-506` 汇总进 `BrowserStateSummary`；`:508-510` 记录原始 viewport 供坐标换算。

### 6.2 送 LLM 前如何缩放/编码

- **缩放配置**：`browser/session.py:429-432` `llm_screenshot_size: tuple[int,int] | None = Field(default=None, description='Target size … Coordinates from LLM will be scaled back to original viewport size.')`。云服务（`beta/service.py:4320-4325`）未显式指定时**默认 `(1400, 850)`**；本地默认为 `None`（不缩放）。
- **缩放实现**：`agent/prompts.py:375-401` `_resize_screenshot`：按 `llm_screenshot_size` 用 **PIL LANCZOS** 缩放（`:395`）、**保存为 PNG**（`:397`）、base64（`:398`）；失败则用原图（`:399-401`）。
- **消息组装**：`agent/prompts.py:442-474`：过滤 4px 占位图（`:442`），按序给截图加文本标签「Current screenshot:」/「Previous screenshot:」（`:452-460`），逐张缩放（`:463`）后以 **data URL** `data:image/png;base64,{...}` + `media_type='image/png'` + `detail` 档位塞入 `ContentPartImageParam`（`:466-474`）。
- **Anthropic 序列化**：`llm/anthropic/serializer.py:78-95`：data URL → `ImageBlockParam(source=Base64ImageSourceParam(data, media_type, type='base64'))`；非 data URL → `URLImageSourceParam`。`_parse_base64_url`（`:45-61`）只认 `image/jpeg|png|gif|webp`，不识别则回落 `image/jpeg`。
- **坐标回算**：`tools/service.py:611-626` `_convert_llm_coordinates_to_viewport`：`actual = llm / llm_size * original_viewport`（`:617-619`）。

### 6.3 是否把「可交互元素」与截图绑定编号

- **是，双重编号且同源**：
  - **DOM 文本**：`dom/serializer/serializer.py:1030-1033` 给可交互节点前缀 `*[{selector_index}]`；`selector_index` 与 `_selector_map` 在 `:755-756` 分配（`:649-658` 处理 backend id 冲突）。
  - **画到截图上**：`browser/python_highlights.py:383-399` `process_element_highlight` 用 **selector_map 的键**作为 `index_text` 绘制编号框（注释：「Use the selector-map key because that is the index shown to the model.」），框坐标按 `device_pixel_ratio` 从 CSS 像素换到设备像素（`:357-362`）；总入口 `create_highlighted_screenshot`（`:405-452`）逐元素画框后回写 PNG base64。
  - 开关：`browser/profile.py:697-703` `highlight_elements=True`（默认开）、`dom_highlight_elements=False`、`filter_highlight_ids=True`（有意义文本 <3 字符才显示编号，见 `python_highlights.py:386-394`）。
- 即：**模型在同一「状态」里同时看到带 `[k]` 的 DOM 文本和带 `k` 编号框的截图，两处 `k` 是同一 `selector_map` 键**——这是本项目「语义桥/SoM」可参考的最强范式。

---

## ⑦ 对本项目 by-ref vs 内联决策的确定性结论（能定什么/不能定什么）

### 7.1 规范/源码能确凿定下的（A/B 级）

1. **内联 image content 是规范一等公民，且不挂任何 server capability 前置**——`CallToolResult.content: ContentBlock[]`，`ImageContent{type,data,mimeType}` 直接可用（`schema.ts:1809-1838,2305-2306,2340-2361`；`tools.mdx:404-439`）。**因此「默认内联」在协议上是零风险的默认选择。**
2. **`resource_link` 不自带能力、也不要求 `resources/read`（规范未强制）**——它只是「可被订阅/ fetch 的 URI」（`tools.mdx:453-454`），且不保证出现在 `resources/list`（`tools.mdx:468-471`；`schema.ts:1712`）。规范里唯一与 resources capability 挂钩的是 `EmbeddedResource` 的 **SHOULD**（`tools.mdx:475-476`），与本项目的 link 方案无关。
3. **但 by-ref 在 SDK 上必然要新增 resources 面**：`McpServerImpl.ConfigureResources` 全 handler 为 null 时**直接不声明 `resources` capability**（`:1208-1215`）；不声明 capability，客户端发 `resources/read` 会得不到服务端支持 → **resource_link 变死链**。所以「用 by-ref」= 「必须同时实现 `resources/list` + `resources/read` + 声明 capability」+ 同步握手/README 触发条件（本仓铁律）。
4. **本仓库当前确实没有 resources 面**（`DotNetDebuggerMcpCmd.cs:383-387` 仅 `WithToolsFromAssembly`；全 `src` 检索 0 命中），现有 `screenshot` 是**纯内联** `ImageContentBlock.FromBytes` + ≥2MB 落盘返回文本路径（`DebugScreenshotTool.cs:120-136,200-201`）。
5. **Anthropic 官方两个 computer-use 参考实现 + browser-use，全部内联**：computer-use-demo `loop.py:363-372`（`image` 块 `media_type: image/png`）、best-practices `image.py:78-102`（JPEG base64）、browser-use `prompts.py:466-474`（PNG data URL）。**没有任何一份官方参考走 `resource_link`/by-ref。**
6. **官方对截图尺寸有明确共识**（可作为本项目默认缩放依据）：API vision 编码器 28×28 tile、长边 ≤1568px、tile 数 ≤1568；超限会被二次 resize 导致坐标漂移（best-practices `image.py:1-15`、`constants.py:124-130`）；computer-use-demo 更保守，推荐 XGA/WXGA 上限（`README.md:167-173`）；browser 官方基准 1456×819（`coordinate_scaling.py:17-18`）。

### 7.2 本轮**不能**确定的（需补调研 / 客户端源码，等级 C）

1. **各 MCP 客户端是否真的会 fetch `resources/read`、是否渲染 `resource_link`** —— 本轮**未核实任何客户端源码**（Claude Desktop / Claude Code / VS Code / Cursor）。此前 README §决策点 2 所依据的「Claude Desktop 拒绝混合内容、Claude Code 不 fetch」来自低星网页与 issue，**本文件不为其背书**，也不推翻；要定 by-ref 必须先做客户端源码核实。
2. **by-ref 与「≥2MB 落盘返回路径」哪个对 agent 更可用** —— 依赖 1 的客户端行为，规范不裁定。
3. **默认缩放上限取 1568 还是沿用 2000** —— 属另一份 `02-image-economy.md` 的图像经济取舍；本文件只提供官方 vision 侧的 1568 依据，不替代该决策。
4. **`data` 长度/像素的协议级硬上限** —— 规范**未找到**；只有 provider/vision 侧建议（⑤），不是 MCP 协议约束。

### 7.3 建议（仅基于上述确凿证据，不越界）

- **默认继续内联**（证据 1/4/5）：协议零前置、现有实现已是内联、官方参考全内联。
- **by-ref 若要上，则是一次「新增 resources 能力族」的完整改动**（证据 3）：需实现 `resources/list`+`resources/read`、声明 capability、按铁律同步根 `README.md` 与握手 `AppText.HandshakeFeatureIntro`（新增能力族必须补触发条件），并扩回归断言；且其价值最终取决于 7.2-1 的客户端行为核实。
- **`resource_link` 可作为「大图/存档」的可选替代文本路径**（不改变默认内联），但只有补齐 resources 面后才可用（证据 2/3）。

---

## ⑧ 来源（各 clone commit + 文件:行号清单）

### 8.1 四个 clone 的 commit（`git -C <path> log -1 --format="%H | %ad | %s"`）

| clone | commit | 日期 | subject |
|---|---|---|---|
| `E:\Code\Projects\Externals\modelcontextprotocol` | `ab3a39c13bd23be691c2760e1c6c5c15a64582e1` | Thu Sep 24 02:39:54 2026 +0100 | Merge pull request #3302 from corykinney/main |
| `E:\Code\Projects\Externals\csharp-sdk`（工作树 HEAD） | `c40ee044fd415c70da5176c749cb5ef02f2b59f6` | Fri Sep 18 20:07:30 2026 -0400 | fix(client): skip the SSE fallback when the server/discover probe is rejected with 400/404 (#1855) |
| `E:\Code\Projects\Externals\csharp-sdk`（**引用版本 tag `v2.2.0`**） | `6fa3825973949a9c4f0cd8af344e15a8db09dc35` | Thu Aug 13 01:23:07 2026 -0700 | Release v2.2.0 (#1813) |
| `E:\Code\Projects\Externals\claude-quickstarts` | `dee71163217524eed07d79d00ffea5a7d02cedda` | Thu Sep 24 17:44:54 2026 -0400 | Daily brief quickstart: tag the agent with anthropic_cookbook metadata (#501) |
| `E:\Code\Projects\Externals\browser-use` | `4cbe921673b48a488f5415d9159249afd12a625b` | Sat Sep 26 00:29:32 2026 -0700 | Fix Actor input semantics and add CDP primitives (#5889) |

> 本仓现状：`ModelContextProtocol` 版本 `2.2.0`（`src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj:45`）；包版本 `2.1.0`（`.csproj:21`）。

### 8.2 spec 仓库（`E:\Code\Projects\Externals\modelcontextprotocol`）

- `docs/specification/2026-07-28/server/resources.mdx`：`:39-82`（capabilities）、`:74-81`（MUST 响应 resources/list）、`:85-132`（resources/list）、`:134-179`（resources/read）、`:181-229`（templates）、`:300-334`（Resource / Resource Contents）、`:336-342`（Annotations）
- `docs/specification/2026-07-28/server/tools.mdx`：`:404-416`（Tool Result / content types / annotations）、`:427-439`（Image Content）、`:451-471`（Resource Links）、`:473-494`（Embedded Resources）
- `schema/2026-07-28/schema.ts`：`:793-855`（ServerCapabilities）、`:934-969`（Icons/BaseMetadata）、`:1229-1230`（ReadResourceResult）、`:1441-1474`（Resource）、`:1514-1555`（ResourceContents/Text/Blob）、`:1710-1721`（ResourceLink）、`:1723-1744`（EmbeddedResource）、`:1809-1838`（CallToolResult）、`:2305-2306`（ContentBlock union）、`:2340-2361`（ImageContent）、`:2371+`（AudioContent）
- 其他版本对照：`docs/specification/2025-06-18/server/{resources,tools}.mdx`、`schema/2025-06-18/schema.ts`（`resource_link` 示例 `2025-06-18/server/tools.mdx:254`）；`docs/specification/{2024-11-05,2025-03-26,2025-11-25,2026-07-28,draft}/`
- 官网锚点（B）：`https://modelcontextprotocol.io/specification/2026-07-28/server/tools`（`#image-content`/`#resource-links`/`#embedded-resources`）、`.../server/resources#capabilities`、`.../basic/index`

### 8.3 csharp-sdk（tag `v2.2.0`，行号用 `git show v2.2.0:<path>` 实测）

- `src/Common/McpProtocolVersions.cs`：`:17,25,28,31,34,39,50,58`
- `src/ModelContextProtocol.Core/Protocol/ContentBlock.cs`：`:30,31,47-48,57-58,66-67,77`、`:413-426`（TextContentBlock）、`:430,446-451,466,474-475,503-504,523-524`（ImageContentBlock）、`:651,654,666-667`（EmbeddedResourceBlock）、`:678,681,686-688,693-694,704-705,723-724,739-740,748-749,757-758`（ResourceLinkBlock）
- `src/ModelContextProtocol.Core/Protocol/ServerCapabilities.cs`：`:54-55`
- `src/ModelContextProtocol.Core/Protocol/CallToolResult.cs`：`:32,37-38,43-44,65-66`
- `src/ModelContextProtocol.Core/Server/McpServerImpl.cs`：`:105,1198,1208-1215,1217-1225,1336-1337,1339-1367`
- `src/ModelContextProtocol.Core/Server/AIFunctionMcpServerTool.cs`：`:296,309,313,321,703-709`
- `samples/EverythingServer/Tools/TinyImageTool.cs`：`:10-18`（`DataContent` 返回图片）
- `src/ModelContextProtocol.Core/Client/McpClientOptions.cs`：`:55-59`（默认偏好 `2026-07-28`）

### 8.4 本仓库

- `src/DotNetDebuggerMcp/DotNetDebuggerMcpCmd.cs`：`:383-387`（仅 `.WithToolsFromAssembly()`，无 resources）
- `src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs`：`:120-124`（≥2MB/指定路径落盘）、`:132-136`（`ImageContentBlock.FromBytes` 内联）、`:200-201`（错误仅文本）
- `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`：`:21`（`<Version>2.1.0`）、`:45`（`ModelContextProtocol` `2.2.0`）
- 检索结论：`src/**` 无 `ConfigureResources`/`ResourceCollection`/`ListResourcesHandler`/`ReadResourceHandler`/`McpServerResource`/`WithResources`/`WithPrompts`/`McpServerPrompt`

### 8.5 `claude-quickstarts`（`computer-use-demo/`）

- `computer_use_demo/tools/computer.py`：`:46`（`zoom` 加入 action）、`:56-62`（MAX_SCALING_TARGETS）、`:99-100`（`_screenshot_delay=2.0`,`_scaling_enabled=True`）、`:102-111`（options 坐标空间）、`:228-242`（screenshot + resize）、`:244-250`（screenshot_size = 模型坐标帧）、`:257-265`（capture）、`:279-302`（scale_coordinates）、`:428-462`（20251124 + zoom action）、`:475-509`（zoom 裁剪+fit）、`:517-537`（toolset 成员含 zoom）、`:543-566`（toolset 说明：坐标仍为 screenshot pixels）
- `computer_use_demo/tools/base.py`：`:23-30`（`ToolResult.base64_image`）
- `computer_use_demo/loop.py`：`:244-290`（图像裁剪，保 cache）、`:342-382`（构造 tool_result 图片块）
- `computer-use-demo/README.md`：`:152-173`（WIDTH/HEIGHT、XGA/WXGA 建议）

### 8.6 `claude-quickstarts`（`computer-use-best-practices/`）

- `computer_use/image.py`：`:1-15`（28×28 tile、1568 长边/1568 tile、二次 resize 漂移 ~14%）、`:38-75`（`target_image_size`）、`:78-102`（`resize_and_encode`，LANCZOS+JPEG+min_bytes）
- `constants.py`：`:114-130`（jpeg_quality=75、min_screenshot_bytes=1024、px_per_token=28、max_edge_px=1568、max_tokens=1568、browser_viewport=1456×819）、`:148-157`（image pruning 默认）、`:332-347`（SYSTEM_PROMPT 坐标纪律）
- `computer_use/tools/computer.py`：`:255-275`（坐标双向换算+回显）、`:277-294`（take_screenshot）、`:428-453`（zoom）
- `README.md`：`:40-42`、`:198-206`、`:304-316`、`:348-422`

### 8.7 `claude-quickstarts`（`browser-use-demo/`）

- `browser_use_demo/tools/coordinate_scaling.py`：`:12-33`（Claude 视觉基准尺寸表）、`:106-153`（坐标缩放）
- `browser_use_demo/tools/browser.py`：`:63-68`（ref/coordinate 参数）、`:163-165`（截图仅视觉确认）、`:352-372`（截图 base64 PNG）、`:814-849`（read_page → DOM 树+refs）、`:416-445`（坐标缩放调用）
- `browser_use_demo/browser_tool_utils/browser_dom_script.js`：`:357-384`（`ref_<n>` 分配与 YAML 输出）

### 8.8 `browser-use`

- `browser_use/browser/watchdogs/screenshot_watchdog.py`：`:27-88`（CDP 截图，`:55-62` 先移除高亮）、`:64-83`（png + clip）
- `browser_use/browser/session.py`：`:429-432`（`llm_screenshot_size` 默认 None）、`:663` / `:1241-1243`（缓存初始化/失效）、`:1595-1614`（状态摘要缓存/强制重取）
- `browser_use/browser/watchdogs/dom_watchdog.py`：`:381-388`（并行截图任务）、`:404-419`、`:488-510`（构建+缓存状态、记录原始 viewport）
- `browser_use/agent/service.py`：`:1094-1099`（每步必截）
- `browser_use/agent/prompts.py`：`:375-401`（`_resize_screenshot` LANCZOS+PNG）、`:442-474`（标签 + data URL 图片块）
- `browser_use/llm/anthropic/serializer.py`：`:45-61`（媒体类型白名单）、`:78-95`（`Base64ImageSourceParam`）
- `browser_use/tools/service.py`：`:611-626`（LLM 尺寸 → viewport 坐标回算）
- `browser_use/browser/python_highlights.py`：`:357-362`（CSS→设备像素）、`:383-399`（编号=selector_map 键）、`:405-452`（`create_highlighted_screenshot`）
- `browser_use/browser/profile.py`：`:697-703`（highlight 开关默认值）
- `browser_use/dom/serializer/serializer.py`：`:649-658`（索引分配）、`:755-756`（selector_map 写入）、`:1030-1033`（`*[k]` 前缀）
- `browser_use/beta/service.py`：`:4320-4325`（云服务默认 `llm_screenshot_size=(1400,850)`）
- 变化检测检索：`grep perceptual|image_hash|screenshot_hash|changed_pixels` over `browser_use/browser/**` → **0 命中（未找到）**
