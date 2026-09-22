# screenshot 截图工具实现计划（2026-09-22）

> 来源 spec（三轮审查冻结，commit `646854d`）：`docs/planning/specs/2026-09-22-screenshot-tool-design.md`
> 执行方式：7 个任务（T1-T7）顺序执行，每任务独立可验收、独立中文 commit、TDD（先写测试跑失败，再实现跑过）。
> 本计划代码段均为可直接落盘的实现：WGC 段（T5）是 2026-09-22 spike 实测跑通版本（UiSampleApp 762×552 → PNG 24KB 验证通过）；`CallToolResult`/`ImageContentBlock` API 是从本机 ModelContextProtocol 2.2.0 DLL 反射核实的。
> 执行前必读：根 `AGENTS.md`（开发铁律）+ `src/DotNetDebuggerMcp/AGENTS.md` + `src/DotNetDebugger.Engine/AGENTS.md` + `tests/AGENTS.md`。

## A. 全局铁律（每任务收尾核对）

1. MCP 参数全带默认值 + 每个工具方法带 `CancellationToken cancellationToken = default`（不写 `[Description]`）；`[Description]` 中文、面向 agent、注明默认值。
2. 工具错误=中文文本返回、不抛异常（screenshot 返回 `Task<CallToolResult>`，失败=纯文本 content，**不设 `IsError`**——与现有 `Task<string>` 工具行为一致，agent 按文案识别）。
3. stdout 只承载 MCP 协议——本计划**不动** `DotNetDebuggerMcpCmd` 启动日志配置。
4. 改 MCP 工具面 → 根 `README.md` 同 commit（T6 一次做齐）。
5. 测试前置：`powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1`（本计划不改该脚本，D10）。
6. 每任务验证基线：`dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` + 本任务测试项目 `dotnet test`。

## B. spec → 任务映射（覆盖自查）

| spec 内容 | 落点 |
|---|---|
| §4.1 TFM 全链 / PackAsTool hack / FlaUI 复核 / System.Drawing.Common / 路径连带 | T1（System.Drawing.Common 进 T3） |
| §3.1 window 定位规则 / §4.2 FindMainWindow、DPI | T2 |
| §3.1 region/k 换算（Engine 归属）/ §4.2 缩放编码裁剪纯黑 | T3（纯管线）+ T4（换算入口） |
| §4.2 CaptureScreen / §4.3 回退链 2-4 / §5.2 屏外与全失败文案 | T4 |
| §4.3 WGC 细节（interop/CreateFreeThreaded/ApiInformation/APPWINDOW/nudge/500ms）/ CaptureWindow 编排 | T5 |
| §3.1-3.3 参数·Description·返回契约·头部 / §5.1 双轨落盘 / §5.2 错误全表 / §7 README·握手·AppConfig | T6 |
| §7 AGENTS×3 修订 / CHANGELOG / §6.5 手工验收 / §6.4 CI 预案 / spec 状态收尾 | T7 |
| D9 不入缓存不进 ToolPipeline（`IsErrorResult` 不动）/ §8 明确不做 | 全程（出现即偏航） |

## C. 跨任务数据形状定稿（先于一切代码，各任务以此为准）

新建 `src/DotNetDebugger.Engine/Capture/` 目录，命名空间 `DotNetDebugger.Engine.Capture`：

```csharp
// CaptureModels.cs —— 对 spec §4.2 的说明：spec 列举「hwnd/标题/rect/IsIconic」为必备字段；
// 为落实 §3.3 头部「目标 (pid=N)」「命中 N 个可见窗口」与「已裁至屏幕交集」文案，补 3 个派生字段
// （Pid / MatchCount / ClippedToScreen），语义零变化、不引入新行为。
public sealed record WindowHandleInfo(
    IntPtr Hwnd, string Title, System.Drawing.Rectangle Rect, bool IsIconic, int Pid, int MatchCount);

public sealed record CaptureResult(
    byte[] Image,            // 编码后字节（png/jpeg）
    int Width, int Height,               // 输出（缩放后）尺寸
    int NativeWidth, int NativeHeight,   // 原生（缩放前）尺寸：window=窗口、screen=全屏、region=裁剪区
    string? WindowTitle,                 // window 模式标题；screen/region 为 null
    string Source,                       // "WGC" | "PrintWindow" | "BitBlt"（screen/region 恒 BitBlt）
    bool WasAllBlack,                    // 输出图纯黑采样（头部「备注」行数据源）
    bool ClippedToScreen = false);       // region 部分越界已裁交集（头部「尺寸」行注记）

// Engine 侧错误通道：约定中文文案由 Engine 生成，宿主 catch 后原样返回（spec §5.2「屏外判定由
// Engine 执行并回传约定错误，宿主不触碰坐标换算」的落地方式）。
public sealed class CaptureException : Exception
{
    public CaptureException(string message) : base(message) { }
}
```

门面（T2 建文件放 FindMainWindow+DPI，T4 追加 CaptureScreen，T5 追加 CaptureWindow）：

```csharp
// ScreenCapture.cs
public static class ScreenCapture
{
    public static WindowHandleInfo? FindMainWindow(int processId, string titleSubstring);
    public static CaptureResult CaptureScreen(System.Drawing.Rectangle? clipInImageSpace,
        int maxDimension, string format, int quality);
    public static CaptureResult CaptureWindow(IntPtr hwnd, int maxDimension, string format, int quality);
}
```

参数归属（spec §4.2/§5.1）：`maxDimension`/2MB 阈值常量定义在宿主 `AppConfig`，**经参数传入 Engine**；`format∈{png,jpeg}`、`quality 0-100` 由宿主校验/clamp 后传入；缩放、k 换算、裁剪、编码、纯黑检测**唯一归属 Engine**，宿主只做参数校验、轮询、头部组装、落盘、CallToolResult 组装。

---

## T1 全链升 windows TFM + 宿主 PackAsTool hack + FlaUI 复核 + 输出路径文档同步

**Why**：WGC 需要 WinRT 投影，只有 `net10.0-windows10.0.22621.0` 才有编译期投影（spec §4.1/D7）；windows TFM 触发 NETSDK1146（PackAsTool 禁平台标签），须先落 spike 实证的两段 Target hack；输出目录 `bin/Debug/net10.0/` → `bin/Debug/net10.0-windows10.0.22621.0/` 使既有路径引用全部失效。风险最大、放最前。

**Files（修改，无新建）**
- 9 个 csproj 的 `<TargetFramework>` → `net10.0-windows10.0.22621.0`：`src/DotNetDebugger.Engine`、`src/DotNetDebugger.Session`、`src/DotNetDebugger.Web`、`src/DotNetDebuggerMcp`、`tests/DotNetDebugger.Engine.Tests`、`tests/DotNetDebugger.Session.Tests`、`tests/DotNetDebugger.Web.Tests`、`tests/DotNetDebuggerMcp.Tests`。
  **不升**：`src/DotNetDebugger.Decompiler`、`tests/DotNetDebugger.Decompiler.Tests`（不引 Engine，NU1201 无涉）、`src/DotNetDebuggerMcp.Client`（不引宿主；执行时以 csproj ProjectReference 复核一次）、`tests/TestData/UiSampleApp`（已是 `net10.0-windows`）。
- `src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj`：加两段 hack Target（L76 前）+ FlaUI 段复核（L48-62）。
- 路径/TFM 文档同步：根 `opencode.json`、根 `AGENTS.md`、`src/DotNetDebuggerMcp/AGENTS.md`、`tests/AGENTS.md`、`src/DotNetDebugger.Session/AGENTS.md`、`src/DotNetDebugger.Web/AGENTS.md`（凡指本项目 TFM 的 `net10.0` 字样；Engine AGENTS 的 TFM 句留 T7 与纪律修订一起）、`src/DotNetDebugger.Engine/AGENTS.md`（仅路径行可先不动）。

**Steps**

1. 改 9 个 csproj 的 `<TargetFramework>`。
2. 宿主 csproj 末尾（`</Project>` 前）加 spike 实证 hack（spec §4.1 原文）：

   ```xml
   <!-- PackAsTool 的 NETSDK1146：dotnet tool 禁止带平台标签的 TFM（官方错误列表 2026-05 仍在，
        dotnet/sdk#52716 官方支持进行中）。spike 实测（SDK 10.0.401，2026-09-22）：原样 pack ❌、
        官方 RID-specific 路 ❌、两段 Target 清空/恢复平台标识 ✅ pack→install→run 全过。
        升 SDK 版本后必须重验 dotnet pack + tool install；官方支持落地后删除本 hack。 -->
   <Target Name="HackBeforePackToolValidation" BeforeTargets="_PackToolValidation">
     <PropertyGroup><TargetPlatformIdentifier></TargetPlatformIdentifier><TargetPlatformMoniker></TargetPlatformMoniker></PropertyGroup>
   </Target>
   <Target Name="HackAfterPackToolValidation" AfterTargets="_PackToolValidation" BeforeTargets="PackTool">
     <PropertyGroup><TargetPlatformIdentifier>Windows</TargetPlatformIdentifier></PropertyGroup>
   </Target>
   ```
3. FlaUI 段复核（spec §4.1 两分支，先试直引）：
   - 尝试：删 `PackageDownload`×2 + `Reference HintPath`×2 + `Interop.UIAutomationClient`/`System.Management` 显式引用，改为 `<PackageReference Include="FlaUI.Core" Version="$(FlaUIVersion)" />` + `FlaUI.UIA3` 同款；L48-52 旧注释整段改写（旧结论「宿主必须保持 net10.0」已过期，改为指向下方 hack Target 说明）。
   - `dotnet build` 过 → 直引成立，连带确认无 NU1701/AssetTargetFallback 残留压制；U1 定向测试：`dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "DotNetDebuggerMcp.Tests.DebugUiToolsTests"`。
   - build不过 → `git checkout -- src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` 后仅重做第 2 步 hack + 保留 PackageDownload 现状、**只改写 L48-52 注释**（直引不可行的原因补一句实测记录）。
4. 全链回归：`dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` → 先跑 `generate-testdata.ps1` → 全量 `dotnet test`（5 个测试项目；重点确认 `McpSessionConcurrencyTests` stdout 零噪声护栏仍绿、测试 CWD 上溯定位不受输出目录变化影响——`TestDataPaths`/`TestPaths` 均从 `AppContext.BaseDirectory` 上溯找 slnx，无需改代码）。
5. pack 三关（**用 `--tool-path` 局部安装，不碰用户全局工具**）：

   ```powershell
   $out = "$env:TEMP\opencode\pack-verify"; Remove-Item $out -Recurse -Force -ErrorAction SilentlyContinue
   dotnet pack -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj -o $out
   dotnet tool install DotNetDebuggerMcp --tool-path "$out\bin" --add-source $out --version 1.9.1
   & "$out\bin\DotNetDebuggerMcp.exe" -h    # run 关：打印帮助即过
   ```
   （版本取 csproj `<Version>` 当前值；三关任一失败=hack 失效，停下来排查，勿带病往下。）
