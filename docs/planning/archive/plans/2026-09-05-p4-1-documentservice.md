# P4-1 DocumentService（反编译文档 + IL→行映射）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `DotNetDebugger.Decompiler` 库实现 `DocumentService`——对指定程序集类型产出「干净反编译文本 + 行列表」与「methodToken+IL offset → 反编译文本行号」的语句级映射表，供 P4 Web 代码视图停点语句高亮与断点定位（spec §6）。

**Architecture:** 复用 InProcessDecompiler 的 PEFile/UniversalAssemblyResolver/DecompilerSettings/CSharpDecompiler 构造管线。**关键产出管线（探针 2 实测确证，勿绕开）**：① `DecompileType` 得 SyntaxTree；② **文本输出必须用 `TextWriterTokenWriter` 包 `TokenWriter.WrapInWriterThatSetsLocationsInAST`**（ILSpy 官方 PDB 生成路径，dnSpyEx/VS 同源）——writer 写文本时把真实行列回写 AST 节点，使无 PDB 程序集节点也有真实位置；③ `decompiler.CreateSequencePoints(tree)` 得 `Dictionary<ILFunction, List<SequencePoint>>` → 序列化语句级映射。纯服务端、可单测，落 Decompiler 库 `Document/` 命名空间。

**Tech Stack:** .NET 10 / C# / ICSharpCode.Decompiler 11.0.0.9375（`CSharpDecompiler`/`TokenWriter.WrapInWriterThatSetsLocationsInAST`/`CreateSequencePoints`/`SequencePoint` + `MetadataNaming` 元数据定位）/ xunit.v3

**Spec:** `docs/planning/specs/2026-09-05-p4-webui.md`（§6 文档模型与 IL→行映射——探针 2 实测修正终版）

## Global Constraints

- **无浏览器/无 Web 依赖**：纯服务端组件，放 `DotNetDebugger.Decompiler` 库（`DotNetDebugger.Decompiler.Document` 命名空间），不引 Session/Web/宿主。
- **产出管线禁止用 `tree.ToString()`**：文本必须经 `TextWriterTokenWriter` + `WrapInWriterThatSetsLocationsInAST` 输出（spec §6 探针 2 依据——裸 ToString 节点位置留 (0,0) 映射全错）。行号坐标 = 该 writer 输出的文本真实行号（1-based）。
- **映射降级**（ILSpy #1901 教训）：表达式体/无体成员可能只映射方法首行（最粗粒度可接受）；token 完全无 sequence point 则不产条目（UI 不假高亮）。不做 PDB 语句级增强（本引擎无 PDB 已可语句级，无需额外）。
- **干净文本无行号前缀**：DocumentService 产出供 Monaco 展示的干净源码，不带宿主 stdout 行号前缀/头部块（spec §6）。
- **错误处理中文提示不抛异常**：沿用 InProcessDecompiler 约定，返回结果对象含错误。
- **命名空间/格式一致**：方法 token 用 `MetadataTokens.GetToken(func.Method.MetadataToken)` 转 int（与 Engine DebugBreakpoint.MethodToken int 一致）；类型全名格式与 list_types 一致（`MetadataNaming.FullName`/`FindType`）。
- 测试：新建 `tests/DotNetDebugger.Decompiler.Tests/`，用 `tests/TestData/ILSpyMcp.TestSamples.dll` 稳定类型验证映射正确性。
- 每个 Task 结束提交；提交信息用中文。

---

## 目标文件结构

```
src/DotNetDebugger.Decompiler/
  Document/
    DocumentModels.cs          # SourceDocument/IlToLineEntry 等纯数据模型
    DocumentService.cs         # 反编译文档 + IL→行映射表产出与查询
tests/DotNetDebugger.Decompiler.Tests/
  DotNetDebugger.Decompiler.Tests.csproj
  TestDataPaths.cs
  DocumentServiceTests.cs      # 映射命中/降级/错误/反向查询
```

---

## Task 总览

- **Task 1** 测试项目 + DocumentModels + DocumentService 核心（位置回写 writer 管线 + CreateSequencePoints 映射），单测映射命中
- **Task 2** 映射降级与错误提示（表达式体/无 sp 方法/坏程序集/未找到类型），单测
- **Task 3** 反向查询（行 → 方法 token+ilStart，设断点用）+ 提交

