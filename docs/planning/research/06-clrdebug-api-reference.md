# ClrDebug 0.4.2 最小调试器 API 参考（Windows + .NET 10）

> 目标：用 `ClrDebug` 0.4.2（lordmilko，MIT）实现进程内 .NET 调试引擎（启动/附加、token+IL offset 断点、continue、单步、读线程/调用栈/局部变量、first-chance 异常）。
> 全部签名逐行核对自 NuGet 包 0.4.2 对应的源码 commit `9628778`（nupkg 的 nuspec `<repository commit>` 字段）与官方仓库 Samples/NetCore。**凡标 "核对" 即逐字摘自上述源码，可直接照抄。**
>
> 关键结论先行：
> - 0.4.2 目标框架 **netstandard2.0 + net8.0**，零依赖。net10.0 项目可直接引用。
> - 走 **dbgshim** 引导（自动/手动两模式），`DbgShim` 类已在 ClrDebug 中封装，**不需要**自己 P/Invoke dbgshim（仅需 `NativeLibrary.Load` 拿 hModule）。
> - 回调模型是 **事件委托**，不是强制继承：`CorDebugManagedCallback` 实现全部 `ICorDebugManagedCallback*` 接口并暴露 `OnXxx` 事件 + `OnAnyEvent`。
> - **ICorDebug 必须创建/使用于 MTA 线程**（构造器直接抛 `InvalidOperationException` 拦 STA）。回调由调试器内部线程串行触发（"All callbacks are serialized, called in the same thread, and called with the process in the synchronized state"）。

出处基线：
- NuGet 页：https://www.nuget.org/packages/ClrDebug （0.4.2，net8.0 + netstandard2.0，无依赖）
- 仓库：https://github.com/lordmilko/ClrDebug （0.4.2 tag = commit `9628778`）
- NetCore 示例：https://github.com/lordmilko/ClrDebug/tree/master/Samples/NetCore
- README（Getting Started / ICorDebug / NetCore 指引）：https://github.com/lordmilko/ClrDebug#readme

---

## 1. 包结构、入口类与进程启动/附加

### 1.1 程序集与命名空间

| 项 | 值 |
|---|---|
| NuGet 包 | `ClrDebug` 0.4.2（MIT） |
| TFMs | `netstandard2.0`、`net8.0`（net10.0 兼容 net8.0 资产） |
| 依赖 | 无 |
| 命名空间 | 全部在 **`ClrDebug`** 一个命名空间内（wrapper、原生接口、扩展、event args） |
| 程序集 | `ClrDebug.dll` |

主要静态工具在 `static partial class Extensions`（位于 `ClrDebug` 命名空间），提供：
- `CLRCreateInstance()`（读 `ICLRMetaHost`→runtime→`ICorDebug` 的捷径，**仅 .NET Framework/桌面 CLR**）
- Nifty globals：`CLSID_CLRMetaHost`、`CLRCreateInstance` 等
- 便捷启动/创建扩展方法
- `CorDebugValue.As<T>()`、`GetObjectForIUnknown`、`DiaStringsUseComHeap` 等

建议 `using static ClrDebug.Extensions;`（官方样例也如此）。

### 1.2 无参构造 `CorDebug()` —— 桌面 CLR 专用捷径（核对）

```csharp
// ClrDebug/Managed/Cordb/CorDebug.cs
public CorDebug()   // 内部：CLRCreateInstance() → CLRMetaHost.GetRuntime() → GetInterface<ICorDebug>(CLSID_CLRDebuggingLegacy)，随后自动 Initialize()
{
    Initialize();
}
```
- **仅限 Windows**；**在 STA 线程调用直接抛 `InvalidOperationException`**（源码注释：ICorDebug 对象若在 STA 上创建，回调线程跨 apartment 调 RCW 会崩）。
- **该构造面向 .NET Framework CLR 场景**。.NET 5+/.NET Core（你要调试的目标）**不能**用它 —— 需走 dbgshim（见 §2）。

### 1.3 ICorDebug 本体：`ClrDebug.CorDebug`（核对）

类：`public partial class CorDebug : ComObject<ICorDebug>`（`ComObject<T>` 基类提供 `Raw` 属性访问原生 COM 接口）。

成员（全部核对自 CorDebug.cs）：

```csharp
public void Initialize();                       // HRESULT Initialize()
public void Terminate();
public void SetManagedHandler(ICorDebugManagedCallback pCallback);   // 必须 Initialize 后、Create/DebugActive 前调用
public void SetUnmanagedHandler(ICorDebugUnmanagedCallback pCallback);

// ICorDebug.CreateProcess —— 完整 COM 形态
public CorDebugProcess CreateProcess(
    string lpApplicationName, string lpCommandLine,
    SECURITY_ATTRIBUTES lpProcessAttributes, SECURITY_ATTRIBUTES lpThreadAttributes,
    bool bInheritHandles, CreateProcessFlags dwCreationFlags,
    IntPtr lpEnvironment, string lpCurrentDirectory,
    STARTUPINFOW lpStartupInfo, ref PROCESS_INFORMATION lpProcessInformation,
    CorDebugCreateProcessFlags debuggingFlags);

// ICorDebug.DebugActiveProcess —— 附加到已存在进程（.NET Core 场景常用）
public CorDebugProcess DebugActiveProcess(int id, bool win32Attach);

// 其他
public CorDebugProcess GetProcess(int dwProcessId);
public CorDebugProcessEnum EnumerateProcesses();
public void CanLaunchOrAttach(int dwProcessId, int win32DebuggingEnabled);
```

