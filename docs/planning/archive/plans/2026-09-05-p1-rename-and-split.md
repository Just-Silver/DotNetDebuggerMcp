# P1 仓库改名与拆分 Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 将现 `ILSpyMcp`（单 exe 反编译 MCP 服务器，net10.0）无损重构为 **DotNet-Debugger-MCP 五项目解决方案**：`DotNetDebugger.Decompiler`（纯能力库：Metadata+Decompiler+Config 最小子集）、`DotNetDebugger.Engine`/`Session`/`Web`（空壳占位）、`DotNetDebuggerMcp`（宿主 exe，含现有全部反编译 MCP 工具），行为零变化、全部测试通过。

**Architecture:** P1 只做「改名 + 能力抽库」，不动任何调试逻辑。Decompiler 库边界 = **纯能力层**（用户拍板）：`Metadata/`(17)+`Decompiler/`(1)+它们依赖的 `Configuration` 最小子集（AppText 的 2 个成员 + AppConfig 的 4 个字段），不含 MCP 工具/管道/缓存/格式化。宿主 exe 保留 Tools/Pipeline/Caching/Formatting/Services/剩余 Configuration，16 个反编译 MCP 工具不拆走（后续 P3 才接调试工具）。命名空间根从 `ILSpyMcp` → `DotNetDebugger`（能力库）/ `DotNetDebuggerMcp`（宿主）。

**Tech Stack:** .NET 10 / C# / xunit.v3 / ModelContextProtocol 2.2.0 / ICSharpCode.Decompiler 11.0.0.9375

**Spec:** `docs/planning/specs/2026-09-05-overview-design.md`（本 plan 从 spec §2/§3/§7-P1 论证）+ 命名 `decisions.md D6/D8`

## Global Constraints

- 命名映射（spec §2 / decisions D6/D8）：
  - 仓库名 `DotNet-Debugger-MCP`；解决方案 `DotNetDebuggerMcp.slnx`
  - 库 `src/DotNetDebugger.Decompiler/DotNetDebugger.Decompiler.csproj` → 程序集/命名空间 `DotNetDebugger.Decompiler`（内部按子目录：`.Metadata`、`.Configuration`；`Decompiler` 的 `InProcessDecompiler` 归 `DotNetDebugger.Decompiler` 根下）
  - 宿主 `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` → 命名空间 `DotNetDebuggerMcp`；`PackageId`/`ToolCommandName` = `dotnet-debugger-mcp`（**注意：NuGet 包 id 含连字符是合法且惯例**，CLI 工具命令名同）
  - Engine/Session/Web 三库 **P1 只建空 csproj 占位**（不实现），供解决方案骨架完整
  - MCP server 注册名（server.json name / opencode.json key）实施确认，P1 暂用 `dotnetdebugger`
- 版本：P1 不 bump 主版本（保持 `<Version>1.4.0`），但 **csproj/`.mcp/server.json`/CHANGELOG 三处同步纪律**延续（AGENTS.md）
- **stdout 纯净纪律不可破坏**（`ILSpyMcpCmd.OnExecuteAsync` 的 `ClearProviders`+`AddConsole(LogToStandardErrorThreshold=Trace)` 配置），P1 拆分后必须保留等价配置；`McpSessionConcurrencyTests` 是回归护栏
- 命名空间风格：全程 file-scoped namespace（现状一致）；改命名空间可整文件替换 `ILSpyMcp` → `DotNetDebugger`/`DotNetDebuggerMcp`
- **行为零变化**：16 个 MCP 工具名/签名/输出格式、CLI 参数、测试数据 dll 断言、握手 ServerInstructions 文本在 P1 后必须全部保持（工具前缀仅由客户端注册名决定，注册名变化是 P1 之外的独立决策）
- 历史文档（CHANGELOG 已发布段落、docs/ROADMAP）不改写史实；`docs/planning/` 规划文档是本重构的记录，可更新
- 测试数据：`tests/TestData/*.dll` 由 `generate-testdata.ps1` 生成、git 忽略；脚本内命名空间 `ILSpyMcp.Samples` 与 dll 名在 P1 改名后需同步（见 Task 5），改完重新生成
- 每个 Task 结束提交；提交信息用中文
- 计划假定执行者在分支 `plan/dynamic-debugging-and-rename` 上工作（P1 落地在独立实现分支上做，见 P0）

---

## 目标文件结构（Task 0 建立）

