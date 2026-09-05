# 01 · 愿景与范围（VISION & SCOPE）

> 状态：**愿景已落地**（P1-P4 大部分实现，2026-09-05；保留作历史背景）。日期：2026-09-05。

## 1. 背景与现状

原仓库（历史名 `ILSpyMcp`，2026-09-05 改名 DotNet-Debugger-MCP）：一个 .NET MCP 服务器（net10.0、PackAsTool），在**进程内**用 NuGet 包 `ICSharpCode.Decompiler`（MIT）实现反编译，全部走 stdio。功能成熟：

- 16 个 MCP 工具（`decompile*`/`list_types`/`signature`/`hierarchy`/`dependencies`/`call_graph`/`search_string`/`field_access`/`interface_usage`/`generic_instantiations`/`call_chain`/`assembly_info`/`cache_stats` 等），CLI 与 MCP 共用执行层。
- 纯元数据层走 PEReader（不加载程序集、不反编译 IL），反编译走 ICSharpCode.Decompiler。
- 严格的 stdout（仅 MCP 协议）/ stderr（日志）隔离；共享缓存；并发回归护栏测试。

**痛点**：项目名带 ILSpy，但实际与 ILSpy 无组织关联，只依赖其 NuGet 包；且只做**静态**反编译，无**动态调试**。

## 2. 目标愿景

拆分为 **5 个程序集（1 个宿主 exe + 4 个库）**，模块化开发，进程永远合一（MCP 与 Web 是同一会话的两个投影）：

```
┌────────────────────────────────────────────────────────────────────────┐
│  #5 DotNetDebuggerMcp（宿主 exe / .NET tool / PackAsTool）              │
│  · CLI 入口 + MCP 服务器（stdio，向 agent 暴露工具）                    │
│  · 装配根：握手 ServerInstructions + 拉起 Session；--web 时同进程起 Web  │
└───────────────┬─────────────────────────────────┬──────────────────────┘
                │ 引                              │ 引
     ┌──────────▼───────────────┐   ┌─────────────▼──────────────────────┐
     │ #3 DotNetDebugger.Session │   │ #4 DotNetDebugger.Web（库）         │
     │ 会话服务：命令串行化        │   │ · Blazor Server + BootstrapBlazor  │
     │ · DebugEvent 事件总线      │   │ · SignalR 电路推送事件 → 面板刷新    │
     │ · 状态快照 + agent 轨迹日志 │   │ · Monaco 互操作代码视图            │
     │ · token/IL→反编译行映射     │   │ · 展示面（MCP 是控制面）            │
     └──────┬──────────────┬─────┘   └───────────────────────────────────┘
            │ 引          │ 引
  ┌─────────▼────────┐  ┌─▼──────────────────────────┐
  │ #2 Engine（库）   │  │ #1 Decompiler（库）          │
  │ 调试引擎底层      │  │ 反编译/静态分析              │
  │ · ClrDebug 封装  │  │ · ICSharpCode.Decompiler    │
  │ · 断点/步进/栈/值 │  │ · PEReader 元数据层（现有迁入）│
  │ · 异常           │  └────────────────────────────┘
  └─────────────────┘
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

### 4.2 先做（WebUI v1）——已定栈 Blazor Server + BB + Monaco 互操作（decisions D4）
> 面板/职责为需求侧描述；具体 BB 组件分工与事件→刷新机制在 P4 细化 spec 中定。
- 服务端：宿主 exe 内嵌 Kestrel 承载 Blazor Server + 静态资源。
- 代码视图：Monaco（Blazor 互操作）只读展示 + 当前行高亮 + 断点 gutter。
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
| 动态调试底座 | **ClrDebug（MIT，ICorDebug COM 全量托管封装）+ Microsoft.Diagnostics.DbgShim（MIT）** + 自研事件循环/断点/栈帧/值树（**已确认**，D3） |
| 不直接引用 | dnSpyEx 调试栈（GPL-3.0、无 NuGet、耦合 Roslyn fork）→ 只当参考；debug-mcp（AGPL-3.0）→ 只借鉴工具面行为 |
| 辅助 | Microsoft.Diagnostics.NETCore.Client / ClrMD（MIT）做只读监控与 dump 事后分析（v1 可后置） |
| Web 推送 | **Blazor Server SignalR 电路**承载调试事件（Session Channel → 组件刷新）—— **已定**（D4） |
| Web 代码视图 | **Monaco 作 Blazor 互操作组件**（read-only + deltaDecorations 断点/当前行）—— **已定**（D4） |
| 前端框架 | **Blazor Server + BootstrapBlazor 组件库**（纯 .NET 全栈，无 React/Node）—— **已定**（D4） |
| IL→反编译行 | 服务端用 SequencePointBuilder 思路（Decompiler 内，同设置产出映射表），事件到行号**服务端解析后**再推浏览器（**独立于前端选型，已定**） |

## 6. 里程碑（已按 D7 定稿）

**顺序原则**：先引擎/MCP 后 Web；总览 spec + 分阶段实施计划。每阶段独立落地交付、可 review。

| 阶段 | 内容 | 交付物 |
|---|---|---|
| **P1 仓库改名与拆分** | 仓库 → DotNet-Debugger-MCP；解决方案拆 **5 项目骨架**；现有反编译/静态分析代码无损迁入 Decompiler 库；命名空间/PackageId/CLI/注册名/README/CHANGELOG/CI 全量同步 | 可构建、全测试通过的改名后仓库 |
| **P2 动态调试引擎 v1** | Engine 库：会话管理(启动/附加/断开) + token/IL 断点 + continue + step into/over/out + 线程/栈/局部变量 + 异常断点 + 统一 DebugEvent 流（前置 spike） | 引擎库 + CLI 驱动验证 + 引擎单测 |
| **P3 会话 + MCP 调试工具面** | Session 库 + 宿主 MCP：debug_* 工具经 stdio 可用；并发串行化护栏 | MCP 工具集 + 端到端验证 + 文档 |
| **P4 WebUI** | Web 库（Blazor Server + BootstrapBlazor + Monaco 互操作）渲染调试过程 + agent 轨迹时间线（回放）；控制面 MCP 与展示面 Web 解耦 | WebUI + 端到端验证 |
| **P5 打磨发布** | README/示例/打包/CI/版本 1.5.0 发布 | 发布版 |

> v2 候选（不阻塞）：表达式求值安全子集、PDB 行断点、模块延迟绑定、EventPipe/ClrMD 旁路、多会话。

## 7. 命名（用户问题 6，已拍板 2026-09-05）

**最终决策**：主项目/仓库名 = **DotNet-Debugger-MCP**（用户：「就 DotNet-Debugger-MCP 了」）。完整映射见 `decisions.md` D6。

命名考虑轨迹（供追溯）：
- 原候选 `DotNet-Tools-MCP` 易与官方 `dotnet tool` 全局工具生态混淆，且未表达反编译+调试定位。
- 「Debugger 只覆盖调试，未覆盖反编译」→ 提出伞词路线（Peek/Insight/X-Ray，dnSpy 的 "Spy" 是先例）+ 两段式命名（伞词定身份，后缀分能力）。
- 用户最终选择直接以 **Debugger** 为主名（接受其调试主导定位；反编译作为静态分析辅助能力并入产品线）。
- 子项目 A（反编译库）与子项目 B（调试引擎）具体库名待实施阶段确认（建议见 D6）。
