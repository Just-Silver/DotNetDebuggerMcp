# screenshot 通用化 · 阶段一 实施计划

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** 把截图能力从 `Engine/Capture/`、UI 自动化从宿主 `Services/Ui/` 抽成两个零宿主依赖的库（`SharpSight.Capture` / `SharpSight.UiAutomation`，**行为不变**），并据此落地「寻址扩展 + 坐标模型 `origin/scale/frameId` + 双轴 1568 + WebP + `includeCursor`」，同步文档/握手/回归。

**Architecture:** 两库零宿主依赖（Capture 不引 FlaUI，UiAutomation 不引 Capture/Engine）；`Engine` 迁出 `Capture/` 后去掉 `System.Drawing.Common`；宿主是唯一组合点，只做 MCP 参数绑定/编排/头部/落盘。阶段二（OCR/SoM/diff/独立 NuGet）另立计划。

**Tech Stack:** .NET 10（`net10.0-windows10.0.22621.0`）、`System.Drawing.Common 10.0.12`、`SixLabors.ImageSharp 3.1.12`、`FlaUI 5.0.0`、WinRT 投影、xunit.v3 + MTP。

**Spec:** `docs/planning/specs/2026-09-27-screenshot-generalization-design.md`（本计划只实现其 §14「阶段一」范围：库化 + 寻址 + 1568 + WebP + `includeCursor` + 文档同步）

## Global Constraints

- 全链 TFM `net10.0-windows10.0.22621.0`（非 windows TFM 不得引用本链项目，NU1201）。
- 所有 MCP 工具参数**带默认值**；`[Description]` 中文、面向 agent、注明默认值、必填标「（必填）」；方法末位 `CancellationToken cancellationToken = default`（不暴露、不写 Description）。
- 工具返回 `Task<CallToolResult>`（`screenshot` 图片类例外）；一切错误返回**中文提示文本、不抛异常**；不设 `IsError`。
- `screenshot` **不入缓存、不过 ToolPipeline、不写 AgentView/Actions**。
- stdout 只承载 MCP 协议帧；**严禁**改动 `ClearProviders`+`AddConsole(LogToStandardErrorThreshold=Trace)`。
- 改 MCP 工具 ⇒ 同 commit 同步 根 `README.md` + 握手 `AppText.HandshakeFeatureIntro` + 回归断言 `HandshakeFeatureIntro_覆盖全部能力族触发条件`。
- 跨层字面量常量集中（`AppConfig` / `AppText` / 新 `CaptureText`）。
- 测试串行纪律：`DotNetDebugger.Engine.Tests` 的 `Parallelization(None)` 不得删；宿主 `[Collection("AppServices")]`；自起子进程必须持续排空 stdout/stderr。
- 需先 `powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1` 生成测试程序集。
- 版本号三处同步仅在发布时；本计划只新建 `CHANGELOG.md` 的 `[Unreleased]` 段。

## File Structure

| 路径 | 责任 |
|---|---|
| `src/SharpSight.Capture/*` | **新库**：捕获（Screen/Display/Window/Element）+ 图像管线（缩放/编码/裁剪/纯黑）+ 光标 |
| `tests/SharpSight.Capture.Tests/*` | **新测试项目**：迁入原 Engine capture 测试 |
| `src/SharpSight.UiAutomation/*` | **新库**：FlaUI/UIA 元素模型 + 定位 + 交互 + 代际号 |
| `src/DotNetDebugger.Engine/Capture/` | **删除**（迁出）；csproj 去 `System.Drawing.Common` |
| `src/DotNetDebuggerMcp/Services/Ui/*` | **删除**（迁出） |
| `src/DotNetDebuggerMcp/Services/UiSemanticResolver.cs` | 迁入新库（若其依赖 Decompiler 则改由宿主注入，见 Task 2 Step 2） |
| `src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs` | 扩展参数/头部/编排（薄暴露） |
| `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs` | `ui_find` 输出加 `frameId`；`ui_action`/`ui_input`/`ui_get` 加可选 `frameId` |
| `DotNetDebuggerMcp.slnx` | 增 2 个项目 + 1 个测试项目 |
| `src/DotNetDebuggerMcp/Configuration/AppConfig.cs` | `ScreenshotMaxDimension 2000→1568`、新增 `ScreenshotMaxMarks`、`OcrDefaultLanguage` |
| 根 `README.md` / `AppText.HandshakeFeatureIntro` / `DotNetDebuggerMcpCmdTests.cs` / `CHANGELOG.md` / 各 `AGENTS.md` | 文档与回归同步 |

---

## Task 0: 前置 Spike（阻塞后续坐标/裁剪口径）

**Files:**
- Create: `docs/planning/research/screenshot-generalization/spike-2026-09-27.md`

**Interfaces:**
- Produces: 三条结论（WGC 窗口帧几何口径；Engine 去 `System.Drawing.Common` 是否安全；测试 seam 方案），供 Task 3/5/6/10 引用。

- [ ] **Step 1: Spike A —— WGC 窗口帧几何**

