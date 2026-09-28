# TODO（DotNetDebuggerMcp 宿主近期待办）

> 近期待办，完成一项删一项；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

> **当前无未完成近期待办。**
> **2026-09-10 清理归档**：已完成历史已删除（细节见 git log 与 `docs/planning/specs/`）。已交付：W1 现场改写、V3 统一时间线、DB1 敏感脱敏、DB2 按名白名单、D1 对象深读、D2 子进程跟随、V4 语料断言、U1+U1A 全 UIA 化 UI 自动化、V1 一键复验；**2026-09-10 CoreMes 实证修复**：实例方法参数名对齐 `this`、同址重复断点、`debug_run_to` 陈旧停点、`debug_evaluate` `$exception`、`search_string` 生成类型、ilOffset 中文提示、源行断点聚合提示、新增 `debug_terminate`/`debug_modules`、`debug_*` 描述订正、D2 无 CLR 不虚报、D1 文案。W3 数据断点、W2 SetIP、V2 崩溃 dump、`frameIndex` 帧选择 **已转 `docs/ROADMAP.md`**。

## 本轮 CoreMes 动态调试实证（2026-09-10，47 工具全量实测）——全部处置

> 来源：对 WPF 应用 CoreMes（离线跑）做 47 工具全量实测，端到端闭环（launch→断点→单步→异常→UI→verify PASS）跑通。所有实证缺陷当日修复：详见上方归档段与 git log；唯一剩余项 `frameIndex`（读非栈顶帧）价值中等偏低，已转 `docs/ROADMAP.md`「近期评估转远期」节（含 spike 前置与触发条件）。本清单清空，无需保留明细。

## 已评估关闭/远期（防重复立项，一行结论）

- **func-eval 主动调用业务方法** = **关闭**（async/UI/外设方法 func-eval 必死锁；纯函数触发需求未见）。
- **W2 完整 SetIP/强制返回** = 远期（已转 ROADMAP 2026-09-08）。
- **V2 崩溃自动 dump** = 远期（注入 `DOTNET_DbgEnableMiniDump` 改环境 + spike 不确定；退出码/崩溃判定小增量随 V3 已完成）。
- **W3 数据断点** = 远期（2026-09-10 spike 实测：A `CorDebugValue.CreateBreakpoint` 恒 E_NOTIMPL、B 无创建端，均不可行；用已知写入点条件断点替代）。
- **`frameIndex` 帧选择** = 远期（2026-09-10 转 ROADMAP：便利性增强非能力跃迁，须先 spike 非活动帧可读性）。
- **ClrMD live 内存分析** = ROADMAP（dump 事后分析，live 会话内与 ICorDebug 冲突）。
- **多调试会话并行** = ROADMAP（Engine 实测相互干扰）。

## 2026-09-28 截图能力黑盒实测（batch2）——复核后处置

