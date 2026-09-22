# screenshot 截图工具设计（2026-09-22）

> 目标：为多态模型提供**独立于调试会话**的截图 MCP 工具，用于 GUI 观察与自动化冒烟。
> 状态：经用户逐节评审 + 独立审查（r4-coder/deepseek-v4.1-flash，Issues Found 三阻断五建议已全部修复落档）+ PackAsTool 打包 spike 实证（2026-09-22）；待用户终审后转 writing-plans。

## 1. 背景与定位

- 现有 10+ 个 debug_* 工具全部输出 `Task<string>` 文本；观察 UI 状态需要图片通道。
- **截图是独立工具**：不要求活动调试会话（用户明确决定），与 debug_* 只共享落盘目录与文案风格。
- **Web 展示面整体冻结**（2026-09-22，已记入 `src/DotNetDebugger.Web/TODO.md`）：本工具只做 MCP 工具面，不为 Web 设计任何展示/回放。
- 业界参考（高星/官方）：Anthropic computer-use（`zoom.region` 屏幕坐标空间规范）、chrome-devtools-mcp 52k★（≥2MB 落盘降级、format/quality/filePath、image+文本双轨）、playwright-mcp 37k★（scale 降采样、Description 写能力边界）、mcp-screenshot-server（`mode: fullscreen|window|region` 单工具多模式）。

## 2. 用户拍板决策记录