> ClrDebug 约定：**每个 COM 方法都有两个版本** —— `TryXxx(...)` 返回 `HRESULT`，`Xxx(...)` 内部 `ThrowOnNotOK()` 后返回结果；包装型返回值在 HRESULT 失败时为 `null`/default。`HRESULT` 有 `.ThrowOnNotOK()`/`.ThrowOnFailed()` 扩展与自定义 `COMException`。

### 1.4 便捷 `CreateProcess` 扩展（核对，README 主推用法）

```csharp
// ClrDebug/Extensions/Extensions.CorDebug.cs
public static CorDebugProcess CreateProcess(
    this CorDebug corDebug,
    string lpCommandLine,                                 // 必填，应用名须是首个参数
    SECURITY_ATTRIBUTES? lpProcessAttributes = null,
    SECURITY_ATTRIBUTES? lpThreadAttributes = null,
    bool bInheritHandles = true,
    CreateProcessFlags dwCreationFlags = 0,
    IntPtr? lpEnvironment = null,
    string lpCurrentDirectory = null,
    STARTUPINFOW? lpStartupInfo = null,
    CorDebugCreateProcessFlags debuggingFlags = CorDebugCreateProcessFlags.DEBUG_NO_SPECIAL_OPTIONS,
    Action<IntPtr> closeHandle = null);                   // 非 Windows 平台必传；Windows 默认用 CloseHandle
```
内部自动关闭 `PROCESS_INFORMATION.hProcess/hThread`。调用前**必须**已 `Initialize()` + `SetManagedHandler()`，否则抛 `InvalidOperationException`（提示文案即为此场景）。

README 启动/附加示例（桌面 CLR）：
```csharp
var corDebug = new CorDebug();                                  // 已含 Initialize
var callback = new CorDebugManagedCallback();
callback.OnAnyEvent += (s, e) => { Console.WriteLine(e.Kind); e.Controller.Continue(false); };
corDebug.SetManagedHandler(callback);
var process = corDebug.CreateProcess("powershell.exe", dwCreationFlags: CreateProcessFlags.CREATE_NEW_CONSOLE);
while (true) Thread.Sleep(1);                                   // 事件泵：回调驱动
```

---

## 2. dbgshim 加载与 .NET Core/.NET 5+ 引导

### 2.1 结论

- **ClrDebug 内置 `DbgShim` 类型**，封装全部 dbgshim 导出（`GetProcAddress`+委托逐函数绑定内部完成），**不需要**手工 P/Invoke `CreateDebuggingInterfaceFromVersion*`。
- 你要做的是：**先拿到 dbgshim 模块句柄**（3 选 1）：
  1. `NativeLibrary.Load("dbgshim.dll")`（.NET Core 进程内自动解析——但目标进程是独立 .NET 进程时，宿主未必带 dbgshim）；
  2. 更稳：NuGet 包 **`Microsoft.Diagnostics.DbgShim`**（或 `Microsoft.Diagnostics.DbgShim.win-x64`）随应用分发，`NativeLibrary.Load(路径)`；
  3. 官方样例从 `C:\Program Files\dotnet` 下现找（`DbgShimResolver`，见 §7）。
- 官方 NetCore 样例构造 `new DbgShim(NativeLibrary.Load(dbgShimPath))`（net8 分支）或 `new DbgShim(NativeMethods.LoadLibrary(dbgShimPath))`（旧）。
- 版本注意：样例注释强调 "the version of dbgshim you use doesn't really matter"，dbgshim API 向后兼容。
- 对 .NET 5+：**推荐 `CreateDebuggingInterfaceFromVersionEx`（手动）或 `RegisterForRuntimeStartup`（自动）**。版本参数传 `CorDebugInterfaceVersion.CorDebugVersion_4_0`。`CreateDebuggingInterfaceFromVersion`（不带 Ex）会把调试器固化为 2.0 版，勿用。`CreateDebuggingInterfaceFromVersion2` 仅在 macOS 沙箱才需要传 appGroupId；`...3` 仅当要自定义 DBI/DAC 库提供者。

### 2.2 `DbgShim` 关键成员（全部核对）