6. 路径/TFM 文档同步（用 `git grep` 收口，`docs/planning` 历史归档不改）：

   ```bash
   # 仓库根执行；两条命令命中后逐条人工判断（第二条会同时命中已改/不该改的，按下方口径筛）
   git grep -n "bin/Debug/net10\.0" -- '*.md' '*.json' ':(exclude)docs/planning'
   git grep -n "net10\.0" -- 'AGENTS.md' 'src/*/AGENTS.md' 'tests/AGENTS.md'
   ```
   逐条判断：指**输出路径** → 换 `bin/Debug/net10.0-windows10.0.22621.0/`（`opencode.json` 的 MCP 绑定 exe、根/宿主 AGENTS 验证命令与本地调试注意段、`tests/AGENTS.md` 测试 CWD 基准句）；指**已升项目 TFM** → 换 `net10.0-windows10.0.22621.0`（Session/Web/宿主 AGENTS 的边界句；Decompiler AGENTS 不动）；已是 `net10.0-windows`（UiSampleApp 原生写法）或已含 `windows10` 的命中忽略。两条命令不再有**新增**待改命中=完成。

**Verify**：第 4/5/6 步输出全绿 + `git diff --stat` 仅含上述文件。
**Commit**：`build: 全链升 net10.0-windows22621 TFM，宿主 PackAsTool 两段 hack 与 FlaUI 引用复核，输出路径文档同步`

---

## T2 Engine Capture 骨架：数据模型 + FindMainWindow + DPI + 单测

**Why**：窗口定位是 window/region 模式的地基（spec §3.1 定位规则、§4.2 API）；纯 Win32 枚举无外部依赖，可先行独立验收。

**Files**
- 新建 `src/DotNetDebugger.Engine/Capture/CaptureModels.cs`（§C 定稿内容）。
- 新建 `src/DotNetDebugger.Engine/Capture/ScreenCapture.cs`（本任务只含：DPI + FindMainWindow + GetWindowInfo 辅助；CaptureScreen/CaptureWindow 留 T4/T5 追加）。
- 修改 `tests/DotNetDebugger.Engine.Tests/TestPaths.cs`：加 `public static string UiSampleAppExe { get; } = Locate("tests", "TestData", "UiSampleApp", "UiSampleApp.exe");`
- 新建 `tests/DotNetDebugger.Engine.Tests/UiSampleAppProcess.cs`（起停 helper）。
- 新建 `tests/DotNetDebugger.Engine.Tests/CaptureTestHelpers.cs`（等窗出现的轮询辅助，T2/T4/T5 测试共用）。
- 新建 `tests/DotNetDebugger.Engine.Tests/CaptureWindowFindTests.cs`。

**Steps（TDD：先 1-2，跑红，再 3-4，跑绿）**

1. 写 `CaptureWindowFindTests.cs` 骨架与用例（UiSampleApp 启动方式见下方 helper；每个用例 finally 必停进程）：

   ```csharp
   [Collection("Capture")]   // Engine.Tests 全局 ParallelMode.None 已串行；Collection 仅为语义标注
   public sealed class CaptureWindowFindTests
   {
       [Fact] public void FindMainWindow_ByPid_HitsUiSampleWindow()
       {
           using var app = UiSampleAppProcess.Start();
           var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
           Assert.NotNull(w);
           Assert.Equal("UiSample", w!.Title);
           Assert.Equal(app.Process.Id, w.Pid);
           Assert.True(w.Rect.Width > 0 && w.Rect.Height > 0);
           Assert.True(w.MatchCount >= 1);
       }

       [Fact] public void FindMainWindow_ByTitleSubstring_Hits_IgnoringCase()
       {
           using var app = UiSampleAppProcess.Start();
           var w = CaptureTestHelpers.WaitFound(title: "uisample", TimeSpan.FromSeconds(5));   // 忽略大小写
           Assert.NotNull(w);
       }

       [Fact] public void FindMainWindow_NoMatch_ReturnsNull()
       {
           Assert.Null(ScreenCapture.FindMainWindow(processId: 4185100 /*大概率不存在*/, titleSubstring: ""));
           Assert.Null(ScreenCapture.FindMainWindow(0, "这个标题一定不存在-" + Guid.NewGuid()));
       }
   }
   ```

   轮询辅助 `CaptureTestHelpers.cs`（T4/T5 的窗口用例同样引用它，勿在各测试类重复写）：

   ```csharp
   namespace DotNetDebugger.Engine.Tests;

   internal static class CaptureTestHelpers
   {
       /// <summary>按 pid 轮询等窗口出现（窗口创建异步，50ms 间隔，超时返回 null）。</summary>
       public static WindowHandleInfo? WaitFound(int pid, TimeSpan timeout)
       {
           var deadline = DateTime.UtcNow + timeout;
           WindowHandleInfo? w;
           do
           {
               w = ScreenCapture.FindMainWindow(pid, "");
               if (w is not null) return w;
               Thread.Sleep(50);
           } while (DateTime.UtcNow < deadline);
           return null;
       }

       /// <summary>按标题子串轮询（忽略大小写）。</summary>
       public static WindowHandleInfo? WaitFound(string title, TimeSpan timeout)
       {
           var deadline = DateTime.UtcNow + timeout;
           WindowHandleInfo? w;
           do
           {
               w = ScreenCapture.FindMainWindow(0, title);
               if (w is not null) return w;
               Thread.Sleep(50);
           } while (DateTime.UtcNow < deadline);
           return null;
       }
   }
   ```

2. 起停 helper `UiSampleAppProcess.cs`（**`UseShellExecute=true` 不重定向即满足排空纪律**——GUI 子进程不继承 stdout/stderr 管道，继承了会让调用方管道永不关闭，spike 实测教训；spec §6.1 同款防御的落地）：

   ```csharp
   internal sealed class UiSampleAppProcess : IDisposable
   {
       public Process Process { get; }
       private UiSampleAppProcess(Process p) => Process = p;

       public static UiSampleAppProcess Start()
       {
           KillLeftovers();
           var exe = TestPaths.UiSampleAppExe;
           if (!File.Exists(exe))
               throw new FileNotFoundException("UiSampleApp.exe 不存在，请先运行 generate-testdata.ps1", exe);
           var p = Process.Start(new ProcessStartInfo(exe)
           { WorkingDirectory = Path.GetDirectoryName(exe)!, UseShellExecute = true })!;
           return new UiSampleAppProcess(p);
       }

       public void Dispose()
       {
           try { if (!Process.HasExited) Process.Kill(entireProcessTree: true); } catch { /* 已退出忽略 */ }
           KillLeftovers();
       }

       private static void KillLeftovers()
       {
           foreach (var p in Process.GetProcessesByName("UiSampleApp"))
               try { p.Kill(entireProcessTree: true); } catch { /* 忽略 */ }
       }
   }
   ```

3. 跑 `dotnet test --project tests/DotNetDebugger.Engine.Tests/DotNetDebugger.Engine.Tests.csproj` → 编译失败/红（类型与方法尚不存在）= 预期。
4. 实现 `CaptureModels.cs`（§C 原样）与 `ScreenCapture.cs` 本任务部分：

   ```csharp
   using System.Diagnostics;
   using System.Runtime.InteropServices;

   namespace DotNetDebugger.Engine.Capture;

   /// <summary>窗口/屏幕截图门面（spec：docs/planning/specs/2026-09-22-screenshot-tool-design.md §4.2）。
   /// 截图与 ICorDebug/命令泵零交互（§4.4），首次调用统一设进程 DPI 为 Per-Monitor V2（物理像素，
   /// 与 region 坐标空间定义一致；运行时调用、不用 manifest，已设置时容忍 ERROR_ACCESS_DENIED）。</summary>
   public static class ScreenCapture
   {
       // DPI：幂等，仅首次生效
       private static int _dpiSet;   // 0=未设 1=已设
       private const int ProcessPerMonitorDpiAwareV2 = 4;

       [DllImport("user32.dll")] private static extern bool SetProcessDpiAwarenessContext(IntPtr value);
       [DllImport("user32.dll")] private static extern bool EnumWindows(EnumWindowsProc cb, IntPtr extra);
       [DllImport("user32.dll")] private static extern bool IsWindowVisible(IntPtr hwnd);
       [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint flags); // 2=GA_ROOT
       [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
       [DllImport("user32.dll")] private static extern bool IsIconic(IntPtr hwnd);
       [DllImport("user32.dll")] private static extern int GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
       [DllImport("user32.dll", CharSet = CharSet.Unicode)]
       private static extern int GetWindowText(IntPtr hwnd, System.Text.StringBuilder text, int maxCount);

       private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr extra);

       [StructLayout(LayoutKind.Sequential)]
       private struct Rect { public int Left, Top, Right, Bottom; }

       internal static void EnsureDpi()
       {
           if (Interlocked.CompareExchange(ref _dpiSet, 1, 0) != 0) return;
           SetProcessDpiAwarenessContext(new IntPtr(ProcessPerMonitorDpiAwareV2));
           // 失败（已由 manifest/调用方设置 → ERROR_ACCESS_DENIED=5，或系统过旧 → ERROR_INVALID_PARAMETER=87）均容忍
       }

       /// <summary>枚举顶层可见窗口定位目标主窗（spec §3.1：processId&gt;0 仅按 pid、忽略标题；
       /// processId=0 且标题非空仅按标题子串（忽略大小写）；都空返回 null（宿主保证不传双空）。
       /// EnumWindows 顺序即 Z 序（顶→底），首个命中即 Z 序最前；MatchCount=全部命中数。</summary>
       public static WindowHandleInfo? FindMainWindow(int processId, string titleSubstring)
       {
           EnsureDpi();
           if (processId <= 0 && string.IsNullOrWhiteSpace(titleSubstring)) return null;

           WindowHandleInfo? first = null;
           var count = 0;
           EnumWindows((h, _) =>
           {
               if (!IsWindowVisible(h) || GetAncestor(h, 2 /*GA_ROOT*/) != h) return true;
               GetWindowThreadProcessId(h, out var pid);
               if (processId > 0)
               {
                   if (pid != (uint)processId) return true;
               }
               else
               {
                   var sb = new System.Text.StringBuilder(256);
                   GetWindowText(h, sb, sb.Capacity);
                   if (sb.ToString().IndexOf(titleSubstring, StringComparison.OrdinalIgnoreCase) < 0) return true;
               }
               count++;
               if (first is null)
               {
                   GetWindowRect(h, out var r);
                   var tsb = new System.Text.StringBuilder(256);
                   GetWindowText(h, tsb, tsb.Capacity);
                   first = new WindowHandleInfo(h, tsb.ToString(),
                       new System.Drawing.Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
                       IsIconic(h), (int)pid, count);
               }
               return true;   // 继续枚举以统计 MatchCount
           }, IntPtr.Zero);

           if (first is null) return null;
           return first with { MatchCount = count };
       }
   }
   ```

   要点：① `GetWindowThreadProcessId` 返回值是**线程 id**，进程 id 只能取 `out` 参数（spike 实录 bug）；② pid 匹配分支**不看标题**（spec §3.1 钉死的择一语义）；③ `first with { MatchCount = count }` 补全计数（record 免二次 Win32 调用）。