```
DotNetDebuggerMcp.slnx
global.json
opencode.json            # mcp.servers 注册名改 dotnetdebugger；命令指向新宿主 exe
src/
  DotNetDebugger.Decompiler/            # 纯能力库（P1 迁移）
    DotNetDebugger.Decompiler.csproj    # Library, net10.0
    Metadata/   # 原 17 文件，命名空间 ILSpyMcp.Metadata → DotNetDebugger.Decompiler.Metadata
    Decompiler/ # 原 InProcessDecompiler.cs → DotNetDebugger.Decompiler.Decompiler 命名空间（文件随迁）
    Configuration/  # 新建 DecompilerConfig.cs + DecompilerText.cs（能力层自用常量，internal）
  DotNetDebugger.Engine/    # 空壳占位（csproj + Class1 或 README）
  DotNetDebugger.Session/   # 空壳占位
  DotNetDebugger.Web/       # 空壳占位
  DotNetDebuggerMcp/                 # 宿主 exe
    DotNetDebuggerMcp.csproj         # Exe, net10.0, PackAsTool, PackageId=dotnet-debugger-mcp
    .mcp/server.json                 # name/identifier 改 dotnetdebugger
    Configuration/   # AppConfig.cs（宿主专用字段）+ AppText.cs（宿主专用：HandshakeFeatureIntro 等）
    Tools/ Services/ Pipeline/ Caching/ Formatting/ Metadata/ Decompiler/ Validation/ UpdateCheck/
        # 命名空间根 ILSpyMcp.* → DotNetDebuggerMcp.*
        # Metadata/Decompiler/Configuration 引用改为 ProjectReference 到 Decompiler 库
    ILSpyMcpCmd.cs → DotNetDebuggerMcpCmd.cs（类名同步）
    Program.cs（顶层语句，引用改名）
  DotNetDebuggerMcp.Client/            # e2e（原 ILSpyMcp.Client，命名空间 DotNetDebuggerMcp.Client）
tests/
  DotNetDebugger.Decompiler.Tests/     # 原 Metadata/Decompiler 相关单测迁入（命名空间）
  DotNetDebuggerMcp.Tests/             # 原 ILSpyMcp.Tests 迁入（含并发护栏）——见 Task 5 决策
```

> 依赖树（spec §3）：Decompiler 库无内部依赖；宿主 exe → ProjectReference Decompiler（+ Engine/Session/Web 暂不引）；Client → 无 ProjectReference（dotnet run 宿主）。

---

## Task 总览

- **Task 1** 建立 5 项目骨架 + 新 slnx + 空壳库（含分支建立，见 Task 1 Step 1）
- **Task 2** Decompiler 库源码迁入（Metadata 17 + Decompiler 1 + Configuration 自建常量），命名空间替换，库独立编译绿
- **Task 3** 宿主 exe 源码迁入（Tools/Pipeline/Caching/Formatting/Services/UpdateCheck/Validation/Cmd/Program + 宿主 AppConfig/AppText），命名空间替换 + ProjectReference，宿主编译绿
- **Task 4** 解跨程序集 internal/常量问题（DecompilerText/DecompilerConfig 与宿主 AppText/AppConfig 衔接）
- **Task 5** Client + Tests 迁入（含并发护栏），全量测试绿
- **Task 6** 删旧目录（src/ILSpyMcp、src/ILSpyMcp.Client、tests/ILSpyMcp.Tests、tests/TestData 旧命名空间脚本重生成）
- **Task 7** 工程配置同步（server.json / opencode.json / .gitignore / generate-testdata.ps1 / CI build.yml / README / CHANGELOG）
- **Task 8** 端到端 + 回归验收（Client 跑全部工具，stdio 纯净，行为对照旧版）

---

### Task 1: 建立 5 项目骨架 + 新 slnx + 空壳库

**Files:**
- Create: `DotNetDebuggerMcp.slnx`（覆盖旧 `ILSpyMcp.slnx`）
- Create: `src/DotNetDebugger.Decompiler/DotNetDebugger.Decompiler.csproj`
- Create: `src/DotNetDebugger.Engine/DotNetDebugger.Engine.csproj` + `Placeholder.cs`
- Create: `src/DotNetDebugger.Session/DotNetDebugger.Session.csproj` + `Placeholder.cs`
- Create: `src/DotNetDebugger.Web/DotNetDebugger.Web.csproj` + `Placeholder.cs`
- Create: `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`（骨架，先不含源码）
- Delete: `ILSpyMcp.slnx`

**Interfaces:**
- Consumes: 无（全新结构）
- Produces: 新 slnx 指向 5 项目；Decompiler 库 csproj 供 Task 2 迁码；宿主 csproj 供 Task 3 迁码

- [ ] **Step 1: 从当前 plan 分支开 P1 实现分支**

```bash
git checkout -b feature/p1-rename-split  # 基于当前 plan 分支或 master？——从 master 开，避免 plan 文档混入
git checkout master && git checkout -b feature/p1-rename-split
```
> 说明：P1 实现分支应基于 `master`（纯净生产代码），`docs/planning/` 规划文档已在 plan 分支。若需在实现分支保留规划参考，可 `git cherry-pick` 需要的 commit 或之后合并。

- [ ] **Step 2: 写 5 个 csproj**

`src/DotNetDebugger.Decompiler/DotNetDebugger.Decompiler.csproj`:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>DotNetDebugger.Decompiler</RootNamespace>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="ICSharpCode.Decompiler" Version="11.0.0.9375" />
  </ItemGroup>
</Project>
```

`src/DotNetDebugger.Engine/DotNetDebugger.Engine.csproj`（Session/Web 同形，改名字）:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>DotNetDebugger.Engine</RootNamespace>
  </PropertyGroup>
</Project>
```
（Engine/Session/Web 的 Placeholder.cs：`namespace DotNetDebugger.Engine; public static class Placeholder {}` 之类最小可编译内容）