---

## Task 1: 测试项目 + DocumentModels + DocumentService 核心

**Files:**
- Create: `tests/DotNetDebugger.Decompiler.Tests/DotNetDebugger.Decompiler.Tests.csproj`
- Create: `tests/DotNetDebugger.Decompiler.Tests/TestDataPaths.cs`
- Create: `src/DotNetDebugger.Decompiler/Document/DocumentModels.cs`
- Create: `src/DotNetDebugger.Decompiler/Document/DocumentService.cs`
- Test: `tests/DotNetDebugger.Decompiler.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: `ICSharpCode.Decompiler.CSharp.CSharpDecompiler`（`DecompileType`/`CreateSequencePoints`）、`ICSharpCode.Decompiler.CSharp.OutputVisitor`（`TextWriterTokenWriter`/`CSharpOutputVisitor`/`TokenWriter.WrapInWriterThatSetsLocationsInAST`）、`ICSharpCode.Decompiler.DebugInfo.SequencePoint`、`ICSharpCode.Decompiler.Metadata.PEFile`/`UniversalAssemblyResolver`、`System.Reflection.Metadata.Ecma335.MetadataTokens`；`DotNetDebugger.Decompiler.Metadata.MetadataNaming`（`FindType`/`FullName`）
- Produces: `SourceDocument`（AssemblyPath/TypeFullName/Text/Lines/Mapping/Error）、`IlToLineEntry`（MethodToken/IlOffset/EndOffset/Line/Column）；`DocumentService.GetTypeDocument(assemblyPath, typeFullName)`；`GetLineForIlOffset(doc, methodToken, ilOffset)` → int?；`GetIlStartForLine(doc, line)` → (methodToken, ilStart)?

**关键设计（产出管线，探针 2 实测校准）：**
- `SourceDocument.Mapping` 为 `IlToLineEntry` 列表（每可见 SequencePoint 一条，按 methodToken+IlOffset 有序）。
- **token 取法**：CreateSequencePoints 的 key 是 `ILFunction`，`func.Method`（public 字段，IMethod）→ `func.Method.MetadataToken`（EntityHandle）→ `MetadataTokens.GetToken(...)`。
- **命中降级**：无可见 sp 的 token 不产条目；每函数若末尾有 hidden 段（GetSequencePoints 自动补 `[num, CodeSize)` hidden）不产条目。

- [ ] **Step 1: 建测试项目**

`tests/DotNetDebugger.Decompiler.Tests/DotNetDebugger.Decompiler.Tests.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>DotNetDebugger.Decompiler.Tests</RootNamespace>
    <IsPackable>false</IsPackable>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="Microsoft.NET.Test.Sdk" Version="18.9.0" />
    <PackageReference Include="xunit.runner.visualstudio" Version="4.0.0"><PrivateAssets>all</PrivateAssets><IncludeAssets>runtime; build; native; contentfiles; analyzers; buildtransitive</IncludeAssets></PackageReference>
    <PackageReference Include="xunit.v3" Version="4.0.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\..\src\DotNetDebugger.Decompiler\DotNetDebugger.Decompiler.csproj" />
  </ItemGroup>
</Project>
```
把项目加入 `DotNetDebuggerMcp.slnx` 的 `/tests/` 组。
`TestDataPaths.cs`（仿宿主）：逐级上溯 slnx，定位 `tests/TestData/ILSpyMcp.TestSamples.dll`，暴露 `TestSamplesDll`。

- [ ] **Step 2: DocumentModels**

`DocumentModels.cs`:
```csharp
namespace DotNetDebugger.Decompiler.Document;

/// <summary>一个反编译类型文档（代码视图展示源）。文本干净无行号前缀，Lines 按 1-based 索引。</summary>
public sealed record SourceDocument(
    string AssemblyPath,
    string TypeFullName,
    string Text,
    string[] Lines,
    IReadOnlyList<IlToLineEntry> Mapping,
    string? Error = null)
{
    public bool IsSuccess => Error is null;
}

/// <summary>一条「方法 token + IL 区间 → 反编译文本行列」映射（无 PDB 亦有效）。</summary>
public sealed record IlToLineEntry(
    int MethodToken,
    int IlOffset,       // 区间起始（含）
    int EndOffset,      // 区间结束（不含）
    int Line,           // 1-based
    int Column);
