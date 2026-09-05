# 调研 · 本地 ILSpy 仓库摸底（反编译库 / 调试映射能力）

> 路径：`E:\Code\Projects\Externals\ILSpy`（icsharpcode/ILSpy，MIT）。日期：2026-09-05。
> 结论速览：本仓库**完全可以从源码构建出 `ICSharpCode.Decompiler` 库**（netstandard2.0，MIT）；调试映射组件 `SequencePointBuilder` 内嵌于此库但为 **internal**（不可外部直接引用，可当参考实现，ILSpy 的调试器在姊妹仓库 icsharpcode/ILSpy.Debugger，本 checkout 不含）。

## 1. 顶层目录与核心库
- 核心库源码：`ICSharpCode.Decompiler/`；项目 `ICSharpCode.Decompiler/ICSharpCode.Decompiler.csproj`。
- 其他：ICSharpCode.BamlDecompiler、ICSharpCode.Decompiler.Generators（分析器）、.PowerShell、.TestRunner、.Tests、ICSharpCode.ILSpyCmd、ICSharpCode.ILSpyX、ILSpy（UI 主程序）、ILSpy.Tests(.Windows)、ILSpy.ReadyToRun、TestPlugin 等；根文件 ILSpy.sln / ILSpy.Desktop.slnf / ILSpy.VSExtensions.slnx / Directory.Build.props / Directory.Packages.props / VERSION（构建生成，本地未见）。

## 2. ICSharpCode.Decompiler.csproj
- **TargetFramework：`netstandard2.0`**，LangVersion 14。
- PackageReference（运行依赖）：`System.Collections.Immutable 9.0.0`、`System.Reflection.Metadata 9.0.0`；其余 Microsoft.Sbom.Targets / SourceLink / NetAnalyzers 均分析期 PrivateAssets。
- 引用 `ICSharpCode.Decompiler.Generators`（分析器，ReferenceOutputAssembly=false）；`Microsoft.NETCore.App.Ref [8.0.0]` PackageDownload。

## 3. SequencePointBuilder
- 路径：`ICSharpCode.Decompiler/CSharp/SequencePointBuilder.cs`
- 命名空间 `ICSharpCode.Decompiler.CSharp`；`class SequencePointBuilder : DepthFirstAstVisitor`，**internal**。
- 作用：遍历反编译语法树，收集每个 C# statement 关联的 IL 指令区间 `[Offset, EndOffset)` → 形成 IL↔源位置映射（供调试器把当前 IL offset 映射到反编译源码行）。
- ⚠️ 列偏移与 CSharpFormattingOptions 强耦合：**用哪个 DecompilerSettings 出文本，必须用同一设置跑它**。

## 4. Debugger 相关
- ILSpy.sln 含 18 项目，**无独立 Debugger 项目**；调试支持（SequencePointBuilder 及 `ICSharpCode.Decompiler.DebugInfo` 命名空间）内嵌库本体，ILSpy 调试器在姊妹仓库 `icsharpcode/ILSpy.Debugger`。

## 5. 版本标识
- csproj PackageVersion 为 `8.0.0.0-noversion` 占位，构建目标 `ILSpyUpdateAssemblyInfo` 从根 `VERSION` 文件注入真实版本；本地未见 VERSION → 静态无法定版。

## 6. 对本项目的意义
- 现有 NuGet `ICSharpCode.Decompiler 11.0.0.9375` 已满足反编译；本地源码可查 SequencePointBuilder/ILSpy.Debugger 的**映射算法与边界处理参考**（ILSpy issue #1901：Release/异常路径无 sequence point / 区间重叠 / 列不可靠等，见 research/04 §5）。