`src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`（宿主，先不含源码）:
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0</TargetFramework>
    <OutputType>Exe</OutputType>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <PackAsTool>true</PackAsTool>
    <PackageType>McpServer</PackageType>
    <PackageId>dotnet-debugger-mcp</PackageId>
    <Version>1.4.0</Version>
    <ToolCommandName>dotnet-debugger-mcp</ToolCommandName>
    <Authors>Just-Silver</Authors>
    <RepositoryType>git</RepositoryType>
    <RepositoryUrl>https://github.com/Just-Silver/DotNet-Debugger-MCP.git</RepositoryUrl>
    <PackageProjectUrl>https://github.com/Just-Silver/DotNet-Debugger-MCP</PackageProjectUrl>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <PackageTags>AI; MCP; server; stdio; decompiler; debugger; dotnet</PackageTags>
    <Description>内置反编译与调试能力的 .NET MCP 服务器（dotnet-debugger-mcp）。</Description>
    <GenerateDocumentationFile>True</GenerateDocumentationFile>
  </PropertyGroup>
  <ItemGroup>
    <None Include=".mcp\server.json" Pack="true" PackagePath="/.mcp/" />
    <None Include="..\..\README.md" Pack="true" PackagePath="/" />
  </ItemGroup>
  <ItemGroup>
    <PackageReference Include="McMaster.Extensions.Hosting.CommandLine" Version="5.1.0" />
    <PackageReference Include="Microsoft.Extensions.Hosting" Version="10.0.10" />
    <PackageReference Include="ModelContextProtocol" Version="2.2.0" />
  </ItemGroup>
  <ItemGroup>
    <ProjectReference Include="..\DotNetDebugger.Decompiler\DotNetDebugger.Decompiler.csproj" />
  </ItemGroup>
</Project>
```

> Task 3 迁入源码后再补 InternalsVisibleTo。

- [ ] **Step 3: 建新 slnx 覆盖旧文件**

`DotNetDebuggerMcp.slnx`:
```xml
<Solution>
  <Folder Name="/src/">
    <Project Path="src/DotNetDebugger.Decompiler/DotNetDebugger.Decompiler.csproj" />
    <Project Path="src/DotNetDebugger.Engine/DotNetDebugger.Engine.csproj" />
    <Project Path="src/DotNetDebugger.Session/DotNetDebugger.Session.csproj" />
    <Project Path="src/DotNetDebugger.Web/DotNetDebugger.Web.csproj" />
    <Project Path="src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj" />
  </Folder>