> 来源：截图三件套（`screenshot`/`screenshot_windows`/`screenshot_displays`）黑盒探索测试，产物 `D:\下载\test\batch2\`（报告 9 条）。**已逐条本机实测复核**，处置如下。

**本轮修复（8 条真缺陷，已随 `8aa9f95` 修复并加回归测试）**
- D1 `element` 序号跨工具口径不一致（说明自相矛盾）→ 说明已明确「序号必须是「无过滤」`ui_find` 的 index」并推荐控件名/AutomationId。
- D2 最小化窗口抓取失败归因「无桌面会话」→ 改为「已最小化，请先还原/改用 screen·region」。
- D3 失败的 element 查找推进全局帧号数十 → `FindForCaptureAsync` 不产帧，成功定位后产一帧。
- D4 `screenshot_displays` 缺「缩放比」→ 恒输出。
- D5 `@active` 特值大小写敏感 → 大小写不敏感。
- D6 前台窗口命名不一致 → 统一「前台窗口」。
- D7 `filePath` 不展开环境变量 → 展开 `%VAR%`。
- D9 最小化主窗多报「尺寸极小」→ 已最小化时不报。

**已评估关闭（防重复立项）**
- D8 `filePath` 静默覆盖已存在文件 = **关闭**（显式指定路径即写入，属预期行为）。
- 观察 3 `hwnd` 非空且无效时不回退 `windowTitle`/`processId` = **关闭**（文档定义为「优先」；陈旧 hwnd 报错比静默换目标更安全）。
- 观察 2 `mode=region` 下 `timeoutSeconds` 被忽略 = **关闭**（文档已限定该参数为「窗口出现」的秒数）。

**待决策（需设计，暂不实施）**
- D1 的**序号口径统一**：是否让 `ui_find` 过滤时并列输出全量序号（如 `[g=3 f=1]`），或让 `screenshot element` 跟随最近 `ui_find` 的过滤上下文——涉及 `ui_*` 契约与 `_lastFind` 缓存语义，需单独评估（含 `UiFrameRegistryTests.Find_And_FindForCapture_ShareSameIndexSequence` 影响）。

## 2026-09-28 截图未覆盖项补测（batch3）——处置

> 来源：对 batch2 遗留未覆盖项的补测，产物 `D:\下载\test\batch3\`（报告 27 项：**PASS 24 / FAIL 0 / UNCOVERED 3**；测试者未读 `src/`）。

**本轮修复（1 条新缺陷）**
- **D6 超长窗口标题静默截断**（>255 字符被截到 255 且无任何提示，看似"标题就这么长"）→ 截断处补 `…` 明示；刻意仍**不返回完整超长标题**（对定位无价值、且会让 `screenshot_windows` 等灌爆上下文）。commit `166cdd5`，回归 `CaptureWindowTitleTests`（3 例）。

**纠错（batch2）**
- batch2 辅助脚本 `img-diff.ps1` 有 bug（`$A/$a` 大小写不敏感同一变量 + `[string]` 类型约束把 Bitmap 强转成字符串 → 比较循环不执行、**恒报 diff=0**）→ 已修；据此**作废 batch2 §5「非前台窗口 `includeCursor` 不叠加」结论**（修正脚本重测：非前台同样叠加，diff=144）。batch2 报告已加更正横幅。
- batch2 §4 两条缺陷（`debug_terminate` 运行态报成功未杀、前台无标题窗备注矛盾）已分别随 `96b11dd` 修复。

**已确认（原存疑）**
- E9 极小窗「尺寸极小」阈值 = `min(宽,高) < 32`（31 触发 / 32 不触发）；图像尺寸 = `GetWindowRect`。
- element 无独立 HWND 回退裁剪（WPF）/ 下拉弹层独立顶层窗 / 越界 / console 无窗 / 旧帧护栏 均符合预期；`filePath` 落盘失败→明确提示 + 内联返回图片；`screenshot_windows` >200 行截断 + 上限提示。

**F3 已覆盖并修复（2026-09-28，副屏改 125% 实测）**
- **非 100% 缩放下按「逻辑像素」上报（真缺陷）**：DPI 感知上下文常量误写 `+4`，而官方定义 `DPI_AWARENESS_CONTEXT_PER_MONITOR_AWARE_V2 = -4` → `SetProcessDpiAwarenessContext` 恒失败（`ERROR_INVALID_PARAMETER` 87）、进程停留 DPI-UNAWARE → `screenshot_displays`/`screenshot` 把 1920x1080 报成 1536x864、缩放报 100%（违背「虚拟屏物理像素 / 原生 1:1」）。已修（commit `d443c3a`），回归 `DpiAwarenessTests`（常量合法性 + 非 100% 显示器矩形须等于 `EnumDisplaySettings` 真实模式）。修复后实测 `display=2` → 1920x1080 / 缩放 125%。
- 注：官方文档**推荐用应用清单**（`dpiAwareness=PerMonitorV2`）设定进程默认 DPI 感知、而非 API 调用（"can lead to unexpected application behavior"）。本库是能力库、必须能自设，故先修 API 常量；**并已给宿主 exe 加清单**（`src/DotNetDebuggerMcp/app.manifest` + csproj `ApplicationManifest`）做双保险——对「直接跑 exe」形态生效（opencode 即是）；`dotnet tool`/`dotnet exec` 形态由 `dotnet` 宿主决定，仍靠库内 `EnsureDpi`。

**仍需环境（低优先）**
- F4 无桌面会话（锁屏/断开）分支：需锁屏/断开会话触发。
- C3 GDI「光标未叠加」失败备注分支：需特殊显卡/RDP 配置，一般可忽略。

## 实施后遗留观察项（低优先）

- screenshot `display=left`/`right` 的**多显示器真机验证**：**已完成（2026-09-28，双屏实机）**——副屏位于主屏**左侧**时，`display=left` 正确命中副屏、`display=right` 正确报「主屏右侧没有相邻显示器」；`mode=screen` 原点/尺寸与虚拟屏一致；跨屏 `region` 原点换算正确。**注：显示器布局可变**（后实测副屏改到主屏**上方** `(0,-1080)`，此时 left/right 均报无相邻、`screen` 原点随虚拟屏变化——行为自洽）。详见 `D:\下载\test\batch3\报告.md` G1/G2/G3。

- U1A：① locator 严格 Name 全等使「UIA Name 随内容变化」控件同 index 二次操作判 stale（既定契约）；② `ui_wait` 释放 gate 后 `window` 跨操作复用（降级轮询，无崩溃证据）。