写一个临时控制台/单测（不提交）：对 `tests/TestData/UiSampleApp/UiSampleApp.exe` 主窗，分别取 WGC 首帧尺寸、`GetWindowRect`、`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS)`，打印三者。
Run: `dotnet run --project <临时工程>`（或用现有 `CaptureWindowWgcTests` 加一次性打印）
Expected: 得到「WGC 帧 == GetWindowRect」或「== EXTENDED_FRAME_BOUNDS」的确定结论。

- [ ] **Step 2: Spike B —— Engine 去 `System.Drawing.Common`**

Run: `Select-String -Path "src\DotNetDebugger.Engine\**\*.cs" -Pattern "System\.Drawing"`
Expected: 仅 `Capture/` 下命中（→ 迁出后可安全移除包引用）。

- [ ] **Step 3: Spike C —— 2MB/默认目录测试 seam**

阅读 `Tests/DotNetDebuggerMcp.Tests/ScreenshotToolTests.cs` 与 `DebugScreenshotTool.cs:113-136`，确定 seam 方案（把 `AppConfig.InlineImageBase64Bytes` 改为 internal 可注入字段，或工具方法加 internal 重载参数），写进 spike 文档结论。

- [ ] **Step 4: 写 spike 文档并提交**

把 A/B/C 结论写入 `spike-2026-09-27.md`（每条：方法/观测/结论/对哪个 Task 的影响）。
```bash
git add docs/planning/research/screenshot-generalization/spike-2026-09-27.md
git commit -m "docs(screenshot): 阶段一前置 spike 结论（WGC 帧几何/Engine 依赖/seam）"
```

---

## Task 1: 抽出 `SharpSight.Capture`（行为不变）

**Files:**
- Create: `src/SharpSight.Capture/SharpSight.Capture.csproj`
- Move: `src/DotNetDebugger.Engine/Capture/{CaptureModels,GdiCapture,ImagePipeline,ScreenCapture,WgcCapture}.cs` → `src/SharpSight.Capture/`
- Create: `tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj`
- Move: `tests/DotNetDebugger.Engine.Tests/{CaptureTestHelpers,CaptureScreenTests,CaptureWindowFindTests,CaptureWindowGdiTests,CaptureWindowWgcTests,ImagePipelineTests}.cs` → `tests/SharpSight.Capture.Tests/`
- Modify: `src/DotNetDebugger.Engine/DotNetDebugger.Engine.csproj`、`DotNetDebuggerMcp.slnx`、`src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`

**Interfaces:**
- Produces: 命名空间 `SharpSight.Capture`（原 `DotNetDebugger.Engine.Capture` 的公开面**原样**：`ScreenCapture`、`CaptureResult`、`WindowHandleInfo`、`CaptureException`、`ImagePipeline`）。
- Consumes: 无（Phase-1 Task 3+ 在其上扩展）。

- [ ] **Step 1: 建库 csproj**

`src/SharpSight.Capture/SharpSight.Capture.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <AllowUnsafeBlocks>true</AllowUnsafeBlocks>
    <RootNamespace>SharpSight.Capture</RootNamespace>
    <IsPackable>true</IsPackable>
    <PackageId>SharpSight.Capture</PackageId>
    <Version>0.1.0</Version>
    <Authors>Just-Silver</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <Description>SharpSight 截图/图像能力库（捕获 + 图像管线 + 标注 + OCR）；Windows-only。</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="System.Drawing.Common" Version="10.0.12" />
  </ItemGroup>
  <ItemGroup>
    <InternalsVisibleTo Include="SharpSight.Capture.Tests" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: `git mv` 迁文件并改命名空间**

```bash
git mv src/DotNetDebugger.Engine/Capture/CaptureModels.cs   src/SharpSight.Capture/CaptureModels.cs
git mv src/DotNetDebugger.Engine/Capture/GdiCapture.cs      src/SharpSight.Capture/GdiCapture.cs
git mv src/DotNetDebugger.Engine/Capture/ImagePipeline.cs   src/SharpSight.Capture/ImagePipeline.cs
git mv src/DotNetDebugger.Engine/Capture/ScreenCapture.cs   src/SharpSight.Capture/ScreenCapture.cs
git mv src/DotNetDebugger.Engine/Capture/WgcCapture.cs      src/SharpSight.Capture/WgcCapture.cs
```
把 5 个文件的 `namespace DotNetDebugger.Engine.Capture;` 全部改为 `namespace SharpSight.Capture;`，并把文件内对同命名空间的引用（若有显式全名）同步。

- [ ] **Step 3: Engine 去依赖 + 宿主/解决方案接线**

- `src/DotNetDebugger.Engine/DotNetDebugger.Engine.csproj`：删除 `System.Drawing.Common` 的 `PackageReference` 及其注释块（第 22-24 行）。
- `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`：`<ProjectReference>` 组新增 `..\SharpSight.Capture\SharpSight.Capture.csproj`；csproj 里不再需要 Capture 相关特殊处理。
- `DotNetDebuggerMcp.slnx`：`/src/` 下新增 `<Project Path="src/SharpSight.Capture/SharpSight.Capture.csproj" />`。
- 全仓替换宿主对旧命名空间的引用：`Select-String -Path "src\DotNetDebuggerMcp\**\*.cs","tests\**\*.cs" -Pattern "DotNetDebugger\.Engine\.Capture"`，改为 `SharpSight.Capture`。

- [ ] **Step 4: 迁测试项目**

`tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj`（复制 `DotNetDebugger.Engine.Tests.csproj` 的包引用，改 `RootNamespace` 与 `ProjectReference` 指向 `SharpSight.Capture`）：
```xml
<ProjectReference Include="..\..\src\SharpSight.Capture\SharpSight.Capture.csproj" />
```
`git mv` 6 个测试文件；改命名空间/`using`；`slnx` 的 `/tests/` 下新增该项目。`CaptureTestHelpers` 里对 Engine 其它类型的引用（若有）一并核对。

- [ ] **Step 5: 构建**

Run: `dotnet build -c Release src/SharpSight.Capture/SharpSight.Capture.csproj && dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`
Expected: 0 错误 0 警告。

- [ ] **Step 6: 跑迁入的测试**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj`
Expected: 与迁入前同数通过（锁屏/无头 `Assert.Skip` 照旧）。

