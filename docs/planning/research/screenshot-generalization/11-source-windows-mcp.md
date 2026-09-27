# 11 · Windows-MCP 源码深挖（CursorTouch/Windows-MCP 7.3k★, MIT, 59e77f6）

> **来源等级：A 源码核实**（本地 clone `Externals/Windows-MCP` @ `59e77f6`，`CursorTouch/Windows-MCP` 7.3k★ MIT）。生成日期 2026-09-27。
> 本文件所有 `file:line` 均按该 clone 实测行号（已用 `read` + `Get-Content .Count` 双重核对）。凡源码中没有的能力，一律写「未在源码中找到」，不做经验补全。

## ① 结论速览

| 维度 | 做法 | 证据 file:line |
|---|---|---|
| 工具形态 | 两个工具：`Snapshot`（语义树 + 可选截图）与 `Screenshot`（纯截图，跳过 UI 树抽取） | `src/windows_mcp/tools/snapshot.py:25-47`、`74-120` |
| 寻址 | 仅 `display`（0 基活动显示器索引）与 `region`（虚拟桌面像素 xyxy）；**无 hwnd / 窗口标题 / 前台窗口寻址**；region 优先于 display | `tools/snapshot.py:44-45,90-91`；`desktop/service.py:1110-1212` |
| 返回形态 | 返回 `fastmcp.utilities.types.Image`（MCP image content，PNG）；**不落盘、无 `resource_link`、无大小阈值回退**；文本块在前、图片块在后 | `tools/_snapshot_helpers.py:13,110-113,236-239` |
| 图像经济 | 双轴独立比例取 **min**，硬上限 1920×1080 + 环境变量 `WINDOWS_MCP_SCREENSHOT_SCALE∈[0.1,1.0]`；**仅 PNG**；**无 webp/jpeg/灰度/quality/diff/录制** | `_snapshot_helpers.py:21,24-34,91`；`desktop/service.py:241-259` |
| 语义桥 | UIA 树 → 语义树 + 可交互/可滚动元素清单（0 基整数 `label` = 列表下标），图形编号与 `label` 对齐；**无 OCR** | `tree/views.py:185-209`；`desktop/service.py:651-663,1273-1304` |
| 坐标空间 | 虚拟桌面像素；截图被降采样时以元数据回传 `Coordinate Scale = 1/scale` 要求 agent 乘回去 | `_snapshot_helpers.py:186-210` |
| 旧帧护栏 | **无** capture/snapshot id、无代际号；`label` 只是「当前列表下标」 | `desktop/service.py:273-290,651-663` |
| 缓存/失效 | 单槽 `Desktop.desktop_state`，每次 `get_state` 整体覆盖；无 TTL/无代际/无内容哈希；截图后端有进程级降级黑名单 | `desktop/service.py:82,273-290`；`desktop/screenshot.py:280-285` |
| 安全 | HTTP：Bearer/OAuth + IP allowlist + TrustedHost + SSRF 防护；工具可裁剪；**截图无擦除/模糊，密码字段 value 未脱敏** | `infrastructure/auth.py:23-79`；`security.py:25-65`；`tree/service.py:601-609` |

## ② screenshot 参数面（逐参数表）

Windows-MCP 没有单一 `screenshot` 工具，而是 `Snapshot` 与 `Screenshot` 两个；两者共用同一底层 `capture_desktop_state`（`tools/_snapshot_helpers.py:52-140`），差别只在内部固定的开关与是否附 UI 树。**全部参数都有默认值，无必填参数。**

### Snapshot（`tools/snapshot.py:37-47`，工具名 `Snapshot`，`:26`）

| 参数 | 类型 | 默认值 | 说明（取自工具 description，`tools/snapshot.py:27`） | 必填 |
|---|---|---|---|---|
| `use_vision` | `bool \| str` | `False` | 是否附截图（含 cursor highlight） | 否 |
| `use_dom` | `bool \| str` | `False` | 浏览器取网页 DOM 元素替代浏览器 UI（Chrome/Edge/Firefox） | 否 |
| `use_annotation` | `bool \| str` | `True` | 在截图上画彩色框标注元素（默认开） | 否 |
| `use_ui_tree` | `bool \| str` | `True` | 是否抽取可交互 / 可滚动元素树；关掉可加速 | 否 |
| `width_reference_line` | `int \| None` | `None` | 叠加竖向参考线数量（须与 height 同时给才生效） | 否 |
| `height_reference_line` | `int \| None` | `None` | 叠加横向参考线数量 | 否 |
| `display` | `list[int] \| None` | `None` | 0 基活动显示器索引，如 `[0]`/`[0,1]`；省略=默认全桌面 | 否 |
| `region` | `list[int] \| str \| None` | `None` | 虚拟桌面像素 `[left,top,right,bottom]`；优先于 display；越界报错 | 否 |
| `ctx` | `Context` | `None` | fastmcp 框架注入，非 agent 参数 | 否 |

### Screenshot（`tools/snapshot.py:86-92`，工具名 `Screenshot`，`:75`）