```

- [ ] **Step 3: DocumentService 核心（探针校准的产出管线）**

`DocumentService.cs`:
```csharp
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.CSharp.OutputVisitor;
using ICSharpCode.Decompiler.Metadata;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace DotNetDebugger.Decompiler.Document;

public static class DocumentService
{
    public static SourceDocument GetTypeDocument(string assemblyPath, string typeFullName)
    {
        try
        {
            using var module = OpenModule(assemblyPath);   // FileStream + PEFile
            var resolver = new UniversalAssemblyResolver(assemblyPath, false, module.Metadata.DetectTargetFrameworkId());
            var settings = new DecompilerSettings { ThrowOnAssemblyResolveErrors = false };
            var decompiler = new CSharpDecompiler(assemblyPath, resolver, settings);

            var handle = MetadataNaming.FindType(module.Metadata, typeFullName);
            if (handle is null)
                return new SourceDocument(assemblyPath, typeFullName, "", [], [], $"未找到类型 {typeFullName}");

            var typeDef = module.Metadata.GetTypeDefinition(handle.Value);
            var fullName = new FullTypeName(MetadataNaming.FullName(module.Metadata, typeDef));
            var tree = decompiler.DecompileType(fullName);

            // 关键：位置回写 writer 输出（勿用 tree.ToString()）
            var sw = new StringWriter();
            var raw = new TextWriterTokenWriter(sw) { IndentationString = "\t" };
            var locWriter = TokenWriter.WrapInWriterThatSetsLocationsInAST(raw);
            tree.AcceptVisitor(new CSharpOutputVisitor(locWriter, settings.CSharpFormattingOptions));
            var text = sw.ToString();

            var mapping = BuildMapping(decompiler, tree);
            return new SourceDocument(assemblyPath, typeFullName, text,
                text.Replace("\r\n", "\n").Split('\n'), mapping);
        }
        catch (Exception ex)
        {
            return new SourceDocument(assemblyPath, typeFullName, "", [], [], $"反编译失败：{ex.Message}");
        }
    }

    private static IReadOnlyList<IlToLineEntry> BuildMapping(CSharpDecompiler decompiler, SyntaxTree tree)
    {
        var dict = decompiler.CreateSequencePoints(tree);
        var entries = new List<IlToLineEntry>();
        foreach (var (func, sps) in dict)
        {
            var token = MetadataTokens.GetToken(func.Method.MetadataToken);
            foreach (var sp in sps)
            {
                if (sp.IsHidden) continue;
                entries.Add(new IlToLineEntry(token, sp.Offset, sp.EndOffset, sp.StartLine, sp.StartColumn));
            }
        }
        return entries.OrderBy(e => e.MethodToken).ThenBy(e => e.IlOffset).ToList();
    }

    /// <summary>停点定位：IL offset → 反编译行号（二分查 [IlOffset, EndOffset)）。</summary>
    public static int? GetLineForIlOffset(SourceDocument doc, int methodToken, int ilOffset) { ... }

    private static PEFile OpenModule(string assemblyPath) { ... }  // 仿 InProcessDecompiler.OpenModule
}
```

- [ ] **Step 4: 写命中测试并跑绿**

`DocumentServiceTests.cs`:
```csharp
public sealed class DocumentServiceTests
{
    [Fact] GetTypeDocument_BigClass_文本含类型且行数正确()
        // IsSuccess、Text 含 "class BigClass"、Lines.Length == 文本行数

    [Fact] GetTypeDocument_BigClass_映射含BigMethod且offset0有行()
        // 元数据定位 BigMethod token（运行时解析不硬编码），断言 Mapping 含该 token 且
        // 存在 IlOffset=0 的 entry，其 Line ∈ [1, Lines.Length]，且 Lines[Line-1] 非空

    [Fact] GetLineForIlOffset_BigMethod首条sp_返回对应行()
        // 取该 token 第一个 entry，GetLineForIlOffset(token, entry.IlOffset) == entry.Line