5. 跑 Engine.Tests → 绿；`dotnet build -c Release` 宿主基线仍绿。
**Verify**：`dotnet test --project tests/DotNetDebugger.Engine.Tests/DotNetDebugger.Engine.Tests.csproj` 全绿（含既有调试用例，串行勿并行开多份）。
**Commit**：`feat(engine): Capture 模块骨架——窗口枚举 FindMainWindow、DPI 感知与数据模型，附 UiSampleApp 定位单测`

---

## T3 Engine 图像管线 ImagePipeline：k 缩放/裁剪/编码/纯黑 + System.Drawing.Common + 单测

**Why**：纯逻辑（无 UI 依赖），是 T4/T5 抓取结果的统一后处理（spec §4.2「缩放/编码/裁剪职责唯一归属 Engine」）。先于抓取层可独立测绿。

**Files**
- 修改 `src/DotNetDebugger.Engine/DotNetDebugger.Engine.csproj`：加 `<PackageReference Include="System.Drawing.Common" Version="10.0.0" />`（附注释：PNG/JPEG 编码、缩放、裁剪、纯黑采样——spec §4.1 新增依赖；restore 报版本不存在时改用 nuget.org 当前 10.0.x 稳定版并在注释记录）。
- 新建 `src/DotNetDebugger.Engine/Capture/ImagePipeline.cs`。
- 新建 `tests/DotNetDebugger.Engine.Tests/ImagePipelineTests.cs`。

**Steps（TDD）**

1. 先写测试（纯逻辑，无进程、快跑）：

   ```csharp
   public sealed class ImagePipelineTests
   {
       // 用 internal 门面：Pipeline 入参是 Bitmap，测试直接构造合成图（不经过抓取）
       private static Bitmap Make(int w, int h, Color fill)
       {
           var b = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
           using var g = Graphics.FromImage(b);
           using var br = new SolidBrush(fill);
           g.FillRectangle(br, 0, 0, w, h);
           return b;
       }

       [Fact] public void Scale_DownAboveMax_KeepsAspectRatio()
       {
           using var src = Make(4000, 2000, Color.SteelBlue);
           var r = ImagePipeline.Process(src, src.Size, maxDimension: 2000, "png", 80, "t", "WGC");
           Assert.Equal(2000, r.Width);
           Assert.Equal(1000, r.Height);          // 2:1 纵横比守恒
           Assert.Equal(4000, r.NativeWidth);      // 原生保留
           Assert.Equal("WGC", r.Source);
           Assert.False(r.WasAllBlack);
           Assert.True(r.Image.AsSpan(0, 4).SequenceEqual("\x89PNG"u8));   // PNG magic 字节
       }

       [Fact] public void Scale_BelowMax_NoScale_OutputEqualsNative()
       {
           using var src = Make(800, 600, Color.White);
           var r = ImagePipeline.Process(src, src.Size, 2000, "png", 80, "t", "WGC");
           Assert.Equal(800, r.Width);
           Assert.Equal(800, r.NativeWidth);
       }

       [Fact] public void Jpeg_Encodes_JpegMagic_AndQualityClampedByCaller()
       {
           using var src = Make(300, 200, Color.Coral);
           var r = ImagePipeline.Process(src, src.Size, 2000, "jpeg", 50, null, "BitBlt");
           Assert.Equal(0xFF, r.Image[0]);
           Assert.Equal(0xD8, r.Image[1]);         // JPEG SOI
       }

       [Fact] public void AllBlack_Detected_True_OnPureBlack_False_OnAnyPixel()
       {
           using (var black = Make(64, 64, Color.FromArgb(255, 0, 0, 0)))
           {
               var r = ImagePipeline.Process(black, black.Size, 2000, "png", 80, null, "BitBlt");
               Assert.True(r.WasAllBlack);
           }
           using (var gray = Make(64, 64, Color.FromArgb(255, 1, 1, 1)))   // 任一非 0 即非黑
           {
               var r = ImagePipeline.Process(gray, gray.Size, 2000, "png", 80, null, "BitBlt");
               Assert.False(r.WasAllBlack);
           }
       }

       [Fact] public void WindowTitle_PassedThrough_And_ClippedDefault_False()
       {
           using var src = Make(50, 50, Color.Red);
           var r = ImagePipeline.Process(src, src.Size, 2000, "png", 80, "UiSample", "PrintWindow");
           Assert.Equal("UiSample", r.WindowTitle);
           Assert.False(r.ClippedToScreen);
       }

       [Fact] public void UnknownFormat_Throws_NotSilentlyFallsBack()
       {
           using var src = Make(10, 10, Color.Red);
           Assert.ThrowsAny<Exception>(() => ImagePipeline.Process(src, src.Size, 2000, "gif", 80, null, "WGC"));
       }
   }
   ```

2. 跑红。
3. 实现 `ImagePipeline.cs`：

   ```csharp
   using System.Drawing.Drawing2D;
   using System.Drawing.Imaging;

   namespace DotNetDebugger.Engine.Capture;

   /// <summary>截图后处理唯一归属（spec §4.2）：k 等比缩放、格式编码、纯黑采样。
   /// k = min(1, maxDimension / max(kBase 宽, 高))；kBase 由调用方给定——window 模式=源图（窗口）
   /// 自身尺寸，screen/region 模式=虚拟屏原生尺寸（region 与 screen 用同一 k，保证两图空间恒一致，
   /// spec §3.1）。裁剪（含 region 换算）在 CaptureScreen 入口完成，本类只消费裁好的源图。</summary>
   internal static class ImagePipeline
   {
       public static CaptureResult Process(Bitmap source, Size kBase, int maxDimension,
           string format, int quality, string? windowTitle, string sourceName, bool clippedToScreen = false)
       {
           var k = Math.Min(1.0, (double)maxDimension / Math.Max(kBase.Width, kBase.Height));
           var outW = Math.Max(1, (int)Math.Round(source.Width * k));
           var outH = Math.Max(1, (int)Math.Round(source.Height * k));

           using Bitmap scaled = k < 1.0 ? Resize(source, outW, outH) : CopyOf(source);
           var allBlack = IsAllBlack(scaled);
           var bytes = Encode(scaled, format, quality);
           return new CaptureResult(bytes, scaled.Width, scaled.Height,
               source.Width, source.Height, windowTitle, sourceName, allBlack, clippedToScreen);
       }

       private static Bitmap CopyOf(Bitmap src)
       {
           var b = new Bitmap(src.Width, src.Height, PixelFormat.Format32bppArgb);
           using var g = Graphics.FromImage(b);
           g.DrawImage(src, 0, 0, src.Width, src.Height);
           return b;
       }

       private static Bitmap Resize(Bitmap src, int w, int h)
       {
           var b = new Bitmap(w, h, PixelFormat.Format32bppArgb);
           using var g = Graphics.FromImage(b);
           g.CompositingMode = CompositingMode.SourceCopy;
           g.InterpolationMode = InterpolationMode.HighQualityBicubic;
           g.SmoothingMode = SmoothingMode.HighQuality;
           g.PixelOffsetMode = PixelOffsetMode.HighQuality;
           g.DrawImage(src, new Rectangle(0, 0, w, h));
           return b;
       }

       /// <summary>纯黑采样=全像素 RGB 均为 0（spec：抓到纯黑不报错，只在头部加「备注」）。</summary>
       public static bool IsAllBlack(Bitmap bmp)
       {
           var rect = new Rectangle(0, 0, bmp.Width, bmp.Height);
           var bd = bmp.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
           try
           {
               unsafe
               {
                   var stride = Math.Abs(bd.Stride);
                   for (var y = 0; y < bd.Height; y++)
                   {
                       var row = (byte*)bd.Scan0 + y * bd.Stride;
                       for (var x = 0; x < bd.Width; x++)
                       {
                           var p = row + x * 4;   // BGRA
                           if (p[0] != 0 || p[1] != 0 || p[2] != 0) return false;
                       }
                   }
               }
               return true;
           }
           finally { bmp.UnlockBits(bd); }
       }

       private static byte[] Encode(Bitmap bmp, string format, int quality)
       {
           using var ms = new MemoryStream();
           switch (format.ToLowerInvariant())
           {
               case "png":
                   bmp.Save(ms, ImageFormat.Png);
                   break;
               case "jpeg":
               case "jpg":
                   var codec = ImageCodecInfo.GetImageEncoders()
                       .First(c => c.FormatID == ImageFormat.Jpeg.Guid);
                   using (var ep = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality,
                       (long)Math.Clamp(quality, 0, 100)))
                   using (var eps = new EncoderParameters(1) { Param = { [0] = ep } })
                       bmp.Save(ms, codec, eps);
                   break;
               default:
                   throw new ArgumentException($"不支持的输出格式: {format}");
           }
           return ms.ToArray();
       }
   }
   ```

   工程开关：Engine csproj 加 `<AllowUnsafeBlocks>true</AllowUnsafeBlocks>`（`IsAllBlack` 的指针扫描；比逐 `GetPixel` 快一个量级）。
4. 跑绿。
**Verify**：Engine.Tests 全绿 + 宿主 Release build 基线。
**Commit**：`feat(engine): 图像管线（k 等比缩放/编码/纯黑采样），Engine 引入 System.Drawing.Common`

---

## T4 Engine GDI 抓取：PrintWindow/BitBlt + CaptureScreen/region 换算 + 单测

**Why**：GDI 是 screen/region 的唯一路径与 window 的回退 2/3 道（spec §4.3）；region 的 k 换算/屏外判定入口在 `CaptureScreen`（spec §5.2「屏外判定由 Engine 执行并回传约定错误」）。

**Files**
- 新建 `src/DotNetDebugger.Engine/Capture/GdiCapture.cs`。
- 修改 `src/DotNetDebugger.Engine/Capture/ScreenCapture.cs`：追加 `CaptureScreen`（本任务）；`CaptureWindow` 骨架留 T5。
- 新建 `tests/DotNetDebugger.Engine.Tests/CaptureScreenTests.cs`；新建/扩展 `tests/DotNetDebugger.Engine.Tests/CaptureWindowGdiTests.cs`。

**Steps（TDD）**