| 参数 | 类型 | 默认值 | 说明 | 必填 |
|---|---|---|---|---|
| `use_annotation` | `bool \| str` | `False` | 是否画元素框（默认**不画**，给干净图） | 否 |
| `width_reference_line` | `int \| None` | `None` | 同上 | 否 |
| `height_reference_line` | `int \| None` | `None` | 同上 | 否 |
| `display` | `list[int] \| None` | `None` | 同上 | 否 |
| `region` | `list[int] \| str \| None` | `None` | 同上 | 否 |
| `ctx` | `Context` | `None` | 框架注入 | 否 |

**内部固定**：`Screenshot` 调 `capture_desktop_state(use_vision=True, use_dom=False, use_annotation=<参数>, use_ui_tree=False, ...)`（`tools/snapshot.py:95-106`）。

**未暴露为工具参数的旋钮**（源码里没有对应参数）：
- 编码格式 / `quality` / `format`：**未在源码中找到**（只有内部写死 PNG）。
- 缩放因子 `scale`：只由环境变量 `WINDOWS_MCP_SCREENSHOT_SCALE` 控制（`_snapshot_helpers.py:24-34`），非 MCP 参数。
- 落盘路径 `filePath`：**未在源码中找到**。
- `window` / `hwnd` / `monitor`（单显示器编号）参数：**未在源码中找到**（只有 0 基 `display` 数组）。
- 字符串容错：`display`/`region` 支持 Claude Desktop 把数组序列化成 JSON 字符串的情形，由 `_as_region` 反解析（`_snapshot_helpers.py:46-49`）；布尔参数支持字符串 `"true"`（`:42-43`）。

## ③ 寻址（显示器 / 窗口 / 前台 / 区域 / hwnd / 负坐标）

### 3.1 全屏（默认）
`display=None` → `parse_display_selection` 返回 `None` → `capture_rect=None` → 后端抓**整个虚拟屏**。证据：`_snapshot_helpers.py:75`；`desktop/service.py:122-131`；`desktop/screenshot.py:220`（Pillow `all_screens=True`）、`:259`（mss 用 `monitors[0]`）。

### 3.2 显示器选择（0 基活动显示器索引）
- 解析：`parse_display_selection`（`desktop/service.py:1110-1137`）接受 `int` 或数组；拒绝 bool；要求非负整数、去重；空返回 `None`。
- 语义：`_active_display_indices`（`uia/core.py:788-805`）用 `EnumDisplayDevicesW` 枚举，**只对 `DISPLAY_DEVICE_ATTACHED_TO_DESKTOP` 的显示设备按枚举顺序赋 0..N**；`GetDisplays`（`uia/core.py:808-885`）把 `szDevice` 名映射到该 index，读不到则用 `next_fallback_index()`；结果按 index 排序。即「0 基活动显示器索引」不是 Win32 监视器枚举次序，而是**已连桌面的显示设备枚举次序**。
- 多选：`get_display_union_rect`（`desktop/service.py:1181-1212`）对选中显示器 `rect` 取 **min(left)/min(top)/max(right)/max(bottom) 的包围盒**——因此选多个不相邻显示器时会**包含中间空隙**（不是逐输出拼接）。非法索引抛 `ValueError` 并列出可用索引（`:1194-1204`）。
- 元数据工具：`DisplayInventory`（`tools/display.py:46-61`）返回 index/device/primary/bounds/work_area/resolution/orientation/effective_dpi/scale；DPI 与 orientation 取值见 `uia/core.py:748-785,865-877`。

### 3.3 区域选择
`parse_region_selection`（`desktop/service.py:1139-1166`）：必须是长度 4 的整数 `[left,top,right,bottom]`；要求 `right>left && bottom>top`；与虚拟屏矩形 `get_screen_box()`（源自 `GetVirtualScreenRect`，含负 left/top）求交，**无重叠即抛错**（`:1156-1164`）。region **优先于 display**：`capture_rect = region_rect if region_rect is not None else (display union or None)`（`:124-131`），且 `display` 仍会被记录进 `screenshot_displays`（`:283`）。

### 3.4 窗口 / hwnd / 前台窗口寻址
- **截图 hwnd 寻址：未在源码中找到。**
- **窗口标题寻址（截图）：未在源码中找到。** 窗口标题只用于 `App` 工具的 `switch`/`resize`，通过模糊匹配 `_find_window_by_name`（`desktop/service.py:412-439`，`fuzzywuzzy.process.extractOne(score_cutoff=70)`）。
- **前台窗口寻址（截图）：未在源码中找到。** 前台窗口仅被记录为状态（`get_active_window`，`desktop/service.py:963-1020`；`Window` 结构见 `desktop/views.py:27-45`，带 `handle`），默认全桌面截图自然包含它，但没有「只截前台窗口」的开关。
- 全仓无 `PrintWindow`/`BitBlt`；截图统一是**屏幕像素抓取**，不是按窗口句柄渲染窗口内容（grep 结果：仅 `flash_overlay.py` 与窗口枚举使用 `hwnd`；`uia/core.py:1228` 有 `GetWindowRect` 辅助但无窗口截图调用）。UIA 树内部用窗口句柄选择遍历范围（`desktop/service.py:138-141,172-190`），但这不构成「截图寻址」。