```csharp
// ClrDebug/Extensions/Extensions.DbgShim.cs
public class DbgShim
{
    public DbgShim(IntPtr hModule);   // hModule = NativeLibrary.Load(dbgsShimDllPath)，null 抛 ArgumentNullException

    // ---- 启动挂起进程（跨平台，CreateProcess 子集）----
    public CreateProcessForLaunchResult CreateProcessForLaunch(
        string lpCommandLine,
        bool bSuspendProcess,
        IntPtr lpEnvironment = default,
        string lpCurrentDirectory = null);
    public void ResumeProcess(IntPtr hResumeHandle);
    public void CloseResumeHandle(IntPtr hResumeHandle);

    // ---- 手动模式（推荐给引擎）----
    public IntPtr GetStartupNotificationEvent(int debuggeePID);   // 返回事件句柄
    public EnumerateCLRsResult EnumerateCLRs(int debuggeePID);    // 找已加载 CLR 模块
    public string CreateVersionStringFromModule(int pidDebuggee, string szModuleName);
    public CorDebug CreateDebuggingInterfaceFromVersionEx(CorDebugInterfaceVersion iDebuggerVersion, string szDebuggeeVersion);
    public void CloseCLREnumeration(EnumerateCLRsResult result);

    // ---- 自动模式 ----
    public IntPtr RegisterForRuntimeStartup(int dwProcessId, RuntimeStartupCallback pfnCallback, IntPtr parameter = default);
    public void UnregisterForRuntimeStartup(IntPtr pUnregisterToken);
}

// 结果结构
public struct CreateProcessForLaunchResult { public int ProcessId; public IntPtr ResumeHandle; }   // 核对：public int ProcessId / IntPtr ResumeHandle
public struct EnumerateCLRsResult
{
    public EnumerateCLRsResultItem[] Items;   // 每项 { IntPtr Handle; string Path; } —— Handle 是“continue-startup 事件”
}
public struct EnumerateCLRsResultItem { public IntPtr Handle { get; } public string Path { get; } }

// 自动模式友好回调（样例用其扩展版本；已核对定义在 Extensions.DbgShim.cs:946）
public delegate void RuntimeStartupCallback(CorDebug pCordb, IntPtr parameter, HRESULT hr);
public static IntPtr RegisterForRuntimeStartup(this DbgShim dbgShim, int dwProcessId, RuntimeStartupCallback pfnCallback, IntPtr parameter = default);
```

> `CreateProcessForLaunchResult`：从 `Extensions.DbgShim.cs` `TryCreateProcessForLaunch` 的 out 参数 `pProcessId, pResumeHandle` 得到，结果结构公开 `ProcessId`/`ResumeHandle`。
> `EnumerateCLRsResultItem.Path` 即 CLR 模块全路径（如 `...\coreclr.dll`）。

### 2.3 `CorDebugInterfaceVersion` 枚举

`CorDebugVersion_2_0` / `CorDebugVersion_4_0`（样例用 `CorDebugVersion_4_0`）。若期望值未精确匹配调试目标版本，dbgshim 内部 `CheckCompatibility` 按主版本容错（样例注释说明 4 与 4.5 均按 4 处理）。

---

## 3. 事件回调模型

### 3.1 `CorDebugManagedCallback`（核对自 `Managed/Cordb/Callbacks/CorDebugManagedCallback.cs`）

```csharp
public partial class CorDebugManagedCallback
    : ICorDebugManagedCallback, ICorDebugManagedCallback2, ICorDebugManagedCallback3, ICorDebugManagedCallback4
{
    public event EventHandler<CorDebugManagedCallbackEventArgs> OnAnyEvent;   // 每个事件后都会触发

    // 每个原生回调对应一个事件（EventArgs 名 = <Event>CorDebugManagedCallbackEventArgs）
    public event EventHandler<CreateProcessCorDebugManagedCallbackEventArgs> OnCreateProcess;
    public event EventHandler<ExitProcessCorDebugManagedCallbackEventArgs> OnExitProcess;
    public event EventHandler<CreateThreadCorDebugManagedCallbackEventArgs> OnCreateThread;
    public event EventHandler<ExitThreadCorDebugManagedCallbackEventArgs> OnExitThread;
    public event EventHandler<LoadModuleCorDebugManagedCallbackEventArgs> OnLoadModule;
    public event EventHandler<UnloadModuleCorDebugManagedCallbackEventArgs> OnUnloadModule;
    public event EventHandler<LoadClassCorDebugManagedCallbackEventArgs> OnLoadClass;
    public event EventHandler<UnloadClassCorDebugManagedCallbackEventArgs> OnUnloadClass;
    public event EventHandler<BreakpointCorDebugManagedCallbackEventArgs> OnBreakpoint;
    public event EventHandler<StepCompleteCorDebugManagedCallbackEventArgs> OnStepComplete;
    public event EventHandler<BreakCorDebugManagedCallbackEventArgs> OnBreak;
    public event EventHandler<ExceptionCorDebugManagedCallbackEventArgs> OnException;   // first-chance
    public event EventHandler<DebuggerErrorCorDebugManagedCallbackEventArgs> OnDebuggerError;
    public event EventHandler<CreateAppDomainCorDebugManagedCallbackEventArgs> OnCreateAppDomain;
    public event EventHandler<LogMessageCorDebugManagedCallbackEventArgs> OnLogMessage;
    public event EventHandler<ControlCTraceCorDebugManagedCallbackEventArgs> OnControlCTrace;  // (Callback2)
    // …Callback2/3/4 事件：OnNameChange/OnUpdateModuleSymbols/OnEditAndContinueRemap/OnBreakpointSetError/OnFunctionRemapComplete、OnMDA、OnCustomNotification、OnBeforeGarbageCollection 等
}
```

