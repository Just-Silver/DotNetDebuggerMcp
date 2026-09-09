# 2026-09-09 实施前计划审查裁定（Plan Review Verdicts & Binding Corrections）

> **审查状态（2026-09-09 完成）**：10 份计划全部通过 plan review 循环——DB2/D2/V4/V1/W3 首轮 Approve；W1/DB1/U1 re-review Approve；D1 round2 Approve（含 round1 修复）；V3 round3 Approve（含 round1/round2 修复）。修正已全部写回计划正文（commits `4dcb3d8`/`081a1e3`/`5df1da2`/`a3f87e0`/`d2400e0`/`待`）。**可进入实施**（按 subagent-driven-development，实施中任务级 reviewer 复核代码 diff）。
> 每份计划实施收尾后，控制器把对应修正回写进计划正文（保持计划文件真实）。

## 结论摘要

| 计划 | 结论 | 必修 |
|---|---|---|
| W1 debug_set | Issues Found | 是 |
| V3 debug_timeline | Issues Found | 是 |
| DB1 脱敏 | 有条件通过 | 是 |
| D1 debug_object | Issues Found | 是 |
| D2 子进程 | Approve | 否（2 低建议） |
| DB2 白名单 | Approve | 否（1 低建议） |
| V4 语料 | Approve | 否 |
| U1 ui_* | 有条件通过 | 是 |
| V1 debug_verify | Ready | 否（5 建议） |
| W3 数据断点 | Ready | 否（3 建议） |

---

## W1 —— 必修修正（re: 审 W1）
1. Task1 spike：`WriteProbe.Run(Holder h, Holder alt, int[] arr, int seed)` 无 `n` 参数——候选测入口即存活的 **`seed` 参数**（Scalar("6") 写回读回），`j` 局部可作补充；删 `n`/「seed+1」残留注释。
2. Task2 `WritePathTests` **必须补保留用例**：写参数 `seed`（root="seed"）→ 读回新值断言（防 spike 临时文件删除后「栈上值类型局部/参数 ✅」无长期护栏）。
3. Task4 代码 `DotNetDebugger.Engine.Models.PathNode` → 裸 **`PathNode`**（`DotNetDebugger.Session`，与全仓一致）；Task3 草稿 `ExpressionNode.PathNode` 非嵌套 → 裸 `PathNode`。
4. e2e 错误面「value=`abc` 写整型字段」预期文案：`abc` 会被 `WriteValueParser` 解析为 `CopyPath` → 引擎报「目标 X 是值类型，请给字面量数字/bool…」——按引擎真实输出断言，不写死「按目标类型/文法」。
5. Task2 Files 方法名以 Step 正文为准：`WriteScalarToGeneric` / `ReadFieldIsInitOnly`（不是 `ConvertScalarToBytes`/`RefuseReadonly`）。
6. `WriteScalarToGeneric` 的目标种类分派直接镜像 `DebugEngineCore.ReadScalarRaw`（`g.Type` 布尔/字符/整型/浮点分派已现成，勿另查 Externals）。

## V3 —— 必修修正
1. Task1 历史测试断言改：投喂 `MaxHistory+5`(=505) 条 BreakpointHit + **1** 条 EngineLog（共 506）→ 500 上限挤掉最早 **6** 条 BP，EngineLog 保留 → `Assert.Contains(EngineLog)`；删未用 `dropped`；注释同步 6 条。
2. 测试接缝竞态：internal `Start(IAsyncEnumerable<DebugEvent>)` **返回消费者 Task**（public `Start(DebugSession)` 丢弃），测试 `await` 后 `SnapshotHistory().Count==MaxHistory` 再断言。
3. Task3 launch 退出码测试 **launch 后先 `ContinueAsync`** 再轮询 `ExitCode`（LaunchAndAttach 返回时冻结 Main 前，不继续永不退出）。
4. Task3 Exited 捕获：`Activate` 返回后（目标冻结、不会退出）再注册/补设 `process.Exited` 处理器并**闭包捕获 `active`** 写 `active.ExitCode`；勿在早注册的处理器里走 `Manager.Active`（会话被替换后会把旧进程退出码写到新会话）。
5. Task4 attach-Exited 提示同步到 debug_state 与 debug_timeline 状态行（spec §4.6：attach「退出码不可得」）。
6. 删除 Task2 Produces 的 `TimelineKind`（Task4 用内联 `validKinds`，避免双份清单）；明确「v1 只取最近 N 条、不做 start-end 翻页（拍板 §4.4/输出约定）」。