</Solution>
```
删除旧 `ILSpyMcp.slnx`。

- [ ] **Step 4: 编译验证**

```bash
dotnet build DotNetDebuggerMcp.slnx -c Release
```
Expected: BUILD SUCCEEDED，5 项目全编译（Decompiler 空库、宿主无源码可能报「无入口点」——宿主先放一个临时空 `Program.cs` 顶层 `return 0;` 直到 Task 3 迁入真 Program）。

> 注：宿主 csproj 为 Exe 且当前无源码，需临时 `Program.cs` 占位才能编译；Task 3 覆盖为真实现。

- [ ] **Step 5: 提交**

```bash
git add -A && git commit -m "重构: 建立 DotNet-Debugger-MCP 五项目骨架（slnx + 空壳库 + 宿主占位）"
```

---

### Task 2: Decompiler 库源码迁入（Metadata + Decompiler + 自建常量）

**Files:**
- Copy (git mv): `src/ILSpyMcp/Metadata/*.cs` (17) → `src/DotNetDebugger.Decompiler/Metadata/`
- Copy: `src/ILSpyMcp/Decompiler/InProcessDecompiler.cs` → `src/DotNetDebugger.Decompiler/Decompiler/InProcessDecompiler.cs`
- Create: `src/DotNetDebugger.Decompiler/Configuration/DecompilerConfig.cs` + `DecompilerText.cs`

**Interfaces:**
- Consumes: Task 1 的 Decompiler csproj
- Produces: 编译绿、可被宿主引用的纯能力库（命名空间 `DotNetDebugger.Decompiler` / `.Metadata` / `.Decompiler`；内部常量类 `DecompilerConfig`/`DecompilerText` 取代对宿主 AppConfig/AppText 的引用）

**关键事实（盘点已确认）：**
- Metadata 17 文件 100% file-scoped `namespace ILSpyMcp.Metadata;`，仅 `ExternalCallExpander.cs` 引 `ILSpyMcp.Configuration`（用 AppConfig.ExternalExpandMaxDepth/MaxNodes）
- InProcessDecompiler.cs 用 `ILSpyMcp.Configuration`（AppText.DecompileFailurePrefix/StartsWithDecompileFailure、AppConfig.MaxOutputBytes）+ `ILSpyMcp.Metadata`（MetadataNaming 等）
- 能力库需要的最小常量集 = AppConfig.ExternalExpandMaxDepth、ExternalExpandMaxNodes、MaxOutputBytes + AppText.DecompileFailurePrefix、StartsWithDecompileFailure

- [ ] **Step 1: 用 git mv 迁移 Metadata 17 文件并批量替换命名空间**

```bash
mkdir -p src/DotNetDebugger.Decompiler/Metadata
git mv src/ILSpyMcp/Metadata/*.cs src/DotNetDebugger.Decompiler/Metadata/
# 批量替换文件内命名空间与 using
#   namespace ILSpyMcp.Metadata; → namespace DotNetDebugger.Decompiler.Metadata;
#   using ILSpyMcp.Configuration; → （改为指向库内新常量，见 Step 2/3）
#   using ILSpyMcp.Metadata; → using DotNetDebugger.Decompiler.Metadata;（如内部跨文件）
```
> 执行：用 PowerShell 逐文件 `(Get-Content -Raw).Replace(...) | Set-Content`，或用 IDE 全局替换。命名空间替换须先于常量替换，避免误伤。

- [ ] **Step 2: 建库内常量类 DecompilerConfig + DecompilerText**

`src/DotNetDebugger.Decompiler/Configuration/DecompilerConfig.cs`:
```csharp
namespace DotNetDebugger.Decompiler.Configuration;

/// <summary>能力库自用的内部常量（与宿主 AppConfig 拆分后归库）：避免库依赖宿主程序集。</summary>
internal static class DecompilerConfig
{
    /// <summary>call_chain 跨程序集调用展开的最大递归深度（原 AppConfig.ExternalExpandMaxDepth）。</summary>
    public const int ExternalExpandMaxDepth = 5;
    /// <summary>call_chain 单次跨程序集调用展开最多外部节点数（原 AppConfig.ExternalExpandMaxNodes）。</summary>
    public const int ExternalExpandMaxNodes = 200;
    /// <summary>单次反编译生成文本字符数上限（原 AppConfig.MaxOutputBytes，值与宿主缓存上限一致）。</summary>
    public const long MaxOutputBytes = 64 * 1024 * 1024;
}
```

`src/DotNetDebugger.Decompiler/Configuration/DecompilerText.cs`:
```csharp
namespace DotNetDebugger.Decompiler.Configuration;

/// <summary>能力库自用的用户可见文案常量（拆分自 AppText，仅 Decompiler 层用到的最小集）。</summary>
internal static class DecompilerText
{
    public const string DecompileFailurePrefix = "反编译失败：";
    public static bool StartsWithDecompileFailure(string text)
        => text.StartsWith(DecompileFailurePrefix, StringComparison.Ordinal);
}
```

- [ ] **Step 3: 改 ExternalCallExpander.cs 与 InProcessDecompiler.cs 引用**

`ExternalCallExpander.cs`：`using ILSpyMcp.Configuration;` → `using DotNetDebugger.Decompiler.Configuration;`；`AppConfig.ExternalExpandMaxDepth` → `DecompilerConfig.ExternalExpandMaxDepth`；同 MaxNodes。

`InProcessDecompiler.cs`：`using ILSpyMcp.Configuration;` → `using DotNetDebugger.Decompiler.Configuration;`；`using ILSpyMcp.Metadata;` → `using DotNetDebugger.Decompiler.Metadata;`；`AppText.DecompileFailurePrefix` → `DecompilerText.DecompileFailurePrefix`；`AppText.StartsWithDecompileFailure` → `DecompilerText.StartsWithDecompileFailure`；`AppConfig.MaxOutputBytes` → `DecompilerConfig.MaxOutputBytes`。

> 该文件里其它对 `ILSpyMcp.*` 的引用（若有）一并替换为 `DotNetDebugger.Decompiler.*`。

- [ ] **Step 4: 库单独编译**

```bash
dotnet build src/DotNetDebugger.Decompiler/DotNetDebugger.Decompiler.csproj -c Release
```
Expected: BUILD SUCCEEDED。若报缺类型（Metadata 内部引用的其它宿主类型），回到 Step 1 补充替换。

- [ ] **Step 5: 提交**

```bash
git add -A && git commit -m "重构: Decompiler 能力库迁入（Metadata+Decompiler+自建常量，命名空间替换为 DotNetDebugger.Decompiler）"
```

---

### Task 3: 宿主 exe 源码迁入（Tools/Pipeline/Caching/Formatting/Services/UpdateCheck/Validation/Cmd/Program）

**Files:**
- Copy (git mv): `src/ILSpyMcp/Tools/*.cs` (16)、`Services/*.cs` (3)、`Pipeline/*.cs` (1)、`Caching/*.cs` (1)、`Formatting/*.cs` (2)、`Validation/*.cs` (1)、`UpdateCheck/*.cs` (3)、根 `ILSpyMcpCmd.cs`、根 `Program.cs` → `src/DotNetDebuggerMcp/`（按子目录）
- Copy: `src/ILSpyMcp/Configuration/AppConfig.cs`、`AppText.cs` → `src/DotNetDebuggerMcp/Configuration/`（宿主专用字段保留）

**Interfaces:**
- Consumes: Task 1 宿主 csproj + Decompiler 库（Task 2）
- Produces: 宿主 exe 编译绿；全部 16 MCP 工具仍在宿主内注册（`WithToolsFromAssembly` 扫宿主程序集）

**关键事实（盘点已确认）：**
- 宿主代码命名空间根 `ILSpyMcp` → `DotNetDebuggerMcp`（如 `ILSpyMcp.Tools` → `DotNetDebuggerMcp.Tools`）
- 宿主引用 Decompiler 库的部分：原 `ILSpyMcp.Metadata` → `DotNetDebugger.Decompiler.Metadata`；原 `ILSpyMcp.Decompiler`（InProcessDecompiler）→ `DotNetDebugger.Decompiler.Decompiler`；原 `ILSpyMcp.Configuration` 中「库用常量」改指 DecompilerConfig/DecompilerText（宿主内保留自己的 AppConfig/AppText，内含宿主字段 + 指向库常量）
- `Program.cs` 顶层语句 `using ILSpyMcp;` → `using DotNetDebuggerMcp;`，入口类 `ILSpyMcpCmd` → `DotNetDebuggerMcpCmd`
- `ILSpyMcpCmd` 类名 → `DotNetDebuggerMcpCmd`（文件同名）

- [ ] **Step 1: git mv 宿主各目录**

```bash
mkdir -p src/DotNetDebuggerMcp/{Tools,Services,Pipeline,Caching,Formatting,Validation,UpdateCheck,Configuration}
git mv src/ILSpyMcp/Tools src/DotNetDebuggerMcp/Tools
git mv src/ILSpyMcp/Services src/DotNetDebuggerMcp/Services
git mv src/ILSpyMcp/Pipeline src/DotNetDebuggerMcp/Pipeline
git mv src/ILSpyMcp/Caching src/DotNetDebuggerMcp/Caching
git mv src/ILSpyMcp/Formatting src/DotNetDebuggerMcp/Formatting
git mv src/ILSpyMcp/Validation src/DotNetDebuggerMcp/Validation
git mv src/ILSpyMcp/UpdateCheck src/DotNetDebuggerMcp/UpdateCheck
git mv src/ILSpyMcp/Configuration/AppConfig.cs src/DotNetDebuggerMcp/Configuration/AppConfig.cs
git mv src/ILSpyMcp/Configuration/AppText.cs src/DotNetDebuggerMcp/Configuration/AppText.cs
git mv src/ILSpyMcp/ILSpyMcpCmd.cs src/DotNetDebuggerMcp/DotNetDebuggerMcpCmd.cs
git mv src/ILSpyMcp/Program.cs src/DotNetDebuggerMcp/Program.cs
```
> `CacheSignatures.cs`、`ToolParameterText.cs`、`CacheStatsTool` 相关属宿主，原在 `Configuration/` 或 `Tools/` 目录的一并 git mv 到宿主对应目录。

- [ ] **Step 2: 全宿主文件命名空间批量替换**

对 `src/DotNetDebuggerMcp/**/*.cs` 批量替换：
```
namespace ILSpyMcp.X → namespace DotNetDebuggerMcp.X      （宿主内子命名空间）
namespace ILSpyMcp;  → namespace DotNetDebuggerMcp;
using ILSpyMcp.Tools; → using DotNetDebuggerMcp.Tools;     （宿主内互相引用）
...
```
对**跨程序集引用**（指向 Decompiler 库）单独处理：
```
using ILSpyMcp.Metadata;   → using DotNetDebugger.Decompiler.Metadata;
using ILSpyMcp.Decompiler; → using DotNetDebugger.Decompiler.Decompiler;
```
> 注意：宿主 `using ILSpyMcp.Configuration;` 分两类——用宿主 AppConfig/AppText 的保留 `using DotNetDebuggerMcp.Configuration;`；引用已入库常量（ExternalCallExpand* / MaxOutputBytes / DecompileFailure*）的改指向库（Task 4 统一处理）。Metadata/Decompiler 物理目录从宿主删除（已在库）。

- [ ] **Step 3: 入口改名**

`Program.cs`：`using ILSpyMcp;` → `using DotNetDebuggerMcp;`；`RunCommandLineApplicationAsync<ILSpyMcpCmd>` → `<DotNetDebuggerMcpCmd>`。
`DotNetDebuggerMcpCmd.cs`：类名 `ILSpyMcpCmd` → `DotNetDebuggerMcpCmd`（含文件内所有引用与注释同步）。

- [ ] **Step 4: 宿主 csproj 补全**

`src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` 追加（供测试程序集访问 internals）：
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="DotNetDebuggerMcp.Tests" />
  </ItemGroup>
```