1. 先写测试：

   ```csharp
   public sealed class CaptureScreenTests
   {
       // 期望虚拟屏尺寸由测试自己用 GetSystemMetrics 取（76/77/78/79），不断言具体分辨率
       [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

       [Fact] public void Screen_Full_NoClip_MatchesVirtualScreenScaled()
       {
           var vw = GetSystemMetrics(78); var vh = GetSystemMetrics(79);   // CX/CY VIRTUALSCREEN
           var r = ScreenCapture.CaptureScreen(null, 2000, "png", 80);
           var k = Math.Min(1.0, 2000.0 / Math.Max(vw, vh));
           Assert.Equal((int)Math.Round(vw * k), r.Width);
           Assert.Equal((int)Math.Round(vh * k), r.Height);
           Assert.Equal(vw, r.NativeWidth);
           Assert.Equal("BitBlt", r.Source);
           Assert.False(r.ClippedToScreen);
       }

       [Fact] public void Region_FullyInside_ReturnsClipSize_NotClipped()
       {
           // maxDimension 大→k=1，图像空间=原生空间，断言直白
           var r = ScreenCapture.CaptureScreen(new Rectangle(10, 10, 80, 60), 4000, "png", 80);
           Assert.Equal(80, r.Width);
           Assert.Equal(60, r.Height);
           Assert.False(r.ClippedToScreen);
       }

       [Fact] public void Region_PartiallyOutside_ClipsIntersection_MarksClipped()
       {
           // 同参数下 k 恒等，先取 screen 图像空间宽度作越界构造基准
           var imgW = ScreenCapture.CaptureScreen(null, 2000, "png", 80).Width;
           var r = ScreenCapture.CaptureScreen(new Rectangle(imgW - 40, 0, 200, 100), 2000, "png", 80);
           Assert.True(r.ClippedToScreen);
           Assert.True(r.Width > 0);
           Assert.True(r.Width <= imgW);          // 交集不越出图像空间
       }

       [Fact] public void Region_FullyOutside_Throws_CaptureException_WithSpecMessage()
       {
           var ex = Assert.Throws<CaptureException>(() =>
               ScreenCapture.CaptureScreen(new Rectangle(99999, 99999, 10, 10), 2000, "png", 80));
           Assert.Contains("完全在屏幕范围", ex.Message);
           Assert.Contains("之外", ex.Message);
       }
   }

   public sealed class CaptureWindowGdiTests
   {
       [Fact] public void CaptureWindow_UiSample_NonBlack_SizeMatchesWindowRect()
       {
           using var app = UiSampleAppProcess.Start();
           var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
           Assert.NotNull(w);
           var r = ScreenCapture.CaptureWindow(w!.Hwnd, 2000, "png", 80);
           Assert.False(r.WasAllBlack);
           Assert.Contains(r.Source, new[] { "PrintWindow", "BitBlt" });   // T4 尚无 WGC；T5 接入后本断言仍允许（不强制 WGC）
           Assert.Equal(r.NativeWidth, r.Width);   // UiSampleApp ~762px 宽 < 2000，不触发缩放
           Assert.True(Math.Abs(r.Width - w.Rect.Width) <= 2 && Math.Abs(r.Height - w.Rect.Height) <= 2,
               $"尺寸 {r.Width}x{r.Height} vs 窗口 {w.Rect.Width}x{w.Rect.Height}（±2 容差：DWM 阴影/边框取整）");
           Assert.Equal("UiSample", r.WindowTitle);
       }
   }
   ```

   说明：T4 阶段 `CaptureWindow` 尚无 WGC，`Source` 按回退结果为 `PrintWindow`（非黑）或 `BitBlt`（PrintWindow 画黑时），故用 `Assert.Contains` 二选一；T5 接入 WGC 后此断言仍成立（不断言 WGC——环境差异，spec §6.2 只要求非纯黑+尺寸一致）。
2. 跑红。
3. 实现 `GdiCapture.cs`：

   ```csharp
   using System.Drawing;

   namespace DotNetDebugger.Engine.Capture;

   /// <summary>GDI 抓取（spec §4.3 回退链 2/3 道 + screen/region 唯一路径）：
   /// PrintWindow 让窗口自画（不怕遮挡，现代 DComposition 窗口可能画黑）；BitBlt 从屏幕剪矩形
   /// （被遮挡处截到遮挡物，Source=BitBlt 即 best-effort 信号）。</summary>
   internal static class GdiCapture
   {
       private const uint PwRenderFullContent = 0x00000002;
       private const uint SrccopyWithCaptureBlt = 0x40CC0020;

       [DllImport("user32.dll")] private static extern bool PrintWindow(IntPtr hwnd, IntPtr hdc, uint flags);
       [DllImport("user32.dll")] private static extern IntPtr GetDC(IntPtr hwnd);
       [DllImport("user32.dll")] private static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
       [DllImport("user32.dll")] private static extern bool BitBlt(IntPtr dst, int x, int y, int w, int h,
           IntPtr src, int srcX, int srcY, uint rop);
       [DllImport("user32.dll")] private static extern bool GetWindowRect(IntPtr hwnd, out Rect rect);
       [DllImport("user32.dll")] private static extern int GetSystemMetrics(int index);

       [StructLayout(LayoutKind.Sequential)]
       private struct Rect { public int Left, Top, Right, Bottom; }

       /// <summary>第 2 道：PrintWindow(PW_RENDERFULLCONTENT)。失败返回 null 由调用方回退。</summary>
       public static Bitmap? TryPrintWindow(IntPtr hwnd)
       {
           if (!GetWindowRect(hwnd, out var r)) return null;
           var w = r.Right - r.Left; var h = r.Bottom - r.Top;
           if (w <= 0 || h <= 0) return null;
           try
           {
               var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
               using var g = Graphics.FromImage(bmp);
               var hdc = g.GetHdc();
               var ok = PrintWindow(hwnd, hdc, PwRenderFullContent);
               g.ReleaseHdc(hdc);
               if (!ok) { bmp.Dispose(); return null; }
               return bmp;
           }
           catch (Exception) { return null; }
       }

       /// <summary>第 3 道：BitBlt 屏幕上该窗口矩形（遮挡敏感；最小化窗口由调用方跳过）。</summary>
       public static Bitmap? TryBitBltWindow(IntPtr hwnd)
       {
           if (!GetWindowRect(hwnd, out var r)) return null;
           var w = r.Right - r.Left; var h = r.Bottom - r.Top;
           if (w <= 0 || h <= 0) return null;
           var screen = GetDC(IntPtr.Zero);
           if (screen == IntPtr.Zero) return null;
           try
           {
               var bmp = new Bitmap(w, h, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
               using var g = Graphics.FromImage(bmp);
               var dst = g.GetHdc();
               var ok = BitBlt(dst, 0, 0, w, h, screen, r.Left, r.Top, SrccopyWithCaptureBlt);
               g.ReleaseHdc(dst);
               if (!ok) { bmp.Dispose(); return null; }
               return bmp;
           }
           catch (Exception) { return null; }
           finally { ReleaseDC(IntPtr.Zero, screen); }
       }

       /// <summary>screen/region：BitBlt 虚拟屏指定原生区域。GetDC 失败抛约定错误（spec §5.2）。</summary>
       public static Bitmap CaptureScreenBits(Rectangle nativeClip)
       {
           var screen = GetDC(IntPtr.Zero);
           if (screen == IntPtr.Zero)
               throw new CaptureException("窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。");
           try
           {
               var bmp = new Bitmap(nativeClip.Width, nativeClip.Height,
                   System.Drawing.Imaging.PixelFormat.Format32bppArgb);
               using var g = Graphics.FromImage(bmp);
               var dst = g.GetHdc();
               var ok = BitBlt(dst, 0, 0, nativeClip.Width, nativeClip.Height,
                   screen, nativeClip.X, nativeClip.Y, SrccopyWithCaptureBlt);
               g.ReleaseHdc(dst);
               if (!ok) { bmp.Dispose(); throw new CaptureException("窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。"); }
               return bmp;
           }
           finally { ReleaseDC(IntPtr.Zero, screen); }
       }

       internal static Rectangle VirtualScreenRect()
       {
           var x = GetSystemMetrics(76); var y = GetSystemMetrics(77);
           var w = GetSystemMetrics(78); var h = GetSystemMetrics(79);
           return new Rectangle(x, y, w, h);
       }
   }
   ```

4. `ScreenCapture.cs` 追加 `CaptureScreen` 与（T4 版）`CaptureWindow`：

   ```csharp
   /// <summary>screen/region：GDI BitBlt 虚拟屏（spec §4.3：screen/region 不开 WGC）。
   /// clipInImageSpace=「mode=screen 返回图像素空间」坐标（宿主仅解析格式，换算在此）：
   /// 图像空间求交（空=抛 spec §5.2 屏外约定错误，W/H 用图像空间尺寸——agent 可自查口径）；
   /// 非空交 → round 换算原生空间裁剪（BitBlt 只抓交集，性能）→ 交≠原输入即 ClippedToScreen。</summary>
   public static CaptureResult CaptureScreen(Rectangle? clipInImageSpace, int maxDimension, string format, int quality)
   {
       EnsureDpi();
       var native = GdiCapture.VirtualScreenRect();
       var k = Math.Min(1.0, (double)maxDimension / Math.Max(native.Width, native.Height));
       var imgW = Math.Max(1, (int)Math.Round(native.Width * k));
       var imgH = Math.Max(1, (int)Math.Round(native.Height * k));

       Rectangle nativeClip;
       var clipped = false;
       if (clipInImageSpace is { } c)
       {
           var inter = Rectangle.Intersect(c, new Rectangle(0, 0, imgW, imgH));
           if (inter.IsEmpty)
               throw new CaptureException($"region ({c.X},{c.Y},{c.Width},{c.Height}) 完全在屏幕范围 ({imgW}x{imgH}) 之外。");
           clipped = inter != c;
           nativeClip = new Rectangle(
               native.X + (int)Math.Round(inter.X / k),
               native.Y + (int)Math.Round(inter.Y / k),
               Math.Max(1, (int)Math.Round(inter.Width / k)),
               Math.Max(1, (int)Math.Round(inter.Height / k)));
           nativeClip.Intersect(native);   // round 兜底夹紧（最多溢出 1px）
           if (nativeClip.Width <= 0 || nativeClip.Height <= 0)
               throw new CaptureException($"region ({c.X},{c.Y},{c.Width},{c.Height}) 完全在屏幕范围 ({imgW}x{imgH}) 之外。");
       }
       else nativeClip = native;

       using var bmp = GdiCapture.CaptureScreenBits(nativeClip);
       return ImagePipeline.Process(bmp, native.Size, maxDimension, format, quality,
           windowTitle: null, sourceName: "BitBlt", clippedToScreen: clipped);
   }

   /// <summary>window 抓取编排（T4 版=GDI 两道；T5 在此链首插入 WGC）。</summary>
   public static CaptureResult CaptureWindow(IntPtr hwnd, int maxDimension, string format, int quality)
   {
       EnsureDpi();
       var info = GetWindowInfo(hwnd) ?? throw new CaptureException(
           "窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。");

       // 第 2 道：PrintWindow → 黑图回退
       Bitmap? bmp = GdiCapture.TryPrintWindow(hwnd);
       var source = "PrintWindow";
       if (bmp is not null && ImagePipeline.IsAllBlack(bmp)) { bmp.Dispose(); bmp = null; }

       // 第 3 道：BitBlt（最小化窗口屏幕无内容，spec §4.2 跳过）
       if (bmp is null && !info.IsIconic)
       {
           bmp = GdiCapture.TryBitBltWindow(hwnd);
           source = "BitBlt";
       }

       if (bmp is null)
           throw new CaptureException("窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。");

       using (bmp)
           return ImagePipeline.Process(bmp, bmp.Size, maxDimension, format, quality,
               info.Title, source);
   }

   /// <summary>取窗口基础信息（定位/头部/抓取共用）；hwnd 无效返回 null。</summary>
   internal static WindowHandleInfo? GetWindowInfo(IntPtr hwnd)
   {
       if (hwnd == IntPtr.Zero || !GetWindowRect(hwnd, out var r)) return null;
       var sb = new System.Text.StringBuilder(256);
       GetWindowText(hwnd, sb, sb.Capacity);
       GetWindowThreadProcessId(hwnd, out var pid);
       return new WindowHandleInfo(hwnd, sb.ToString(),
           new Rectangle(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top),
           IsIconic(hwnd), (int)pid, MatchCount: 1);
   }
   ```

   （`ScreenCapture.cs` 顶部补 `using System.Drawing;`。）