### 3.5 多显示器与负坐标
- 虚拟屏原点：`GetVirtualScreenRect` 返回 `(left,top,width,height)`，用 `SM_XVIRTUALSCREEN/SM_YVIRTUALSCREEN/...`（`uia/core.py:667-678`）；副屏在主屏左/上时 left/top 为负。
- Pillow 裁剪：`_build_crop_box` 把捕获矩形**减去虚拟屏 left/top** 再 `crop`（`desktop/screenshot.py:35-48`）——这是负坐标换算的关键。
- mss：直接用绝对 left/top（`desktop/screenshot.py:260-266`）。
- dxcam：只在**某一个 DXGI output 完整包含**目标矩形时可用，否则该后端不可用（`desktop/screenshot.py:152-210`）。
- 区域校验：与含负原点的 `screen_rect` 求交（`desktop/service.py:1156-1164`）。
- 标注/网格换算：优先用 `capture_rect.left/top`，否则用虚拟屏 left/top 作偏移（`desktop/service.py:1258-1261,1277-1280`）；元素框再 `clamp` 到图像内（`:1281-1286`）。

## ④ 返回形态与图像经济

### 4.1 返回形态
- 结果是一个 **list**：第 1 项为纯文本状态块，第 2 项（有图时）为 `fastmcp.utilities.types.Image(data=screenshot_bytes, format="png")`（`tools/_snapshot_helpers.py:13,236-239`）。即 **MCP image content**，本仓**不自行拼 base64、不返回 `resource_link`**（全仓 grep 无 `resource_link`/`base64` 图像编码，仅 OAuth/PowerShell 用 base64，见 `infrastructure/oauth.py:74`、`powershell/service.py:190`）。
- **不落盘**：全仓无「截图写文件」路径；`FileSystem` 工具只处理调用方给的文本/路径（`tools/filesystem.py:53-79`）。
- **无大小阈值触发落盘/降级**：**未在源码中找到**。仅 README 提示 Claude Desktop 大约 1 MB 工具结果上限、建议用 `WINDOWS_MCP_SCREENSHOT_SCALE=0.5`（`README.md:631`）。
- 文本块内容：Cursor Position、Screenshot Original Size / Coordinate Scale、Visible Displays、Selected Displays、Screenshot Region + `Coordinate Space: Virtual desktop coordinates`、Screenshot Backend，然后 Active/All Desktop、Focused Window、Opened Windows，`Snapshot` 再附 UI Tree（`_snapshot_helpers.py:178-234`）。

### 4.2 编码格式
- **仅 PNG**。截图路径为 `screenshot.save(buffered, format="PNG")`（`_snapshot_helpers.py:111`），封装 `format="png"`（`:238`）。
- `as_bytes=True` 分支会用 `format="PNG", optimize=True, compress_level=6`（`desktop/service.py:265-269`），但 Snapshot 路径传的是 `as_bytes=False`（`_snapshot_helpers.py:86`），随后又用**默认参数**重新存 PNG（`:110-113`）——故 `optimize` 实际不生效。
- **jpeg / webp / quality / 灰度**：**未在源码中找到**（grep `ocr|webp|jpeg|jpg|grayscale|quality` 在 `screenshot.py`/`service.py`/`_snapshot_helpers.py` 全无命中）。

### 4.3 降采样（双轴）
`desktop/service.py:241-259`：

```
if max_image_size:                       # 1920×1080
    scale_width  = max_w / w if w > max_w else 1.0
    scale_height = max_h / h if h > max_h else 1.0
    scale = min(scale, scale_width, scale_height)   # 双轴独立比例取最小
if scale != 1.0:
    screenshot = screenshot.resize((w*scale, h*scale), Image.LANCZOS)
```

- 硬上限常量：`MAX_IMAGE_WIDTH, MAX_IMAGE_HEIGHT = 1920, 1080`（`_snapshot_helpers.py:21`），经 `max_image_size=Size(1920,1080)` 传入（`:91`）。
- 环境变量 `WINDOWS_MCP_SCREENSHOT_SCALE`：默认 `"1.0"`，解析失败回落 1.0，超范围按 `[0.1,1.0]` 夹紧（`_snapshot_helpers.py:24-34`）。
- 重采样滤镜：`Image.LANCZOS`（`desktop/service.py:258`）。
- 降采样后记录 `screenshot_original_size`（原尺寸）与 `screenshot_scale`（应用比例）（`desktop/service.py:235,254,280-281`），并在文本块回传 `Screenshot Coordinate Scale = round(1/scale, 6)` 与「image pixel × scale → 屏幕坐标」换算示例（`_snapshot_helpers.py:186-197`）。

### 4.4 抓取后端链
`desktop/screenshot.py`：注册表 + 优先级自动链 **dxcam(10) → mss(20) → pillow(100)**（`:104-249,288-375`）；`WINDOWS_MCP_SCREENSHOT_BACKEND` 选后端（`:51-62`）。静默失败护栏：`_is_usable_capture` 只做结构检查（尺寸>0、可 `load()`，**允许纯黑屏**，`:298-319`）；不可用帧的后端加入进程级 `_degraded_backends` 黑名单并切下一个（`:280-285,344-346,363-370`）；全部失败回退 Pillow（`:375`）。

