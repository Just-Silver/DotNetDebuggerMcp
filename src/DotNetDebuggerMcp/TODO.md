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

**本轮修复（真缺陷）**
- [ ] D1 `element` 序号跨工具口径不一致：`ui_find` 带过滤时返回**过滤后**相对序号，`screenshot element` 用**无过滤全量**序号，同数字异义且截图报成功 → 修：`element` 说明删除「建议先用 ui_find 取 index」的矛盾措辞，明确序号口径并推荐用控件名/AutomationId。
- [ ] D2 最小化窗口抓取失败提示误导（归因「无桌面会话」）→ 修：失败且 `IsIconic` 时给「已最小化，请先还原」文案。
- [ ] D3 失败的 element 查找把全局帧号推进数十（内部 50ms 轮询每次都产帧）→ 修：`FindForCaptureAsync` 不产帧，成功定位后由 `screenshot element` 产帧一次。
- [ ] D4 `screenshot_displays` 缺说明承诺的「缩放比」（100% 时不输出）→ 修：总是输出缩放比。
- [ ] D5 `windowTitle="@active"` 特值大小写敏感 → 修：改大小写不敏感（含 `mode=foreground` 兼容校验）。
- [ ] D6 前台窗口在 `foreground` 与 `windowTitle=@active` 下头部命名不一致 → 修：统一「前台窗口」。
- [ ] D7 `filePath` 不展开环境变量（`%TEMP%\x.png` 生成字面目录）→ 修：落盘前 `ExpandEnvironmentVariables`。
- [ ] D9 最小化主窗被多余备注「尺寸极小，可能不是目标主窗」→ 修：`IsIconic` 时不再报尺寸极小。

**已评估关闭（防重复立项）**
- D8 `filePath` 静默覆盖已存在文件 = **关闭**（显式指定路径即写入，属预期行为）。
- 观察 3 `hwnd` 非空且无效时不回退 `windowTitle`/`processId` = **关闭**（文档定义为「优先」；陈旧 hwnd 报错比静默换目标更安全）。
- 观察 2 `mode=region` 下 `timeoutSeconds` 被忽略 = **关闭**（文档已限定该参数为「窗口出现」的秒数）。

**待决策（需设计，暂不实施）**
- D1 的**序号口径统一**：是否让 `ui_find` 过滤时并列输出全量序号（如 `[g=3 f=1]`），或让 `screenshot element` 跟随最近 `ui_find` 的过滤上下文——涉及 `ui_*` 契约与 `_lastFind` 缓存语义，需单独评估（含 `UiFrameRegistryTests.Find_And_FindForCapture_ShareSameIndexSequence` 影响）。

## 实施后遗留观察项（低优先）

- screenshot `display=left`/`right` 的**多显示器真机验证**待补（2026-09-28 记录）：邻屏解析逻辑已有构造数据单测（`ScreenshotToolTests.ResolveDisplayIndex_PrimaryLeftRightOneBasedAndErrors`），但真实 `EnumerateDisplays()` 在多屏下的坐标/主屏判定（负坐标、各屏 DPI 不一、主屏不在最左等）尚缺端到端验证；`SharpSight.Capture.Tests.DisplayEnumeratorTests.CaptureDisplay_SecondMonitor_*` 在单显示器机器上 Skip。**待有双屏机器时手工跑一次 `screenshot display=left` 与 `display=right` 确认。**

- U1A：① locator 严格 Name 全等使「UIA Name 随内容变化」控件同 index 二次操作判 stale（既定契约）；② `ui_wait` 释放 gate 后 `window` 跨操作复用（降级轮询，无崩溃证据）。