**使用方式（两选一）**：
1. 直接用实例 + 订阅事件（简单）：
   ```csharp
   var cb = new CorDebugManagedCallback();
   cb.OnAnyEvent += (s, e) => e.Controller.Continue(false);
   cb.OnLoadModule += (s, e) => Console.WriteLine(e.Module.Name);
   corDebug.SetManagedHandler(cb);
   ```
2. 派生 + 覆写 `HandleEvent`（需要事件前后插入逻辑时）：
   ```csharp
   protected virtual HRESULT HandleEvent<T>(EventHandler<T> handler, CorDebugManagedCallbackEventArgs args)
       where T : CorDebugManagedCallbackEventArgs
   // 默认实现：handler?.Invoke(this,(T)args); 然后 OnAnyEvent?.Invoke(this,args); 返回 S_OK
   ```
   README 说明派生类应调 `base.HandleEvent` 或自行全量分发，可用 `RaiseOnAnyEvent(args)` 触发共享处理。

### 3.2 EventArgs 结构（核对）

```csharp
public abstract class CorDebugManagedCallbackEventArgs : EventArgs
{
    public abstract CorDebugManagedCallbackKind Kind { get; }   // 事件类型
    public CorDebugController Controller { get; }               // 惰性包装 ICorDebugController（Process/AppDomain 级）
    public bool Continue { get; set; } = true;                  // 便捷标记（不会自动调用，仍需自己调 Controller.Continue）
}
```

事件具体属性（全部核对自 `Managed/EventArgs/CorDebugManagedCallback/`）：

| 事件 | EventArgs 关键属性 |
|---|---|
| OnCreateProcess | `CorDebugProcess Process` |
| OnExitProcess | 同（基类含 Controller/Process 逻辑） |
| OnCreateThread / OnExitThread | `CorDebugThread Thread`、`CorDebugAppDomain AppDomain`（基类 `AppDomainThreadDebugCallbackEventArgs`） |
| OnLoadModule / OnUnloadModule | `CorDebugAppDomain AppDomain`、`CorDebugModule Module` |
| OnLoadClass / OnUnloadClass | `CorDebugClass Class`（含 AppDomain/Thread） |
| OnBreakpoint | `CorDebugAppDomain AppDomain`、`CorDebugThread Thread`、`CorDebugBreakpoint Breakpoint` |
| OnStepComplete | `CorDebugAppDomain AppDomain`、`CorDebugThread Thread`、`CorDebugStepper Stepper`、`CorDebugStepReason Reason` |
| OnException | `CorDebugAppDomain AppDomain`、`CorDebugThread Thread`、`int Unhandled`（0=first-chance，非 0=未处理） |
| OnDebuggerError | `HRESULT ErrorHR`、`int ErrorCode` |

### 3.3 线程与执行模型（核对自 XmlDoc）

> "All callbacks are serialized, called in the same thread, and called with the process in the synchronized state. Each callback implementation must call `CorDebugController.Continue` to resume execution. If `Continue` is not called before the callback returns, the process will remain stopped and no more event callbacks will occur until `Continue` is called."

实践要点：
- 回调天然发生在调试器内部线程（非你创建 ICorDebug 的线程）。由于对象要求 MTA 创建、且无需 STA 消息泵，回调线程可直接操作 wrapper（ClrDebug 特意要求 MTA 创建以保证跨线程 COM 直调可用）。
- **每个回调处理完毕都要 `e.Controller.Continue(false)`**（带内事件传 `false`）。
- 设断点/读栈/单步这类"问目标"操作只能在进程同步停止（回调内）进行；主线程做不了这些（典型做法：引擎线程只负责等事件队列，事件循环由回调 + 阻塞队列/`AutoResetEvent` 驱动，如 sharpdbg 的事件泵模式）。

---

## 4. 按 token 下 IL 断点（模块→函数→IL 断点链）

全部核对自 `Managed/Cordb/`：