### 4.5 diff / 变化检测 / 录制
**未在源码中找到**（无逐帧比对、无变化检测、无录屏）。

## ⑤ 语义桥与元素树

### 5.1 输出
`Snapshot` 的 UI Tree 段由三部分拼成（`tools/_snapshot_helpers.py:97-99,230-234`）：
1. 语义树 `semantic_tree`（父子层级：desktop → window → structural/interactive/scrollable）（`tree/views.py:102-155,185-193`）。
2. 可交互元素清单 `interactive_elements`（`tree/views.py:195-201`）。
3. 可滚动元素清单 `scrollable_elements`（`tree/views.py:203-209`）。

元素行格式：`<连接符> (x,y) <control_type> "<name>"  [action: <动作>]<元数据>`，例如 `[focused]`、`[value:"…"]`、`[range:0-100]`、`[toggle:on]`、`[shortcut:…]`、`[password]`（`tree/views.py:23-51,69-86`）。动作由控件类型经 `_ACTION_MAP` 映射（edit→fill、check box→toggle、combo box→select、slider→slide、radio button→select、document→scroll，余 click）（`tree/views.py:7-20`）。语义树按 window 分组渲染（`:69-86`），子节点顺序经 `_reverse_children_order` 修正（`:165-169`）。

### 5.2 可交互判定（细则见 5.7）
控件类型白名单 `INTERACTIVE_CONTROL_TYPE_NAMES`（`tree/config.py:1-27`，含 Button/Edit/CheckBox/RadioButton/ComboBox/Hyperlink/SplitButton/TabItem/TreeItem/DataItem/HeaderItem/TextBox/Spinner/Slider/ScrollBar 等）；Legacy `AccessibleRole` 白名单 `INTERACTIVE_ROLES`（`:29-63`）。遍历侧门禁与特判在 `tree/service.py:490-555`。

### 5.3 元素编号与寻址（label）
- 编号 = **列表下标**：`get_coordinates_from_label(label)`（`desktop/service.py:651-663`）先查 `interactive_nodes[label]`；若 `label >= len(interactive_nodes)` 则取 `scrollable_nodes[label - len(interactive_nodes)]`；越界抛 `IndexError`。批量版 `get_coordinates_from_labels`（`:665-685`）。
- 返回的是元素中心坐标 `(center.x, center.y)`（`:663,684`；`center` 由 bbox 中点算出，`tree/views.py:232-233`）。
- 所有输入工具都接受 `label`：`Click`/`Type`/`Scroll`/`Move`（`tools/input.py:23-30,251-252,285-286,321-322,368-369`）、`MultiSelect`/`MultiEdit`（`tools/multi.py:42-49,95-99`）。`Click` 的 `clicks` 语义 0=仅悬停、1=单击、2=双击（`tools/input.py:228,257-258`）。

### 5.4 截图与元素清单如何绑定
- **同一次 `get_state` 产出**：`capture_desktop_state` 调一次 `desktop.get_state(...)`，返回的 `desktop_state` 同时含截图与 `tree_state`（截图字节在 `:108-113` 从 `desktop_state.screenshot` 取；元素文本在 `:97-99` 从 `desktop_state.tree_state` 取）。
- **无 snapshot id / 无 generation / 无时间戳**：绑定是隐式的「最近一次 `get_state` 结果」；`desktop.desktop_state` 是单槽（见 ⑦）。

### 5.5 元素图形编号（Set-of-Marks 式标注）
- `use_annotation=True` 时，`get_annotated_screenshot` 对 `nodes=tree_state.interactive_nodes` 逐个画框，**编号 `i = enumerate` 下标**（`desktop/service.py:224-231,1303-1304`）；标签文本即 `str(i)`（`:1293`），画在框右上、越界则翻到框下（`:1294-1299`）。
- 因此**图上数字与 `Click(label=i)` 直接对应**（两者都是 `interactive_nodes` 的 0 基下标）；`scrollable_nodes` 的 label 从 `len(interactive_nodes)` 起，但**不参与标注绘制**（标注只画 interactive）。
- 颜色：每元素随机十六进制色（`get_random_color`，`:1238-1239`）；框线宽固定 2（`:1291`）；字体 `arial.ttf` 12px，失败回落默认字体（`:1232-1236`）。
- 光标高亮：单独画红色十字圆环 + `CURSOR` 标签（`:1306-1320`）。
- 参考线：`grid_lines=(w_count,h_count)` 时按图宽高等分画竖/横线（`:1264-1271`）；注意只有宽高**都**给了才生效（`_snapshot_helpers.py:77-79`）。