- [ ] **Step 5: 宿主编译**

```bash
dotnet build src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj -c Release
```
Expected: BUILD SUCCEEDED。逐个修复编译错误（多为命名空间/引用指向）。

- [ ] **Step 6: 提交**

```bash
git add -A && git commit -m "重构: 宿主 DotNetDebuggerMcp 源码迁入（Tools/Services/Pipeline/Caching/Formatting/UpdateCheck/Validation/Cmd/Program），命名空间替换 + ProjectReference"
```

---

### Task 4: 跨程序集 internal/常量衔接（宿主 AppConfig/AppText ↔ 库 DecompilerConfig/DecompilerText）

**Files:**
- Modify: `src/DotNetDebuggerMcp/Configuration/AppConfig.cs`（删除已入库的 3 字段或改为引用库常量）
- Modify: `src/DotNetDebuggerMcp/Configuration/AppText.cs`（同理）

**Interfaces:**
- Consumes: Task 2 的 DecompilerConfig/DecompilerText
- Produces: 无重复常量定义；宿主引用库值时单一来源；行为不变

**关键事实：**
- 库用常量已在 Task 2 复制为 DecompilerConfig/DecompilerText（internal，Decompiler 库程序集内）
- 宿主 AppConfig/AppText 仍有大量宿主专用字段（缓存/超时/更新检查/HandshakeFeatureIntro），必须保留
- **宿主若仍需要** ExternalExpandMaxDepth/MaxOutputBytes/DecompileFailurePrefix 值（如 ToolPipeline 判 `DecompileFailurePrefix`、`IsErrorResult` 依赖 `StartsWithDecompileFailure`），因库内是 internal 宿主无法直接访问 → 用 InternalsVisibleTo 让宿主访问库 internal，宿主不重复定义值

- [ ] **Step 1: 审查宿主对已入库常量的实际引用**

```bash
grep -rn "ExternalExpandMaxDepth\|ExternalExpandMaxNodes\|MaxOutputBytes\|DecompileFailurePrefix\|StartsWithDecompileFailure" src/DotNetDebuggerMcp/ src/DotNetDebugger.Decompiler/
```
列出宿主侧仍引用这些符号的文件；据此确定 InternalsVisibleTo 覆盖面。

- [ ] **Step 2: 库 csproj 加 InternalsVisibleTo 暴露给宿主**