## DB1 —— 必修修正
1. **补测试数据改造步**：`generate-testdata.ps1` 给 `class Bag`（脚本内最后一个类型，字段表末尾追加不位移）增敏感字段 `Password`/`Token`，bag 模式 Main 初始化器赋真实凭据形值（如 `"hunter2"`、`"Bearer eyJ…"`）；重跑脚本。**勿在 Bag 的 A/S 之间插字段**（`Assert.Contains("A, S")` 可用字段清单不破）；不动既有方法体。
2. Task2 Step2 `debug_evaluate` 出口：删伪代码残句 `Redact(result.TypeName is "System.String" ? null : null, …)`，最终形态 = ① `IsSensitiveExpression(expression)` 命中 → 值整行占位；② 未命中对 `result.Display` 走 `LooksLikeSecret`；③ 命中时标量行同段附**单次提示**（与 debug_variables 同文案）。
3. `IsSensitiveExpression` 标识符正则补 `-`：`[A-Za-z_][A-Za-z0-9_-]*`（对齐 DebugMCP）。
4. DB1 计数提示与 DB2 白名单叠加：若同批落地，计数放**白名单过滤之后**（只数可见命中值）。

## D1 —— 必修修正
1. **终值语义收口**：`ReadObjectAtPath` 定位到终值后显式校验——null 引用 / 非对象或数组（标量 CorDebugGenericValue / 字符串）→ 抛中文「该路径不是对象/数组（当前为标量/字符串/null），请给对象或数组路径」；宿主 children 空兜底只留给「空对象/空数组」。
2. **`$exception` 路径支持**：ExpressionParser 不接受 `$` 开头 → `debug_object` 工具层对 `$exception` / `$exception.…` **前缀特判**（根固定 `$exception`，剩余段再经 ExpressionParser 解析取 Segments）；写进 Task2 工具实现步骤。
3. **时序**：Engine 测试用 `"drill 5"`（沿用 bag 模式 delay 5s），宿主 e2e 用 `debug_launch … drill 0` 不受影响。
4. 名称修正：`DebugEngineCore` 现无 `ResolvePathValue`/`FindThread`——Task1 先做小重构（把 `ReadPathValue:771` 的「根解析+逐段求值→EvalValue」抽成私有 `ResolvePathValue(thread, root, segments)`；线程查找提为 `FindThread(threadId)`），再在其上实现。
5. 环断言写法：`deep.Display` 不会有 `<cyclic>`（根的 Display 是「N 字段」）——测试沿 `Next` 链递归 children 找某层 `<cyclic>`。
6. 数组递归补测试：`Drill(DrillNode root, int[] arr)`（签名改不改 token，锚点按名读），`debug_object "arr" depth=1` 断言 3 个元素。
7. **limit 参数落地**：`ReadObjectAtPathAsync(…, int depth, int limit = 32, …)` 与展开器逐层透传（默认 32，宿主钳制 1-128），非 no-op；超限提示「共 N 个（前 M）」。
8. `ExpandNode` 别对引用无条件先 `ReadValue(expand:)`（避免每层重复全量展开）；default/叶路径按需浅读。