### 5.6 OCR
**未在源码中找到任何 OCR**（无 `Windows.Media.Ocr`、无第三方 OCR、无文字识别）。唯一的文本来源是 UIA 属性（Name/Value/TextPattern）与浏览器 DOM/IA2 文本（`tree/service.py:591-634,761-767`；IA2 见 `:900-941`、`tree/ia2.py`）。`Scrape` 是 HTTP 抓网页或读活跃标签页 DOM，**不是 OCR**（`tools/scrape.py:32-46`）。

### 5.7 可交互控件筛选（任务问题 ⑨）
判定分两层：

**A. 控件类型白名单**（`tree/config.py`）：`INTERACTIVE_CONTROL_TYPE_NAMES`（`:1-27`）；结构型 `STRUCTURAL_CONTROL_TYPE_NAMES = {Pane,Group,Custom,ToolBar,Tab,MenuBar}`（`:67-74`）；信息型 `INFORMATIVE_CONTROL_TYPE_NAMES = {Text,Image,StatusBar}`（`:76-88`）；Legacy role 白名单 `INTERACTIVE_ROLES`（`:29-63`）；默认动作集 `DEFAULT_ACTIONS = {Click,Press,Jump,Check,Uncheck,Double Click}`（`:90`）。

**B. 遍历时逐节点判定**（`tree/service.py:482-555`）：
1. 可见性：`area > 0` **且**（`not IsOffscreen` 或 控件是 `EditControl` 或（`ListItemControl` 且窗口是浏览器））**且** `IsControlElement`（`:484-491`）。
2. 必须 `IsEnabled`（`:494-495`）。
3. 键盘可达启发：Edit/Button/CheckBox/RadioButton/TabItem/ListItem 直接视为可聚焦，否则读 `IsKeyboardFocusable`（`:497-501`）。
4. 交互判定分支（`:504-555`）：
   - 浏览器 `DataItemControl` 且不可聚焦 → 非交互（`:506-507`）。
   - 非浏览器 `ImageControl` 且可聚焦 → 交互（`:508-509`）。
   - 类型命中白名单/`DocumentControl` → 查 Legacy `AccessibleRole ∈ INTERACTIVE_ROLES`；若又是 Image 则需可聚焦（`:510-527`）。
   - 浏览器 `GroupControl` → 有 ExpandCollapse 状态、或 role 命中且（默认动作命中或可聚焦）才算交互（`:528-553`）。
   - `DocumentControl` 恒为交互（`:554-555`）。
5. 富元数据回填：focused/shortcut/help_text/toggle_state（`:558-589`）、密码与 value 与选中文本（`:591-634`）、ComboBox 展开态/多选/选中项（`:636-682`）、Slider RangeValue min/max/value（`:684-696`）。

**可滚动**：非交互/信息类型、非 offscreen 且 `ScrollPattern.VerticallyScrollable` → `ScrollElementNode`，并给 `random_point_within_bounding_box(scale_factor=0.8)` 取一个靠内的随机中心（`tree/service.py:429-480`；`tree/utils.py:5-23`）。

**窗口级过滤**：`is_overlay_window` 丢掉名字含 `Overlay` 或（无名字且无子）的窗口（`desktop/service.py:914-926`）；`get_windows` 还要求窗口有 WindowPattern 且 `CanMinimize && CanMaximize`、矩形非空或为最小化（`:1053-1089`）；`EXCLUDED_APPS`/`AVOIDED_APPS` 常量（`desktop/config.py:5-15`）。

## ⑥ 坐标空间与旧帧护栏

### 6.1 坐标空间定义
- 动作坐标 = **虚拟桌面像素坐标**，直接交给 `uia.Click/SetCursorPos/MoveTo`（`desktop/service.py:687-708,856-858`；`uia` 进程在导入时设为 PerMonitor DPI aware，`uia/core.py:1820-1821`）。
- `label` 解析出的也是同一坐标空间（元素 `center`，`desktop/service.py:663`）。
- 截图图像坐标是**降采样后的图像像素**；当 `scale<1` 时，agent 必须把图像像素乘 `Coordinate Scale = round(1/scale,6)` 才能得到屏幕坐标（`_snapshot_helpers.py:186-197`）。
- 区域截图时文本块额外声明 `Coordinate Space: Virtual desktop coordinates`（`_snapshot_helpers.py:208-210`）。
- 标注绘制把元素 bbox 从虚拟桌面坐标减去捕获原点、再 clamp 到图像（`desktop/service.py:1258-1304`）。

### 6.2 旧帧护栏
- **无 capture/snapshot id、无 generation、无时间戳、无内容哈希**（`DesktopState` 字段见 `desktop/views.py:65-80`；无此类字段）。
- `label` 解析基于「当前 `desktop.desktop_state`」（`desktop/service.py:652,667`），而 `get_state` **每次调用都整体覆盖** `self.desktop_state`（`:273-290`）。
- 使用前置检查：`Click/Type/Scroll/Move` 在 `desktop_state is None` 时报「Desktop state is empty. Please call Snapshot first.」（`tools/input.py:25-26`；`tools/multi.py:43-44,82-83`）。
- **明确的旧帧陷阱**：`Screenshot` 以 `use_ui_tree=False` 运行，其 `desktop_state.tree_state` 是仅含合成根节点、`interactive_nodes=[]`、`scrollable_nodes=[]` 的占位（`desktop/service.py:191-203`）。因此在一次 `Snapshot` 之后若调用 `Screenshot`，之前记下的 `label` 会因列表为空而 `IndexError`（`get_coordinates_from_label`，`:651-663`）。
- 另一个漂移源：带 `region` 时元素/窗口/光标会被重新裁剪到区域（`:209-215,1324-1492`），`label` 下标随之整体变化。
- 即：**Windows-MCP 没有防止「用过期坐标」的机制**——这正是本项目需要补 `snapshot id`/generation 的反例依据。