`src/DotNetDebugger.Decompiler/DotNetDebugger.Decompiler.csproj` 追加：
```xml
  <ItemGroup>
    <InternalsVisibleTo Include="DotNetDebuggerMcp" />
  </ItemGroup>
```
> 用 internal + InternalsVisibleTo（不 public 化常量类），避免过早扩大 API 面；P3 有真实外部消费者再评估 public。

- [ ] **Step 3: 修宿主引用**

宿主侧凡原 `AppConfig.ExternalExpandMaxDepth`/`MaxOutputBytes`/`AppText.DecompileFailurePrefix`/`StartsWithDecompileFailure` 处，改 `DecompilerConfig.*`/`DecompilerText.*` 并加 `using DotNetDebugger.Decompiler.Configuration;`。宿主 AppConfig/AppText **删除**已入库的重复定义，避免双源漂移。

- [ ] **Step 4: 编译 + 单测跑通**

```bash
dotnet build src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj -c Release
dotnet test --project tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj -c Release
```
Expected: 编译绿；测试绿（行为未因常量转发变化）。
> 注：此时旧测试项目引用的是旧主项目路径，若 Task 5 尚未迁移测试，此步需先临时把旧 Tests csproj 的 ProjectReference 指向新宿主 csproj（`..\..\src\DotNetDebuggerMcp\DotNetDebuggerMcp.csproj`）——见 Task 5 Step 0 说明；否则跳过此单测命令。

- [ ] **Step 5: 提交**

```bash
git add -A && git commit -m "重构: 宿主与 Decompiler 库常量衔接（InternalsVisibleTo + 单一来源转发，删重复定义）"
```

---

### Task 5: Client 与 Tests 迁入

**Files:**
- Create: `src/DotNetDebuggerMcp.Client/`（git mv 自 `src/ILSpyMcp.Client/`，18 cs，命名空间 `ILSpyMcp.Client` → `DotNetDebuggerMcp.Client`）
- Create: `src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj`（覆盖旧，无 ProjectReference，ModelContextProtocol 2.2.0）
- Create: `tests/DotNetDebugger.Decompiler.Tests/`（**可选**：若拆分 Metadata/Decompiler 单测出库则建；见下方决策点）
- Create: `tests/DotNetDebuggerMcp.Tests/`（git mv 自 `tests/ILSpyMcp.Tests/`，41 cs，命名空间 `ILSpyMcp.Tests` → `DotNetDebuggerMcp.Tests`）

**Interfaces:**
- Consumes: Task 3/4 的宿主 exe、Task 2 的 Decompiler 库
- Produces: 全量测试可跑；e2e Client 指向新宿主路径

**决策点（执行者需判断）：**
- 现有 41 个测试文件里，哪些测 Metadata/Decompiler 能力（如 `*MetadataTests`/`InProcessDecompiler*`/`MemberResolverTests` 等），哪些测宿主管道/工具（`ToolPipelineTests`/`ToolExecutorTests`/`*ToolTests`/`McpSessionConcurrencyTests`/`ILSpyMcpCmdTests`）。
- 若 Metadata/Decompiler 相关测试随库走，建 `tests/DotNetDebugger.Decompiler.Tests/`；若测试与库 internal 强耦合，Decompiler 库也需对该测试程序集 InternalsVisibleTo。
- 默认（简化）：**测试项目先整体迁到 `tests/DotNetDebuggerMcp.Tests/`**，通过 ProjectReference 同时引宿主 + Decompiler 库，命名空间统一改；后续 P2/P3 再按需拆 Decompiler 独立测试项目。此默认最省事且保证行为验证完整。
- Client 输出目录 `tests/.ilspymcp-client-out/` 保留（.gitignore 同步见 Task 7）。

- [ ] **Step 1: git mv Client**

```bash
mkdir -p src/DotNetDebuggerMcp.Client
git mv src/ILSpyMcp.Client/Program.cs src/ILSpyMcp.Client/ClientRunner.cs src/ILSpyMcp.Client/TestDataHelper.cs src/ILSpyMcp.Client/ToolCallCase.cs src/ILSpyMcp.Client/*Cases.cs src/DotNetDebuggerMcp.Client/
```
（或整目录 `git mv src/ILSpyMcp.Client src/DotNetDebuggerMcp.Client` 再删旧 csproj）
- Client csproj 文件：`src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj`，内容同旧但去掉对旧主项目路径假设；`Program.cs` L4 server 项目路径改 `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`，L6 输出目录可改名 `tests/.dotnetdebugger-client-out/`（.gitignore 同步）
- 命名空间 `ILSpyMcp.Client` → `DotNetDebuggerMcp.Client`（17 文件 + Program 顶层 using）

- [ ] **Step 2: git mv Tests**

```bash
mkdir -p tests/DotNetDebuggerMcp.Tests
git mv tests/ILSpyMcp.Tests/*.cs tests/DotNetDebuggerMcp.Tests/
```
- csproj：`tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj`，ProjectReference 指向 `..\..\src\DotNetDebuggerMcp\DotNetDebuggerMcp.csproj`（必要时加 Decompiler 库引用）
- 命名空间 `ILSpyMcp.Tests` → `DotNetDebuggerMcp.Tests`（41 文件）
- `TestDataPaths.cs` 锚点文件：`Locate("tests","TestData",...)` 上溯找 `ILSpyMcp.slnx` → 改找 `DotNetDebuggerMcp.slnx`
- `TestDataPaths.cs`/`TestAssemblyWriter.cs` 里 `ILSpyMcp.TestSamples`/`ILSpyMcp.Samples` 引用随 Task 7 的 generate-testdata 改名联动