```csharp
// 入口 A：附加/启动得到进程 CorDebugProcess
CorDebugProcess process = ...;   // 来自 DebugActiveProcess / CreateProcess / CreateProcessForLaunch+CreateDebuggingInterfaceFromVersionEx+DebugActiveProcess

// 进程 → 应用域 → 程序集 → 模块（无“进程直接列模块”的公开属性，模块挂在 Assembly 下）
CorDebugAppDomain[] domains = process.AppDomains;                       // 或 EnumerateAppDomains()
CorDebugAssembly[] asms = domains[i].Assemblies;                        // 或 EnumerateAssemblies()
CorDebugModule[] mods  = asms[i].Modules;                               // 或 EnumerateModules()

// 也可以从 LoadModule 事件直接拿 CorDebugModule（推荐：模块加载时缓存，按名/基址索引）

// 模块 → 函数（ICorDebugModule.GetFunctionFromToken，token 为 mdMethodDef）
//   public CorDebugFunction GetFunctionFromToken(mdMethodDef methodDef)   // 核对
CorDebugFunction fn = module.GetFunctionFromToken(new mdMethodDef((uint)methodToken /* 如 0x06000005 */));

// 函数 → IL 代码（属性形式；对应 ICorDebugFunction.GetILCode()）
CorDebugCode ilCode = fn.ILCode;      // public CorDebugCode ILCode { get; }   // 核对

// IL 代码 → 断点（offset = IL offset；对应 ICorDebugCode.CreateBreakpoint(offset)）
//   public CorDebugFunctionBreakpoint CreateBreakpoint(int offset)          // 核对
CorDebugFunctionBreakpoint bp = ilCode.CreateBreakpoint(ilOffset);

// 断点激活（ICorDebugBreakpoint 默认创建即激活？——ICorDebugCode.CreateBreakpoint 注释：
//   “Before the breakpoint is active, it must be added to the process object.”）
//   核对 ICorDebugBreakpoint：public bool IsActive { get; } 与 public void Activate(bool bActive)
bp.Activate(true);   // 若需要确保激活；IsActive 可查询
```

**命中判定**：`OnBreakpoint` 事件里，`e.Breakpoint` 是 `CorDebugBreakpoint`（`CorDebugBreakpoint.New` 自动分派成 `CorDebugFunctionBreakpoint` 等）。比较用 `CorDebugFunctionBreakpoint.Function`（`CorDebugFunction`）与 `CorDebugFunctionBreakpoint.Offset`（IL offset）：
```csharp
cb.OnBreakpoint += (s, e) =>
{
    var fbp = e.Breakpoint as CorDebugFunctionBreakpoint;      // 或 e.Breakpoint 直接
    // fbp.Function.Token == 期望 token（CorDebugFunction.Token，类型 mdMethodDef）
    // fbp.Offset == 期望 IL offset（public int Offset { get; }，核对）
    // ……读栈/变量……
    e.Controller.Continue(false);
};
```

### token 类型（核对自 `Extensions/Tokens.cs`）

- `mdMethodDef`、`mdTypeDef`、`mdToken` 等是一组**强类型 struct**，含 `uint Value`、`int Rid`、`CorTokenType Type`（`Type = (CorTokenType)(Value & 0xFF000000)`）。
- **`int`/`uint` ↔ token 双向隐式转换**：`new mdMethodDef((uint)0x06000005)` 或直接 `mdMethodDef md = 0x06000005;`（整型字面量适合放变量/常量后传）。
- `CorDebugFunction.Token`（`mdMethodDef` 属性）可反向读回当前函数 token。

### `CorDebugFunction` 关键成员（核对）

```csharp
public CorDebugModule Module { get; }        // ICorDebugFunction.GetModule()
public CorDebugClass Class { get; }          // GetClass()
public mdMethodDef Token { get; }            // GetToken()
public CorDebugCode ILCode { get; }          // GetILCode()
public CorDebugCode NativeCode { get; }      // GetNativeCode()
```

### 用元数据枚举模块 token（可选）
```csharp
// 扩展：ClrDebug/Extensions/Extensions.CorDebugModule.cs
var mdi = module.GetMetaDataInterface<MetaDataImport>();   // public static T GetMetaDataInterface<T>(this CorDebugModule)
// 随后 mdi.EnumMethodDefs()…（MetaDataImport 提供 IMetaDataImport 的托管枚举扩展）
```

---

## 5. 单步

### 5.1 创建 Stepper（核对）

```csharp
// ICorDebugThread.CreateStepper —— 线程级
public CorDebugStepper CreateStepper();      // 核对自 CorDebugThread.cs:472

// ICorDebugFrame.CreateStepper —— 帧级（在回调里拿当前帧后创建更精确）
public CorDebugStepper CreateStepper();      // 核对自 CorDebugFrame.cs:302
```

### 5.2 Step/StepRange（核对自 CorDebugStepper.cs）

```csharp
public void Step(bool bStepIn);                          // true=step into；false=step over
public void StepRange(bool bStepIn, COR_DEBUG_STEP_RANGE[] ranges, int cRangeCount);
// 控制：
public void Activate(bool bActive);          // 核对：public void Activate(bool)（继承自 CorDebugStepper 自带）
public void Deactivate();                    // 核对存在（ICorDebugStepper.Deactivate）
public bool IsActive { get; }                // 核对 CorDebugStepper.cs:32
public void SetUnmappedStopMask(CorDebugUnmappedStop mask);   // 控制 IL 边界停止行为
public void SetRangeIL(bool bIL);            // StepRange 范围按 IL 还是 native
```