## ⑦ 缓存与失效

- **单槽状态缓存**：`self.desktop_state = DesktopState(...)`，实例字段（`desktop/service.py:82,273-290`）。`get_state` 每次重新枚举显示器、窗口、光标并重抓截图，无 TTL、无代际、无 ETag。
- `Tree.tree_state` 字段在 `__init__` 置 `None`（`tree/service.py:70`）且全仓无其他写入点（grep `tree_state` 无 `self.tree_state=`）——**未在源码中找到其作为跨调用缓存的用途**，属死字段。
- **UIA COM 缓存**：`CacheRequestFactory.create_tree_traversal_cache()` 生成一次性 `CacheRequest`（`tree/cache_utils.py:34-126`），单次遍历内批取属性/模式以减少跨进程 COM 往返；请求在每个 `get_nodes` 会话新建（`tree/service.py:876-881`），**不跨调用复用**。缓存内含 `RuntimeId`（`cache_utils.py:62-72`），用于树遍历的环检测/去重（`tree/service.py:47-57,410-427`）。
- **元素预算**：默认上限 `DEFAULT_MAX_TREE_ELEMENTS = 500`，可用 `WINDOWS_MCP_MAX_TREE_ELEMENTS` 覆盖（`tree/budget.py:19,22-52`）；每次捕获新建预算（`tree/service.py:79-81`）；耗尽即停止下钻并置 `truncated`（`budget.py:71-91`；`tree/service.py:153-159,792-796`），响应附截断提示（`tree/views.py:89-95,191-192`）。
- **截图后端降级**：`_degraded_backends` 进程级集合，一旦某后端返回不可用帧即永久跳过（`desktop/screenshot.py:280-285,344-346,363-370`）——进程生命周期内失效。
- **显示器变化**：无 `WM_DISPLAYCHANGE`/会话事件监听；每次 `get_state` 重新 `GetDisplays()`（`desktop/service.py:122`）即自然刷新，但**无显式缓存失效路径**（因为本就没缓存显示器清单）。

## ⑧ 安全

- **传输层**：
  - 默认 `stdio`；HTTP 传输若绑定非 loopback 且无鉴权且未显式 `--allow-insecure-remote` 则**拒绝启动**（`__main__.py:667-678`）。
  - Bearer：`AuthKeyMiddleware` 用 `secrets.compare_digest` 常量时间比较，支持 OAuth token 回退（`infrastructure/auth.py:23-59`）；仅 OAuth 时用 `OAuthOnlyMiddleware`（`:62-79`）。
  - IP allowlist（CIDR）中间件（`infrastructure/security.py:97-124`）；CORS 默认不发头（`__main__.py:95-104,644-648`）；`TrustedHostMiddleware` 做 DNS rebinding 防护（`:686-694`）；TLS cert/key（`:650-651,713`）。
- **SSRF 防护**：`validate_url` 只允许 http/https、拒绝带凭据 URL、解析后拒绝 private/loopback/link-local/multicast/reserved/unspecified 地址（`security.py:25-65`），被 `Desktop.scrape` 调用（`desktop/service.py:893`）。
- **能力裁剪**：`--tools` / `--exclude-tools` / 配置 `[tools].exclude` 可从注册表移除工具（`__main__.py:633-644,349-394`）。
- **工具标注**：各工具带 `ToolAnnotations`（readOnlyHint/destructiveHint/idempotentHint/openWorldHint），例如 Snapshot/Screenshot 标只读幂等（`tools/snapshot.py:28-34,77-83`）。这是**声明式**提示，**不是**执行前的危险动作 gating 或用户同意机制——**未在源码中找到**同意/二次确认流程。
- **敏感数据擦除**：**未在源码中找到**（grep `redact|blur|mask|pixelat|sanitiz` 仅命中无关项）。具体：UIA 树对密码控件只打 `[password]` 标记并**跳过逐词框提取**（`tree/service.py:592-609`），但同一分支仍把 `LegacyIAccessibleValue` 写进 `metadata['value']`（`:600-602`）——即**密码 value 未在代码层脱敏**，且**截图本身不做任何遮挡/模糊**。
- **其它**：`Clipboard` 工具可直接读原始剪贴板文本（`tools/clipboard.py:29-34`）；遥测默认开启（PostHog，`__main__.py:260-261`），manifest 声明不采集工具参数/输出（`manifest.json:45-51`）。截图不落盘，故**无截图文件泄漏路径**（见 4.1）。

## ⑨ 可借鉴清单（服务①/②/③）

