# 调研 · 本地 dnSpy 仓库摸底（能否作为库嵌入）

> 路径：`E:\Code\Projects\Externals\dnSpy`（dnSpyEx 续作，GPL-3.0）。日期：2026-09-05。
> 结论速览：调试引擎实现层（Impl + dndbg）**几乎无 WPF/MEF 依赖、很干净**；但项目边界不干净（csproj 用 WindowsDesktop SDK + UseWPF + 引 VisualStudio.Text.UI.Wpf；目录混杂 UI 文件；MEF-Export 装配点分散）。**作为库直接引用不可行**（GPL + 无 NuGet + 工程耦合），只当实现参考。唯一能干净落进纯 net10.0 的是 `dnSpy.Debugger.DotNet.Metadata`（仅依赖 dnlib）。

## 1. 仓库顶层结构
`Build/`、`dnSpy/`（主程序 + Contracts + Roslyn）、`Extensions/dnSpy.Debugger/`（调试引擎）、`Libraries/`、`dnSpy.sln`、`DnSpyCommon.props`、`DnSpyRoslyn.props`、`build.ps1` 等。
调试相关：`Extensions/dnSpy.Debugger/` 含 dnSpy.Debugger、dnSpy.Debugger.DotNet、.CorDebug、.Interpreter、.Metadata、.Mono、Mono.Debugger.Soft、AppHostInfoGenerator、netcorefiles 子模块。
契约层：`dnSpy/dnSpy.Contracts.Debugger*`（4 个）。Roslyn 求值：`dnSpy/Roslyn/`。

## 2. 调试器核心 csproj（均不写 TargetFramework，从 DnSpyCommon.props 继承）
- `DnSpyCommon.props`：`<TargetFrameworks>net48;net10.0-windows</TargetFrameworks>`；常量：DnSpyAssemblyVersion 6.6.0.0、RoslynVersion 5.9.0、DnlibVersion 4.5.0、IcedVersion 1.21.0、MSDiagRuntimeVersion(Microsoft.Diagnostics.Runtime) 1.1.142101、DbgShimVersion 9.0.661903、NewtonsoftJson 13.0.4、DotNetPackageVersion 10.0.11 等。
- `dnSpy.Debugger.DotNet.CorDebug`：`WindowsDesktop SDK + UseWPF`，PackageRef：Iced 1.21.0、Microsoft.Diagnostics.Runtime 1.1.142101、Microsoft.VisualStudio.Text.UI.Wpf 15.5.27130、Microsoft.Diagnostics.DbgShim 9.0.661903。
- `dnSpy.Debugger`：UseWPF，Iced + VisualStudio.Text.UI.Wpf。
- `dnSpy.Contracts.Debugger`：UseWPF（应经链），VisualStudio.Text.UI.Wpf。
- `dnSpy.Contracts.DnSpy`（被 Contracts.Debugger 依赖）：UseWPF+UseWindowsForms、System.ComponentModel.Composition 10.0.11、VisualStudio.Text.UI.Wpf、Ookii.Dialogs.Wpf 等。

## 3. dndbg 层
**无独立 csproj**：是 `dnSpy.Debugger.DotNet.CorDebug` 项目内源码子目录 `dndbg/`（COM/、DotNet/、Engine/ 三子目录，95 cs，命名空间 `dndbg.*`），随项目一起 net48+net10.0-windows 编译；源码零 WPF/零 MEF using。

## 4. Roslyn 求值相关
- `Roslyn.ExpressionCompiler`（git 子模块 dnSpyEx/Roslyn.ExpressionCompiler）：`Core/ExpressionCompiler`（产物 Microsoft.CodeAnalysis.ExpressionEvaluator）与 `CSharp/CSharpExpressionCompiler`（产物 Microsoft.CodeAnalysis.CSharp.ExpressionEvaluator），Import DnSpyRoslyn.props → `netstandard2.0;net10.0-windows`，依赖 Microsoft.CodeAnalysis(.CSharp) **5.9.0**。
- `dnSpy.Roslyn`：UseWPF，Microsoft.CodeAnalysis.CSharp.Features 5.9.0 / VisualBasic.Features 5.9.0、VisualStudio.Language.Intellisense 15.5.27130、VisualStudio.Text.UI.Wpf。
- Roslyn 版本集中在 DnSpyCommon.props：RoslynVersion=5.9.0。

## 5. 引擎层 UI/MEF 依赖（关键结论）
- `Impl/`（59 cs，含 DbgEngineImpl*.cs、DebuggerThread.cs）：**0 个文件含 UI using**；仅 4 文件用 `System.ComponentModel.Composition`（Export 装配点）。
- `dndbg/`（95 cs）：**0 UI、0 MEF**，纯 COM Interop + 引擎封装，依赖仅 System.* + dnlib。
- 全项目 203 cs、944 条 using：UI using 只出现在 3 个文件（`UI/UIDispatcher.cs` 与 2 个 StartDebuggingOptionsPage）；MEF using 分散在约 18 个 *Impl/Provider/Formatter/Hook 文件。
- **结论**：真正做调试的代码几乎无 WPF/MEF，但 csproj 层（UseWPF、Text.UI.Wpf、WindowsDesktop SDK）与文件混合（UI/Dialogs/TextEditor 在目录内）+ MEF 导出外壳，使「整体引用」不可行；若剥离需按文件集合挑非 UI 子集 + 去掉 MEF 外壳 → 高风险高维护。

## 6. 价值定位（对本项目）
- `dndbg` + `Impl` + `DebuggerThread` + `CoreCLRHelper` = **ICorDebug 引导与状态机的最佳协议参考**（clean-room：读协议、不抄码）。
- 表达式求值双模（IL 解释器 + ICorDebugEval）为远期全功能求值提供思路。
- 许可：GPL-3.0 → 不可链接、不可复制代码进 MIT 项目。