**用法**：事件回调（Breakpoint 或 StepComplete）内
```csharp
var stepper = e.Thread.CreateStepper();   // e.Thread : CorDebugThread
stepper.Step(false);                       // step over
e.Controller.Continue(false);
```
**完成通知**：`OnStepComplete`，`e.Reason`（`CorDebugStepReason`：STEP_NORMAL/STEP_RETURN/STEP_CALL/STEP_EXCEPTION_FILTER/STEP_EXCEPTION_HANDLER/STEP_INTERCEPT/STEP_EXIT）判断为何停下。

---

## 6. 读线程、调用栈与局部变量

### 6.1 线程枚举（核对自 CorDebugController.cs / CorDebugThread.cs）

```csharp
CorDebugThread[] threads = process.Threads;            // Controller 级 EnumerateThreads().ToArray()
CorDebugThread t = ...;
int osTid = t.Id;                                       // public int Id { get; }  （GetID）
int volTid = t.VolatileOSThreadID;                      // public int VolatileOSThreadID { get; }
CorDebugAppDomain ad = t.AppDomain;                     // public CorDebugAppDomain AppDomain { get; }
long taskId = t.TaskID;                                 // public long TaskID { get; }
```

### 6.2 调用栈两种走法

**(a) 线程直接拿 active frame 链（不展开 native）**
```csharp
CorDebugFrame f = t.ActiveFrame;     // public CorDebugFrame ActiveFrame { get; }（GetActiveFrame；自动 New 分派）
// 帧间导航（核对 CorDebugFrame.cs）：
CorDebugFrame caller = f.Caller;     // public CorDebugFrame Caller { get; }
CorDebugFrame callee = f.Callee;     // public CorDebugFrame Callee { get; }
```

**(b) StackWalk（展开整个栈，含 native 帧、内帧）**
```csharp
CorDebugStackWalk sw = t.CreateStackWalk();    // public CorDebugStackWalk CreateStackWalk()（CorDebugThread.cs:862）
do {
    CorDebugFrame frame = sw.Frame;            // public CorDebugFrame Frame { get; }（GetFrame；S_FALSE=当前为 native 帧时 Try 版区分）
    // 处理 frame……
    sw.Next();                                 // public void Next()（推进；到底后 Try 返回 CORDBG_E_PAST_END_OF_STACK）
} while (...);
```
> `CorDebugStackWalk.GetFrame` 的 HRESULT 语义：S_OK=托管帧、S_FALSE=原生帧、CORDBG_E_PAST_END_OF_STACK=结束。因此用 `TryGetFrame(out var f)` 循环判 HRESULT 更稳（返回类型 `CorDebugFrame`）。

### 6.3 Frame → Function/Code/token（核对 CorDebugFrame.cs）

```csharp
public CorDebugFunction Function { get; }     // GetFunction（无关联函数时可能失败）
public CorDebugCode Code { get; }             // GetCode
public mdMethodDef FunctionToken { get; }     // GetFunctionToken（public mdMethodDef FunctionToken { get; } 核对）
public void GetStackRange(...);               // (属性 StackRange 或 GetStackRangeResult)
```

### 6.4 IL 帧的 IP/局部变量/实参（核对 CorDebugILFrame.cs）

`CorDebugFrame.New(ICorDebugFrame)` 自动分派：`ICorDebugILFrame → CorDebugILFrame`、`ICorDebugInternalFrame → CorDebugInternalFrame`、`ICorDebugNativeFrame → CorDebugNativeFrame`（核对 `CorDebugFrame.New`）。所以从 `sw.Frame`/`t.ActiveFrame` 拿到的对象可 `is CorDebugILFrame` 后直接转型。

```csharp
CorDebugILFrame ilf = frame as CorDebugILFrame;
if (ilf != null)
{
    GetIPResult ip = ilf.IP;                       // public GetIPResult IP { get; }（含 pnOffset + CorDebugMappingResult）
    ilf.SetIP(int nOffset);                        // public void SetIP(int nOffset)
    ilf.CanSetIP(int nOffset);                     // 预先探测

    CorDebugValueEnum localsEnum = ilf.EnumerateLocalVariables();   // public CorDebugValueEnum EnumerateLocalVariables()
    CorDebugValueEnum argsEnum   = ilf.EnumerateArguments();        // public CorDebugValueEnum EnumerateArguments()
    CorDebugValue[] locals = ilf.LocalVariables;   // 便捷属性 EnumerateLocalVariables().ToArray()
    CorDebugValue[] args   = ilf.Arguments;        // 便捷属性 EnumerateArguments().ToArray()
    // ILCodeKind 版本：EnumerateLocalVariablesEx(ILCodeKind flags)
}
```
- 枚举器可 `foreach`（ClrDebug 把标准枚举器包装成 `IEnumerable<T>`）。
- 每个元素是 `CorDebugValue`（`CorDebugValue.New` 自动分派为 CorDebugReferenceValue / CorDebugGenericValue / CorDebugStringValue / CorDebugArrayValue 等子类），读值走具体子类（如 `.GetValue()`、`.String`）。
- 注意：变量枚举**索引与调试信息/SymbolStore 的槽号对应**；精确到名字需 Portable PDB/`ISym*`（ClrDebug 同样包装 `ISymUnmanaged*`）。