1. **双轴 min 降采样 + 回传 `1/scale` 换算元数据**（②）——`desktop/service.py:241-259`、`_snapshot_helpers.py:186-197`。可直接移植：默认上限 1920×1080、LANCZOS、并以显式 `Coordinate Scale` 提示 agent 乘回。
2. **`display`（0 基活动显示器数组）/ `region`（虚拟桌面像素）双寻址 + `region` 优先**（①）——`tools/snapshot.py:44-45,90-91`、`desktop/service.py:1110-1212`。注意其「显示器索引=已连桌面设备枚举序」与「多选取包围盒（会含空隙）」两点语义，本项目应显式定义并写进参数说明。
3. **区域裁剪同步作用于元素/窗口/光标**（①③）——`desktop/service.py:209-215,1324-1492`。保证「截图范围 = 元素清单范围 = 坐标空间」三者一致，避免框画到区域外。
4. **元素 0 基整数 label + 编号图与 label 对齐**（③）——`desktop/service.py:651-663,1303-1304`。可直接借鉴其「图上数字 ↔ `Click(label=i)`」的 SoM 闭环，并把 `scrollable` 追加在 `interactive` 之后的编号约定显式化（本项目建议改为可寻址 id 而非纯下标，见 ⑩ 反面）。
5. **可交互/可滚动的判定规则**（③）——`tree/config.py:1-90`、`tree/service.py:490-555,429-480`。白名单 + role + 可聚焦 + 可见性 + enabled + 特殊分支，是本项目 UIA 元素筛选可直接对照/升级的基线（本项目可再并入 FlaUI 的 `IsOffscreen/IsEnabled` 语义）。
6. **参考线/网格叠加**（①）——`desktop/service.py:1264-1271`（成本低、对空间推理有用；注意「两者都需提供」的现有约束）。
7. **元素预算 + 截断标记**（③）——`tree/budget.py:19,71-91`、`tree/views.py:89-95`。本项目已有 `MaxMemberMatches` 风格上限，可对标其「上限可配 + 响应显式标注 truncated」。
8. **截图后端自动链 + 静默失败降级 + 进程级黑名单**（②稳健性）——`desktop/screenshot.py:280-285,343-375`。思路可移植到 .NET：WGC 失败→GDI 兜底，且对「看似成功实则空帧」加结构校验与降级（其只查结构、允许纯黑屏的取舍值得保留）。
9. **UIA 密码字段标记**（③/安全）——`tree/service.py:592-599`。可作为本项目「元素清单标注敏感控件」的参考；但**不要照抄其 value 未脱敏的做法**（见 ⑩）。
10. **截图后可视化闪示（可选）**——`desktop/flash_overlay.py:1-12,60-111`。「抓取完成后才显示、下一次抓取前拆掉，故不会出现在图里」是干净的交互设计（本项目若做展示面可选借鉴）。
11. **浏览器 DOM 语义桥 + Firefox IA2 兜底**（③）——`tree/service.py:698-712,900-941`、`tree/ia2.py`。若本项目要做浏览器元素，这是「UIA 无 RootWebArea 时退回 IAccessible2」的可信参考。
12. **显示器/DPI 只读元数据工具**（①）——`tools/display.py:46-61`、`uia/core.py:748-785,808-885`。返回 index/device/bounds/work_area/orientation/effective_dpi/scale，可直接映射到本项目的显示器清单。
13. **元素 bbox 裁剪到「窗口 ∩ 屏」**（①）——`tree/service.py:253-264`（`iou_bounding_box`），防越界框。

## ⑩ 不采纳清单

1. **坐标注入式动作**（`uia.Click/SetCursorPos/SendKeys/DragDrop/MoveTo`，`desktop/service.py:687-882`）：本项目动作走 UIA 语义调用（Invoke/Value/Toggle/Select 等），不移动真实光标、不注入输入。仅吸收其「截图 ↔ 坐标/label 绑定」的表示法。
2. **Python 截图后端**（Pillow `ImageGrab`、dxcam/DXGI、mss，`desktop/screenshot.py:104-375`）：本项目已是纯 Windows .NET，用 WGC/GDI 链；这些库不可移植。
3. **单槽 `desktop_state` + 纯下标 label、无 generation/无 snapshot id 的缓存模型**（`desktop/service.py:652,273-290`）：**这正是要修正的反例**——本项目须引入 `snapshot id`/代际号，并在 label/id 解析时校验帧归属，避免旧帧坐标（见 ⑥.2 的 `Screenshot` 清空树陷阱）。
4. **密码 value 未脱敏**（`tree/service.py:600-602`）：安全缺口，不照抄；本项目应在元素清单与截图中做敏感字段处理（清单可标 `password`，value 不落文本）。
5. **随机颜色框 + 固定 12px 字体、无缩放**（`desktop/service.py:1232-1239,1275,1291`）：噪声大、缩略图编号不可读；本项目应改用高对比纯色底 + 字号随图缩放（参考 10-source-omniparser 的标签摆放/字号规则）。
6. **响应内嵌 cursor 高亮环**（`desktop/service.py:1306-1320`）：agent 场景非必需，且与「元素编号图」叠加会增噪；本项目默认应关闭、或仅在显式请求时提供。
7. **遥测默认开启**（`__main__.py:260-261`）：本项目不应默认外发遥测。
8. **默认全桌面捕获 + 无 hwnd/窗口标题寻址**（截图寻址面）：本项目要补「窗口级/客户区/hwnd/前台窗口」寻址，Windows-MCP 在此为空白。
9. **无 OCR / 无 diff / 无 webp / 无灰度 / 无落盘**：均为 Windows-MCP 的空白，不是「不采纳」而是「本项目要补的能力」（本项目经济线用 webp/灰度/降采样/diff/by-ref）。
10. **参考线要求宽高同时提供**（`_snapshot_helpers.py:77-79`）：约束过紧，本项目应允许单轴。
11. **多显示器多选取包围盒会含中间空隙**（`desktop/service.py:1181-1212`）：语义易误导，本项目若支持多选应明确「并集包围盒」或改为逐屏拼接并说明。

