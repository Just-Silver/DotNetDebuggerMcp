# 01 · 愿景与范围（VISION & SCOPE）

> 状态：草稿（随澄清演进）。日期：2026-09-05。

## 1. 背景与现状

当前仓库 `ILSpyMcp`：一个 .NET MCP 服务器（net10.0、PackAsTool），在**进程内**用 NuGet 包 `ICSharpCode.Decompiler`（MIT）实现反编译，全部走 stdio。功能成熟：

- 16 个 MCP 工具（`decompile*`/`list_types`/`signature`/`hierarchy`/`dependencies`/`call_graph`/`search_string`/`field_access`/`interface_usage`/`generic_instantiations`/`call_chain`/`assembly_info`/`cache_stats` 等），CLI 与 MCP 共用执行层。
- 纯元数据层走 PEReader（不加载程序集、不反编译 IL），反编译走 ICSharpCode.Decompiler。
- 严格的 stdout（仅 MCP 协议）/ stderr（日志）隔离；共享缓存；并发回归护栏测试。

**痛点**：项目名带 ILSpy，但实际与 ILSpy 无组织关联，只依赖其 NuGet 包；且只做**静态**反编译，无**动态调试**。

## 2. 目标愿景

拆分为三个相对独立的项目/模块，模块化开发，最终主项目引用另两个：

```
┌────────────────────────────────────────────────────────────────────┐
│  主项目（对外 MCP 服务器 + WebUI Host）                              │
│  · 对 agent 暴露 MCP 工具（stdio）                                  │
│  · 内嵌调试会话服务 + 事件总线（Channel<DebugEvent>）               │
│  · 拉起 localhost WebUI（HTTP/SSE）实时渲染 agent 主导的调试过程    │
└───────────────┬──────────────────────────────┬────────────────────┘
                │ 引用                          │ 引用
     ┌──────────▼──────────┐         ┌──────────▼──────────┐
     │ 子项目 A：静态反编译  │         │ 子项目 B：动态调试    │
     │ 分析（现有代码改名）  │         │ 引擎（dnSpyEx 式，    │
     │ · ICSharpCode.Decompiler │      │ 新写，ICorDebug 通道） │
     │ · PEReader 元数据层  │         │ · 启动/附加/断点/单步  │
     └─────────────────────┘         │ · locals/栈/线程/求值  │
                                     │ · 事件源（供 Web 与 MCP）│
                                     └────────────────────────┘
```

- **agent 主导**：控制面是 MCP 工具（agent 决策），Web 主要做**过程可视化/回放展示**（人也可能轻量点按钮），像 dnSpyEx 调试窗口的观感，但突出「agent 每步决策 → 调试动作 → 结果」的可回放轨迹。
- **反编译资产打通静态→动态**：现有元数据/反编译用于「下断点前先定位方法 token / 看源码」，动态事件把 `method token + IL offset` 映射回反编译行做高亮（SequencePointBuilder 思路）。

## 3. 平台与运行约束

- .NET 10，Windows 优先（ICorDebug/DbgShim 全平台可用但先 Windows 验证）。
- 许可底线：主项目/库保持 **MIT 兼容**。dnSpyEx（GPL-3.0）与 debug-mcp（AGPL-3.0）**只读参考、不链接不抄码**；可参考/链接的 MIT 资产见调研报告。
- 反编译引擎沿用 NuGet `ICSharpCode.Decompiler`（MIT）；本地 ILSpy 源码主要用于查看 `SequencePointBuilder` 等调试映射能力的参考。
- stdout 纯净原则延续到 WebUI 时代（Web 走 HTTP 端口，不占用 stdio）。

## 4. 范围（本期）

### 4.1 先做（动态调试引擎 v1）
- 启动 / 附加 .NET 目标进程；断开。
- 断点：按方法 token + IL offset（现有元数据层直接产出）；模块未加载延迟绑定（v1 可先只支持已加载模块）。
- 单步：step into / over / out。
- 状态读取：线程列表、调用栈（栈帧 → 方法 → IL offset → 反编译行映射）、局部变量/参数（值树，v1 到「标量 + 简单对象首层字段」）。
- first-chance 异常断点（类型过滤）v1 可选。
- 表达式求值 v1：**安全子集**（AST 静态分析禁止副作用），函数求值经 ICorDebugEval2（可选，失败降级提示）。
- 调试事件统一为 `DebugEvent` 流（Channel），同一引擎同时喂 MCP 与 Web。