### 6.5 first-chance 异常

`OnException` 事件即 first-chance（`e.Unhandled == 0` 时是第一次有机会处理；非 0 表示即将未处理退出）。在 handler 内读 `e.Thread` 栈即可看到抛出点上下文。若要"接住后继续"，读完后 `e.Controller.Continue(false)`。若想让它变成未处理终止，则不要 Continue / 依序走完即可（ICorDebug 语义：不 Continue 会挂起）。

---

## 7. Samples/NetCore 源码结构与核心流程

路径（tag 0.4.2，也可看 master）：
`Samples/NetCore/{Program.cs, DbgShimResolver.cs, NativeMethods.cs, NetCore.csproj, README.md}`

### 7.1 Program.cs 两种引导（核对自源码）

```csharp
// 准备：找 dbgshim.dll 路径并加载
var dbgShimPath = DbgShimResolver.Resolve();     // 从输出目录/runtimes/C:\Program Files\dotnet 下找
var dbgshim = new DbgShim(NativeLibrary.Load(dbgShimPath));   // .NET 8+；旧框架用 NativeMethods.LoadLibrary

// 挂起启动（CreateProcessForLaunch 不带 CREATE_NEW_CONSOLE 等标志；true=挂起）
var process = dbgshim.CreateProcessForLaunch(dbgTargetPath, true);
try
{
    Manual(dbgshim, process.ProcessId, process.ResumeHandle);
    // 或 Automatic(dbgshim, process.ProcessId, process.ResumeHandle);
}
finally { dbgshim.CloseResumeHandle(process.ResumeHandle); }
```

**Manual（推荐给引擎：可控时序、可超时）**：
```csharp
var startupEvent = dbgshim.GetStartupNotificationEvent(pid); // ① 取“CLR 启动通知事件”
dbgshim.ResumeProcess(resumeHandle);                          // ② 恢复主线程（否则 CLR 不加载）
NativeMethods.WaitForSingleObject(startupEvent, -1);          // ③ 等 CLR 就绪（可带超时）
var enumResult = dbgshim.EnumerateCLRs(pid);                  // ④ 取 CLR 模块
var runtime = enumResult.Items.Single();
var versionStr = dbgshim.CreateVersionStringFromModule(pid, runtime.Path);  // ⑤ 版本串
var cordebug = dbgshim.CreateDebuggingInterfaceFromVersionEx(
    CorDebugInterfaceVersion.CorDebugVersion_4_0, versionStr);              // ⑥ 建 ICorDebug
InitCorDebug(cordebug, pid);                                  // ⑦ 初始化并附加（见下）
NativeMethods.SetEvent(runtime.Handle);                       // ⑧ 放行 CLR 继续启动
dbgshim.CloseCLREnumeration(enumResult);
```

**InitCorDebug**（Manual/Automatic 共用）：
```csharp
private static void InitCorDebug(CorDebug cordebug, int pid)
{
    cordebug.Initialize();
    var cb = new CorDebugManagedCallback();
    cb.OnAnyEvent += (s, e) => e.Controller.Continue(false);   // 默认全 Continue
    cb.OnLoadModule += LoadModule;
    cordebug.SetManagedHandler(cb);
    cordebug.DebugActiveProcess(pid, false);                    // 附加；win32Attach=false
}
```

**Automatic（RegisterForRuntimeStartup）**：
```csharp
IntPtr unregisterToken = IntPtr.Zero;  CorDebug cordebug = null;  HRESULT hr;
var wait = new AutoResetEvent(false);
dbgshim.ResumeProcess(resumeHandle);    // 注意时序竞态说明（见 README gotcha）
unregisterToken = dbgshim.RegisterForRuntimeStartup(pid, (pCordb, parameter, callbackHR) =>
{
    cordebug = pCordb;   // 回调签名 (CorDebug, IntPtr, HRESULT)
    hr = callbackHR;
    wait.Set();
});
wait.WaitOne();
if (cordebug == null) throw new DebugException(hr);
dbgshim.UnregisterForRuntimeStartup(unregisterToken);
InitCorDebug(cordebug, pid);
```

### 7.2 README 结论表（核对）

| 函数 | 说明 |
|---|---|
| `CreateDebuggingInterfaceFromVersion` | 勿用（强制 debugger 版本 2.0） |
| `CreateDebuggingInterfaceFromVersionEx` | **推荐（手动）** |
| `CreateDebuggingInterfaceFromVersion2` | macOS 沙箱才需要 appGroupId |
| `CreateDebuggingInterfaceFromVersion3` | 需自定义 DBI/DAC 提供者时 |
| `RegisterForRuntimeStartup` | **推荐（自动）** |
| `RegisterForRuntimeStartupEx/3` | 同上扩展 |

dbgshim 版本无关紧要（向后兼容设计）。

---

## 8. 落到你自己的引擎的最小骨架（伪代码，全部 API 均为上述核对过的名字）