## ⑪ 来源

**Commit**：`59e77f6cf0716759ba4a8622df22524d07c10acc`（短哈希 `59e77f6`；`Sun Sep 27 09:11:19 2026 +0800`；`fix(tree): cut branches that repeat an ancestor's RuntimeId (#429)`）。
**仓库**：`E:\Code\Projects\Externals\Windows-MCP`（GitHub `CursorTouch/Windows-MCP`，MIT，server.json 标 version 1.0.1 / package 0.8.6，`server.json:10,15`）。

**读取的文件（行号为实测；文件本身行数在括号内）**：

| 文件 | 读取/引用关键行 |
|---|---|
| `src/windows_mcp/tools/snapshot.py`（123） | 25-47, 74-120 |
| `src/windows_mcp/tools/_snapshot_helpers.py`（239） | 13, 21, 24-49, 52-140, 143-239 |
| `src/windows_mcp/desktop/service.py`（1493） | 82, 84-312, 324-325, 412-439, 651-708, 856-882, 914-926, 963-1093, 1110-1219, 1221-1493 |
| `src/windows_mcp/desktop/screenshot.py`（375） | 28-62, 95-249, 274-375 |
| `src/windows_mcp/desktop/views.py`（103） | 20-103 |
| `src/windows_mcp/desktop/utils.py`（108） | 17-24, 71-108 |
| `src/windows_mcp/desktop/config.py`（15） | 3-14 |
| `src/windows_mcp/desktop/flash_overlay.py`（567） | 1-12, 23-111 |
| `src/windows_mcp/tools/input.py`（504） | 14-30, 217-504 |
| `src/windows_mcp/tools/multi.py`（105） | 17-105 |
| `src/windows_mcp/tools/app.py`（113） | 70-113 |
| `src/windows_mcp/tools/display.py`（61） | 24-61 |
| `src/windows_mcp/tools/scrape.py`（65） | 8-65 |
| `src/windows_mcp/tools/clipboard.py`（52） | 10-52 |
| `src/windows_mcp/tools/filesystem.py`（81） | 12-81 |
| `src/windows_mcp/tools/registry.py`（51） | 12-51 |
| `src/windows_mcp/tools/__init__.py`（42） | 1-42 |
| `src/windows_mcp/tree/views.py`（290） | 1-95, 102-209, 232-290 |
| `src/windows_mcp/tree/config.py`（92） | 1-92 |
| `src/windows_mcp/tree/service.py`（1024） | 47-57, 62-185, 187-264, 266-296, 392-480, 482-555, 557-738, 769-796, 868-1006 |
| `src/windows_mcp/tree/budget.py`（91） | 19, 22-91 |
| `src/windows_mcp/tree/cache_utils.py`（213） | 34-126, 128-213 |
| `src/windows_mcp/tree/utils.py`（23） | 5-23 |
| `src/windows_mcp/uia/core.py`（2398） | 620-678, 681-885, 722-745, 748-785, 1228-1235, 1784-1821 |
| `src/windows_mcp/infrastructure/security.py`（124） | 14-65, 97-124 |
| `src/windows_mcp/infrastructure/auth.py`（89） | 14-89 |
| `src/windows_mcp/infrastructure/config.py`（205） | 10-46, 93-158 |
| `src/windows_mcp/__main__.py`（1032） | 60-113, 165-188, 241-291, 349-439, 442-745 |
| `server.json`（22） | 10, 15 |
| `manifest.json`（183） | 4, 45-51, 95-101, 163-182 |
| `README.md` | 627-634, 643, 713-730 |

**核实性 grep（全仓 `src/`）**：`ocr|webp|jpeg|jpg|grayscale|quality`（无截图编码/OCR 命中）；`resource_link|base64|mimeType|image/*`（仅 OAuth/PowerShell 的 base64）；`generation|snapshot_id|capture_id|ttl|invalidate|diff|changed|hash`（无截图代际/diff）；`PrintWindow|BitBlt`（无命中）；`redact|blur|mask|pixelat|sanitiz`（无截图擦除命中）。
