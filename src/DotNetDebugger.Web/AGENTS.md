# DotNetDebugger.Web 开发指南

Blazor Server 调试展示面（RCL 类库，被宿主 `DotNetDebuggerMcp` 承载）。**产品定位：agent 动作的监视器**——agent 经 MCP 工具反编译/调试时，本 Web 实时可视化「agent 在看哪个程序集/类型/方法、断点停在哪个方法」；人类不开 Web 也能用 agent，Web 是可选的观看席。

## 结构

```
Components/
  App.razor            页面壳：css 引用顺序关键（见下）、Monaco loader、CodeViewer.razor.js
  Routes.razor / _Imports.razor
  Layout/MainLayout.razor   BB Layout 壳（Header 主题切换 + Main）
  Pages/Index.razor       首页
  Pages/Debugger.razor   动态调试页（控制条 + 状态 + 左树右代码 + 面板 + AgentView 订阅）
  Debugger/CodeViewer.razor   Monaco 编辑器封装（.razor.js 是互操作桥）
  Debugger/TypeTree.razor     左侧类型树（程序集→命名空间→类型→成员，BB TreeView 懒加载）
  Debugger/LogPanel.razor     联调诊断面板（最右列，展示 MemoryLog 环形日志）
Services/
  AgentViewContext.cs   宿主→Web「agent 正在看什么」可观察共享状态（核心链路）
  TypeTreeData.cs       程序集类型/成员枚举数据源（纯元数据秒回，一次枚举缓存）
  DocumentStore.cs      反编译文档缓存 + 停点 IL→行映射
  DebugViewService.cs   调试命令/状态查询（经 WebHostBootstrap.Manager 共享会话）
  MemoryLog.cs          进程内环形内存日志（联调诊断，MaxEntries=2000，组件/服务打点）
WebHostBootstrap.cs     宿主→Web 装配入口（静态注入 DebugSessionManager + AgentViewContext）
```

**分层纪律**：
- Web 库只引 `DotNetDebugger.Session` + `DotNetDebugger.Decompiler`，**不反引宿主**。宿主共享状态经 `WebHostBootstrap.Configure(manager, agentView)` 静态注入（组件经 `WebHostBootstrap.Manager` / `.AgentView` 访问）。
- 纯服务端逻辑（TypeTreeData/DocumentStore/AgentViewContext）可单测，放 `tests/DotNetDebugger.Web.Tests`。razor/JS 浏览器行为人工验收。

## 布局纪律（踩坑：代码区无滚动条 / 页面整体滚动）

**严禁硬编码像素高度**（`height: calc(100vh - 190px)` 之类）——不能假设屏幕分辨率/视口大小。高度链必须用 **flex 撑满 + min-height:0**：

- BB `.layout-main` 有确定高度（`min-height: calc(var(--bb-layout-height) - header - footer)`）。
- MainLayout 的 Main 内容区是 `d-flex flex-column` + `min-height:100%`，让页面根可感知高度。
- Debugger 页根：`d-flex flex-column flex-grow-1` + `min-height:0`；左树/右代码列各自 `d-flex flex-column min-height:0`。
- **Monaco 编辑器要内部滚动，其容器必须有确定高度**（flex 链最终落到像素）。CodeViewer 容器**不要设 min-height**——`min-height:400px` 曾导致容器被内容撑破、Monaco 超高 → 无内部滚动 → 撑到浏览器整体滚动。Monaco `automaticLayout:true` 会监听容器 resize 自适应。
- 内容超高时让正确的容器滚动（overflow:auto），而不是页面滚动。

## CodeViewer / Monaco

- `Components/Debugger/CodeViewer.razor.js` 是自研最小互操作桥（`window.dotnetDebuggerMonaco`）：create/setValue/deltaDecorations/revealLineInCenter/dispose。前置：`App.razor` 按序加载 `loader.js` + `editor.main.js`（`require.paths.vs` 指向 `_content/DotNetDebugger.Web/lib/monaco-editor/...`）。
- **App.razor 里 CodeViewer.razor.js 的 script 路径必须写对**：`_content/DotNetDebugger.Web/Components/Debugger/CodeViewer.razor.js`。曾误写成 `Components/Debug/`（源码目录是 `Debugger` 不是 `Debug`），导致桥没加载、Monaco 报 `dotnetDebuggerMonaco is undefined`。RCL 资产路径与源码目录名严格一致，改目录名必须同步改 App.razor。
- Monaco 资产（`wwwroot/lib/monaco-editor`）在 csproj 用 `StaticWebAssetEndpointExclusionPattern` 排除出 MapStaticAssets（自带内容哈希名防二次指纹化），由 `UseStaticFiles` 服务原文件。勿改此排除。

## 图标字体（踩坑：TreeView 箭头不显示）

- BB 默认 IconTheme 是 **fa（FontAwesome）**，TreeView 箭头等图标 class 是 `fa-solid fa-*`。
- **必须引 `BootstrapBlazor.FontAwesome` 包 + App.razor 引 `_content/BootstrapBlazor.FontAwesome/css/font-awesome.min.css`**，否则图标字体不加载、箭头/图标渲染空白（bootstrap.blazor.bundle 不含 fa 字体）。
- App.razor css 顺序：font-awesome → bootstrap.blazor.bundle → fluent 主题。

## 左侧类型树（TypeTree / TypeTreeData，对标 dnSpyEx）