- [ ] **Step 7: 提交**

```bash
git add -A
git commit -m "refactor(screenshot): 抽出 SharpSight.Capture（Engine 迁出 Capture/，去 System.Drawing.Common）"
```

---

## Task 2: 抽出 `SharpSight.UiAutomation`（行为不变）

**Files:**
- Create: `src/SharpSight.UiAutomation/SharpSight.UiAutomation.csproj`
- Move: `src/DotNetDebuggerMcp/Services/Ui/{UiAutomationService,UiElementLocator,UiEventWaiter,UiModels,UiPatternDispatcher,UiStateReader}.cs` → `src/SharpSight.UiAutomation/`
- Move: `src/DotNetDebuggerMcp/Services/UiSemanticResolver.cs` → `src/SharpSight.UiAutomation/`（或按 Step 2 决策保留宿主）
- Modify: `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`、`DotNetDebuggerMcp.slnx`、`Tools/Debugger/UiTools.cs`、`Tools/Debugger/DebugScreenshotTool.cs`

**Interfaces:**
- Produces: 命名空间 `SharpSight.UiAutomation`（原 `DotNetDebuggerMcp.Services.Ui` 公开面原样：`UiElementLocator`、`UiElementInfo`、`UiPatternCapabilities`、`UiAutomationService`、`UiEventWaiter`、`UiModels`）。
- Consumes: 无。

- [ ] **Step 1: 建库 csproj（FlaUI）**

`src/SharpSight.UiAutomation/SharpSight.UiAutomation.csproj`：
```xml
<Project Sdk="Microsoft.NET.Sdk">
  <PropertyGroup>
    <TargetFramework>net10.0-windows10.0.22621.0</TargetFramework>
    <ImplicitUsings>enable</ImplicitUsings>
    <Nullable>enable</Nullable>
    <RootNamespace>SharpSight.UiAutomation</RootNamespace>
    <IsPackable>true</IsPackable>
    <PackageId>SharpSight.UiAutomation</PackageId>
    <Version>0.1.0</Version>
    <FlaUIVersion>5.0.0</FlaUIVersion>
    <Authors>Just-Silver</Authors>
    <PackageLicenseExpression>MIT</PackageLicenseExpression>
    <Description>SharpSight UI 自动化能力库（FlaUI/UIA 元素模型与定位）；Windows-only。</Description>
  </PropertyGroup>
  <ItemGroup>
    <PackageReference Include="FlaUI.Core" Version="$(FlaUIVersion)" />
    <PackageReference Include="FlaUI.UIA3" Version="$(FlaUIVersion)" />
  </ItemGroup>
</Project>
```

- [ ] **Step 2: 定 `UiSemanticResolver` 归属**

Run: `Select-String -Path "src\DotNetDebuggerMcp\Services\UiSemanticResolver.cs" -Pattern "using |Decompiler"`
Expected: 若只依赖 BCL（`System.Reflection.Metadata`/`PEReader`）⇒ `git mv` 进新库；若依赖 `DotNetDebugger.Decompiler` ⇒ **保留宿主**，新库定义 `IUiSemanticResolver` 接口由宿主注入（避免新库反引宿主/Decompiler）。把结论写进 Task 0 的 spike 文档。

- [ ] **Step 3: 迁文件 + 改命名空间**

```bash
git mv src/DotNetDebuggerMcp/Services/Ui/UiAutomationService.cs src/SharpSight.UiAutomation/
git mv src/DotNetDebuggerMcp/Services/Ui/UiElementLocator.cs   src/SharpSight.UiAutomation/
git mv src/DotNetDebuggerMcp/Services/Ui/UiEventWaiter.cs      src/SharpSight.UiAutomation/
git mv src/DotNetDebuggerMcp/Services/Ui/UiModels.cs           src/SharpSight.UiAutomation/
git mv src/DotNetDebuggerMcp/Services/Ui/UiPatternDispatcher.cs src/SharpSight.UiAutomation/
git mv src/DotNetDebuggerMcp/Services/Ui/UiStateReader.cs      src/SharpSight.UiAutomation/
```
`namespace DotNetDebuggerMcp.Services.Ui;` → `namespace SharpSight.UiAutomation;`；宿主侧引用 `Services.Ui` 的 `using` 全量替换。