    [Fact] GetTypeDocument_BigMethod_映射密度高()
        // 断言 BigMethod 的 entry 数 > 50（探针实测 603，验证语句级而非方法级）
}
```
跑 `dotnet test --project tests/DotNetDebugger.Decompiler.Tests`，绿。

- [ ] **Step 5: 提交**

```bash
git add -A && git commit -m "feat: Decompiler DocumentService——位置回写writer+CreateSequencePoints 语句级 IL→行映射+单测"
```

---

## Task 2: 映射降级与错误提示

**Files:**
- Modify: `src/DotNetDebugger.Decompiler/Document/DocumentService.cs`
- Test: `tests/DotNetDebugger.Decompiler.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: Task 1
- Produces: 强化 SourceDocument.Error/Mapping 语义（坏程序集/未找到类型/表达式体/无 sp 方法）

- [ ] **Step 1: 写边界测试（先红）**

```csharp
[Fact] GetTypeDocument_未找到类型_Error()            // "No.Such.Type" → IsSuccess=false
[Fact] GetTypeDocument_坏程序集_Error()                // 非程序集文件 → Error 中文提示
[Fact] GetTypeDocument_表达式体属性_仅方法首行映射()     // Props 等表达式体成员 entry 少/首行
[Fact] GetTypeDocument_无sp方法_该token无条目()          // 极端方法（如有）不产假行
[Fact] GetTypeDocument_编译器生成类型_Error或空()         // <Module> 之类（若 FindType 拒绝则 Error）
```

- [ ] **Step 2: 实现/补强使绿**

确保：`sp.IsHidden` 过滤；表达式体成员自然只产首行区间（引擎行为，测试断言其存在性）；坏程序集 OpenModule 抛异常被 catch 成 Error；未找到类型走 Error。跑绿。

- [ ] **Step 3: 提交**

```bash
git add -A && git commit -m "feat: DocumentService 映射降级与错误提示单测（表达式体/无sp/坏程序集/未找到类型）"
```

---

## Task 3: 反向查询（行 → 方法 token + ilStart）

**Files:**
- Modify: `src/DotNetDebugger.Decompiler/Document/DocumentService.cs`
- Test: `tests/DotNetDebugger.Decompiler.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: Task 1/2 `SourceDocument.Mapping`
- Produces: `DocumentService.GetIlStartForLine(SourceDocument doc, int line)` → `(int MethodToken, int IlStart)?`——Web 点击行设语句级断点

- [ ] **Step 1: 写测试（先红）**

```csharp
[Fact] GetIlStartForLine_BigMethod某行_返回该方法token与ilStart()
    // 取 doc.Mapping 某 entry 的 Line，GetIlStartForLine(line) → 返回 MethodToken==该 entry token
[Fact] GetIlStartForLine_无映射行_返回null()
```

- [ ] **Step 2: 实现**

行 → 覆盖该行的 entries（Line 列匹配，取该行起始最小 ilStart；行被多 entry 覆盖取先出现的）。

- [ ] **Step 3: 跑绿 + 提交**

```bash
git add -A && git commit -m "feat: DocumentService 反向查询（行→方法token+ilStart，设语句级断点用）"
```

---

## Self-Review 记录（P4-1 计划）

- **Spec 覆盖**：spec §6（终版：位置回写 writer 管线 + CreateSequencePoints 语句级映射 + 查询 + 降级 + 反向设断点）→ Task 1 核心产出、Task 2 降级/错误、Task 3 反向查询。§9.2 装饰行号依赖 → Task 1 Mapping 行号供 P4-2 消费。
- **设计前提确证**：产出管线经探针 2 实测（无 PDB TestSamples：BigMethod 603 sp、IL0→line9 等全有效）。spec §6 方案沿革记录两次实测。非臆测。
- **占位符扫描**：无 TBD。产出管线代码已按探针验证写法给出；token 取法（func.Method.MetadataToken→GetToken）已反射确证 public。GetLineForIlOffset/GetIlStartForLine/OpenModule 标记「{...}」为机械实现（二分/线性查表、FileStream+PEFile），Task 内补全即可。
- **类型一致性**：IlToLineEntry 的 MethodToken(int)/IlOffset/Line 与 Engine 断点/停点类型一致；DocumentService 静态方法与既有 InProcessDecompiler 风格一致。Line 1-based 与 Monaco 一致。
- **依赖**：仅 Decompiler 库内新增，Session/Web/宿主零改动（P4-2 消费）。不引新 NuGet。

---