5. 跑绿。
**Verify**：Engine.Tests 全绿；宿主 Release build 基线。
**Commit**：`feat(engine): GDI 抓取（PrintWindow/BitBlt 回退与虚拟屏 screen/region 换算裁剪）及单测`

---

## T5 Engine WGC 抓取 + CaptureWindow 首位回退 + 单测

**Why**：WGC 是正确性主力（DWM 取帧：不黑图、被遮挡可截、**无需置顶**）（spec §4.3-1）；代码源于本会话 spike 实测跑通版本，坑已固化在注释。

**Files**
- 新建 `src/DotNetDebugger.Engine/Capture/WgcCapture.cs`。
- 修改 `src/DotNetDebugger.Engine/Capture/ScreenCapture.cs`：`CaptureWindow` 链首插入 WGC。
- 新建 `tests/DotNetDebugger.Engine.Tests/CaptureWindowWgcTests.cs`。

**Steps（TDD）**

1. 先写测试（探测失败即 Skip，spec §6.2/§6.4）：

   ```csharp
   public sealed class CaptureWindowWgcTests
   {
       [Fact]
       public void Wgc_UiSample_Supported_CapturesNonBlack()
       {
           if (!Windows.Graphics.Capture.GraphicsCaptureSession.IsSupported())
               Assert.Skip("本环境不支持 Windows Graphics Capture（CI 无 GPU/无头预案，spec §6.4）");

           using var app = UiSampleAppProcess.Start();
           var w = CaptureTestHelpers.WaitFound(app.Process.Id, TimeSpan.FromSeconds(5));
           Assert.NotNull(w);
           var r = ScreenCapture.CaptureWindow(w!.Hwnd, 2000, "png", 80);
           Assert.False(r.WasAllBlack);
           Assert.Equal("WGC", r.Source);              // 支持环境 WGC 必走通（回退发生=链路 bug）
           Assert.True(Math.Abs(r.Width - w.Rect.Width) <= 2, "±2 容差：DWM 阴影/边框取整");
           Assert.Equal("UiSample", r.WindowTitle);
       }
   }
   ```

2. 跑红。
3. 实现 `WgcCapture.cs`（**spike 实测版**重组；三处实测坑见注释）：

   ```csharp
   using System.Drawing;
   using System.Runtime.InteropServices;
   using Windows.Graphics.Capture;
   using Windows.Graphics.DirectX;
   using Windows.Graphics.DirectX.Direct3D11;
   using Windows.Graphics.Imaging;
   using Windows.Storage.Streams;

   namespace DotNetDebugger.Engine.Capture;

   /// <summary>Windows Graphics Capture 单帧抓窗（spec §4.3 第 1 道，2026-09-22 spike 实测跑通）：
   /// IGraphicsCaptureItemInterop::CreateForWindow（Win32 unpackaged 专用路径）→ CreateFreeThreaded
   /// framepool（免 DispatcherQueue 消息泵死锁）→ 限时等首帧 → SoftwareBitmap → PNG → Bitmap。
   /// 任何失败/超时返回 null 由调用方回退 GDI（不抛）。全程同步封装：线程池 MTA 线程上
   /// WinRT async 用 GetAwaiter().GetResult() 无 SynchronizationContext 死锁风险（spec §4.4）。</summary>
   internal static class WgcCapture
   {
       private const uint EInvalidArg = 0x80070057;
       private static readonly TimeSpan FirstFrameTimeout = TimeSpan.FromMilliseconds(500); // spec §4.3「约 500ms 上限」

       public static Bitmap? TryCaptureWindow(IntPtr hwnd)
       {
           if (!GraphicsCaptureSession.IsSupported()) return null;   // 老系统/无头 → 回退
           try { return CaptureCore(hwnd); }
           catch (Exception) { return null; }                        // 回退链消化一切 WGC 异常
       }

       private static Bitmap? CaptureCore(IntPtr hwnd)
       {
           // 1) item（E_INVALIDARG = owned/无主窗口 → 临时加 WS_EX_APPWINDOW 提升重试，spec §4.3）
           var item = WgcInterop.CreateItemForWindow(hwnd);
           var restoreStyle = IntPtr.Zero;
           if (item is null)
           {
               restoreStyle = WgcInterop.AddAppWindowStyle(hwnd);
               if (restoreStyle == IntPtr.Zero) return null;
               item = WgcInterop.CreateItemForWindow(hwnd);
               if (item is null) { WgcInterop.RestoreStyle(hwnd, restoreStyle); return null; }
           }

           Direct3D11CaptureFramePool? framePool = null;
           GraphicsCaptureSession? session = null;
           try
           {
               // 2) D3D11(BGRA) → IDXGIDevice → WinRT IDirect3DDevice
               var graphicsDevice = WgcInterop.CreateWinrtDevice();
               if (graphicsDevice is null) return null;

               // 3) framepool（CreateFreeThreaded）+ session：关黄框/光标（ApiInformation 探测兼容老系统）
               framePool = Direct3D11CaptureFramePool.CreateFreeThreaded(
                   graphicsDevice, DirectXPixelFormat.B8G8R8A8UIntNormalized, 1, item.Size);
               session = framePool.CreateCaptureSession(item);
               if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                       "Windows.Graphics.Capture.GraphicsCaptureSession", "IsBorderRequired"))
                   session.IsBorderRequired = false;      // Win11 22H2+；Win10 关不掉（黄框闪烁，spec §9 接受）
               if (Windows.Foundation.Metadata.ApiInformation.IsPropertyPresent(
                       "Windows.Graphics.Capture.GraphicsCaptureSession", "IsCursorCaptureEnabled"))
                   session.IsCursorCaptureEnabled = false; // Win10 1903+

               // 4) 等首帧（TCS + FrameArrived 单次消费）→ nudge 催 DWM 出帧
               var tcs = new TaskCompletionSource<Direct3D11CaptureFrame>(
                   TaskCreationOptions.RunContinuationsAsynchronously);
               Windows.Foundation.TypedEventHandler<Direct3D11CaptureFramePool, object> handler = null!;
               handler = (sender, _) =>
               {
                   var f = sender.TryGetNextFrame();
                   if (f is null) return;
                   if (!tcs.TrySetResult(f)) f.Dispose();
                   sender.FrameArrived -= handler;
               };
               framePool.FrameArrived += handler;
               session.StartCapture();
               WgcInterop.Nudge(hwnd);

               if (!tcs.Task.Wait(FirstFrameTimeout)) return null;

               // 5) 等帧 → 编码 → Bitmap（async 抽私有方法、同步等结果：线程池 MTA 无 SynchronizationContext，无死锁）
               using var frame = tcs.Task.Result;
               return Encode(frame).GetAwaiter().GetResult();
           }
           catch (Exception) { return null; }
           finally
           {
               session?.Dispose();
               framePool?.Dispose();
               if (restoreStyle != IntPtr.Zero) WgcInterop.RestoreStyle(hwnd, restoreStyle);
           }
       }

       /// <summary>帧 → SoftwareBitmap → PNG → System.Drawing.Bitmap。
       /// 坑③：new Bitmap(stream) 生命周期绑定流——先入内存流解码再深拷贝到独立位图，
       /// 否则流释放后 GDI+ 对象失效（spike 原版直接写文件未暴露，落 Engine 必须拷贝）。</summary>
       private static async Task<Bitmap?> Encode(Direct3D11CaptureFrame frame)
       {
           using var software = await SoftwareBitmap.CreateCopyFromSurfaceAsync(frame.Surface)
               .AsTask().WaitAsync(TimeSpan.FromSeconds(2));
           using var ras = new InMemoryRandomAccessStream();
           var encoder = await BitmapEncoder.CreateAsync(BitmapEncoder.PngEncoderId, ras);
           encoder.SetSoftwareBitmap(software);
           await encoder.FlushAsync();
           var bytes = new byte[ras.Size];
           using (var dr = new DataReader(ras))
           { await dr.LoadAsync((uint)ras.Size); dr.ReadBytes(bytes); }
           using var ms = new MemoryStream(bytes);
           using var tmp = new Bitmap(ms);
           return new Bitmap(tmp);   // 深拷贝，脱离 stream 生命周期
       }
   }
   ```

   `WgcInterop`（spike 实测段，`internal static class` 同文件或独立文件均可，内容照落）：

   ```csharp
   internal static class WgcInterop
   {
       private static readonly Guid ItemIid = new("79c3f95b-31f7-4ec2-a464-632ef5d30760");   // GraphicsCaptureItem
       private static readonly Guid FactoryIid = new("00000035-0000-0000-c000-000000000046"); // IActivationFactory
       private static readonly Guid InteropIid = new("3628e81b-3cac-4c60-b7f4-23ce0e0c3356");// IGraphicsCaptureItemInterop
       private static readonly Guid IdxgiDeviceIid = new("54ec77fa-1377-44e6-8c32-88fd5f44c84c"); // IDXGIDevice（dxgi.idl 实测核对，
                                                                                                  //  前 8 位对、后 96 位错会稳定 E_NOINTERFACE——勿凭记忆改）
       private const uint EInvalidArg = 0x80070057;

       // —— P/Invoke ——
       [DllImport("combase.dll", ExactSpelling = true)]
       private static extern int WindowsCreateString([MarshalAs(UnmanagedType.LPWStr)] string source, uint length, out IntPtr hstring);
       [DllImport("combase.dll", ExactSpelling = true)]
       private static extern int WindowsDeleteString(IntPtr hstring);
       [DllImport("combase.dll", ExactSpelling = true)]
       private static extern int RoGetActivationFactory(IntPtr classId, ref Guid iid, out IntPtr factory);
       [DllImport("d3d11.dll", ExactSpelling = true)]
       private static extern int D3D11CreateDevice(IntPtr adapter, int driverType, IntPtr software, uint flags,
           IntPtr featureLevels, uint featureLevelCount, uint sdkVersion,
           out IntPtr device, out IntPtr immediateContext, IntPtr chosenFeatureLevel);
       [DllImport("d3d11.dll", ExactSpelling = true)]
       private static extern int CreateDirect3D11DeviceFromDXGIDevice(IntPtr dxgiDevice, out IntPtr graphicsDevice);
       [DllImport("user32.dll")] private static extern bool RedrawWindow(IntPtr hWnd, IntPtr lprcUpdate, IntPtr hrgnUpdate, uint flags);
       [DllImport("dwmapi.dll")] private static extern int DwmFlush();
       [DllImport("user32.dll")] private static extern IntPtr GetWindowLongPtr(IntPtr hwnd, int index);
       [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")]
       private static extern IntPtr SetWindowLongPtr(IntPtr hwnd, int index, IntPtr value);

       private const uint RdwInvalidate = 0x0001, RdwAllChildren = 0x0080, RdwUpdateNow = 0x0100;
       private const int GwlExStyle = -20;
       private const long WsExAppWindow = 0x00040000;

       [UnmanagedFunctionPointer(CallingConvention.StdCall)]
       private delegate int QiDelegate(IntPtr self, ref Guid iid, out IntPtr ppv);
       [UnmanagedFunctionPointer(CallingConvention.StdCall)]
       private delegate int CreateForWindowDelegate(IntPtr thisPtr, IntPtr hwnd, ref Guid iid, out IntPtr item);

       /// <summary>手写 COM QI（vtable slot 0）。坑①：Marshal.QueryInterface 只有 object 重载，
       /// 传 IntPtr 会被装箱成 boxed 对象 → 稳定 E_NOINTERFACE，必须走原生 vtable。</summary>
       private static int QI(IntPtr comPtr, ref Guid iid, out IntPtr result)
       {
           var vtable = Marshal.ReadIntPtr(comPtr);
           var fn = Marshal.ReadIntPtr(vtable, 0);
           return Marshal.GetDelegateForFunctionPointer<QiDelegate>(fn)(comPtr, ref iid, out result);
       }

       /// <summary>RoGetActivationFactory → QI IGraphicsCaptureItemInterop → CreateForWindow（vtable slot 3）。
       /// 坑②：类名长度必须动态取（手写常量数错一位即 CLASS_E_CLASSNOTAVAILABLE）。</summary>
       public static GraphicsCaptureItem? CreateItemForWindow(IntPtr hwnd)
       {
           var className = "Windows.Graphics.Capture.GraphicsCaptureItem";
           var hr = WindowsCreateString(className, (uint)className.Length, out var hs);
           if (hr < 0) return null;
           IntPtr factory = IntPtr.Zero, interop = IntPtr.Zero, itemPtr = IntPtr.Zero;
           try
           {
               var fiid = FactoryIid;
               hr = RoGetActivationFactory(hs, ref fiid, out factory);
               if (hr < 0) return null;
               var iiid = InteropIid;
               hr = QI(factory, ref iiid, out interop);
               if (hr < 0) return null;

               var vtable = Marshal.ReadIntPtr(interop);
               var fn = Marshal.ReadIntPtr(vtable, 3 * IntPtr.Size);   // IUnknown(0-2) 之后第一方法
               var create = Marshal.GetDelegateForFunctionPointer<CreateForWindowDelegate>(fn);
               var iid = ItemIid;
               hr = create(interop, hwnd, ref iid, out itemPtr);
               if (hr < 0) return null;   // E_INVALIDARG 由调用方按 owned 窗口策略提升重试
               return WinRT.MarshalInspectable<object>.FromAbi(itemPtr) as GraphicsCaptureItem;
           }
           finally
           {
               if (itemPtr != IntPtr.Zero) Marshal.Release(itemPtr);
               if (interop != IntPtr.Zero) Marshal.Release(interop);
               if (factory != IntPtr.Zero) Marshal.Release(factory);
               WindowsDeleteString(hs);
           }
       }

       /// <summary>D3D11(BGRA) → IDXGIDevice(QI) → CreateDirect3D11DeviceFromDXGIDevice → WinRT IDirect3DDevice。</summary>
       public static IDirect3DDevice? CreateWinrtDevice()
       {
           var hr = D3D11CreateDevice(IntPtr.Zero, 1 /*Hardware*/, IntPtr.Zero, 0x20 /*BgraSupport*/,
               IntPtr.Zero, 0, 7 /*D3D11_SDK_VERSION*/, out var dev, out var ctx, IntPtr.Zero);
           if (hr < 0) return null;
           try
           {
               var iid = IdxgiDeviceIid;
               hr = QI(dev, ref iid, out var dxgi);
               if (hr < 0) return null;
               try
               {
                   hr = CreateDirect3D11DeviceFromDXGIDevice(dxgi, out var unk);
                   if (hr < 0) return null;
                   try { return WinRT.MarshalInspectable<object>.FromAbi(unk) as IDirect3DDevice; }
                   finally { Marshal.Release(unk); }
               }
               finally { Marshal.Release(dxgi); }
           }
           finally { Marshal.Release(ctx); Marshal.Release(dev); }
       }

       public static void Nudge(IntPtr hwnd)
       {
           RedrawWindow(hwnd, IntPtr.Zero, IntPtr.Zero, RdwInvalidate | RdwAllChildren | RdwUpdateNow);
           try { DwmFlush(); } catch (Exception) { /* 无 DWM 忽略 */ }
       }

       /// <summary>owned/无主窗口提升：加 WS_EX_APPWINDOW 返回原 style（失败返回 Zero）。</summary>
       public static IntPtr AddAppWindowStyle(IntPtr hwnd)
       {
           var old = GetWindowLongPtr(hwnd, GwlExStyle);
           if (old == IntPtr.Zero) return IntPtr.Zero;
           SetWindowLongPtr(hwnd, GwlExStyle, new IntPtr(unchecked((long)((ulong)old | WsExAppWindow))));
           Nudge(hwnd);
           return old;
       }

       public static void RestoreStyle(IntPtr hwnd, IntPtr oldStyle)
           => SetWindowLongPtr(hwnd, GwlExStyle, oldStyle);
   }
   ```