- [ ] **Step 3: 编译 + 全量测试**

```bash
dotnet build DotNetDebuggerMcp.slnx -c Release
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -c Release
```
Expected: BUILD SUCCEEDED；测试全绿（含 `McpSessionConcurrencyTests` stdio 并发护栏）。

- [ ] **Step 4: 提交**

```bash
git add -A && git commit -m "重构: Client 与 Tests 迁入 DotNetDebuggerMcp.Client / DotNetDebuggerMcp.Tests，命名空间替换，测试全绿"
```

---

### Task 6: 删除旧目录结构

**Files:**
- Delete: `src/ILSpyMcp/`（已迁移完，剩余应为空或仅旧 .mcp/server.json——server.json 已重建于宿主）
- Delete: `src/ILSpyMcp.Client/`（已迁移）
- Delete: `tests/ILSpyMcp.Tests/`（已迁移）

**Interfaces:**
- Consumes: Task 1-5 全部完成且绿
- Produces: 仓库只剩 5 项目 + Client/Tests 新结构，无 ILSpyMcp 残留

- [ ] **Step 1: 确认无残留引用**

```bash
grep -rn "ILSpyMcp" src/ tests/ *.slnx global.json opencode.json 2>$null | Select-String -NotMatch "docs/planning" 
```
> 除 `docs/planning/`（规划文档，允许保留历史名提及）与 CHANGELOG 历史段外，不应有源码/工程引用旧名。若发现，回 Task 处理。

- [ ] **Step 2: 删除旧目录**

```bash
git rm -r src/ILSpyMcp src/ILSpyMcp.Client tests/ILSpyMcp.Tests
```

- [ ] **Step 3: 全量重建验证**

```bash
dotnet build DotNetDebuggerMcp.slnx -c Release
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -c Release
```
Expected: 绿。

- [ ] **Step 4: 提交**

```bash
git add -A && git commit -m "重构: 删除旧 ILSpyMcp/ILSpyMcp.Client/ILSpyMcp.Tests 目录（同仓重建完成）"
```

---

### Task 7: 工程配置同步（server.json / opencode.json / .gitignore / generate-testdata.ps1 / CI / README / CHANGELOG）

**Files:**
- Modify: `src/DotNetDebuggerMcp/.mcp/server.json`（新建于宿主）
- Modify: `opencode.json`、`.gitignore`、`tests/TestData/generate-testdata.ps1`
- Modify: `.github/workflows/build.yml`
- Modify: `README.md`、`CHANGELOG.md`、根 `AGENTS.md`（结构描述段）
- Delete: `src/ILSpyMcp/.mcp/server.json`（随 Task 6 删除）

**Interfaces:**
- Consumes: Task 6 后的新结构
- Produces: 仓库从配置到文档全部指向 DotNet-Debugger-MCP，CI 可发版

- [ ] **Step 1: server.json**

在 `src/DotNetDebuggerMcp/.mcp/server.json` 写入（name/identifier 用新注册名，版本保持与 csproj 同步）：
```json
{
  "$schema": "https://static.modelcontextprotocol.io/schemas/2025-12-11/server.schema.json",
  "description": "内置反编译与调试能力的 .NET MCP 服务器（dotnet-debugger-mcp）。",
  "name": "io.github.just-silver/dotnet-debugger-mcp",
  "version": "1.4.0",
  "packages": [
    {
      "registryType": "nuget",
      "identifier": "dotnet-debugger-mcp",
      "version": "1.4.0",
      "transport": { "type": "stdio" },
      "packageArguments": [],
      "environmentVariables": []
    }
  ],
  "repository": {
    "url": "https://github.com/Just-Silver/DotNet-Debugger-MCP",
    "source": "github"
  }
}
```
> 注册名细节（是否 `dotnetdebugger` 无连字符、前缀 `dotnetdebugger_*`）按 AGENTS.md 纪律：若最终决定带连字符的包 id 作为 MCP name 会与 opencode key 不同，需明确映射。此处 name 用包 id 风格，opencode 注册名见 Step 2。

- [ ] **Step 2: opencode.json**

服务器 key `"ilspy"` → `"dotnetdebugger"`，命令指向新宿主 Debug exe：
```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": { "servers": {
    "dotnetdebugger": {
      "type": "local",
      "command": ["./src/DotNetDebuggerMcp/bin/Debug/net10.0/DotNetDebuggerMcp.exe"]
    }
  } }
}
```

- [ ] **Step 3: .gitignore**

`tests/.ilspymcp-client-out/` → `tests/.dotnetdebugger-client-out/`（若 Client 输出改名）；`tests/TestData/*.dll` 保留。若 Client 未改名输出目录，则保留旧条目。

- [ ] **Step 4: generate-testdata.ps1 改名**

脚本内全部 `ILSpyMcp.Samples`/`ILSpyMcp.SamplesExt` → 新测试样本命名空间（如 `DotNetDebugger.TestSamples`），`ILSpyMcp.TestSamples` dll 名 → `DotNetDebugger.TestSamples`/`DotNetDebugger.TestSamplesExt`，跨程序集引用 `using` 同步。改完运行脚本重新生成 dll：
```bash
powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1
```
> 同步改 `TestDataPaths.cs`/`TestAssemblyWriter.cs`/Client `TestDataHelper.cs` 中对 dll 名与样本命名空间的引用（Task 5 遗留联动）。**决策点**：测试样本命名空间可保持 `ILSpyMcp.Samples` 不变（测试数据名与产品名解耦）以最小化改动——执行者权衡：若只改名 dll 文件名不改命名空间，TestAssemblyWriter 里反编译断言目标类型名需同步。**默认：连命名空间一起改**，保持命名一致。

