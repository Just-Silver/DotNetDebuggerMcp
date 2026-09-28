# 截图能力黑盒测试（batch2）上下文背景

> **用途**：换一台电脑后重新对 `DotNetDebuggerMcp` 的**截图能力**做黑盒测试时的自包含背景（含本轮修复清单、复验方法、待决策与未覆盖面）。
> 日期：2026-09-28。代码状态：`master`（含本轮 8 项修复）。原始测试产物（截图/脚本/日志/报告）在**本机** `D:\下载\test\batch2\`——换机后需重跑。

## 1. 被测对象与启动方式

- 被测三件套：`screenshot`、`screenshot_windows`、`screenshot_displays`。
- **首选**：直接用会话内的 `DotNetDebugger` MCP 工具（opencode 绑 `src/DotNetDebuggerMcp/bin/Debug/net10.0-windows10.0.22621.0/DotNetDebuggerMcp.exe`）。
  - **改代码后必须重建 Debug + 重启 opencode**，否则测到的是旧二进制：
    ```powershell
    dotnet build -c Debug src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj
    ```
  - 运行中的服务器会锁定 Debug 输出（MSB3027/MSB3021）——需先重启 opencode 释放锁再构建。
- **备选**（不经 opencode）：Release 服务器走 stdio（MCP JSON-RPC 为**换行分隔 JSON**，非 Content-Length）：
  ```powershell
  dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj
  # 起 bin/Release/net10.0-windows10.0.22621.0/DotNetDebuggerMcp.exe
  # 顺序：initialize → notifications/initialized → tools/call
  ```
  参考实现：`tests/DotNetDebuggerMcp.Tests/DebugMcpToolsTests.cs`（`StdioClientTransport`）、`src/DotNetDebuggerMcp.Client/`。
- 目标程序：`tests/TestData/UiSampleApp/UiSampleApp.exe`（WinForms，控件 `countButton`/`toggleState`/`inputButton`）。首次需 `powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`。
- 产物目录约定：`D:\下载\test\batch2\`（截图/脚本/日志/`报告.md`）。

## 2. 本轮修复（8 项，换机后逐项复验）

| 编号 | 问题 | 修复后应观察到 |
|---|---|---|
| **D1** | `element` 序号与**带过滤**的 `ui_find` 序号不同源：同数字异义且截图**报成功**（静默截错控件） | `element` 说明明确「序号**必须是「无过滤」`ui_find` 的 index**」，推荐用控件名/AutomationId；复验点=说明已如实告知（行为未变：`screenshot{element:"1"}` 仍指无过滤清单第 1 项） |
| **D2** | 最小化窗口抓取失败归因「无桌面会话（服务/无头环境）」 | 改为「目标窗口已最小化，无法抓取：请先还原（`ui_action` verb=windowstate windowstate=normal）或改用 mode=screen/region」 |
| **D3** | 失败的 `element` 查找把全局帧号推进数十（内部 50ms 轮询每次都产帧） | `ui_find`(帧 N) → `screenshot{element:"NoSuch"}` 报错 → `ui_find` 得帧 **N+1**（不再 N+40） |
| **D4** | `screenshot_displays` 缺说明承诺的「缩放比」（100% 时省略） | 输出恒含 `缩放 100%` |
| **D5** | `windowTitle="@active"` 特值大小写敏感 | `@ACTIVE`/`@Active` 均识别为前台窗口；`mode=foreground` 的兼容校验同步放宽 |
| **D6** | 前台窗口命名不一致（`foreground` vs `@active`） | 两入口头部一致：`目标:   前台窗口 "X" (pid=N)` |
| **D7** | `filePath` 不展开环境变量 | `filePath:"%TEMP%\x.png"` 落到真实 `%TEMP%`，不再生成字面 `%TEMP%` 目录 |
| **D9** | 最小化主窗多报「尺寸极小，可能不是目标主窗」 | 已最小化时只保留「目标窗口已最小化…」备注 |

## 3. 已评估关闭（非缺陷，勿重复立项）

- `filePath` 静默覆盖已存在文件（显式指定路径即写入，属预期行为）。
- `hwnd` 非空且无效时不回退 `windowTitle`/`processId`（文档定义为「优先」；陈旧 hwnd 报错比静默换目标更安全）。
- `mode=region` 下 `timeoutSeconds` 被忽略（文档已限定该参数为「窗口出现」的秒数）。

## 4. 待决策（需设计，尚未实施）

- **`element` 序号口径统一**：是否让 `ui_find` 过滤时并列输出全量序号（如 `[g=3 f=1]`），或让 `screenshot element` 跟随最近 `ui_find` 的过滤上下文。涉及 `ui_*` 契约与 `_lastFind` 缓存语义，以及 `UiFrameRegistryTests.Find_And_FindForCapture_ShareSameIndexSequence`。

## 5. 未覆盖面（换机可优先补）

- **多显示器**：`display=left/right` 的真实邻屏解析（上轮机器为单屏 3440×1440）。
- 无标题前台窗口入列（`screenshot_windows` 的 `(无标题)` 标记）。
- `debug_launch` 会话兜底取 pid 的 window/element 定位。
- 多窗口「选择」在多个**尺寸相近**窗口下的择优表现（上轮只覆盖 explorer 一例）。

## 6. 既有约定（避免误报）

- `帧:` **仅 element 模式**输出；`frameId` 对**所有模式**都校验；`ui_find` 与 element 截图推进帧号，普通 `screen/region/window` 不推进。
- `window` 整窗走 WGC，大屏复杂界面 PNG 常 3–5MB → 自动落盘（阈值：base64 后 ≥2MB）只回路径、**不附图片块**。
- 头部 `选择:`/`备注:` 为**事实行**（多窗口选择/无标题/极小/最小化/参数越界）。
- 图像恒为原生 1:1，**本工具不做任何缩放**。
- `region` 的 `w/h ≤ 0` 报「宽/高必须为正整数」（非「格式错误」）。
- 同环境已知干扰：新起的 Photos/Edge/Paint 窗口可能渲染为纯黑（同坐标 BitBlt 亦黑）——是环境问题，工具会正常备注「画面为纯黑」。

## 7. 上轮测试方法要点（可复用）

- `includeCursor` 用**像素差**验证（静态窗口上开/关光标截图比对 diff bbox 应等于光标尺寸 12×19）。
- `screenshot_windows` 的计数用**独立枚举**核对（可见顶层窗口 = 列出数 + 未列出数）。
- 落盘阈值用**高熵整屏**触发（普通窗口可能 <2MB）。