### 4.2 先做（WebUI v1）
- 服务端：主项目内嵌 Kestrel，静态资源 + REST（快照/文档）+ SSE（增量）。
- 代码视图：反编译文档（方法/类型级）+ 当前行高亮 + 断点 gutter。
- 面板：调用栈 / 局部变量 / 线程 / 调试事件日志 / agent 决策轨迹时间线（可回放）。
- 控制：主要由 agent 经 MCP 驱动；Web 提供最小人工控制（continue/step/pause）与「跟随 agent」开关。

### 4.3 后做（见 ROADMAP 或开放问题）
- 全功能 C# 表达式求值（Roslyn 编译求值）。
- Edit & Continue —— 运行时不支持（.NET Core），排除。
- Mono/Unity 调试（dnSpy Mono 栈，GPL，不做或另议）。
- 多进程并发调试会话（v1 单会话）。

## 5. 关键技术选型结论（详见 research/01、04）

| 层 | 结论 |
|---|---|
| 动态调试底座 | **ClrDebug（MIT，ICorDebug COM 全量托管封装）+ Microsoft.Diagnostics.DbgShim（MIT）** + 自研事件循环/断点/栈帧/值树 |
| 不直接引用 | dnSpyEx 调试栈（GPL-3.0、无 NuGet、耦合 Roslyn fork）→ 只当参考；debug-mcp（AGPL-3.0）→ 只借鉴工具面行为 |
| 辅助 | Microsoft.Diagnostics.NETCore.Client / ClrMD（MIT）做只读监控与 dump 事后分析（v1 可后置） |
| Web 推送 | **SSE**（`EventSource`，快照+增量模型）；控制指令走普通 REST POST |
| Web 代码视图 | **Monaco Editor**（read-only + `deltaDecorations` 断点/当前行；观感对齐 VS Code/Theia 调试界面） |
| 前端框架 | **React + TypeScript + Vite**；时间线自绘列表；时序/调用图 mermaid.js 按需加载 |
| IL→反编译行 | 服务端用 ICSharpCode.Decompiler `SequencePointBuilder`（同设置产出映射表），事件到行号**服务端解析后**再推浏览器 |

## 6. 里程碑草图（待实施计划细化）

1. M0 仓库/命名落地 + 解决方案拆分（3 项目骨架），现有反编译代码无损迁入子项目 A。
2. M1 动态调试引擎 v1 最小闭环：附加已运行进程 → 断点（token）→ continue → 命中 → 读栈/变量 → step。CLI 驱动验证。
3. M2 事件总线 + MCP 调试工具面（launch/attach/breakpoint/continue/step/stack/variables/…）经 stdio 可用。
4. M3 WebUI v1：SSE + Monaco 渲染上述事件流，agent 轨迹时间线。
5. M4 表达式求值安全子集 + 异常断点 + PDB 行断点增强。
6. M5 打磨：README/示例/打包/CI/端到端护栏（含 stdio 并发回归）。

## 7. 命名（用户问题 6，已拍板 2026-09-05）

**最终决策**：主项目/仓库名 = **DotNet-Debugger-MCP**（用户：「就 DotNet-Debugger-MCP 了」）。完整映射见 `decisions.md` D6。

命名考虑轨迹（供追溯）：
- 原候选 `DotNet-Tools-MCP` 易与官方 `dotnet tool` 全局工具生态混淆，且未表达反编译+调试定位。
- 「Debugger 只覆盖调试，未覆盖反编译」→ 提出伞词路线（Peek/Insight/X-Ray，dnSpy 的 "Spy" 是先例）+ 两段式命名（伞词定身份，后缀分能力）。
- 用户最终选择直接以 **Debugger** 为主名（接受其调试主导定位；反编译作为静态分析辅助能力并入产品线）。
- 子项目 A（反编译库）与子项目 B（调试引擎）具体库名待实施阶段确认（建议见 D6）。