- [ ] **Step 5: CI build.yml**

- Build 行：`src/ILSpyMcp/ILSpyMcp.csproj` → `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`
- Test 行：`tests/ILSpyMcp.Tests/ILSpyMcp.Tests.csproj` → `tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj`
- Pack 行：同 Build 改路径
- Pack 的 `-p:PackageReleaseNotes=https://github.com/Just-Silver/ILSpyMcp/releases/tag/v$tag` → `.../DotNet-Debugger-MCP/releases/tag/v$tag`
- 提交触发分支若写 `master` 保留；新增 `push` 到新默认分支名（仓库 rename 后 GitHub 默认分支可能仍 master，保留即可）

- [ ] **Step 6: README.md**

- 标题/简介/安装（`dotnet tool install -g ilspymcp` → `dotnet-debugger-mcp`）
- MCP 注册名与工具前缀说明（`ilspy_*` → `dotnetdebugger_*`）
- 工具表/参数表/示例中的路径与描述同步（保留反编译能力描述，调试能力标注「规划中/P3+」）
- 仓库链接、badge

- [ ] **Step 7: CHANGELOG.md**

`[Unreleased]` 段新增重构条目：
```
### 重构
- 仓库/包改名：ILSpyMcp → DotNet-Debugger-MCP（包 id dotnet-debugger-mcp），解决方案拆为五项目
- 反编译/静态分析能力抽为 DotNetDebugger.Decompiler 库（行为不变）
```
历史段落不动（Keep a Changelog 史实）。版本号保持 1.4.0（P1 不 bump），待 P5 发版再 bump。

- [ ] **Step 8: 根 AGENTS.md**

更新「结构」段目录/命名空间描述为 5 项目布局；命令示例（`dotnet run -c Release --project src/ILSpyMcp.Client/...` 等路径）改新路径；「更新版本号三处同步」中的 server.json 路径改新宿主。**历史教训/纪律段落保留不动**（stdout/stderr 纪律等仍然有效）。

- [ ] **Step 9: 提交**

```bash
git add -A && git commit -m "重构: 工程配置与文档全量同步（server.json/opencode/CI/README/CHANGELOG/AGENTS/testdata）"
```

---

### Task 8: 端到端验收（行为零变化确认）

**Files:**
- 无（验证任务）

**Interfaces:**
- Consumes: Task 1-7 完成
- Produces: 验收结论：改名后仓库行为与旧版一致

- [ ] **Step 1: 生成测试数据（若 Task 7 改名后需重新生成）**

```bash
powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1
```

- [ ] **Step 2: 运行 e2e Client**

```bash
dotnet run -c Release --project src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj
```
Expected: 全部工具端到端调用成功；反编译/元数据输出与旧版一致（工具名/格式不变，仅 server 前缀随注册名变化——Client 是进程内直连协议，断言的是工具方法与内容）。

- [ ] **Step 3: stdio 纯净回归**

`McpSessionConcurrencyTests`（已在 Task 5 测试集内）确认并发下 stdout 无噪声。另可裸跑一次握手观察无日志污染：
```bash
dotnet run -c Release --project src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj   # 无参数启动 MCP
# 输入空行不应有任何 stdout 输出；日志走 stderr
```

- [ ] **Step 4: 行为对照抽查**

用 CLI 抽查关键工具输出与旧版（改名前的 git 版本）一致：
```bash
./src/DotNetDebuggerMcp/bin/Release/net10.0/DotNetDebuggerMcp.exe -a tests/TestData/DotNetDebugger.TestSamples.dll -t DotNetDebugger.TestSamples.BigClass -ln 1-30
./src/DotNetDebuggerMcp/bin/Release/net10.0/DotNetDebuggerMcp.exe -a tests/TestData/DotNetDebugger.TestSamples.dll -l c -nc Box
```
对照旧版（`git stash`/切回 master 构建）输出应仅服务器标识不同，内容一致。

- [ ] **Step 5: P1 完成标记**

在 `docs/planning/open-questions.md` 标记 P1 完成；更新 `docs/planning/specs/README.md` 的 P1 状态；合并实现分支回 plan 分支（或 master 视流程）。

- [ ] **Step 6: 提交收尾**

```bash
git add -A && git commit -m "规划: P1 仓库改名与拆分完成（验收记录）"
```

---

## Self-Review 记录（P1 计划）

- **Spec 覆盖**：spec §2 命名布局（Task 1/7）、§3 分层（Task 2/3/4）、§7 P1 边界（全部 Task）、非目标（P1 不动调试）。Gap：spec 未要求 P1 建 Engine/Session/Web 占位库——但 D8 五项目结构要求 slnx 完整，占位合理。
- **占位符扫描**：Task 5 含「决策点」「执行者需判断」——属计划应明确处，已给默认路径；Task 7 Step 4 含执行者权衡——已给默认。其余无 TBD。
- **类型一致性**：命名空间替换规则在 Task 2/3 一致（`ILSpyMcp.Metadata`→`DotNetDebugger.Decompiler.Metadata`；宿主根→`DotNetDebuggerMcp`）；常量类名 Task 2 定义（DecompilerConfig/DecompilerText）在 Task 4 沿用一致。

---