- [ ] **Step 4: 宿主接线**

- 宿主 csproj `<ProjectReference>` 增 `..\SharpSight.UiAutomation\SharpSight.UiAutomation.csproj`；宿主 csproj **去掉**直引的 FlaUI 两行（第 52-55 行）与 `FlaUIVersion` 属性（第 24 行）——改由新库传递（`FlaUI.Core`/`FlaUI.UIA3` 会随 ProjectReference 传递）。
- `slnx` `/src/` 增 `src/SharpSight.UiAutomation/SharpSight.UiAutomation.csproj`。
- 全仓替换 `DotNetDebuggerMcp.Services.Ui` → `SharpSight.UiAutomation`（`Select-String` 定位 `Tools/Debugger/UiTools.cs`、`DebugScreenshotTool.cs`、`Services/*.cs`、`tests/*.cs`）。

- [ ] **Step 5: 构建 + 全量宿主测试**

Run: `dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`
Expected: 0 错误。Run: `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj`
Expected: 全绿（含 `UiToolsTests`/`ScreenshotToolTests`）。

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "refactor(ui): 抽出 SharpSight.UiAutomation（宿主 Services/Ui 迁出，FlaUI 引用下移）"
```

---

## Task 3: 坐标模型 `origin + scale + frameId`（`CaptureResult` 扩展）

**Files:**
- Modify: `src/SharpSight.Capture/CaptureModels.cs`
- Modify: `src/SharpSight.Capture/ScreenCapture.cs`
- Modify: `src/SharpSight.Capture/ImagePipeline.cs`
- Test: `tests/SharpSight.Capture.Tests/CaptureScreenTests.cs`

**Interfaces:**
- Produces: `CaptureResult` 增字段 `OriginX`、`OriginY`、`Scale`、`FrameId`、`DisplayIndex`、`IsClientArea`；`ImagePipeline.Process` 增加 `origin` 透传。
- Consumes: Task 1 的 `CaptureResult`。

- [ ] **Step 1: 写失败测试（origin/scale 正确）**

```csharp
[Fact]
public void CaptureScreen_Region_ReportsOriginAndScale()
{
    // 虚拟屏右半区，限制到 1/4 尺寸
    var r = ScreenCapture.CaptureScreen(new CaptureOptions {
        Clip = new Rectangle(0, 0, 400, 300), MaxWidth = 200, MaxHeight = 150 });
    Assert.Equal(0, r.OriginX);
    Assert.Equal(0, r.OriginY);
    Assert.Equal(0.5, r.Scale, 3);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*ReportsOriginAndScale"`
Expected: 编译失败（`OriginX`/`Scale`/`CaptureOptions` 不存在）。

- [ ] **Step 3: 实现**

`CaptureModels.cs`：`CaptureResult` record 追加（保持既有参数顺序，新增置末带默认值）：
```csharp
public record CaptureResult(
    byte[] Image, int Width, int Height, int NativeWidth, int NativeHeight,
    string? WindowTitle, string Source, bool WasAllBlack, bool ClippedToScreen = false,
    int OriginX = 0, int OriginY = 0, double Scale = 1.0, int FrameId = 0,
    int DisplayIndex = -1, bool IsClientArea = false);
```
`ScreenCapture`：在裁剪/换算后按「抓取矩形左上（虚拟屏物理像素）」填 `OriginX/OriginY`，`Scale = (double)Width / NativeWidth`；`FrameId` 暂由宿主注入（Task 10）。`ImagePipeline.Process` 增加 `int originX, int originY` 入参并回填。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*ReportsOriginAndScale"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(sharp-sight): CaptureResult 增 origin/scale/frameId/display/client 元数据"
```

---

## Task 4: 显示器寻址（`EnumerateDisplays` + `CaptureDisplay`）

**Files:**
- Create: `src/SharpSight.Capture/DisplayEnumerator.cs`
- Modify: `src/SharpSight.Capture/ScreenCapture.cs`
- Test: `tests/SharpSight.Capture.Tests/DisplayEnumeratorTests.cs`

**Interfaces:**
- Produces: `record DisplayInfo(int Index, string DeviceName, bool IsPrimary, Rectangle Bounds, Rectangle WorkArea, double Scale)`；`DisplayInfo[] ScreenCapture.EnumerateDisplays()`；`CaptureResult ScreenCapture.CaptureDisplay(int index)`。
- Consumes: Task 1/3。

- [ ] **Step 1: 写失败测试**

```csharp
[Fact]
public void EnumerateDisplays_HasExactlyOnePrimary()
{
    var ds = ScreenCapture.EnumerateDisplays();
    Assert.NotEmpty(ds);
    Assert.Equal(1, ds.Count(d => d.IsPrimary));
    Assert.All(ds, d => Assert.True(d.Bounds.Width > 0 && d.Bounds.Height > 0));
    Assert.Equal(0, ds[0].Index);   // 0 基、按枚举序
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*HasExactlyOnePrimary"`
Expected: FAIL（`EnumerateDisplays` 不存在）。

- [ ] **Step 3: 实现 `DisplayEnumerator`**

`EnumDisplayMonitors` + `GetMonitorInfoW(MONITORINFOEX)`（**必须设 `cbSize`**）+ `MONITORINFOF_PRIMARY(0x1)` + `szDevice` + `GetDpiForMonitor(MDT_EFFECTIVE_DPI)`（0 基 index = 枚举序；`Scale = dpi/96`）。`CaptureDisplay(index)` 复用 `CaptureScreen` 的 BitBlt，`Clip = Bounds`，回填 `DisplayIndex`/`OriginX/Y`。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*Display*"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(sharp-sight): 显示器枚举与逐屏捕获（EnumerateDisplays/CaptureDisplay）"
```

## Task 5: 窗口寻址扩展（`hwnd` / 前台 / 客户区 / DWM 去阴影）

**Files:**
- Modify: `src/SharpSight.Capture/ScreenCapture.cs`
- Modify: `src/SharpSight.Capture/CaptureModels.cs`
- Test: `tests/SharpSight.Capture.Tests/CaptureWindowBoundsTests.cs`

**Interfaces:**
- Produces: `WindowHandleInfo? ScreenCapture.FindWindowByHwnd(IntPtr hwnd)`；`WindowHandleInfo? ScreenCapture.FindForegroundWindow()`；`CaptureResult ScreenCapture.CaptureWindow(IntPtr hwnd, bool clientArea = false)`；`record WindowBounds(Rectangle ExtendedFrame, Rectangle WindowRect, Rectangle ClientArea)`。
- Consumes: Task 1/3；Task 0 Spike A 的几何结论。

- [ ] **Step 1: 写失败测试（DWM 边框 ≤ WindowRect，客户区 ⊆ 扩展边框）**

```csharp
[Fact]
public void WindowBounds_Relation()
{
    using var app = UiSampleApp.Launch();
    var h = ScreenCapture.FindWindowByHwnd(app.MainWindowHandle)!;
    var b = ScreenCapture.GetWindowBounds(h.Hwnd);
    Assert.True(b.ExtendedFrame.Width <= b.WindowRect.Width);
    Assert.True(b.ExtendedFrame.Contains(b.ClientArea));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*WindowBounds_Relation"`
Expected: FAIL（`FindWindowByHwnd`/`GetWindowBounds` 不存在）。

- [ ] **Step 3: 实现**

- `FindWindowByHwnd`：`IsWindow`+`IsWindowVisible`+`GetAncestor(hwnd, GA_ROOT)==hwnd` 校验后返回 `WindowHandleInfo`。
- `FindForegroundWindow`：`GetForegroundWindow`+`GetWindowThreadProcessId`；NULL → 返回 null（宿主转中文原因）。
- `GetWindowBounds`：`DwmGetWindowAttribute(DWMWA_EXTENDED_FRAME_BOUNDS=9)`（失败回退 `GetWindowRect`）+ `GetClientRect`+`ClientToScreen`。
- `CaptureWindow(hwnd, clientArea)`：`clientArea=true` 用 `ClientArea` 作抓取矩形（走 GDI BitBlt 该矩形）；否则整窗沿用 WGC→PrintWindow→BitBlt 回退链，但**整窗几何改用 Spike A 结论**（若 WGC 帧=`GetWindowRect` 则在裁剪时扣掉扩展边框偏移；写注释记录结论）。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*WindowBounds*"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(sharp-sight): 窗口寻址扩展（hwnd/前台/客户区/DWM 去阴影）"
```

---

## Task 6: 元素级截图 + 代际号（`frameId`）

**Files:**
- Modify: `src/SharpSight.UiAutomation/UiElementLocator.cs`、`UiModels.cs`
- Create: `src/SharpSight.UiAutomation/FrameRegistry.cs`
- Modify: `src/SharpSight.Capture/ScreenCapture.cs`（`CaptureElement`）
- Test: `tests/DotNetDebuggerMcp.Tests/UiFrameRegistryTests.cs`

**Interfaces:**
- Produces: `UiElementLocator.FindForCapture(...)` 返回 `UiElementInfo` 含**结构化 `Rectangle RectPx`** 与 `IntPtr TopLevelHwnd`；`FrameRegistry.Current`（`int` 单调递增）与 `CaptureFor(int frameId, int index)`；`CaptureResult ScreenCapture.CaptureElement(IntPtr topLevelHwnd, Rectangle elementRectPx)`。
- Consumes: Task 3（`CaptureResult`）、Task 5（`FindWindowByHwnd`）。

- [ ] **Step 1: 写失败测试（代际单调 + 旧帧拒绝）**

```csharp
[Fact]
public void FrameRegistry_RejectsStaleFrame()
{
    var reg = new FrameRegistry();
    var f1 = reg.Next();
    var f2 = reg.Next();
    Assert.True(f2 > f1);
    Assert.Throws<StaleFrameException>(() => reg.Validate(f1));
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-method "*FrameRegistry*"`
Expected: FAIL（类型不存在）。

- [ ] **Step 3: 实现**

- `FrameRegistry`：`int Next()`（`Interlocked.Increment`）、`void Validate(int frameId)`（`frameId!=0 && frameId!=Current` → `throw new StaleFrameException(...)` 中文消息）。
- `UiElementInfo`：新增 `Rectangle RectPx`（由现有 `BoundingRectangle` 物理像素直接落结构体，字符串 `Rect` 保留给旧输出）、`IntPtr TopLevelHwnd`（元素 `GetAncestor(GA_ROOT)`）。
- `CaptureElement(hwnd, rectPx)`：先 `CaptureWindow(hwnd)` 拿窗口帧 → `rectPx` 减窗口帧原点 → 与帧求交 → 裁剪；元素属另一顶层窗口时宿主传对应 hwnd。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-method "*FrameRegistry*"`
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(sharp-sight): 元素级截图与 frameId 代际护栏"
```

---

## Task 7: 双轴降采样 + 默认 1568

**Files:**
- Modify: `src/SharpSight.Capture/ImagePipeline.cs`
- Modify: `src/DotNetDebuggerMcp/Configuration/AppConfig.cs`
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs`
- Test: `tests/SharpSight.Capture.Tests/ImagePipelineTests.cs`

**Interfaces:**
- Produces: `ImagePipeline.Process(source, int maxWidth, int maxHeight, string format, int quality, ...)`（**替代**原 `maxDimension` 单参）；`AppConfig.ScreenshotMaxDimension = 1568`。
- Consumes: Task 3。

- [ ] **Step 1: 改失败测试**

```csharp
[Theory]
[InlineData(4000, 3000, 1568, 1568, 1568, 1176)]  // 长边受限
[InlineData(1000, 500,  1568, 1568, 1000, 500)]   // 不放大
[InlineData(4000, 1000, 1568, 600,  1568, 392)]   // 双轴取 min
public void Process_DualAxis(int w, int h, int mw, int mh, int ew, int eh)
{
    using var src = TestImages.Solid(w, h);
    var (img, _) = ImagePipeline.Process(src, mw, mh, "png", 80, null, "test", false);
    Assert.Equal(ew, img.Width);
    Assert.Equal(eh, img.Height);
}
```

- [ ] **Step 2: 跑测试确认失败**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*Process_DualAxis"`
Expected: FAIL（签名不匹配）。

- [ ] **Step 3: 实现**

`ImagePipeline`：`k = min(1, maxW/W, maxH/H)`（`maxW/maxH<=0` 视为不限制）；保留 round 语义与纵横比。`AppConfig.ScreenshotMaxDimension` 2000→**1568**（更新注释）。宿主：`maxDimension` 展开成 `maxW=maxH=maxDimension`；显式 `maxWidth/maxHeight` 优先。

- [ ] **Step 4: 跑测试确认通过**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj`（全量）
Expected: PASS。

- [ ] **Step 5: 提交**

```bash
git add -A
git commit -m "feat(screenshot): 双轴降采样并将默认上限改为 1568"
```

---

## Task 8: WebP（ImageSharp 3.1.12）

**Files:**
- Modify: `src/SharpSight.Capture/SharpSight.Capture.csproj`（加包）
- Modify: `src/SharpSight.Capture/ImagePipeline.cs`
- Test: `tests/SharpSight.Capture.Tests/ImagePipelineTests.cs`

**Interfaces:**
- Produces: `ImagePipeline.Encode` 支持 `"webp"`；产物 magic `RIFF....WEBP`。
- Consumes: Task 7。

- [ ] **Step 1: 加包**

```xml
<PackageReference Include="SixLabors.ImageSharp" Version="3.1.12" />
```
（**不要 v4.x**；**不要** `ImageSharp.Drawing`；**不要** SkiaSharp。）

- [ ] **Step 2: 写失败测试**

```csharp
[Fact]
public void Encode_Webp_HasRiffMagic()
{
    using var src = TestImages.Solid(64, 64);
    var (img, bytes) = ImagePipeline.Process(src, 1568, 1568, "webp", 80, null, "test", false);
    Assert.Equal("RIFF", Encoding.ASCII.GetString(bytes, 0, 4));
    Assert.Equal("WEBP", Encoding.ASCII.GetString(bytes, 8, 4));
}
```

- [ ] **Step 3: 跑测试确认失败**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*Encode_Webp*"`
Expected: FAIL（未知格式抛 `ArgumentException`）。

- [ ] **Step 4: 实现**

`ImagePipeline.Encode` 增 `case "webp"`：`bitmap.LockBits`（`Format32bppArgb`，二进制兼容 `Bgra32`）→ `Image.LoadPixelData<Bgra32>(span, w, h, stride)` → `SaveAsWebp(stream, new WebpEncoder { Quality = clamp(quality) })`（quality 只夹上界，调用方 `Clamp(0,100)`）。宿主 `format` 白名单加 `"webp"`、扩展名 `.webp`、mime `image/webp`。

- [ ] **Step 5: 跑测试确认通过**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj`
Expected: PASS。

- [ ] **Step 6: 验证发布产物无原生资产（Task 0/§11）**

Run: `dotnet pack -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj -o out/pkg && (展开 nupkg 检查 tools/ 下无 *.dll 原生资产列表外文件)`
Expected: 无新增原生 dll（ImageSharp 纯托管）。

- [ ] **Step 7: 提交**

```bash
git add -A
git commit -m "feat(screenshot): 加 WebP 编码（ImageSharp 3.1.12，纯托管）"
```

---

## Task 9: `includeCursor`

**Files:**
- Modify: `src/SharpSight.Capture/WgcCapture.cs`、`ScreenCapture.cs`
- Test: `tests/SharpSight.Capture.Tests/CaptureCursorTests.cs`

**Interfaces:**
- Produces: `ScreenCapture` 抓取选项增 `bool IncludeCursor`。
- Consumes: Task 1/5。

- [ ] **Step 1: 写失败测试（不崩溃且能开关）**

```csharp
[Fact]
public void CaptureScreen_WithCursor_DoesNotThrow()
{
    var withCur = ScreenCapture.CaptureScreen(new CaptureOptions { IncludeCursor = true });
    Assert.True(withCur.Width > 0);
}
```

- [ ] **Step 2: 实现**

WGC：`IsCursorCaptureEnabled` 用 `ApiInformation.TryGetApiContractPresent`/`IsPropertySupported` 探测（失败忽略）。GDI：`GetCursorInfo`+`GetIconInfo`+`DrawIconEx` 叠加（失败在 `Source` 备注「光标未叠加」）。

- [ ] **Step 3: 跑测试 + 提交**

Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj -- --filter-method "*Cursor*"`
```bash
git add -A
git commit -m "feat(sharp-sight): includeCursor（WGC 开关 / GDI 叠光标）"
```

---

## Task 10: 宿主工具面（参数 / 校验 / 头部 / 错误表 + `ui_*` frameId）

**Files:**
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs`
- Modify: `src/DotNetDebuggerMcp/Tools/Debugger/UiTools.cs`
- Modify: `src/DotNetDebuggerMcp/Configuration/AppText.cs`（或新 `CaptureText.cs`）
- Test: `tests/DotNetDebuggerMcp.Tests/ScreenshotToolTests.cs`

**Interfaces:**
- Consumes: Task 1–9 全部库能力。
- Produces: `screenshot` 新参数面（spec §4.1）；`ui_find` 输出含 `帧: frameId=N`；`ui_action`/`ui_input`/`ui_get` 增 `int frameId = 0`。

- [ ] **Step 1: 更新参数面与 `[Description]`**

按 spec §4.1 逐项加参（全部带默认值）：`mode="auto"`、`display=""`、`hwnd=""`、`clientArea=false`、`regionSpace="screen"`、`element=""`、`frameId=0`、`maxWidth=0`、`maxHeight=0`、`grayscale=false`、`diff=false`、`includeCursor=false`（`annotate/ocr/ocrLanguage/ocrFallback/maxMarks` 属阶段二，**本任务不加**）。重写 `[Description]`：中文、注明默认值、写清 `region` 坐标口径与 `origin/scale` 头部。

- [ ] **Step 2: 实现 `mode` 推断与校验（失败测试先行）**

```csharp
[Theory]
[InlineData("element", "", "", "", "", "element")]
[InlineData("auto", "", "123456", "", "", "window")]
[InlineData("auto", "primary", "", "", "", "display")]
[InlineData("auto", "", "", "", "10,10,50,50", "region")]
[InlineData("auto", "", "", "", "", "screen")]
public void ResolveMode_Matrix(string mode, string display, string hwnd, string title, string region, string expected)
    => Assert.Equal(expected, DebugScreenshotTool.ResolveMode(mode, display, hwnd, title, region));
```
`ResolveMode` 为 `internal static`，供测试直调。

- [ ] **Step 3: 头部与错误表**

头部按 spec §4.3 输出（`目标/尺寸/缩放/原点/帧/来源/备注`）；错误文案走常量（新 `CaptureText`）：模式非法、`region` 格式错、region 全屏外、前台窗口不可得、hwnd 无效、超时未找到、抓取全失败、`clientArea`+`element` 冲突、`region`+`window` 冲突、取消。

- [ ] **Step 4: `ui_*` 增 `frameId`**

`ui_find` 结果头部加 `帧: frameId=N`；`ui_action`/`ui_input`/`ui_get` 增 `int frameId = 0` 并调 `FrameRegistry.Validate(frameId)`（0=跳过，向后兼容）。

- [ ] **Step 5: 跑宿主单测**

Run: `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj`
Expected: PASS（含新 `ResolveMode` 矩阵、头部断言、旧帧拒绝）。

- [ ] **Step 6: 本机手工验收（真实 GUI）**

Run: `dotnet run -c Release --project src/DotNetDebuggerMcp.Client/...` 或 `-dbg`；对记事本/副屏/前台/客户区/hwnd 各截一张，核对头部 `原点/缩放/来源` 与图片。
Expected: 图片与头部一致。

- [ ] **Step 7: 提交**

```bash
git add -A
git commit -m "feat(screenshot): 工具面扩展（display/hwnd/foreground/clientArea/element + origin/scale 头部）；ui_* 增 frameId"
```

---

## Task 11: 文档 / 握手 / 回归 / CHANGELOG 同步（同 commit）

**Files:**
- Modify: 根 `README.md`、`src/DotNetDebuggerMcp/Configuration/AppText.cs`、`tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcpCmdTests.cs`
- Modify: `CHANGELOG.md`、根 `AGENTS.md`、`src/DotNetDebugger.Engine/AGENTS.md`、`src/DotNetDebuggerMcp/AGENTS.md`

**Interfaces:**
- Consumes: Task 10 的最终参数面/输出面。

- [ ] **Step 1: 根 README screenshot 章节重写**

写清三目标参数（阶段一范围）、`origin/scale/frameId` 语义、Windows-only、新库说明（`SharpSight.Capture`/`SharpSight.UiAutomation`）。

- [ ] **Step 2: 握手 + 回归断言**

`AppText.HandshakeFeatureIntro` 的 screenshot 触发条件补：多显示器/前台窗口/客户区/hwnd/元素级/WebP/光标；`DotNetDebuggerMcpCmdTests.HandshakeFeatureIntro_覆盖全部能力族触发条件` 逐族补 `Assert.Contains`（漏一族即失败）。

- [ ] **Step 3: AGENTS.md 依赖方向**

- 根 `AGENTS.md`：依赖方向句加 `SharpSight.Capture`/`SharpSight.UiAutomation`（零宿主依赖能力库）；项目地图加两行。
- `src/DotNetDebugger.Engine/AGENTS.md`：边界纪律**去** `System.Drawing.Common`，`Capture/` 结构改为「已迁出至 SharpSight.Capture」。
- `src/DotNetDebuggerMcp/AGENTS.md`：目录结构去 `Services/Ui/`（改引新库）、`screenshot` 描述更新、`ui_*` 增 `frameId`。

- [ ] **Step 4: CHANGELOG 新建 `[Unreleased]`**

```markdown
## [Unreleased]

### 新增
- `screenshot` 工具通用化（阶段一）：多显示器/前台窗口/客户区/hwnd/元素级寻址，统一 `origin/scale/frameId` 坐标元数据，WebP 编码，`includeCursor`，双轴降采样（默认 1568）。
- 新增可复用库 `SharpSight.Capture` 与 `SharpSight.UiAutomation`。
- `ui_find` 返回 `frameId`；`ui_action`/`ui_input`/`ui_get` 新增可选 `frameId` 旧帧护栏。
```

- [ ] **Step 5: 全量回归**

Run: `dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`
Run: `dotnet test --project tests/SharpSight.Capture.Tests/SharpSight.Capture.Tests.csproj`
Run: `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj`
Run: `dotnet run -c Release --project src/DotNetDebuggerMcp.Client/DotNetDebuggerMcp.Client.csproj`
Expected: 全绿；Client e2e 不受影响（screenshot 豁免）。

- [ ] **Step 6: 提交**

```bash
git add -A
git commit -m "docs(screenshot): 同步 README/握手/回归断言/AGENTS/CHANGELOG（阶段一）"
```

---

## 自审记录（写入后逐项核对）

1. **Spec 覆盖（§14 阶段一）**：库化重构→Task 1/2；寻址扩展→Task 4/5/6；坐标模型→Task 3；双轴 1568→Task 7；WebP→Task 8；`includeCursor`→Task 9；工具面→Task 10；文档/握手/回归→Task 11；前置 spike→Task 0。**阶段二项（annotate/ocr/diff/regionSpace=window/独立 NuGet/by-ref）不在本计划**（另立 `2026-09-27-screenshot-generalization-phase2.md`）。
2. **占位符扫描**：无 TBD/“稍后实现”；所有代码步骤含可编译片段或明确命令。
3. **类型一致性**：`CaptureResult` 字段（Task 3）在 Task 5/6/10 一致；`ImagePipeline.Process` 在 Task 7 改签名后 Task 8 沿用；`FrameRegistry.Validate` 在 Task 6 定义、Task 10 消费。

## 执行交接

计划已保存至 `docs/planning/plans/2026-09-27-screenshot-generalization-phase1.md`。两种执行方式：

1. **子代理逐任务执行（推荐）**：每个 Task 派一个新子代理，任务间我做两阶段审查。
2. **本会话内联执行**：按 `executing-plans` 分批执行、检查点复核。

选哪种？