| # | 决策 | 结论 |
|---|---|---|
| D1 | 截图对象 | 被调试/任意目标进程的**主窗口**为核心，另支持全局屏幕与局部区域 |
| D2 | 工具面形状 | **单工具多模式** `mode=window\|screen\|region`（非多工具） |
| D3 | 输出形态 | **MCP image 块直返**，修订为**条件双轨**：≤2MB 附 image 块，超限/指定 filePath 落盘返回路径（chrome-devtools 先例） |
| D4 | 作用范围 | **独立于调试会话**：三模式均不要求会话；window 定位用 `processId`+`windowTitle` 双选择器，两者皆空时若有活动会话则兜底取其目标 pid |
| D5 | 局部坐标系 | **屏幕坐标空间**（= mode=screen 返回图像素空间，原点左上，Anthropic 规范同款） |
| D6 | 窗口未就绪 | `timeoutSeconds` 带超时轮询等窗口出现（默认 5s） |
| D7 | 抓取策略 | **WGC 优先 + GDI 回退**（甲案）；Engine「只引 ClrDebug+DbgShim」纪律**移除**（用户：从未要求过）。WGC 依赖 WinRT 投影 → 全链升 windows TFM；宿主 PackAsTool 的 NETSDK1146 限制经 **spike 实测**（2026-09-22，SDK 10.0.401）用两段 MSBuild Target hack 解决（pack/install/run 三关全过；官方 RID-specific 新路实测同样报错、不可用） |
| D8 | 超限落盘位置 | `%LOCALAPPDATA%\DotNetDebuggerMcp\screenshots\`，`filePath` 可覆盖 |
| D9 | 缓存 | 不入缓存、不过 ToolPipeline（独立工具、缓存无意义）——`IsErrorResult` 七类前缀**不涉及** |
| D10 | GUI 测试来源 | **复用既有 `tests/TestData/UiSampleApp`**（U1A UI 自动化的 WinForms 目标，源码受版本控制、`generate-testdata.ps1` 已构建）——独立审查发现原"自建窗口"方案与之重复，改为复用；不改生成脚本 |
| D11 | 测试形态 | **以单测为主**（Engine 能力单测 + 宿主工具单测），不新增 e2e 闭环 |
| D12 | 工具命名 | `screenshot`（不带 debug_ 前缀，强调独立性） |
| D13 | 打包路线 | 宿主 csproj 加两段 MSBuild Target（`_PackToolValidation` 前清空/后恢复 `TargetPlatformIdentifier`）——spike 实测 pack→install→run 全过；官方 docs（NETSDK1146 错误列表 2026-05 仍在、dotnet/sdk#52716 官方支持进行中）与 SDK 源码核对一致；**不新增 CLI 参数、豁免 Client e2e Cases 约定（依 D11）** |

## 3. 工具面契约（agent 可见）

**新文件** `src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs`。

### 3.1 参数（全部带默认值，符合铁律；CancellationToken 末位不暴露）

| 参数 | 类型/默认 | 语义 |
|---|---|---|
| `mode` | `string = "window"` | `window`（目标主窗口）/ `screen`（全屏）/ `region`（局部） |
| `processId` | `int = 0` | window 定位：按进程 pid（0=未提供）；**优先于 windowTitle** |
| `windowTitle` | `string = ""` | window 定位：窗口标题子串（忽略大小写） |
| `region` | `string = ""` | `mode=region` 必填，`"x,y,w,h"`，**坐标=mode=screen 返回图像素空间**（原点左上）。换算规则无状态确定性：screen 返回前若原生维 >2000px 会等比缩到 2000 内（缩放比 k=min(1, 2000/max(W,H))，W/H=虚拟屏原生尺寸，可由 GetSystemMetrics 现算），宿主收到 region 坐标后除以 k 换回原生像素（**四舍五入 round 取整**）再交 Engine 裁剪——同屏幕下 screen 与 region 空间恒一致 |
| `format` | `string = "png"` | `png` \| `jpeg` |
| `quality` | `int = 80` | jpeg 质量 0-100（越界 clamp），png 忽略 |
| `timeoutSeconds` | `int = 5` | 仅 window：等窗口出现秒数（clamp 0-30，0=立即试一次） |
| `filePath` | `string = ""` | 非空=强制落盘该路径；空=仅超限落默认目录 |

**window 定位规则**：`processId` > `windowTitle` > 活动会话目标 pid 兜底 > 中文提示二选一。命中多个可见窗口 → 取 Z 序最前主窗口，头部注明「命中 N 个可见窗口，已截主窗口」。

### 3.2 Description 草稿（中文、注明默认值、写死坐标规则、含能力边界）

> 截取窗口/屏幕画面返回图片，供多态模型观察 UI 状态做自动化冒烟。独立工具，不要求调试会话。mode=window（默认）按 processId 或 windowTitle 定位目标主窗口（窗口未出现会等 timeoutSeconds 秒，默认 5）；mode=screen 截全屏；mode=region 按 region="x,y,w,h" 截局部，坐标以 mode=screen 返回的图像素为准（原点左上）——建议先 screen 看全景再裁局部。format 默认 png，jpeg+quality 可压体积；图片过大自动改为落盘返回绝对路径。坐标/状态判断仍以 debug_state/debug_stack 为准，本工具只提供视觉观察。

### 3.3 返回契约（`Task<CallToolResult>`，铁律例外）

```
成功 ≤2MB(base64后):  content[0]=文本头部；content[1]=image 块(base64+mime)
成功 >2MB / 指定filePath: content[0]=文本头部 +「已落盘: <绝对路径>」，无 image 块
失败:                  纯文本中文提示，不抛异常
```

文本头部（对齐现有输出约定，纯文本）：

```
目标:   窗口 "标题" (pid=12345)     ← screen 为「屏幕」，region 为「屏幕区域 (x,y,w,h)」
尺寸:   原生 1920x1080 → 1600x900 (83%)   ← 无缩放省略；region 注明与屏幕交集
来源:   WGC                          ← 仅 window：WGC / PrintWindow / BitBlt
备注:   画面为纯黑（目标可能未渲染）  ← 仅全黑时出现
---
```

**铁律修订**：根 `AGENTS.md` 与宿主 `AGENTS.md`「工具返回 `Task<string>`」增加例外条款：图片类工具（当前仅 `screenshot`）返回 `CallToolResult`，错误仍为纯文本内容。

**SDK 验证点**：csharp-sdk 源码已确认支持（`Protocol/CallToolResult.cs` + `McpServerTool`，测试覆盖返回 CallToolResult/IsError/StructuredContent 场景）；实现首日用本仓库锁定的 ModelContextProtocol 包版本跑通最小样例即可，若版本行为不符再回来找用户改走纯落盘路线。

**边界（显式声明）**：`screenshot` **不新增 CLI 入口**（`DotNetDebuggerMcpCmd` 不加截图参数，CLI 调试 `-dbg` 体系与本工具无关）；**豁免 Client e2e Cases 约定**（依 D11 不新增 e2e，单测覆盖）。

## 4. Engine 截图能力

### 4.1 工程变更

- **TFM 全链升级**：`net10.0` → `net10.0-windows10.0.22621.0`（22621 起 `GraphicsCaptureSession.IsBorderRequired` 等 API 进入编译期投影，ApiInformation 运行时探测兼容老系统）。凡直接/间接引用 Engine 的项目同步（Engine、Session、Web、各相关测试项目；Client 不引用则不动）。非 windows TFM 不能引用 windows TFM 项目（NU1201）。
- **宿主 PackAsTool 限制与解法（spike 已实证）**：宿主是 `PackAsTool` 打包的 dotnet tool，NETSDK1146 禁止带平台标签（官方错误列表 2026-05 仍在；dotnet/sdk#52716 官方支持进行中）。**实测结论**（SDK 10.0.401，2026-09-22）：原样 pack ❌ / 官方 RID-specific（`RuntimeIdentifiers=win-x64`）❌ 同样报错 / **两段 MSBuild Target hack ✅ pack→tool install→run 全过**。宿主 csproj 采用：

  ```xml
  <Target Name="HackBeforePackToolValidation" BeforeTargets="_PackToolValidation">
    <PropertyGroup><TargetPlatformIdentifier></TargetPlatformIdentifier><TargetPlatformMoniker></TargetPlatformMoniker></PropertyGroup>
  </Target>
  <Target Name="HackAfterPackToolValidation" AfterTargets="_PackToolValidation" BeforeTargets="PackTool">
    <PropertyGroup><TargetPlatformIdentifier>Windows</TargetPlatformIdentifier></PropertyGroup>
  </Target>
  ```

  附注：装机端无平台过滤（装到非 Windows 会跑不起来）——本包本就 win-x64 only，README/NuGet 描述注明 Windows-only 即可。
- **新增依赖**：`System.Drawing.Common` 进 Engine（编码 PNG/JPEG、缩放、裁剪）。
- **Engine AGENTS.md 纪律修订**：边界条款改为「NuGet 限 ClrDebug + DbgShim + System.Drawing.Common，无宿主依赖」。

### 4.2 API 形状（`Engine/Capture/` 新目录）

```csharp
WindowHandleInfo? FindMainWindow(int processId, string titleSubstring)
    // EnumWindows：可见 + 属主 pid + 标题匹配（忽略大小写），Z 序最前；返回 hwnd/标题/rect/IsIconic
