# P4-1 DocumentService（反编译文档 + 方法位置表）Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 在 `DotNetDebugger.Decompiler` 库实现 `DocumentService`——对指定程序集类型产出「干净反编译文本 + 行列表」与「方法 token → 声明行/方法体范围」位置表，供 P4 Web 代码视图做**方法级**停点高亮与断点定位（spec §6）。

**Architecture:** DocumentService 复用 InProcessDecompiler 的 PEFile/UniversalAssemblyResolver/DecompilerSettings/CSharpDecompiler 构造管线，产出 SyntaxTree 文本；**方法位置表用纯文本结构解析**（方法名 token → 在反编译文本中定位签名行 + 括号配平求方法体范围），不依赖 AST 节点位置。**设计前提（已实测确证，见 spec §6）**：无 PDB 程序集反编译 AST 节点 StartLocation 全为 (0,0)、ILFunction 无原始 IL offset——语句级 IL→行映射不可行，v1 只做方法级（与 dnSpy 一致）；PDB 语句级映射列 v2。纯服务端、可单测，落在 Decompiler 库 `Document/` 命名空间（`DotNetDebugger.Decompiler.Document`）。

**Tech Stack:** .NET 10 / C# / ICSharpCode.Decompiler 11.0.0.9375（`CSharpDecompiler.DecompileType` + `MetadataNaming`/`MemberResolver` 元数据定位）/ xunit.v3

**Spec:** `docs/planning/specs/2026-09-05-p4-webui.md`（§6 文档模型与方法级映射——2026-09-05 实测修正版）

## Global Constraints

- **无浏览器/无 Web 依赖**：纯服务端组件，放 `DotNetDebugger.Decompiler` 库（`DotNetDebugger.Decompiler.Document` 命名空间），不引 Session/Web/宿主。
- **方法级映射是 v1 天花板**（实测确证）：不做语句级 IL→行映射（无 PDB 不可能）；不做 PDB 语句级（列 v2）。所有 UI 高亮/断点定位基于「方法 token → 方法声明行范围」。
- **干净文本无行号前缀**：DocumentService 产出供 Monaco 展示的**干净源码**（SyntaxTree.ToString()），不带宿主 stdout 的 `行号\t` 前缀/头部块（spec §6）。行号坐标 = 干净文本行号（1-based）。
- **方法位置表文本解析**：方法名 token 已知（元数据），从反编译文本定位方法声明。**先实测验证解析命中率**（对 TestSamples 的 BigClass 等多样方法），不臆造正则。
- **错误处理中文提示不抛异常**：沿用 InProcessDecompiler 约定，返回结果对象含错误。
- **命名空间/格式一致**：方法 token 用 `MetadataNaming.FormatToken`；类型全名格式与 list_types 一致。
- 测试：新建 `tests/DotNetDebugger.Decompiler.Tests/`，用 `tests/TestData/ILSpyMcp.TestSamples.dll` 稳定类型验证位置表正确性。
- 每个 Task 结束提交；提交信息用中文。

---

## 目标文件结构

```
src/DotNetDebugger.Decompiler/
  Document/
    DocumentModels.cs          # SourceDocument/TypeMethodPosition 等纯数据模型
    DocumentService.cs         # 按类型全名产出 SourceDocument（文本+行+方法位置表）
tests/DotNetDebugger.Decompiler.Tests/
  DotNetDebugger.Decompiler.Tests.csproj
  DocumentServiceTests.cs      # 方法位置表命中率/错误提示/文本解析边界
```

---

## Task 总览

- **Task 1** 测试项目 + DocumentModels + DocumentService 核心（反编译文本 + 方法位置表文本解析），单测多方法命中
- **Task 2** 解析边界与错误提示（嵌套类型/重载/属性访问器/表达式体/坏程序集），单测
- **Task 3** 反向查询（行 → 所在方法 token，断点定位用）+ 提交

---

## Task 1: 测试项目 + DocumentModels + DocumentService 核心