4. 用下面**完整方法整体替换** T4 版 `CaptureWindow`（三道回退链，spec §4.3）：

   ```csharp
   public static CaptureResult CaptureWindow(IntPtr hwnd, int maxDimension, string format, int quality)
   {
       EnsureDpi();
       var info = GetWindowInfo(hwnd) ?? throw new CaptureException(
           "窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。");

       // 第 1 道：WGC（正确性优先：DWM 取帧不黑图、被遮挡可截、无需置顶；出图即用，黑=真黑由头部备注）
       Bitmap? bmp = WgcCapture.TryCaptureWindow(hwnd);
       var source = "WGC";

       // 第 2 道：PrintWindow → 采样纯黑则继续回退（spec §4.3-2）
       if (bmp is null)
       {
           bmp = GdiCapture.TryPrintWindow(hwnd);
           source = "PrintWindow";
           if (bmp is not null && ImagePipeline.IsAllBlack(bmp)) { bmp.Dispose(); bmp = null; }
       }

       // 第 3 道：BitBlt（最小化窗口屏幕无内容，不回退——spec §4.2 IsIconic 规则）
       if (bmp is null && !info.IsIconic)
       {
           bmp = GdiCapture.TryBitBltWindow(hwnd);
           source = "BitBlt";
       }

       if (bmp is null)
           throw new CaptureException("窗口抓取失败（WGC/PrintWindow/BitBlt 均未成功）——可能处于无桌面会话（服务/无头环境）。");

       using (bmp)
           return ImagePipeline.Process(bmp, bmp.Size, maxDimension, format, quality, info.Title, source);
   }
   ```
5. 跑绿（本机 `IsSupported=True`，spike 已实证 → WGC 断言应全过）。
**Verify**：Engine.Tests 全绿（WGC 用例本机必绿不 Skip）+ 宿主 Release build。
**Commit**：`feat(engine): WGC 截图（interop/CreateFreeThreaded/限时首帧）接入回退链首，window 三道防线完整`

---

## T6 宿主 screenshot 工具：AppConfig 常量 + DebugScreenshotTool + 单测 + README/握手同步

**Why**：agent 可见面（spec §3 全部契约）；铁律「改工具面 README 同 commit」在此一次做齐。

**Files**
- 修改 `src/DotNetDebuggerMcp/Configuration/AppConfig.cs`：加截图三常量。
- 新建 `src/DotNetDebuggerMcp/Tools/Debugger/DebugScreenshotTool.cs`（spec §3 指定路径）。
- 修改 `src/DotNetDebuggerMcp/Configuration/AppText.cs`：`HandshakeFeatureIntro` 何时使用段加 screenshot bullet。
- 根 `README.md`：工具清单加 screenshot 条目 + Windows-only + 落盘目录说明。
- 新建 `tests/DotNetDebuggerMcp.Tests/ScreenshotToolTests.cs`（`[Collection("AppServices")]`——类内有静态 seam，并行纪律见 `tests/AGENTS.md`）。

**Steps**

1. **先跑 SDK 最小样例 gate（spec §3.3 授权的验证点，必须先于业务实现）**：在 `DebugScreenshotTool.cs` 写最小骨架——`[McpServerToolType]` + 一个 `Screenshot` 方法返回 `new CallToolResult { Content = [new TextContentBlock { Text = "ok" }] }` + 一个测试经 `DebugMcpToolsTests.ConnectAsync()` 调 `screenshot` 断言收到文本。
   - **通过** → 继续第 2 步。
   - **不通过**（返回类型不被 MCP 2.2.0 识别等）→ **停下回报用户**，勿自行改路线（spec §3.3 预案：改纯落盘需重新报批）。
   API 事实（本机 2.2.0 DLL 反射核实，勿凭记忆改名）：

   ```csharp
   using ModelContextProtocol.Protocol;   // CallToolResult / TextContentBlock / ImageContentBlock
   // CallToolResult.Content : IList<ContentBlock>（集合表达式 [..] 可初始化）
   // TextContentBlock.Text : string（settable）
   // ImageContentBlock.Data : ReadOnlyMemory<byte> ← 直接放原始字节，SDK 上线时转 base64；MimeType : string
   // CallToolResult.IsError : bool?（本工具失败路径不设——与现有 Task<string> 工具行为一致）
   ```

2. `AppConfig.cs` 追加：

   ```csharp
   /// <summary>screenshot 输出图单边最大像素：超过则等比缩到限内（region/screen 同一 k，spec §3.1）。
   /// 经参数传入 Engine，缩放实现唯一在 Engine。</summary>
   public const int ScreenshotMaxDimension = 2000;

   /// <summary>screenshot 内联返回阈值（base64 后字节数，chrome-devtools 先例 2MB）：达到即改落盘返回路径。</summary>
   public const long InlineImageBase64Bytes = 2 * 1024 * 1024;

   /// <summary>screenshot 超限落盘目录（%LOCALAPPDATA%\DotNetDebuggerMcp\screenshots，与 update-check.json 同根）。</summary>
   public static readonly string ScreenshotsDir = Path.Combine(
       Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
       NuGetPackageId, "screenshots");
   ```