## D2 —— 非阻塞建议（采纳为低成本项）
1. `parentMap` 空且 `currentPid>0` 时附一句「无法判定父子关系（Toolhelp 快照失败）」。
2. 子孙行标注尽量贴近 spec「按 Depth 缩进」形态（Depth 1/2/3 前缀）。

## DB2 —— 非阻塞
1. 删 `MatchName` 中 `if (scope=="exception" && name=="$exception") …` 空转行。

## V4 —— 通过；ContractData 首跑可能全绿（护栏在），勿因未先红怀疑。反射片段 `typeof(...).Assembly` 填宿主程序集类型。

## U1 —— 必修修正
1. **语义反查模块路径**：`Process.MainModule` 是 apphost 原生 exe（无 CLI 元数据）→ 取主模块后若为 `.exe` 尝试**同名 `.dll`**（`Path.ChangeExtension` 存在则用 dll）喂 PEReader；dll 不存在再降级 null。否则语义标注真实链路永远空转。
2. **`Mouse.Scroll` 方向**：FlaUI `MouseTests` 实证 `Scroll(+n)` 上滚、`Scroll(-n)` 下滚（MOUSEEVENTF_WHEEL 正负约定）→ `direction=="up" ? lines : -lines`；修正现有反向映射。滚动生效断言尽量用 ScrollPattern 前后 `VerticalScrollPercent` 对比（不可用则退化为仅返回文案，标记环境相关）。
3. **语义匹配子串**：`UiSemanticResolver.Lookup` 用 `member.Contains(elementName, OrdinalIgnoreCase)`（对齐 spec §5「子串忽略大小写」），非精确。
4. Task2 单测素材用 **DebugTarget.dll**（`DebugTarget.exe` 是 apphost 无元数据）；反查已知成员（如 `WorkBag`）断言候选含 `Program.WorkBag`，未知名返回 null。
5. Task3 UiSampleApp 增**双击**逻辑（如 ListBox 双击切换计数 Label）；e2e #4 断言返回含「物理双击」且（可选）双击后 Label 变化。
6. e2e #5 滚动生效用 ScrollPattern 百分比或 IsOffscreen 判定（WinForms ListBox 非虚拟化时「树里能看到末尾项」滚动前后都为真，不能作断言）。
7. Task1 加显式 fallback 注：本地无 UIInspect.MCP → 线上取 `ChrisPulman/UIInspect.MCP` main 的 `FlaUiAutomationBackend.cs`/`FlaUiAutomationSession.cs`/`UIInspect.MCP.Windows.csproj`；若 `Interop.UIAutomationClient`/`System.Management` 在 net10.0 报 NU1701/资产不兼容，改 PackageDownload+HintPath 同法。

## V1 —— Ready，采纳建议
1. VerifyService **自含** typeName+memberName→token 解析（Services 不得反向引用 Tools；用 Decompiler `MemberResolver`/模块枚举），不引用 `ResolveBreakpointTargetAsync`（该名不存在，`DebugBreakpointTool` 现为 `SetResolvedAsync`/`ParseBreakpointIdText`）。
2. 「无 build 时 PATH 命令」表述改「完整路径或相对 CWD（维持 debug_launch 现语义，不搜 PATH）」。
3. build 分支拼接启动时**产物路径含空格需引号**（记录边界）。
4. `assert.breakpointIndex` 口径落死 =「场景内第 N 个 breakpoint 步骤（0-based 按出现顺序）」。
5. README 场景格式注明「output 当前只支持 contains」。

## W3 —— Ready，采纳建议
1. dataPath 分支隐含**需停态**：先在某断点停住（默认最近停点线程）才能设数据断点；进程运行中设 dataPath → 中文提示（如「请先在目标方法断点停住后设数据断点」）。
2. 值断点命中识别**以 spike 实测回调形态为准**（`BreakpointManager.MatchContent` 按 模块+token+IL 匹配可能认不出无 token 的值断点）。
3. A/B 契约条件断点注明「condition 按命中时现场求值」（命中线程可能与注册线程不同）。
