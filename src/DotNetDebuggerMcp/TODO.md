# TODO（DotNetDebuggerMcp 宿主近期待办）

> 近期待办，完成一项删一项；远期想法见 `docs/ROADMAP.md`；开发指南见同目录 `AGENTS.md`。

> **2026-09-10 清理归档**：已完成历史已删除（细节见 git log 与 `docs/planning/specs/`）。已交付：W1 现场改写、V3 统一时间线、DB1 敏感脱敏、DB2 按名白名单、D1 对象深读、D2 子进程跟随、V4 语料断言、U1+U1A 全 UIA 化 UI 自动化、V1 一键复验。W3 数据断点、W2 SetIP、V2 崩溃 dump **已转 `docs/ROADMAP.md`**。以下仅保留**未完成近期待办**、**本轮 CoreMes 实证发现**与**防重复立项结论**。

## 一、CoreMes 动态调试实证发现（2026-09-10，47 工具全量实测）

> 来源：对 WPF 应用 CoreMes（离线跑）做 47 工具全量实测，端到端闭环（launch→断点→单步→异常→UI→verify PASS）跑通。按严重度排序；**改工具面须同步根 `README.md` 与 CHANGELOG `[Unreleased]`**。

- [ ] **【中】`debug_run_to` 被残留 step 事件提前吞掉**（Session/宿主）：先前单步的 `StepCompleted` 未消费时，run_to 立即返回“停在 STEP_NORMAL，尚未到目标”并清掉临时断点。**修法**：忽略/消费非目标停点事件继续等待。命中路径正常（清空后实测“临时断点命中并自动移除”）。
- [ ] **【中】`debug_evaluate` 不支持 `$exception` 根**：异常停点下 `debug_variables`/`debug_object` 均支持，唯 evaluate 报“表达式子集不支持 `$`”，只能绕道 debug_object；另 `!(a==b)` 括号不支持。**修法**：evaluate 增加 `$exception` 根（括号可选）。
- [ ] **【中】`search_string` 漏检编译器生成类型（async 状态机）**：`"程序启动"` 位于 `App.OnInitialized` 的 `<OnInitialized>d__3.MoveNext`，返回 0 命中（同步方法字面量正常）；`decompile_member` 亦无法按名定位 `CoreMes.App+<OnInitialized>d__3`。**修法**：`list_types`/`search_string`/`decompile_member` 提供“含编译器生成类型”开关或生成类型 token 直达。
- [ ] **【低】`debug_breakpoint_set` ilOffset 非序列点吐裸 HRESULT**：`ilOffset=5/10` 返回 `Error HRESULT CORDBG_E_UNABLE_TO_SET_BREAKPOINT ... COM component.`（英文裸错）；`ilOffset=1`（序列点）正常。**修法**：返回中文提示并说明“需序列点偏移”。
- [ ] **【低】源文件仅文件名定位搜错模块 + 误导提示**：`sourcePath:"App.xaml.cs"` 绑定成功（CoreMes.dll），却提示“PDB 中未找到源文件 App.xaml.cs（模块 Wpf.Ui.Yin.dll）”。**修法**：合并为“已在本模块解析，其它模块无匹配（忽略）”。
- [ ] **【低】缺失工具候选**：① `debug_terminate` 结束目标进程（现 disconnect 后进程继续跑，复验/测试后无法收口，本次靠外部 taskkill）；② `debug_modules`（已加载模块+符号/绑定状态，断点待绑定排障）；③ 帧选择 `frameIndex`（evaluate/variables 指定非栈顶帧）。
- [ ] **【低】描述修正**：`debug_attach`「冻结在 Main 前」对**已运行进程**错误（应“当前执行点”）；`debug_evaluate` 未注明不支持 `$exception`；`debug_breakpoint_set` 未提示 ilOffset 需序列点；`search_string` 未说明不含生成类型；`debug_step` 未提示断点上重复命中；`debug_stack` 空栈可补“async 状态机帧常见”。

## 三、已评估关闭/远期（防重复立项，一行结论）

- **func-eval 主动调用业务方法** = **关闭**（async/UI/外设方法 func-eval 必死锁；纯函数触发需求未见）。
- **W2 完整 SetIP/强制返回** = 远期（已转 ROADMAP 2026-09-08）。
- **V2 崩溃自动 dump** = 远期（注入 `DOTNET_DbgEnableMiniDump` 改环境 + spike 不确定；退出码/崩溃判定小增量随 V3 已完成）。
- **W3 数据断点** = 远期（2026-09-10 spike 实测：A `CorDebugValue.CreateBreakpoint` 恒 E_NOTIMPL、B 无创建端，均不可行；用已知写入点条件断点替代）。
- **ClrMD live 内存分析** = ROADMAP（dump 事后分析，live 会话内与 ICorDebug 冲突）。
- **多调试会话并行** = ROADMAP（Engine 实测相互干扰）。

## 四、实施后遗留观察项（低优先）

- U1A：① locator 严格 Name 全等使「UIA Name 随内容变化」控件同 index 二次操作判 stale（既定契约）；② `ui_wait` 释放 gate 后 `window` 跨操作复用（降级轮询，无崩溃证据）。