- 层级：**程序集 → 命名空间 → 类型 → 成员**，逐级懒加载（BB `TreeView` `OnExpandNodeAsync`：展开节点才建子项）。
- 成员分类 dnSpyEx 顺序：方法（排除属性/事件访问器 get_/set_/add_/remove_，dnSpyEx 用 `GetPropertyAndEventMethods` 同规则）→ 属性 → 事件 → 字段（排除自动属性 backing field 与字段式事件同名 field）。成员枚举在 `TypeTreeData.EnumerateMembers`。
- 方法叶子节点 model 带 `MethodToken`（int）——停点跟随用 token 精确匹配选中方法。
- **懒加载展开是交互异步的**：外部编程式设 `IsExpand=true` 不会触发 `OnExpandNodeAsync`。要编程式展开到深层节点（如停点跟随 `SelectTypeAsync`），需手动调 `OnExpandAsync` 建子节点挂到 `node.Items` 再设 IsExpand，最后 `_tree.SetActiveItem(leaf)`（它内部展开祖先 + 滚动可视 + StateHasChanged）。
- **BB TreeView 按 Items 引用变化刷新**：改 `_items` 内容后要 `_tree.SetItems(_items)`（换引用 + StateHasChanged），仅 `_items.Add` 不刷新。

## AgentViewContext（agent 动作可视化核心链路）

- 宿主在工具执行时写（`ToolExecutor.RunPipelineAsync` 已 hook：反编译类型/成员 → `AgentViewService.Context.Update(assembly, typeName, member)`）。
- Web 端 `Debugger.razor` 订阅 `WebHostBootstrap.AgentView.Changed` → `InvokeAsync` 转 UI 线程 → `TypeTree.SelectTypeAsync(assembly, type, token?)` 树展开选中 + `ShowTypeAsync` 右侧反编译。
- 只处理 `snap.Revision > _lastAgentRevision`（防同一类型重复反编译反复扰动）。
- AgentView 未注入时访问会抛异常——订阅处 try/catch 防御（非 --web 时页面不可达，但代码要稳）。

## 停点跟随

- 断点命中（轮询 `SessionEventBuffer` 状态跃迁到 Stopped）→ 代码高亮 `ApplyStopHighlightAsync` + 树跟随 `SelectStopTypeAsync`（用 `TopFrame.MethodToken` 匹配方法叶子，选中精确到方法）。
- **无条件跟随**：命中模块 == 当前文档 → 树内直接定位；否则经 Engine `GetModulePathAsync`（模块短名→全路径）反查磁盘文件、`FindTypeByToken`（PEReader 元数据）解析类型，整页切到停点类型/方法（树 + 代码 + 装饰一并跟随）。模块未登记/文件不在磁盘则记 MemoryLog 放弃。

## 动态调试页

- 控制条（launch/演示断点/断开/单步/继续）调 `DebugViewService`（经共享 `WebHostBootstrap.Manager`，与 MCP agent 同一会话）。
- 反编译显示统一走 `ShowTypeAsync(assembly, typeFullName)`：先 `_tree.LoadAssembly`（程序集进树，幂等）再 `DocumentStore.GetOrLoadAsync` 反编译 → CodeViewer。
- 演示目标 `DebugTarget.exe`（含托管代码在 `DebugTarget.dll`）从仓库根上溯 `tests/TestData` 定位。

## BB 用法纪律

- BB 组件参数/事件/方法**用 `bb-llms` CLI 查官方文档**（`bb-llms get TreeView`），禁止凭记忆臆造。
- 本地 `E:\Code\Projects\Externals\DebuggerExternals\BootstrapBlazor` 有完整源码，可读实现细节（如 TreeView 展开机制、node-icon css）。
- BB TreeView 关键参数：`Items`/`OnTreeItemClick`/`OnExpandNodeAsync`/`ClickToggleNode`/`SetActiveItem`/`SetItems`。

## Web 启动（--web）与生命周期

- Web 由宿主 `--web` 分支拉起（`WebHostBootstrap.Build` + `RunWithBrowserAsync`，自动端口 + 拉浏览器，默认直达 `/debugger`）。Web 随 MCP 进程存亡；`--web` 双 host 并联（`Task.WhenAll(mcpTask, webTask)`，任一侧结束等另一侧自然完成）。宿主与 Web 共享同一 `DebugSessionManager` 单例（`WebHostBootstrap.Configure` 注入）。
- **`web_open` 幂等 MCP 工具是规划待办（`docs/ROADMAP.md`），当前未实现**——Web 只能靠宿主 CLI 加 `--web` 拉起。仓库根 `opencode.json` 的 MCP command 现带 `--web` 仅为联调便利，产品方向仍是默认不带（用户可能只用反编译）。
- 日志纪律：Web host 日志必须走 stderr（`LogToStandardErrorThreshold`），严禁写 stdout（MCP 协议帧在 stdout）。

## 验证

- 单测：`dotnet test --project tests/DotNetDebugger.Web.Tests/...`（TypeTreeData/DocumentStore/AgentViewContext 纯服务端）。
- 浏览器验收：`dotnet build src/DotNetDebuggerMcp/... -c Release` 后 `--web` 起 server 手动看。改服务端代码需重编译 + 杀旧进程。
- 回归：宿主全量单测（`tests/DotNetDebuggerMcp.Tests`）不能被 Web 改动破坏。