```csharp
// 引擎线程（MTA；不可在 STA 线程创建 CorDebug/DbgShim 产物）
var mta = new Thread(() =>
{
    var dbgshim = new DbgShim(NativeLibrary.Load(dbgShimPath));   // dbgShimPath 来自 NuGet Microsoft.Diagnostics.DbgShim 分发
    var lp = dbgshim.CreateProcessForLaunch(cmdLine, bSuspendProcess: true);
    var ready = dbgshim.GetStartupNotificationEvent(lp.ProcessId);
    dbgshim.ResumeProcess(lp.ResumeHandle);
    WaitForSingleObject(ready, timeoutMs);
    var clrs = dbgshim.EnumerateCLRs(lp.ProcessId);
    var core = clrs.Items.Single(x => x.Path.EndsWith("coreclr.dll", OrdinalIgnoreCase));
    var ver  = dbgshim.CreateVersionStringFromModule(lp.ProcessId, core.Path);
    var cordebug = dbgshim.CreateDebuggingInterfaceFromVersionEx(CorDebugInterfaceVersion.CorDebugVersion_4_0, ver);

    var cb = new CorDebugManagedCallback();
    // 状态机：为每个事件决定行为
    cb.OnCreateProcess += (s,e) => { engine.SetProcess(e.Process); e.Controller.Continue(false); };
    cb.OnLoadModule   += (s,e) => { engine.TrackModule(e.Module);  e.Controller.Continue(false); };
    cb.OnBreakpoint   += (s,e) => { engine.OnBreakpointHit(e); };   // 内部自行 Continue（或继续/单步）
    cb.OnStepComplete += (s,e) => { engine.OnStepDone(e); };
    cb.OnException    += (s,e) => { if (engine.WantFirstChance(e)) engine.OnFirstChance(e); e.Controller.Continue(false); };
    cordebug.SetManagedHandler(cb);

    cordebug.DebugActiveProcess(lp.ProcessId, win32Attach: false);
    dbgshim.CloseResumeHandle(lp.ResumeHandle);
    // 引擎主循环：事件队列驱动（BlockingCollection / ManualResetEventSlim），直到 ExitProcess
    engine.Run();   // 引擎命令（设断点/继续/单步）在回调线程执行
});
mta.SetApartmentState(ApartmentState.MTA);
mta.Start();

// 断点（回调线程/同步态内执行）：
var fn  = module.GetFunctionFromToken(new mdMethodDef((uint)token));
fn.ILCode.CreateBreakpoint(ilOffset).Activate(true);

// 命中后读调用栈与变量（在 OnBreakpoint handler 内）：
var sw  = e.Thread.CreateStackWalk();
do { if (sw.Frame is CorDebugILFrame ilf) { var ip = ilf.IP; var locals = ilf.LocalVariables; /* 读 */ } }
while (sw.TryGetFrame(...) == S_OK && (sw.Next() 成功));

// 单步：
e.Thread.CreateStepper().Step(bStepIn: false);   // 然后 e.Controller.Continue(false)
```

---

## 9. 易错点/注意事项汇总

1. **STA 陷阱**：`new CorDebug()`、dbgshim 产物与回调交互都要求 **MTA**。在 ASP.NET/UI 线程直接做会抛异常或跨 apartment 崩溃。所有调试对象只应在专用 MTA 线程创建/使用。
2. **SetManagedHandler 时序**：`Initialize()` 之后、`CreateProcess`/`DebugActiveProcess` 之前调用，否则抛 `InvalidOperationException`（CreateProcess 扩展的 E_FAIL 分支会提示此文案）。
3. **每个回调必须 Continue**（否则进程停死）。事件 args 的 `Continue` 属性只是便利标记，**不会自动调用**。
4. **手动 vs 自动模式**：引擎选 **Manual**（挂起启动 + `GetStartupNotificationEvent` + 可超时）；自动模式 `RegisterForRuntimeStartup` 内部有竞态与线程问题（详见 README gotcha）。
5. **断点在模块加载前设不了**：跟踪 `OnLoadModule`，按模块名/基址缓存 `CorDebugModule`；对尚未加载的模块要等加载事件后再 `GetFunctionFromToken`。
6. `GetFunctionFromToken` 对非 IL 方法返回 `CORDBG_E_FUNCTION_NOT_IL`；`GetILCode` 可能返回 null（未 JIT/无 IL）。
7. **读内存/对象值应在进程同步（回调内）进行**；回调外调用大多无效或阻塞。
8. 0.4.2 相对 master 的差异仅限 Profiling 相关；本文 CorDebug/DbgShim API 在 0.4.2 与 master 完全一致（diff 已验证）。
9. 接口定义默认 `[ComImport]`（非 AOT 构建），net10.0 运行时直接可用；NativeAOT 才需 `GENERATED_MARSHALLING`/`[GeneratedComInterface]` 构建变体。
10. 你已有 ILSpyMcp 的元数据层（token 反查方法名等）可与这里 `mdMethodDef`/`FunctionToken` 对接：`0x06xxxxxx` 高位即 methoddef。
