# 调研 · 依赖包清单与许可（动态调试引擎 + WebUI）

> 目的：明确「动态调试引擎」技术路线要用的 NuGet 包，逐包给出用途/许可/版本线索。日期：2026-09-05。
> 概念澄清（用户问答，2026-09-05）：**ICorDebug / ClrDebug / ClrMD** 三者关系见文末附录 A。

## 附录 A · ICorDebug / ClrDebug / ClrMD 概念澄清

| | ICorDebug | ClrDebug | ClrMD（Microsoft.Diagnostics.Runtime） |
|---|---|---|---|
| 是什么 | 微软**原生 COM 接口集** | 社区 C# 1:1 封装（lordmilko，MIT，NuGet） | 微软官方 C# 诊断库（MIT，NuGet） |
| 本质 | 非托管 API（ICorDebugProcess/Thread/Value/Eval/Stepper…） | ICorDebug 的 C# 版（底层仍调 COM） | 独立数据读取层，走 DAC（mscordacwks） |
| 定位 | 活动调试（控制执行） | = ICorDebug（同一能力） | 只读内省（不控制执行） |
| 断点/单步/求值/恢复 | ✅ | ✅ | ❌（官方 FAQ：not a debugging api） |
| 读栈/变量/对象 | ✅ | ✅ | ✅（更高层友好） |
| 存在形式 | 非 NuGet（系统原生 mscordbi + dbgshim） | NuGet（内部加载原生 dll） | NuGet |
| 典型使用者 | VS/WinDbg/dnSpy 底层 | C# 写调试器的人（本项目） | dotnet-dump、堆分析 |

- 类比：ICorDebug=手术器械（native 原件）；ClrDebug=同套器械装 C# 手柄（免手写 COM interop；dnSpy 自写 dndbg 95 文件干的就是这件事）；ClrMD=CT/内窥镜（不切开看清内部，不能下刀）。
- 通道区别：ICorDebug/ClrDebug 走**调试通道**（进程真实暂停/继续，每进程仅一个调试器）；ClrMD 走**数据通道**（DAC，live 需暂停或快照，不与调试器争名额，可与调试会话共存）。
- 本项目：引擎主通道用 ClrDebug；ClrMD 仅作可选旁路（dump 事后分析，v1 不引）。

> 核心判断（用户问「dnSpyEx 核心库是否自研」）：**dnSpyEx 调试核心 100% 自研**——dndbg 层手写 ICorDebug COM 封装（95 cs）、Impl 自研引擎状态机、Interpreter 自研 IL 解释器、Roslyn.ExpressionCompiler 自研 fork；底层仅依赖微软系统级非托管组件（ICorDebug/mcordbi + DbgShim），第三方托管包只有工具性的 dnlib/Iced/ClrMD1.x/VS.Text。**生态中没有 MIT 的开箱即用调试引擎库**，引擎层绕不开自研。

## 技术路线定位

| dnSpyEx 自研层 | 我们怎么做 |
|---|---|
| dndbg（手写 ICorDebug COM 封装，95 cs） | **用 ClrDebug（MIT）替代**——它已把全量 COM 封装做完 |
| Impl 引擎状态机（断点/步进/locals/栈/异常） | **自研**（clean-room 参考 dnSpy dndbg+Impl 协议结构） |
| Roslyn.ExpressionCompiler（fork） | **不用**。v1 用官方 Roslyn 做解析/语义 + 自研 AST 安全求值 + ICorDebugEval2 |
| dnSpy.Debugger.DotNet.Interpreter（IL 解释器） | v1 不做（远期可选） |
| dnSpy.Contracts.Debugger 对象模型 | 自研轻量模型（进程/线程/栈帧/值/断点事件） |

## 依赖包清单（动态调试引擎）

| 包 | 用途 | 许可 | 版本线索/备注 |
|---|---|---|---|
| **ClrDebug** | ICorDebug/IMetaData 等非托管诊断 API 全量 1:1 托管封装 | MIT | lordmilko，0.4.x 活跃；含 Samples/NetCore（attach/断点/单步骨架） |
| **Microsoft.Diagnostics.DbgShim**（+ `win-x64` 等 RID 子包） | 原生 dbgshim 引导（CreateDebuggingInterfaceFromVersionEx） | MIT | 坑：PlatformManifest 冲突致 dll 不复制（dotnet/runtime#90187）；备选：运行时从目标 runtime 目录 LoadLibraryEx |
| **ICSharpCode.Decompiler**（已有） | token→方法定位、反编译源码（断点定位 + 源码视图） | MIT | 现有 11.0.0.9375 |
| **System.Reflection.Metadata**（框架自带/现有） | 读 Portable PDB：源文件/行↔方法 token+IL offset | MIT | 现有元数据层同源 |
| **Microsoft.CodeAnalysis.CSharp**（官方 Roslyn） | 表达式求值 v1：语法+语义分析、类型推断 | MIT | 只做静态分析，不做编译注入 |
| ICorDebug COM 运行时侧 | 断点/步进/值/Eval/Stepper（经 ClrDebug 调用） | 系统提供 | mscordbi.dll / DbgShim，无需 NuGet |

### 可选旁路（v1 可后置）
| 包 | 用途 | 许可 |
|---|---|---|
| Microsoft.Diagnostics.NETCore.Client | diagnostic port：EventPipe 监控、启动配置、写 dump | MIT |
| Microsoft.Diagnostics.Runtime（ClrMD） | dump 事后深度分析（不争用 ICorDebug 会话） | MIT |

### 明确不使用
| 资产 | 许可 | 原因 |
|---|---|---|
| dnSpyEx 调试栈（dndbg/Impl/Contracts.Debugger/Roslyn.ExpressionCompiler/Interpreter） | GPL-3.0 | 传染 + 无 NuGet + 耦合；仅 clean-room 参考 |
| debug-mcp | AGPL-3.0 | 传染；Linux 优先；仅借鉴工具面设计 |
| netcoredbg | MIT | 独立调试器二进制非库；留作「进程内需求松动」备选 |

## WebUI 侧包清单（v1 规划）

| 侧 | 包/资产 | 许可 | 备注 |
|---|---|---|---|
| 服务端 | ASP.NET Core（`Microsoft.AspNetCore.App` 框架引用）+ `TypedResults.ServerSentEvents`（.NET 10） | MIT | 不额外引 SignalR |
| 前端 | Monaco Editor（npm `monaco-editor`）+ React 18/19 + TypeScript + Vite（`vite-plugin-monaco-editor`） | MIT | C# 高亮内置 |
| 图 | mermaid.js（动态 import） | MIT | 按需 |
| 图标 | codicon（Monaco 内置字体） | MIT | 断点 glyph |

> 许可判断为一般性常识，落地前建议按公司合规复核。详见 research/01 与 research/04。