3. 实现 `DebugScreenshotTool.cs`（spec §3.1 参数表 + §3.2 Description + §3.3 返回契约 + §5 全部语义）：

   ```csharp
   using DotNetDebugger.Engine.Capture;
   using DotNetDebuggerMcp.Configuration;
   using DotNetDebuggerMcp.Services;
   using ModelContextProtocol.Protocol;
   using ModelContextProtocol.Server;

   using System.ComponentModel;
   using System.Drawing;
   using System.Text;

   namespace DotNetDebuggerMcp.Tools.Debugger;

   /// <summary>screenshot 独立截图工具（spec 2026-09-22-screenshot-tool-design）：
   /// 不要求调试会话、不入缓存不过 ToolPipeline、不写 Actions/AgentView（D9/Web 冻结）。</summary>
   [McpServerToolType]
   public static class DebugScreenshotTool
   {
       /// <summary>2MB 阈值测试 seam（InternalsVisibleTo DotNetDebuggerMcp.Tests；测试置位后 finally 还原）。</summary>
       internal static long InlineImageLimitBytes = AppConfig.InlineImageBase64Bytes;

       [McpServerTool]
       [Description("截取窗口/屏幕画面返回图片，供多态模型观察 UI 状态做自动化冒烟。独立工具，不要求调试会话。" +
           "mode=window（默认）按 processId 或 windowTitle 定位目标主窗口（窗口未出现会等 timeoutSeconds 秒，默认 5）；" +
           "mode=screen 截全屏；mode=region 按 region=\"x,y,w,h\"（mode=region 时必填）截局部，坐标以 mode=screen 返回的图像素为准" +
           "（原点左上）——建议先 screen 看全景再裁局部。format 默认 png，jpeg+quality 可压体积；图片过大自动改为落盘返回绝对路径。" +
           "坐标/状态判断仍以 debug_state/debug_stack 为准，本工具只提供视觉观察。")]
       public static async Task<CallToolResult> Screenshot(
           [Description("截图模式：window（默认，截目标主窗口）/ screen（全屏）/ region（局部）。")] string mode = "window",
           [Description("window 定位：目标进程 pid（0=未提供）；非 0 时优先于 windowTitle。")] int processId = 0,
           [Description("window 定位：窗口标题子串（忽略大小写）；processId=0 时生效。")] string windowTitle = "",
           [Description("mode=region 时必填，\"x,y,w,h\"（mode=screen 返回图像素空间，原点左上）。")] string region = "",
           [Description("输出格式：png（默认）/ jpeg。")] string format = "png",
           [Description("jpeg 质量 0-100（默认 80，越界自动收紧）；png 忽略。")] int quality = 80,
           [Description("仅 window：等窗口出现秒数（默认 5，0-30；0=立即试一次）。")] int timeoutSeconds = 5,
           [Description("非空=强制落盘到该路径；空=仅图片超 2MB 时落盘到本地 screenshots 目录。")] string filePath = "",
           CancellationToken cancellationToken = default)
       {
           try
           {
               // —— 参数校验（spec §5.2 文案逐字）——
               mode = (mode ?? "").Trim().ToLowerInvariant();
               if (mode is not ("window" or "screen" or "region"))
                   return TextOnly($"mode 仅支持 window/screen/region（当前 \"{mode}\"）。");
               format = (format ?? "").Trim().ToLowerInvariant();
               if (format is not ("png" or "jpeg" or "jpg"))
                   return TextOnly($"format 仅支持 png/jpeg（当前 \"{format}\"）。");
               if (format == "jpg") format = "jpeg";
               quality = Math.Clamp(quality, 0, 100);
               timeoutSeconds = Math.Clamp(timeoutSeconds, 0, 30);
               cancellationToken.ThrowIfCancellationRequested();

               Rectangle? clip = null;
               if (mode == "region")
               {
                   var parts = (region ?? "").Split(',');
                   if (parts.Length != 4 || !parts.Select(p => int.TryParse(p.Trim(), out _)).All(x => x))
                       return TextOnly("region 格式应为 \"x,y,w,h\"（mode=screen 返回图像素空间，原点左上）。");
                   var nums = parts.Select(p => int.Parse(p.Trim())).ToArray();
                   if (nums[2] <= 0 || nums[3] <= 0)
                       return TextOnly("region 格式应为 \"x,y,w,h\"（mode=screen 返回图像素空间，原点左上）。");
                   clip = new Rectangle(nums[0], nums[1], nums[2], nums[3]);
               }

               // —— 抓取（轮询在宿主，换算/缩放/编码唯一在 Engine，spec §4.4）——
               CaptureResult result;
               string targetLine;
               var ext = format == "jpeg" ? "jpg" : "png";
               if (mode == "window")
               {
                   var located = await LocateWindowAsync(processId, windowTitle, timeoutSeconds, cancellationToken);
                   if (located.Error is not null) return TextOnly(located.Error);   // spec §5.2 双空/超时文案
                   var info = located.Info!;
                   targetLine = $"目标:   窗口 \"{info.Title}\" (pid={info.Pid})";
                   if (info.MatchCount > 1) targetLine += $"（命中 {info.MatchCount} 个可见窗口，已截主窗口）";
                   result = ScreenCapture.CaptureWindow(info.Hwnd,
                       AppConfig.ScreenshotMaxDimension, format, quality);
               }
               else
               {
                   result = ScreenCapture.CaptureScreen(clip,
                       AppConfig.ScreenshotMaxDimension, format, quality);
                   targetLine = mode == "screen"
                       ? "目标:   屏幕"
                       : $"目标:   屏幕区域 ({clip!.Value.X},{clip.Value.Y},{clip.Value.Width},{clip.Value.Height})";
               }

               // —— 头部（spec §3.3；展示算术宿主做，坐标换算不碰）——
               var header = new StringBuilder();
               header.AppendLine(targetLine);
               var scaled = result.Width != result.NativeWidth || result.Height != result.NativeHeight;
               var pct = (int)Math.Round(100.0 * result.Width / Math.Max(1, result.NativeWidth));
               if (mode == "region")
               {
                   if (scaled || result.ClippedToScreen)
                   {
                       header.Append($"尺寸:   区域 {result.Width}x{result.Height}（{pct}%）");
                       if (result.ClippedToScreen) header.Append("，已裁至屏幕交集");
                       header.AppendLine();
                   }
               }
               else if (scaled)
               {
                   header.AppendLine($"尺寸:   原生 {result.NativeWidth}x{result.NativeHeight} → {result.Width}x{result.Height} ({pct}%)");
               }
               if (mode == "window") header.AppendLine($"来源:   {result.Source}");
               if (result.WasAllBlack) header.AppendLine("备注:   画面为纯黑（目标可能未渲染）");

               // —— 双轨：内联 image 块 vs 落盘（spec §5.1；落盘失败仍附块）——
               var b64Len = ((long)result.Image.Length + 2) / 3 * 4;
               var wantFile = !string.IsNullOrWhiteSpace(filePath) || b64Len >= InlineImageLimitBytes;
               var attachImage = true;
               if (wantFile)
               {
                   try
                   {
                       var full = ResolveScreenshotPath(filePath, mode, ext, processId);
                       File.WriteAllBytes(full, result.Image);
                       header.AppendLine($"已落盘: {full}");
                       attachImage = false;
                   }
                   catch (Exception ex)
                   {
                       header.AppendLine($"已尝试落盘失败: {ex.Message}");   // 仍附块（两害相权保 agent 能看到画面）
                   }
               }
               header.AppendLine("---");

               var content = new List<ContentBlock> { new TextContentBlock { Text = header.ToString() } };
               if (attachImage)
                   content.Add(new ImageContentBlock { Data = result.Image, MimeType = $"image/{format}" });
               return new CallToolResult { Content = content };
           }
           catch (OperationCanceledException)
           {
               return TextOnly("screenshot 已取消（可重试）。");
           }
           catch (CaptureException ex)
           {
               return TextOnly(ex.Message);   // Engine 生成的 spec §5.2 约定文案（屏外/全失败）
           }
           catch (Exception ex)
           {
               return TextOnly($"截图失败：{ex.Message}");   // 防御兜底，不抛（铁律）
           }
       }

       /// <summary>window 定位：pid 优先（忽略标题）→ 标题 → 活动会话目标 pid 兜底 → 中文提示（spec §3.1）。
       /// 轮询 50ms 间隔至 timeoutSeconds（0=立即一次）；超时返回 spec §5.2 文案。</summary>
       private static async Task<(WindowHandleInfo? Info, string? Error)> LocateWindowAsync(
           int processId, string windowTitle, int timeoutSeconds, CancellationToken ct)
       {
           var byPid = processId > 0;
           var byTitle = !byPid && !string.IsNullOrWhiteSpace(windowTitle);
           if (!byPid && !byTitle)
           {
               var sessionPid = DebugSessionService.Manager.Active?.ProcessId ?? 0;
               if (sessionPid > 0) { byPid = true; processId = sessionPid; }
           }
           if (!byPid && !byTitle)
               return (null, "请提供 processId 或 windowTitle 定位窗口（两者皆空时也可先 debug_launch 建立会话自动取目标 pid）。");

           var deadline = DateTime.UtcNow.AddSeconds(timeoutSeconds);
           WindowHandleInfo? hit;
           do
           {
               ct.ThrowIfCancellationRequested();
               hit = byPid
                   ? ScreenCapture.FindMainWindow(processId, "")
                   : ScreenCapture.FindMainWindow(0, windowTitle);
               if (hit is not null) return (hit, null);
               if (DateTime.UtcNow >= deadline) break;
               await Task.Delay(50, ct);
           } while (true);

           var selector = byPid ? $"processId={processId}" : $"标题含 \"{windowTitle}\"";
           return (null, $"{timeoutSeconds} 秒内未找到匹配的可见窗口（{selector}）。");
       }

       private static string ResolveScreenshotPath(string filePath, string mode, string ext, int processId)
       {
           if (!string.IsNullOrWhiteSpace(filePath))
           {
               var dir = Path.GetDirectoryName(Path.GetFullPath(filePath));
               if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
               return Path.GetFullPath(filePath);
           }
           Directory.CreateDirectory(AppConfig.ScreenshotsDir);
           var pidSeg = mode == "window" ? $"-{processId}" : "";
           var name = $"screenshot-{DateTime.Now:yyyyMMdd-HHmmssfff}-{mode}{pidSeg}.{ext}";
           return Path.Combine(AppConfig.ScreenshotsDir, name);
       }

       private static CallToolResult TextOnly(string text)
           => new() { Content = [new TextContentBlock { Text = text }] };
   }
   ```

   落盘语义核对（spec §5.1）：默认文件名 `screenshot-{ts}-{mode}[-{pid}].{ext}`（pid 仅 window ✅）；`filePath` 非空先 `CreateDirectory` 父目录 ✅；`已落盘:`/`已尝试落盘失败:` 行在 `---` 之前 ✅；`jpg` 归一 `jpeg`（MimeType `image/jpeg` ✅）。
4. `AppText.cs` `HandshakeFeatureIntro` 何时使用段追加一行（现有 bullet 结构不动）：

   ```csharp
   "- **当需要观察 GUI 窗口/屏幕画面（看控件状态、布局冒烟）时**，调用 screenshot 截图（window/screen/region 三模式，返回图片；图片过大会改为落盘返回路径）。\n" +
   ```
   （插在 web_open bullet 之后；「具体工具清单见 MCP 工具目录」句已有 `screenshot` 外的前缀描述——把该句中「名称带 `decompile`/`debug`/`web` 等语义前缀」改为「名称带 `decompile`/`debug`/`web` 等语义前缀」保持原样即可，`screenshot` 不带前缀但目录可发现，无需改此句。）