**Files:**
- Create: `tests/DotNetDebugger.Decompiler.Tests/DotNetDebugger.Decompiler.Tests.csproj`
- Create: `src/DotNetDebugger.Decompiler/Document/DocumentModels.cs`
- Create: `src/DotNetDebugger.Decompiler/Document/DocumentService.cs`
- Test: `tests/DotNetDebugger.Decompiler.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: `ICSharpCode.Decompiler.CSharp.CSharpDecompiler`（`DecompileType`）、`ICSharpCode.Decompiler.Metadata.PEFile`/`UniversalAssemblyResolver`；`DotNetDebugger.Decompiler.Metadata.MetadataNaming`（`FindType`/`FullName`）；`System.Reflection.Metadata` 枚举方法 token
- Produces: `SourceDocument`（AssemblyPath/TypeFullName/Text/Lines/Methods/Error）、`TypeMethodPosition`（MethodToken/Name/DeclLine/EndLine/IsAccessor）；`DocumentService.GetTypeDocument(assemblyPath, typeFullName)`

**关键设计（文本解析方法位置表）：**
- 反编译文本 = `SyntaxTree.ToString()`（干净）。行号 1-based。
- 方法 token 收集：从元数据枚举类型的全部方法定义（含属性/事件访问器，token 稳定）。
- 文本解析：对每个 token 对应的方法名，在文本中定位 `{Name}(` 的出现（**排除注释/字符串**——v1 先扫描定位，注释含方法名时可能误命中，Task 2 处理）；从该行前溯到签名起始行（上一空行或 `{`/`;` 后）；方法体范围用**花括号配平**（从签名行首个 `{` 起配平到匹配 `}`）。
- 先小步实测：Task 1 Step 4 用探针打印 TestSamples 若干类型的方法定位结果，校准解析规则后再写死。

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
把项目加入 `DotNetDebuggerMcp.slnx` 的 `/tests/` 组。加 `TestDataPaths.cs`（仿宿主：逐级上溯 slnx 定位 `tests/TestData/ILSpyMcp.TestSamples.dll`）。

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
    IReadOnlyList<TypeMethodPosition> Methods,
    string? Error = null)
{
    public bool IsSuccess => Error is null;
}

/// <summary>一个方法在反编译文本中的位置（方法级映射，无 PDB 天花板）。</summary>
public sealed record TypeMethodPosition(
    int MethodToken,
    string Name,
    int DeclLine,          // 声明起始行（1-based）
    int EndLine,           // 方法体结束行（含 } 所在行；表达式体/无体为声明行）
    bool IsAccessor);      // 是否为属性/事件访问器（get_/set_/add_/remove_）
```

- [ ] **Step 3: DocumentService 核心**

`DocumentService.cs`:
```csharp
using ICSharpCode.Decompiler.CSharp;
using ICSharpCode.Decompiler.Metadata;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;

namespace DotNetDebugger.Decompiler.Document;

public static class DocumentService
{
    public static SourceDocument GetTypeDocument(string assemblyPath, string typeFullName) { ... }

    // 反编译管线（仿 InProcessDecompiler.Execute）：PEFile + UniversalAssemblyResolver +
    // DecompilerSettings{ThrowOnAssemblyResolveErrors=false} + CSharpDecompiler
    // 1) MetadataNaming.FindType(module.Metadata, typeFullName) 定位 handle；未找到返回 Error
    // 2) FullTypeName 换算：new FullTypeName(MetadataNaming.FullName(reader, typeDef))
    // 3) decompiler.DecompileType(fullTypeName) → text = tree.ToString() → Lines
    // 4) BuildMethodPositions(reader, typeDef, text) 文本解析方法位置表
    // 异常 catch 返回 Error（中文提示）

    private static IReadOnlyList<TypeMethodPosition> BuildMethodPositions(
        MetadataReader reader, TypeDefinition type, string text) { ... }
    // 元数据枚举 type.GetMethods()（含访问器），每方法名到文本定位：
    //   FindMethodSignatureLine(text, methodName) → 声明行（定位 {Name}(，排除注释；前溯签名起始）
    //   FindMethodBodyEnd(text, declLine) → 花括号配平求 EndLine（表达式体/接口无体回退 declLine）
}
```

- [ ] **Step 4: 探针校准解析规则（先实测再写死）**

临时在测试项目写探针，对 `ILSpyMcp.Samples.BigClass`（BigMethod/BigHelper/BigHelper2 多样方法）打印「方法名 → 文本中定位到的声明行 + 该行文本」，肉眼校验定位规则（方法名行号、签名起始、括号配平）。校准后删探针、固化规则到 BuildMethodPositions。

- [ ] **Step 5: 写命中率测试并跑绿**

`DocumentServiceTests.cs`:
```csharp
public sealed class DocumentServiceTests
{
    [Fact]
    public void GetTypeDocument_BigClass_文本含类型且行数正确()
        // 断言 IsSuccess、Text 含 "class BigClass"、Lines.Length == 文本行数

    [Fact]
    public void GetTypeDocument_BigClass_方法位置表含BigMethod且声明行在文本内()
        // 运行时用 MemberResolver/元数据查 BigMethod 的 token，断言 Methods 含该 token，
        // DeclLine 在 1..Lines.Length，且 Lines[DeclLine-1] 含 "BigMethod"
    // 更多：BigHelper/BigHelper2 各命中；声明行文本确实含方法名
}
```

- [ ] **Step 6: 提交**

```bash
git add -A && git commit -m "feat: Decompiler DocumentService——反编译文档+方法位置表（文本解析，无PDB方法级映射）+单测"
```

---

## Task 2: 解析边界与错误提示

**Files:**
- Modify: `src/DotNetDebugger.Decompiler/Document/DocumentService.cs`
- Test: `tests/DotNetDebugger.Decompiler.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: Task 1
- Produces: 强化 SourceDocument.Error / Methods 语义（坏程序集/未找到类型/访问器/重载/表达式体）

- [ ] **Step 1: 写边界测试（先红）**

```csharp
[Fact] GetTypeDocument_未找到类型_Error()          // "No.Such.Type" → IsSuccess=false
[Fact] GetTypeDocument_坏程序集_Error()              // 非程序集文件 → Error 中文提示
[Fact] GetTypeDocument_Props_静态属性访问器在位置表()   // Props 类型 get_X/set_X → IsAccessor=true 且命中
[Fact] GetTypeDocument_ManyOverloads_各重载都命中()     // 21 个 Do 重载各自独立行（名字相同靠参数区分）
[Fact] GetTypeDocument_表达式体成员_EndLine合理()       // 表达式体方法 EndLine==DeclLine 或仅一行
```

- [ ] **Step 2: 实现/补强使绿**

处理：注释/字符串含方法名误命中（定位时跳过 `//`、`/* */`、字符串字面量内的 `Name(`）；重载同名方法——用「方法名 + 参数个数/签名子串」二次区分（按 token 的方法签名 param 数从元数据取，匹配 `Name(` 后括号内参数数）；访问器 get_/set_ 名前缀剥离后定位（文本里属性访问器显示为 `get { ... }`/`get => ...`，无 `get_(` 字面——需按访问器语义处理：方法名是元数据 `get_X`，文本里是属性 `X` 的 `get` 块——Task 2 需专门处理，必要时访问器位置并入属性声明行）。表达式体 `=> expr;` 无 `{` → EndLine=DeclLine。跑绿。

- [ ] **Step 3: 提交**

```bash
git add -A && git commit -m "feat: DocumentService 解析边界（重载/访问器/表达式体/注释过滤）+错误提示单测"
```

---

## Task 3: 反向查询（行 → 方法 token）

**Files:**
- Modify: `src/DotNetDebugger.Decompiler/Document/DocumentService.cs`
- Test: `tests/DotNetDebugger.Decompiler.Tests/DocumentServiceTests.cs`

**Interfaces:**
- Consumes: Task 1/2 `SourceDocument.Methods`
- Produces: `DocumentService.GetMethodAtLine(SourceDocument doc, int line)` → `TypeMethodPosition?`——Web 点击行定位所属方法（设方法级断点用）

- [ ] **Step 1: 写测试（先红）**

```csharp
[Fact] GetMethodAtLine_BigMethod声明行_返回BigMethod()   // line=BigMethod.DeclLine → 返回该位置记录
[Fact] GetMethodAtLine_方法体内行_返回所属方法()
[Fact] GetMethodAtLine_两方法之间空行_返回null()
```

- [ ] **Step 2: 实现**

在 Methods（按 DeclLine 有序）上二分/线性找「line ∈ [DeclLine, EndLine]」的记录。

- [ ] **Step 3: 跑绿 + 提交**

```bash
git add -A && git commit -m "feat: DocumentService 反向查询（行→所属方法，设方法级断点用）"
```

---

## Self-Review 记录（P4-1 计划）

- **Spec 覆盖**：spec §6（修正版）方法级方案 → Task 1 位置表、Task 2 边界、Task 3 反向；§9.2 装饰行号依赖 → Task 1 产出 DeclLine 供 P4-2 消费。Gap：模块列表/类型列选是 P4-2 UI 职责，不在本计划。
- **设计前提确证**：无 PDB 时 AST 节点 StartLocation=(0,0)、ILFunction 无原始 offset——已实测探针确证（BigClass 全方法 (0,0)），spec §6 已按此修正，非臆测。
- **占位符扫描**：Task 1 Step 4「探针校准解析规则」是刻意前置（先实测文本解析命中率再写死正则，铁律），非 TBD；解析规则在 Step 4 实测后固化。Task 2 访问器定位标注「需专门处理」——因文本层属性 `get` 块与元数据 `get_X` 名不同构，留 Task 2 实测处理，不臆造。
- **类型一致性**：SourceDocument/TypeMethodPosition 在 Task 1 定义、Task 2/3 沿用；方法 token 全部 int（MetadataTokens.GetToken），与 Engine DebugBreakpoint.MethodToken(int) 一致。
- **依赖**：仅 Decompiler 库内新增，Session/Web/宿主零改动（P4-2 消费）。不引新 NuGet。

---