CaptureResult CaptureWindow(IntPtr hwnd)
    // WGC 优先 → 回退链（见 4.3）；最小化(IsIconic)只走 WGC/PrintWindow，不回退 BitBlt
CaptureResult CaptureScreen(Rectangle? clip)
    // GDI BitBlt 虚拟屏幕（GetSystemMetrics SM_X/Y/CX/CY），clip=原生像素裁剪（region 复用；宿主已按 §3.1 规则把返回图像素坐标换算为原生像素）
record CaptureResult(byte[] Image, int Width, int Height,
                     int NativeWidth, int NativeHeight, string? WindowTitle,
                     string Source /* WGC|PrintWindow|BitBlt */, bool FellBack, bool WasAllBlack)
```

- **DPI**：截图入口首次调用 `SetProcessDpiAwarenessContext(PROCESS_PER_MONITOR_DPI_AWARE_V2)`（幂等，已设置容忍 ERROR_ACCESS_DENIED）——全链物理像素，与 region 坐标空间定义一致。运行时调用，不用 manifest。
- **缩放/编码/裁剪（职责唯一归属 Engine，宿主不碰）**：Engine 内用 System.Drawing 完成 2000px 等比缩放（上限常量由宿主 `AppConfig` 定义、经参数传入 Engine）、format/quality 编码、region 裁剪（含 §3.1 的 k 换算与 round 取整）、纯黑采样检测，直接产出编码后 `byte[]`；宿主只做参数校验、等窗口轮询、头部组装、落盘与 CallToolResult 组装。

### 4.3 抓取回退链（window 模式）

1. **WGC**（正确性优先：DWM 取帧不黑图、被遮挡可截）：
   - `IGraphicsCaptureItemInterop::CreateForWindow`（Win10 全系可用）；
   - `CreateFreeThreaded` 建 framepool（免 DispatcherQueue 消息泵死锁）；
   - `ApiInformation` 探测并关 `IsBorderRequired`（Win11 22H2+）/`IsCursorCaptureEnabled`（Win10 1903+）；
   - owned/无主窗口 E_INVALIDARG → 临时加 `WS_EX_APPWINDOW` 提升，完成后还原；
   - nudge（RedrawWindow/DwmFlush）催 DWM 出帧，限时等首帧（约 500ms 上限）；
   - `GraphicsCaptureSession.IsSupported()` 为 false 或超时/异常 → 进入回退。
2. **PrintWindow**（`PW_RENDERFULLCONTENT`）→ 采样纯黑则继续回退。
3. **BitBlt** 屏幕上窗口矩形（被遮挡处截到遮挡物，`Source=BitBlt` 头部标注 best-effort）。
4. 全失败 → 约定中文错误（见 5.2）。`mode=screen`/`region` **直接走 GDI BitBlt**，不开 WGC 会话。

### 4.4 线程模型

- 截图**不触碰 ICorDebug、不进命令泵**；工具线程直接同步调用（单次几十 ms~数百 ms），与 DebugEngineMTA 零交互，无并发 Continue 风险。
- **等窗口轮询在宿主工具层**（`Task.Delay` 50ms 间隔至 timeoutSeconds），Engine 保持无超时语义的单次查询。

## 5. 输出、降级与错误语义（宿主层）

### 5.1 体积与落盘

1. 缩放由 Engine 按 `AppConfig` 上限执行（见 §4.2，职责唯一归 Engine）；宿主只消费结果的 `NativeWidth/Height` 与缩放后尺寸填头部；
2. base64 后 ≥2MB（`AppConfig` 常量，chrome-devtools 阈值）或 `filePath` 非空 → 落盘：
   - 默认目录 `%LOCALAPPDATA%\DotNetDebuggerMcp\screenshots\screenshot-{yyyyMMdd-HHmmssfff}-{pid}.{ext}`；
   - `filePath` 指定则 `Directory.CreateDirectory` 保证父目录后写入；
   - 返回文本头部 + `已落盘: <绝对路径>`，无 image 块；
   - 落盘失败 → 回退附 image 块（若可附）+ 注明失败原因。
- 落盘文件不自动清理（YAGNI），README 注明位置。

### 5.2 错误语义全表（中文、不抛异常）

| 场景 | 返回 |
|---|---|
| mode/format 非法 | `mode 仅支持 window/screen/region（当前 "x"）。` / 同款 format 提示 |
| region 格式错 | `region 格式应为 "x,y,w,h"（屏幕像素，原点左上）。` |
| region 完全在屏外 | `region (x,y,w,h) 完全在屏幕范围 (WxH) 之外。`（部分越界=裁交集+头部注明，不报错） |
| window 双选择器皆空且无会话 | `请提供 processId 或 windowTitle 定位窗口（两者皆空时也可先 debug_launch 建立会话自动取目标 pid）。` |
| 超时未找到窗口 | `{N} 秒内未找到匹配的可见窗口（processId=… / 标题含 "…"）。` |
| WGC/GDI 全失败、GetDC 失败 | `窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。` |
| 抓到但纯黑 | 不报错，正常返回 + 头部 `备注: 画面为纯黑…` |
| 落盘失败 | 回退 image 块 + `已尝试落盘失败: {原因}` |
| 取消 | `screenshot 已取消（可重试）。` |

### 5.3 明确不涉及

- **不入缓存、不过 ToolPipeline** → `IsErrorResult` 不扩展（D9）；
- **Web 冻结**（见 §1）：不做 Actions/回放相关设计；
- 工具调用不写 `AgentViewService`（那属反编译链路现状）。

## 6. 测试策略（单测为主，D10/D11）

### 6.1 GUI 来源：复用既有 UiSampleApp（D10）

- 直接起 `tests/TestData/UiSampleApp/UiSampleApp.exe`（U1A 的 WinForms 测试目标，`generate-testdata.ps1` 已构建、源码受版本控制），按其 **pid** 走 window 模式断言——不自建窗口（STA/消息泵坑不复存在）、不改生成脚本（零 token 漂移）。
- 测试起子进程遵循既有铁律：**排空 stdout/stderr**（UiSampleApp 虽无控制台输出，仍按 `DebugTargetProcess` 同款防御）；结束 kill 清理。
- 已知注意：UiSampleApp 主窗内容与尺寸在脚本/源码中固定，断言窗口标题、非纯黑、尺寸与窗口 rect 一致即可。

### 6.2 Engine 单测

- `FindMainWindow`：起 UiSampleApp 按 pid 命中；标题子串命中/未命中；多可见窗口取 Z 序最前。
- GDI：BitBlt 全屏 → 尺寸=虚拟屏 + PNG 头合法（**不断言非纯黑**——CI 桌面可能纯色）；PrintWindow/回退链抓 UiSampleApp 主窗 → 非纯黑 + 尺寸与窗口 rect 一致。
- WGC：`IsSupported()` 探测，不支持 → `Assert.Skip`；支持 → 抓 UiSampleApp 主窗非纯黑。
- region：正常 / 部分越界（裁交集）/ 完全越界（约定错误）。
- 缩放：>2000px 等比、纵横比守恒；纯黑检测探针。

### 6.3 宿主工具单测

- 参数校验全表逐条对错误文案；
- 返回结构：`content[0]` 头部含 `目标/尺寸/来源`、`content[1]` image 块 base64 可解码为合法 PNG；
- 落盘分支：`filePath` 强制落盘 → 文件存在 + 返回含路径 + 无 image 块；默认目录分支用测试小阈值常量触发；
- 超时 / 双选择器皆空的中文提示。

### 6.4 CI 风险预案

- GitHub Actions Windows runner 有交互桌面，GDI 一般可用；WGC 视 GPU/会话——**探测失败即 Skip 对应断言，不红**；
- 沿用现有测试串行纪律（Engine 测试 `ParallelMode.None` 不动）。

### 6.5 手工验收

本机对真实 GUI（记事本）三模式各截一张人眼验收：来源标注（WGC）、Win10 黄框闪烁表现、落盘路径、image 块显示。

## 7. 文档与影响面清单

| 项 | 动作 |
|---|---|
| `docs/planning/specs/README.md` | 状态表登记本 spec（仓库惯例：每 spec 一行状态） |
| `docs/planning/README.md` | 文档地图同步登记 |
| 根 `README.md` | 工具清单加 `screenshot`（铁律：同 commit 改到位） |
| 根/宿主 `AGENTS.md` | ① `Task<string>` 加 CallToolResult 例外条款；② 调试工具计数/清单更新 |
| `src/DotNetDebugger.Engine/AGENTS.md` | 边界纪律修订（NuGet 清单 + Capture/ 结构 + TFM 说明） |
| `CHANGELOG.md` `[Unreleased]` | 记 `screenshot` 新工具（使用者可见） |
| `src/DotNetDebugger.Web/TODO.md` | 冻结记录已写入（2026-09-22） |
| `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` | ① TFM 升级；② PackAsTool 两段 hack Target（§4.1，spike 实证写法） |
| `AppConfig` | 新增缩放上限 / 2MB 阈值 / 落盘目录常量 |
| 握手 `HandshakeFeatureIntro` | 实现时检查现有 debug 工具是否在列，在则同步 `screenshot` |
| 版本三处同步 | 发布时（csproj `<Version>` + `.mcp/server.json`×2 + CHANGELOG 段转换），本设计不动 |
| `CacheSignatures`/`CacheStatsTool` | **不涉及**（不入缓存） |

## 8. 明确不做（YAGNI / 冻结项）

- Web 展示、截图回放、`AgentView` hook（Web 冻结）；
- 截图缓存、自动清理落盘文件；
- 多显示器 `display` 参数、`includeCursor`、延迟截图 `delay`；
- 控件/元素级截图（UIAutomation 定位）、跨窗口拼接；
- region 的 `origin` 可选坐标系（D5 已定单一屏幕空间）；
- 截图工具的 e2e 闭环与 `generate-testdata.ps1` 改动（D10/D11）。

## 9. 风险与开放问题

| 风险 | 处置 |
|---|---|
| Win10 WGC 捕获黄框关不掉（Win11 22H2+ 可关） | 接受（截图瞬时）；手工验收确认表现，若不可接受再评估仅 Win11 关/Win10 走 GDI 的开关 |
| SDK 不支持 `Task<CallToolResult>` | csharp-sdk 源码已确认支持；实现首日按锁定包版本跑最小样例，不符则回退纯落盘路线并重新报批 |
| **PackAsTool hack 属社区方案**（NETSDK1146 未官方解除，dotnet/sdk#52716 进行中；SDK 升级可能破坏 hack） | spike 已在 SDK 10.0.401 实证；**升 SDK 版本后必须重验 `dotnet pack` + tool install**；官方支持落地后撤掉 hack（改官方姿势） |
| CI 无头/远程会话截图不可用 | 探测 + Skip（6.4），不阻塞流水线 |
| TFM 全链升级引发兼容问题 | 本就 win-x64 only；升级后全量 build + 全量单测回归 + 宿主打包三关重验（pack/install/run） |
| 番茄红窗在高对比主题/DPI 下采样断言不稳 | 已改用 UiSampleApp（D10），断言基于其固定窗口内容/尺寸；失败信息带采样值便于排查 |