5. README 同 commit（铁律）：读根 `README.md` 现有工具清单格式，加 `screenshot` 一行（参数表/三模式/双轨说明），并注明：**仅支持 Windows**（TFM 升级所致，spec §4.1 附注）+ 落盘默认目录 `%LOCALAPPDATA%\DotNetDebuggerMcp\screenshots\`（spec §5.1「README 注明位置」）。
6. 测试 `ScreenshotToolTests.cs`（经 MCP 协议，参照 `DebugUiToolsTests` 的 `ConnectAsync`/`CallAsync`；若现有 helper 不透出原生 `Content`，用 `McpClient.CallToolAsync` 取 `CallToolResult` 直读——以 `DebugMcpToolsTests` 现状为准）：

   ```csharp
   [Collection("AppServices")]   // 静态 seam 并行纪律
   public sealed class ScreenshotToolTests
   {
       // 用例清单（每条对 spec §5.2/§3.3 一条）：
       // 1 InvalidMode_ReturnsSpecMessage：mode="box" → "mode 仅支持 window/screen/region（当前 \"box\"）。"
       // 2 InvalidFormat_ReturnsSpecMessage：format="gif" → 同款 format 提示
       // 3 Region_Malformed_ReturnsSpecMessage：region="1,2,3" / "a,b,c,d" / "1,2,-3,4" → region 格式文案
       // 4 Window_NoSelector_NoSession_ReturnsPrompt：processId=0,windowTitle=""（新连接默认无活动会话）
       //    → "请提供 processId 或 windowTitle 定位窗口…"
       // 5 Window_PidHit_ReturnsImageBlock：起 UiSampleApp → processId=pid, timeoutSeconds=5
       //    → content[0] 含 $"目标:   窗口 \"UiSample\" (pid={pid})"、含 "来源:   "；
       //      content[1] 为 ImageContentBlock 且 Data 前 8 字节 == 89 50 4E 47 0D 0A 1A 0A（PNG magic）
       // 6 Window_PidMiss_TimesOut_ReturnsSpecMessage：processId=4185100, timeoutSeconds=0
       //    → "0 秒内未找到匹配的可见窗口（processId=4185100）。"
       // 7 Screen_ReturnsImageBlock：mode="screen" → content[0] 含 "目标:   屏幕"、content[1] PNG magic
       // 8 Region_FullyOutside_ReturnsSpecMessage：mode="region", region="99999,99999,10,10"
       //    → 含 "完全在屏幕范围" 且含 "之外"（Engine 文案经 CaptureException 透传）
       // 9 ForcedFilePath_WritesFile_NoImageBlock：filePath=%TEMP%\...\shot.png → 文件存在、
       //    返回含 "已落盘: "、content 恰 1 块（无 image）
       // 10 Seam_LowThreshold_FallsToDefaultDir：置 DebugScreenshotTool.InlineImageLimitBytes=1，
       //    screen 截图 → 返回含 "已落盘: " 且文件落在 AppConfig.ScreenshotsDir；finally 还原 seam 并删测试文件
       // 11 GifMode_JpegRoundTrip（可选）：format="jpeg" → JPEG magic FF D8 + MimeType image/jpeg
       //
       // 断言辅助：
       //   static byte[] PngMagic = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];
       //   UiSampleApp 启停 helper：复制 T2 的 UiSampleAppProcess 形状（宿主测试项目独立副本，
       //   不跨程序集引用测试代码；UseShellExecute=true 同款纪律）
       //   超时/定位轮询：用例 5 起进程后无需预热——工具自身 timeoutSeconds=5 已轮询
   }
   ```

   用例 5 断言注意：`来源:` 允许 WGC/PrintWindow/BitBlt 任一（不过度断言，spec §6.2 同口径）；不比精确像素（spec §9 DPI/主题稳定性）。
7. 跑绿。
**Verify**：
```bash
dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj
dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -- --filter-class "DotNetDebuggerMcp.Tests.ScreenshotToolTests"
```
**Commit**：`feat: screenshot MCP 工具（三模式/轮询定位/头部/双轨落盘/image 块）与 README、握手文案同步`

---

## T7 收尾：AGENTS 纪律修订 + CHANGELOG + spec 状态 + 手工验收与全量回归

**Why**：spec §7 影响面清单的文档半场 + §6.5 手工验收 + 最终回归（T1-T6 已各自 commit，此处只做剩余项）。

**Files**
- `src/DotNetDebugger.Engine/AGENTS.md`：① 边界纪律第一条改为——`net10.0-windows10.0.22621.0`，**只引 NuGet**：`ClrDebug 0.4.2` + `Microsoft.Diagnostics.DbgShim.win-x64 10.0.731102` + `System.Drawing.Common 10.0.0`（截图编码/缩放）。无 ProjectReference、无 MCP/DI/日志/Decompiler 依赖。② 「结构」代码块加一行：`Capture/   ScreenCapture(门面) / WgcCapture / GdiCapture / ImagePipeline / CaptureModels`。③ 首句能力描述补「并提供窗口/屏幕截图捕捉（WGC+GDI，供宿主 screenshot 工具）」。
- `src/DotNetDebuggerMcp/AGENTS.md`：① 目录结构 `Tools/` 行：元数据 13 后加 `+ screenshot 1（独立截图，不走 ToolPipeline/缓存）`；`Tools/Debugger/` 段补一行：`DebugScreenshotTool：screenshot（mode=window/screen/region；独立于调试会话，返回 CallToolResult 含 image 块；不走 Manager、不写 Actions）`；② 铁律速查「工具返回 Task<string>」句加例外：「——例外：`screenshot` 返回 `Task<CallToolResult>`（图片块），错误仍为纯文本 content（spec 2026-09-22-screenshot-tool-design §3.3）」。
- 根 `AGENTS.md`：① 「输出约定」或「关键约束」中「工具方法返回 `Task<string>`」句加同款例外条款；② 「依赖方向」句 `Engine`（只依赖 ClrDebug + DbgShim.win-x64）→ `Engine`（只依赖 ClrDebug + DbgShim.win-x64 + System.Drawing.Common）。
- `CHANGELOG.md` `[Unreleased]`：按现有段格式加使用者可见条目（中文）：
  - `### Added` 下：`- 新增 MCP 工具 screenshot：window/screen/region 三模式截图（WGC 优先、GDI 回退），<2MB 直接返回图片、超限或指定 filePath 落盘返回路径；独立于调试会话（仅 Windows）。`
  - `- 平台基线升级为 Windows（net10.0-windows10.0.22621.0）——NuGet 包自本版本起仅支持 Windows。`
- `docs/planning/specs/README.md` + `docs/planning/README.md`：本 spec 状态「已冻结」→「已实现（计划 `plans/2026-09-22-screenshot-tool.md`，2026-09-22）」。
- 检查（不改）：`IsErrorResult`/`CacheSignatures` 确认未被触碰（D9）、`DotNetDebuggerMcpCmd` 无新 CLI 参数（spec §3.3 边界）、`Web/TODO.md` 冻结记录仍在。

**Steps**

1. 落上述文档修改（自查：`git grep -n "只依赖 ClrDebug" -- 'AGENTS.md' 'src/*/AGENTS.md'` 应只命中新表述）。
2. CI 分片不变式核对（`tests/AGENTS.md` 纪律——新测试类 `ScreenshotToolTests` 默认应落 `core` 片）：读 `.github/workflows/build.yml` 提取 6 片过滤串，逐片：
   `dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj -c Release --no-build --list-tests -- <分片串>`
   各片「已发现 N 个」相加 = 不带过滤的全量数（互斥+覆盖完整）。若 `ScreenshotToolTests` 落空任何片 → 按纪律补进 `core` 片过滤串。
3. 手工验收（spec §6.5，需用户配合）：
   - `dotnet build -c Debug src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj` → 请用户重新启用 opencode 的本仓库 MCP（当前为防编译锁已关闭）并重启会话。
   - 对真实 GUI（记事本）三模式各截一张人眼验收：`screenshot window windowTitle=记事本`、`screenshot screen`、`screenshot region`（先 screen 取全景坐标再裁）；核对：来源标注（预期 WGC）、Win10 黄框闪烁表现（spec §9）、超限时落盘路径与文件、客户端 image 块正常显示、被遮挡窗口截图不带遮挡物（可将记事本置于其他窗口之下再截）。
4. 最终全量回归（一条龙）：

   ```bash
   powershell -ExecutionPolicy Bypass -File tests/TestData/generate-testdata.ps1
   dotnet build -c Release src/DotNetDebuggerMcp/DotNetDebuggerMcp.csproj
   # 5 个测试项目逐个 dotnet test（Engine/Session 串行套件已在项目内保证）
   dotnet test --project tests/DotNetDebugger.Decompiler.Tests/DotNetDebugger.Decompiler.Tests.csproj
   dotnet test --project tests/DotNetDebugger.Engine.Tests/DotNetDebugger.Engine.Tests.csproj
   dotnet test --project tests/DotNetDebugger.Session.Tests/DotNetDebugger.Session.Tests.csproj
   dotnet test --project tests/DotNetDebugger.Web.Tests/DotNetDebugger.Web.Tests.csproj
   dotnet test --project tests/DotNetDebuggerMcp.Tests/DotNetDebuggerMcp.Tests.csproj
   # pack 三关复跑（同 T1 第 5 步命令，hack 最终确认）
   ```
5. 清理：spike 临时目录 `%TEMP%\opencode\spike-packtool\` 可整目录删除（throwaway，本计划已消化其全部产出）。
**Verify**：第 1-4 步全绿；`git status` 干净（全部已 commit）。
**Commit**：`docs: screenshot 收尾——AGENTS 纪律与依赖句修订、CHANGELOG、spec 状态置已实现`

---

## E. 执行期 Gate（遇到即停，勿自行改路线）

| Gate | 触发 | 处置 |
|---|---|---|
| T1 pack 三关任一失败 | hack 失效（SDK 升级等） | 停下回报用户（spec §9：升 SDK 必须重验） |
| T1 FlaUI 直引失败 | 依赖解析错误 | 按两分支回退现状只改注释（非阻断，继续） |
| T6 步骤 1 最小样例失败 | MCP 2.2.0 不支持 `Task<CallToolResult>` | 停下回报用户（spec §3.3 预案：改纯落盘需重新报批） |
| WGC 测试本机 `IsSupported=False` | 环境异常（spike 实证应为 True） | Skip 不红，回报用户确认环境 |
| 任何任务出现 spec 未覆盖的新语义决策 | — | 停下回报用户，勿在计划外自扩 |

## F. 明确不做（spec §8，执行期出现即偏航）

Web 展示/回放、截图缓存与自动清理、多显示器 `display` 参数、`includeCursor`、`delay`、控件级截图、region `origin` 坐标系、e2e 闭环、`generate-testdata.ps1` 改动、CLI 新参数、版本号三处同步（发布时另做）、`IsErrorResult`/`CacheSignatures` 扩展。
