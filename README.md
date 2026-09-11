# DotNet Debugger MCP（DotNetDebuggerMcp）

内置反编译引擎（[ICSharpCode.Decompiler](https://github.com/icsharpcode/ilspy)）与动态调试引擎（ClrDebug/ICorDebug）的 .NET MCP 服务器。在 [opencode](https://opencode.ai) 等 MCP 客户端中直接对 .NET 程序集（dll / exe）做反编译、类型探测、源码写盘与**动态调试**（启动/附加进程、断点、单步、读调用栈与变量），开箱即用。另提供 **Web 网页调试展示面**（Blazor Server：反编译代码视图 + 调用栈/变量/线程面板，与 MCP agent 共享调试会话，可实时观看 agent 调试）——agent 按需调 `web_open` 工具开启（幂等），或启动时带 `--web` 手动开启。

## 目录

- [环境要求](#环境要求)
- [安装](#安装)
- [接入 opencode](#接入-opencode)
- [核心约定](#核心约定)
- [工具一览](#工具一览)
- [命令行调试](#命令行调试)
- [工具参数](#工具参数)
- [使用示例](#使用示例)
- [第三方组件](#第三方组件)
- [License](#license)

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)
- Windows（反编译/静态分析跨平台可用；`debug_*` 动态调试与 `ui_*` UI 自动化需 Windows——ui_* 全 UIA 语义 pattern，**不移动光标/不注入输入/不抢前台**，只要求目标窗口在 UIA 树里可见；远程/服务会话可能读不到目标窗口）

## 安装
### 安装
```bash
dotnet tool install --global DotNetDebuggerMcp 
```

### 升级
```bash
dotnet tool update --global DotNetDebuggerMcp
```

### 卸载
```bash
dotnet tool uninstall --global DotNetDebuggerMcp
```

查看版本 / 帮助：`DotNetDebuggerMcp -v` / `DotNetDebuggerMcp -h`

## 接入 opencode

### opencode v2

`opencode.json`（或 `opencode.jsonc`）中注册本地 MCP，服务器名称放在 `mcp.servers` 下：

```jsonc
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "servers": {
      "DotNetDebugger": {
        "type": "local",
        "command": ["DotNetDebuggerMcp"]
      }
    }
  }
}
```

### opencode v1

v1 中服务器名称直接放在 `mcp` 下（v2 仍兼容此写法）：

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "DotNetDebugger": {
      "type": "local",
      "command": ["DotNetDebuggerMcp"]
    }
  }
}
```

重启 opencode 后工具以 `dotnetdebugger_*` 前缀暴露（v2 中工具名为 `<服务器名>_<工具名>`）。`assembly` / `outputDir` 的相对路径以 opencode 会话的工作目录解析。

## 核心约定

- **输出带头部信息块**：`程序集 / 目标 / 总行数 / 当前输出 / 剩余` + `---` 分隔线，命中缓存时追加 `缓存: 命中`。
- **行号与分页**：结果按 `行号<TAB>内容` 输出，默认返回前约 8 KB，可用 `lines="start-end"`（如 `200-400`）按行号分页，单次最多约 32 KB。
- **缓存**：除写盘工具外全部结果按 `程序集 + 参数` 共享缓存（64 MB LRU，固定 30 分钟滑动过期 + 5 分钟定时清理，程序集更新自动失效），超时/失败不入缓存。
- **类型名格式**：与 `dotnetdebugger_list_types` 输出一致（`命名空间.类型`，嵌套用 `+`，泛型带 arity 如 ``GenericBox`1``），行首类别前缀（如 `class Foo.Bar`）可直接复用。
- **Token 闭环**：`dotnetdebugger_signature` 每行行尾附成员 token（`0x06…`），`#MEMBER` 分隔行含 `token`，均可直接用于 `dotnetdebugger_decompile_member` / `dotnetdebugger_decompile_il` / `dotnetdebugger_call_graph` / `dotnetdebugger_call_chain` / `dotnetdebugger_field_access` 精确定位。

## 工具一览

### 反编译

| 工具 | 用途 |
| ---- | ---- |
| `dotnetdebugger_decompile` | 按类型反编译源码到 stdout（类型级，含全部成员） |
| `dotnetdebugger_decompile_member` | 按成员名子串或 token 反编译一个或多个成员，多匹配合并输出、超 20 个仅列签名 |
| `dotnetdebugger_decompile_il` | 按方法 token（`0x06…`）反汇编方法体为 IL 文本——async 状态机等编译器生成类型反编译为 C# 失败/难读时的 IL 兜底 |
| `dotnetdebugger_decompile_to_dir` | 反编译写入目录（全量或 `typeName` 逗号分隔批量，单文件输出） |
| `dotnetdebugger_decompile_to_project` | 以可编译项目形式反编译整个程序集到目录（按命名空间嵌套） |
| `dotnetdebugger_call_chain` | 从起始方法出发的正向调用序列 + 被调用内部成员反编译 |

### 结构探测（纯元数据，秒回）

| 工具 | 用途 |
| ---- | ---- |
| `dotnetdebugger_list_types` | 列出实体类型（c/i/s/d/e 可组合），支持名称/命名空间子串过滤；默认过滤编译器生成类型 |
| `dotnetdebugger_signature` | 类型成员签名 API 地图（字段/方法/属性/事件），行尾附 token（`0x06` 方法/`0x04` 字段等） |
| `dotnetdebugger_hierarchy` | 基类链（上溯 `System.Object`）/ 接口 / 程序集内继承实现者；`includeIndirect=true` 一次返回全部间接后代 |
| `dotnetdebugger_dependencies` | 成员签名引用的内部类型及反向引用；`includeExternal=true` 追加 `全名 [程序集名]` 外部类型 |
| `dotnetdebugger_call_graph` | 方法体调用关系清单（双向，扫描 `call`/`callvirt`/`newobj` 等）；`token` 模式反向定位调用点，`includeExternal` 同上 |
| `dotnetdebugger_interface_usage` | 接口组合视图：实现者 + 调用点（`类型::成员 → 接口成员`）+ 签名引用；`includeIndirect` 含子接口/实现者子类 |
| `dotnetdebugger_generic_instantiations` | 泛型实例化的两段使用点：签名中 / 方法体调用中；`typeName` 可带或不带 `` `1`` arity |
| `dotnetdebugger_search_string` | 按字符串字面量子串反查成员（忽略大小写），输出 `类型::成员 字符串值 token`。默认跳过编译器生成类型；找 async 方法/lambda 里的字面量（位于生成的状态机/闭包类型内）需 `includeCompilerGenerated=true` |
| `dotnetdebugger_field_access` | 追踪字段的读/写/取地址三段来源（空段输出 `（无）`） |
| `dotnetdebugger_assembly_info` | 程序集概览：名称版本、目标框架、引用清单、类型计数、入口点 |

### 辅助

| 工具 | 用途 |
| ---- | ---- |
| `dotnetdebugger_cache_stats` | 共享缓存状态：占用/上限、条目数、命中率与逐条明细 |

### 动态调试（需可启动/附加的 .NET 进程）

| 工具 | 用途 |
| ---- | ---- |
| `dotnetdebugger_debug_processes` | 列出本机可附加的 .NET 进程（pid/进程名/CLR 版本，dbgshim 权威探测——只列实际探测到 CLR 的进程，无 CLR 进程不误列；调试器自身已排除；`filter` 进程名子串过滤；进程名排序，当前会话目标行标注「← 当前会话」，超 100 条截断提示用 filter）——选 pid 后 `debug_attach`。存在当前会话目标时，其 .NET 子孙进程行尾标注父链（「← 会话目标(X) 的子进程/第N代孙进程（父 Y）」）并附切换引导 |
| `dotnetdebugger_debug_launch` / `dotnetdebugger_debug_attach` | 启动或附加 .NET 进程建立调试会话（异步返回，带默认超时）。launch 蹲守 CLR 启动、停在 Main 前——任意程序无需启动配合即可从第一行业务代码前调试。`debug_launch` 可传 `workingDirectory`（默认空=目标 exe 所在目录，对齐手动启动 exe；Web 应用等以工作目录定位 appsettings/静态资源的目标应显式传 bin/publish 目录）与 `environment`（KEY=VALUE 多行或分号，如 `ASPNETCORE_ENVIRONMENT=Development`）；返回与 `debug_state` 均含目标 pid，launch 返回另含实际生效工作目录 |
| `dotnetdebugger_debug_breakpoint_set` / `_remove` / `_clear` / `_list` | 下/删/清/列断点，四种定位：模块+方法 token+IL offset（signature 行尾取 token，未加载模块登记待绑定）；`typeName`+`memberName` 按类型内方法名子串定位（命中唯一方法即设断点；属性/事件/字段成员提示改用访问器 token；多方法匹配返回 `#MEMBER` 清单用 `methodToken` 精确重设）；`typeName`+`line` 按反编译视图行（需模块已加载）；`sourcePath`+`line` 按 PDB 源码行（模块未加载/未命中时登记延迟项，模块加载后自动按 PDB 解析绑定——launch 冻结 Main 前可直接设源行断点）。可选 `hitCount`（第 N 次命中起生效）与 `mode`（stop=命中停 / trace=命中不停记轨迹，`debug_wait` 批量取回）；可选 `condition`（P6 子集表达式如 `i == 3`，为真才停/记——语法错当场拒绝，命中时求值失败放行并在 debug_state/debug_wait 反馈「条件未通过」防静默空等）。**「值变化即停」的数据断点暂不支持**：ICorDebug `ICorDebugValue::CreateBreakpoint` 实测恒 `E_NOTIMPL`（运行时未实现，官方 doc 标注 not implemented），且 ICorDebug 无数据断点创建 API（VS 等 .NET「值变化断点」走自有硬件寄存器机制，非公开 ICorDebug 口）——「某字段被莫名改错（多线程/回调/副作用）」请用**已知写入点的条件断点**定位：对可疑写入方法设断点 + `condition`（如 `h.Counter != 期望值` 才停），或 trace/单步观察；对已定位但不知何时被改的字段，可先断住再看谁写它（`field_access` 静态分析列出写入方候选） |
| `dotnetdebugger_debug_continue` / `dotnetdebugger_debug_step` / `dotnetdebugger_debug_wait` | 继续执行 / 单步（into/over/out，进程需停在断点）/ 等待进程停下（默认 10s，直接返回停点现场，默认附停点上下文与目标最近控制台输出）。`debug_step` 停在编译器生成的 async 状态机帧（类型形如 `<Foo>d__N`）时返回附行断点引导（建议断还原源码的 await 行，勿 step into 状态机 MoveNext） |
| `dotnetdebugger_debug_run_to` | 运行到目标位置后停下（对标 VS 运行到光标处 / Run to Cursor）：在目标处设一次性临时断点并继续运行，命中即停、**临时断点自动移除**。目标定位同 `debug_breakpoint_set` 的 `typeName`+`line`（decompile 输出行号）或 `typeName`+`memberName`（方法名子串，命中唯一方法）。超时 / 命中其它断点 / 进程退出均返回对应提示且临时断点一并自动清理。单步后立即调用亦可靠（自动跳过 continue 前旧快照与残留单步停点）。边界：只对「目标会被进程自然执行到」有效——停住不自己走的路径请改用普通断点 + `debug_continue` |
| `dotnetdebugger_debug_state` | 查询会话状态与最近停点（进程是否停下/停在何处；停点时附反编译视图上下文——停点类型是编译器生成的 async 状态机 `<Foo>d__N` 时头部附备注：对应 async 方法 Foo、业务代码见外壳方法还原源码、建议用行断点断 await 行；已退出时显示退出码（launch 会话）；attach 到长活目标且进程在跑时提示「已附加成功、进程独立运行」） |
| `dotnetdebugger_debug_timeline` | 查看当前会话的日志+事件+agent 动作统一时间线（按时间对齐复盘：先 A 日志→命中 B 断点→agent 改值→再 C 异常）。三源：目标进程输出（`log`，仅 launch 会话有）、调试事件（`bp`/`step`/`exc`/`skip`/`trc`/`state`/`engine`）、agent 动作（`act`）。`lines` 取最近 N 条（默认 100），`filter` 子串过滤（忽略大小写），`kind` 按源筛选；头部报告三源总量与显示行数、时间范围 |
| `dotnetdebugger_debug_output` | 查看被调试进程的控制台输出（stdout/stderr，旧→新；仅 launch 会话捕获，运行中可随时拉取） |
| `dotnetdebugger_debug_stack` / `dotnetdebugger_debug_variables` / `dotnetdebugger_debug_threads` | 读调用栈 / 局部变量 / 线程（进程停时）。`debug_variables` 支持 **`names` 按名白名单**（逗号分隔，空=全量；精确忽略大小写匹配局部/参数展示名——含 PDB 局部名、元数据参数名、无符号名的 `slotN`，与异常停点的 `$exception` 伪变量；未知名会在返回尾段列出当前帧可用名）；异常停点额外返回 `$exception` 当前异常对象：类型/Message/一级字段。`debug_stack` 每帧输出 `类型.方法 [token]`（解析失败降级为 `模块!token+ILoffset`；token 保留供下断点）；帧类型是编译器生成的 async 状态机（`Ns.X+<Foo>d__N`）时标注原方法：`Ns.X+<Foo>d__N (状态机 Foo).MoveNext` |
| `dotnetdebugger_debug_modules` | 列出当前调试会话已加载的模块（短名 + 磁盘路径）——确认目标程序集是否已加载，行/成员/源码行断点定位排障 |
| `dotnetdebugger_debug_evaluate` | 求值表达式读当前值（纯读、无副作用，进程停时）：成员访问 `a.b.c`、数组/字符串**任意下标** `a[i]`（引擎按路径直读，不受变量树一级 32 子项截断限制）、一元 `!`、单次比较（`== != < <= > >=`）、字面量 int/string/true/false/null。属性不可直接读——按 `X→_x→_X→<X>k__BackingField` 字段约定降级，未命中报错附可用字段清单；未知根名报错附可用变量清单；异常停点伪根 `$exception` 可直接求值（如 `$exception._message`、`$exception.InnerException`）。不支持算术/方法调用/赋值/链式比较/括号 |
| `dotnetdebugger_debug_object` | **对象结构受控递归下钻（D1）**：把已定位的对象/数组按 `depth` 层展开 children（默认 2，上限 6），每层字段/元素上限 `limit`（默认 32，范围 1-128），同路径环输出 `<cyclic>` 占位不再下钻——探索「对象里有什么」逐级展开，不盲猜路径。`path` 同 `debug_evaluate`（根=栈顶帧局部/参数名 + 字段/下标，支持 `$exception` 伪根如 `$exception.InnerException`）；标量/字符串/null 终值返回中文「不是对象/数组」提示。**与 `debug_evaluate` 分工：它取标量值，本工具看结构** |
| `dotnetdebugger_debug_set` | **停点现场改写（W1）**：把局部变量/参数/对象字段/数组元素改成给定值，返回「原值 → 新值」回显（防误判改写是否触及原因）。`path` 同 `debug_evaluate`（根=局部/参数名 + 字段/下标）；`value` 支持 `null`（引用置空）/ `true`/`false` / 数字（**无符号 `0x` 十六进制**，或**带符号十进制**整数/小数/科学计数，可带 `m/f/d` 后缀；小数/后缀按目标类型转换——浮点目标接受，整型拒小数/后缀，**decimal 目标 v1 不支持**（如 `order.Total`，中文降级提示）；**枚举仅底层 GenericValue 形态可写整数值，enum 对象字段 v1 降级**（如 `h.Kind`，中文降级提示））/ 同帧另一条对象路径（引用重定向如 `cfg.Backup`）。不支持改 readonly/const/静态字段、构造新对象、字符串内容（双引号/单引号文本不在文法内，char 用整数码点写）。改写敏感路径（如 `b.Password`）时「原值 → 新值」回显同样过 DB1 脱敏（原/新值替换为占位符）。**风险：写目标进程内存可能使其崩溃——只改确认的变量；目标引用当前为 null 时的重定向不做类型校验（无 deref 对象可比），需自行保证同型；改完 `debug_continue` 观察行为** |
| `dotnetdebugger_debug_exceptions` / `_clear` | first-chance 异常断点：按类型全名或短名（`.短名` 结尾，忽略大小写）过滤，不匹配的异常跳过并在 debug_wait/debug_state 提示跳过情况 / 清除 |
| `dotnetdebugger_debug_verify` | **一键复验（V1）**：读场景 JSON 文件（`target` 启动快照 + 可选 `build` 重编译 + `steps` 断言序列）自动执行到 **PASS/FAIL**——改完 bug 后自证修复的最后一跳。可选 `build` 先重编译工程（**产物自动拿取**：build 成功后按项目默认输出路径定位，不设置 OutputPath；`target.commandLine` 写工程入口 exe 文件名+参数，首段文件名须与编译产物同名），再启动并执行断点/continue/断言步骤（`breakpointHit`/`evaluate`/`output`/`state`/`noException`；另含 **`uiAction` 驱动 UI + `uiAssert` 断言 UI 状态** 的 U1A 闭环），断言失败即停（fail-fast）并返回失败步骤+目标输出尾部。无 `build` 时 `commandLine` 用完整路径/相对路径。场景格式与边界见 §使用示例「一键复验」 |
| `dotnetdebugger_web_open` | 打开 Web 调试监视器（幂等：已启动返回现有地址不重复启动；首次自动拉起默认浏览器） |
| `dotnetdebugger_debug_disconnect` | 断开调试会话（目标进程继续独立运行） |
| `dotnetdebugger_debug_terminate` | 终止调试会话的目标进程（强制结束，非正常退出）并关闭会话——调试/复验结束收口（`debug_disconnect` 只断开、进程继续跑） |

> 全部工具内置引擎，无需额外安装。除写盘外均支持 `lines` 分页；反编译类额外支持 `timeoutSeconds`（默认 30s）。
> 动态调试用法：`debug_launch`/`debug_attach` 建会话 → 断点四种下法：`debug_breakpoint_set`+token（`signature`/`decompile_member` 行尾取）、`typeName`+`memberName`（想断某类型里名字带 X 的方法，直接说方法名）、`typeName`+`line`（decompile 输出行号，看到哪行断哪行）、`sourcePath`+`line`（堆栈里的源文件行号，断案发现场）→ `debug_continue` 运行 → `debug_wait` 等停点（直接返回停点现场，免轮询，默认附目标最近控制台输出）；停后 `debug_stack`/`debug_variables` 观察（帧变量多/只想看某几个时 `debug_variables names="i,order"` 按名白名单读取——逗号分隔、忽略大小写、空=全量；命中对象照常逐字段脱敏，未知名会列出当前帧可用名）、`debug_evaluate` 求值深层表达式（`order.Customer.Name`、`list._items[50]`、`i == retryCount`，纯读无副作用）、**想知道对象里有什么再逐级下钻用 `debug_object`**（`debug_object "order.Customer" depth=3`——把对象/数组按 depth 层展开 children 树，同路径环自动标 `<cyclic>`、`limit` 控每层宽度；与 `debug_evaluate` 分工：它取标量值，本工具看结构）、**「改值验证假设再继续」用 `debug_set`**（把现场变量改成新值，返回 原值→新值 回显，改完 `debug_continue` 观察行为是否变化——二分定位因果实验；注意写进程内存有崩目标风险，只改确认的变量）、`debug_step` 单步、`debug_disconnect` 断开（目标继续运行——调试/复验结束要结束目标进程用 `debug_terminate`）。**想「让进程直接跑到某处再停下看现场」用 `debug_run_to`**（目标定位同 `typeName`+`line` / `typeName`+`memberName`，命中自动移除临时断点——对标 VS 运行到光标处）。目标进程的控制台输出（stdout/stderr）随 launch 自动捕获，`debug_output` 随时拉取（attach 附加的会话不捕获）。**复盘整段调试经过（目标日志 ↔ 断点/异常/trace 事件 ↔ agent 动作按时间对齐）用 `debug_timeline`**。**改完源码想「重编译→重启→重跑关键路径→断言结果」一次自证修复，用 `debug_verify`**（读场景 JSON 文件：`target` 启动快照 + 可选 `build` 重编译（产物自动拿取）+ `steps` 断言序列，执行到 PASS/FAIL、断言失败即停——见使用示例「一键复验闭环」）。控制工具异步返回；等停点用 `debug_wait`（超时返回当前状态，不报错），停点快照也可随时经 `debug_state` 查询。**读值输出对疑似凭据自动脱敏**：`debug_variables`/`debug_evaluate`/trace 轨迹的变量值若按变量名（api key/password/token/credential/auth/连接串等，归一化 exact-match）或按值内容形态（JWT/PEM/Bearer/`Key=…` 等）判定像凭据，输出替换为 `[已脱敏:疑似凭据]` 占位符并附提示——请用该出口提供的类型/长度/null 等非敏感信息判断，勿读原始值；null/平凡值不动（「token 是 null」照常可调），子串不误伤（`tokenCount` 等照常可读）。**目标自起子进程（Web/服务类目标把业务代码跑在子进程，如 dotnet run 起的 app 再 spawn worker/testhost）时**：`debug_processes` 会把当前会话目标的 .NET 子孙进程链标注出来（行尾「← 会话目标(X) 的子进程/第N代孙进程（父 Y）」+ 尾部切换引导）——单活动会话下需先停当前会话（`debug_disconnect`/停断点）再 `debug_attach <childPid>` 单独调试子进程；子进程自己的控制台输出不在 launch 的 `debug_output` 捕获范围。

### UI 自动化（`ui_find` / `ui_action` / `ui_input` / `ui_get` / `ui_wait`——全 UIA 语义，真实操作运行中的 .NET UI 应用）

| 工具 | 用途 |
| ---- | ---- |
| `dotnetdebugger_ui_find` | 按 **进程（pid 或进程名）+ 窗口/控件条件**列出 UI 控件（**无视觉**——文本清单：`index/Name/Type/AutoId/patterns=能力清单/Rect` + **同名成员语义候选**）。`patterns=` 只列该控件真实支持的能力（invoke/toggle/selectionitem/expandcollapse/value/rangevalue/scroll/scrollitem/window/legacy），供选择 verb。`text`=控件 Name 子串忽略大小写（同时匹配 AutomationId）、`automationId` 精确、`type`=UIA 类型名（Button/Text/Edit/List/ListItem/CheckBox/Window…，TextBlock→Text、TextBox→Edit、ListBox→List 别名自动归一）；`title`=窗口标题精确匹配，空=该进程首个顶层窗口；`limit`=条数上限。返回的 **index 供 `ui_action`/`ui_input`/`ui_get` 复用**；语义候选可用 `decompile_member` 看成员实现 |
| `dotnetdebugger_ui_action` | 对控件执行**语义动作**（真实操作，有产线副作用）：`verb` ∈ `invoke`（默认动作）/`toggle`（可勾选）/`select`（列表·树·页签项）/`expand`/`collapse`/`focus`（仅控件）/`scroll`（需 `direction=up/down`+`lines`）/`scrollintoview`/`windowstate`（`normal/maximized/minimized`，按进程定位顶层窗口、忽略 index/name/type）。**按控件能力分派 pattern，而非固定「点一下」**——复选框用 `toggle`、列表项用 `select`、下拉/树节点用 `expand`。**无右键/双击**：UIA 无该类入口、物理输入已移除，传 `rightclick`/`doubleclick` 返回明确中文拒绝并引导改用 `verb=invoke`。`index`=上次 ui_find 序号（优先，pid 不匹配/越界会提示重新 ui_find）；或 `name`/`type` 即时唯一定位（歧义返回候选清单）。返回注明实际命中的 pattern（如「已 select（SelectionItemPattern）」），操作写入 AgentActionLog |
| `dotnetdebugger_ui_input` | 对控件**写值**（真实操作，有产线副作用）：按 `ValuePattern` → `RangeValuePattern` → `LegacyIAccessiblePattern` 分派（文本框写文本、滑块/数值框写数字）；只读控件或无写值能力返回中文提示。定位同 `ui_action`。操作写入 AgentActionLog |
| `dotnetdebugger_ui_get` | **读取控件状态（只读、无副作用）**：`what` ∈ `value`（优先 ValuePattern，无则 LegacyIAccessible）/`name`/`toggle`/`selected`/`expandstate`/`rangevalue`/`enabled`/`offscreen`/`rect`/`helptext`；无对应 pattern 返回中文提示。定位同 `ui_action`。读出的值展示前按控件 Name/AutoId 走 DB1 敏感脱敏（命中凭据规则替换为 `[已脱敏:疑似凭据]`） |
| `dotnetdebugger_ui_wait` | **事件化**等待 UI 状态变化/控件出现（只读、无副作用）：对目标窗口订阅结构/属性变化事件，命中即返回；无事件时每 200ms 轮询兜底。`text`=期望出现的控件文本（Name 子串忽略大小写）；或 `textChangedFrom`+`textChangedTo` **成对**等控件文本从 X 变 Y（动作后的状态确认，如 手动→自动）。`timeoutSeconds` 默认 30（1-300）；**超时返回当前状态提示、不报错** |

> UI 自动化用法（**不需活动 debug 会话**，可先操作 UI 到某状态再 attach；需目标进程已运行且是 .NET UI 应用）：`ui_find`（按进程/类型/文本定位控件，拿 index/patterns 能力清单/语义候选）→ 预埋断点：`debug_breakpoint_set`（想断的成员如切换处理函数 `typeName`+`memberName`，语义候选可直接用）→ `ui_action index=N verb=invoke`（按语义触发，业务代码自然执行）→ `debug_wait`（断点命中看现场：`debug_stack`/`debug_variables`/`debug_evaluate`）→ `ui_wait textChangedFrom=X textChangedTo=Y`（状态变更二次确认）。**语义标注**：ui_find 命中控件时对 Name/AutomationId 做**同名成员反查**（类型全名.成员候选，如按钮 AutoId=toggleState → `MainForm.OnToggleState`——反查的是反编译元数据同名成员，非 XAML 绑定还原）；无候选请手动 `decompile_member` 查。**全 UIA 语义、无物理输入**：不移动真实光标、不注入鼠标/键盘、不抢前台（仅窗口最小化时经 WindowPattern 还原；`focus` 对带 HWND 控件可能激活顶层窗口）。`ui_action`/`ui_input` 是**真实产线操作**（改状态/点按钮/写值），全部写入 AgentActionLog 供复盘；个别自绘控件无任何 pattern 时明确「不支持」（UIA-only 的硬边界）。

## 命令行调试

MCP 模式外，`DotNetDebuggerMcp` 可直接以命令行执行同等功能，便于本地调试。参数与 MCP 工具一一对应。

### Web 调试展示面（web_open / --web）

宿主可按需启动一个网页调试展示面（Blazor Server，内嵌 Kestrel），把反编译与调试可视化。两条入口收敛到同一幂等启动（进程内只起一个 Kestrel，混用不重复启动）：

```bash
DotNetDebuggerMcp --web                                  # 手动模式：启动即开（自动选空闲端口并拉起默认浏览器）
DotNetDebuggerMcp --web --web-port 8090                  # 指定端口（不自动拉起需手动开 http://127.0.0.1:8090）
```

- **agent 按需开启（推荐）**：MCP server 默认不带 `--web`；agent 调 `web_open` 工具按需打开（缺省 0 = 自动选空闲端口，可传 `port` 指定），首次启动自动拉起默认浏览器，重复调用幂等返回同一地址。
- **页面功能**：反编译代码视图（Monaco 编辑器，断点/当前执行行装饰）+ 动态调试面板（调用栈/局部变量/线程）+ 最小控制（启动并附加/断点/继续/单步/断开）+ agent 操作时间线。
- **双模式**：单独跑 `--web` 时页面可人工 launch/attach 目标调试；MCP server 起来后（`--web` 或 `web_open`）与 agent 共享同一调试会话——agent 经 `debug_*` 工具调试，浏览器实时观看（断点命中代码高亮、面板随停点刷新）。
- 调试目标进程以静默窗口运行（不弹控制台框）。

### 反编译

```bash
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.Program                          # dotnetdebugger_decompile
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.Program -mn Main                  # dotnetdebugger_decompile_member 按名
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -mn Main                                   # 跨程序集按名（省略 -t）
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -tt 0x02000004 -mn Main                    # 按类型 token 消歧后搜成员
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -o src                                     # dotnetdebugger_decompile_to_dir 全量
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -o src -t "MyApp.IWorker,MyApp.Worker"     # 批量写盘多类型
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -o src -p --nested-directories             # dotnetdebugger_decompile_to_project
```

> `dotnetdebugger_decompile_il`（方法体 IL 反汇编）当前仅 MCP 工具可用、无命令行开关——需在 MCP 客户端内按方法 token 调用（agent 场景）。

### 结构探测

```bash
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -ai                                        # dotnetdebugger_assembly_info
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -l csi                                     # dotnetdebugger_list_types
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -l c -nc Box -ns MyApp.Core                 # 名称/命名空间过滤
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.Program -s                        # dotnetdebugger_signature
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.Program -hc [-i]                  # dotnetdebugger_hierarchy (-i 含间接后代)
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.Program -d [-x]                   # dotnetdebugger_dependencies (-x 含外部)
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.Program -cg [-x]                  # dotnetdebugger_call_graph
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -cg -tk 0x06000005                         # 按方法 token 反向定位调用者
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.IWorker -iu [-i]                  # dotnetdebugger_interface_usage
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.GenericBox -gi                    # dotnetdebugger_generic_instantiations
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -ss "配置Key" [-t MyApp.Program]           # dotnetdebugger_search_string
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -fa -t MyApp.Program -fn _count            # dotnetdebugger_field_access 按名
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -fa -tk 0x04000005                         # 按字段 token
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -t MyApp.Program -mn Parse -cc [-x]        # dotnetdebugger_call_chain
DotNetDebuggerMcp -a bin/Debug/MyApp.dll -cc -tk 0x06000010                         # 按方法 token 定位起始方法
```

### 通用参数

| 短参 | 长参 | 说明 |
| ---- | ---- | ---- |
| `-a` | `--assembly` | 程序集路径（.dll/.exe），相对 CWD 解析 |
| `-t` | `--type` | 类型全名 |
| `-mn` | `--membername` | 成员名子串（忽略大小写） |
| `-tt` | `--typetoken` | 类型 token（`0x02` 开头），用于 `typeName` 歧义消歧 |
| `-tk` | `--token` | 方法/字段 token（方法 `0x06` / 字段 `0x04`），精确定位 |
| `-s` | `--signatures` | 成员签名（需 `-t`） |
| `-hc` | `--hierarchy` | 继承/接口关系（需 `-t`） |
| `-d` | `--dependencies` | 内部引用（需 `-t`） |
| `-cg` | `--callgraph` | 方法体调用关系 |
| `-iu` | `--interfaceusage` | 接口使用情况（需 `-t` 为接口） |
| `-gi` | `--genericinstantiations` | 泛型实例化使用点（需 `-t`） |
| `-cc` | `--callchain` | 调用序列 + 被调成员反编译 |
| `-ai` | `--assembly-info` | 程序集概览 |
| `-l` | `--list` | 类型类别（c/i/s/d/e 可组合） |
| `-nc` | `--namecontains` | 类型名子串过滤（配合 `-l`） |
| `-ns` | `--namespacecontains` | 命名空间子串过滤（配合 `-l`） |
| `-ss` | `--searchstring` | 字符串字面量子串反查 |
| `-fa` | `--fieldaccess` | 字段读写点追踪 |
| `-fn` | *(field name)* | 字段名（配合 `-fa`） |
| `-o` | `--outputdir` | 输出目录 |
| `-p` | `--project` | 项目形式（配合 `-o`） |
| | `--nested-directories` | 按命名空间嵌套目录（仅 `-p`） |
| `-i` | `--indirect` | 含全部间接后代/实现者（配合 `-hc`/`-iu`） |
| `-x` | `--external` | 同时输出/展开跨程序集外部类型（配合 `-d`/`-cg`/`-cc`） |
| `-ln` | `--lines` | 行号分页 `start-end` |
| | `--timeout` | 超时秒数（默认 30） |
| `-c` | `--check` | 检查 DotNetDebuggerMcp 新版本（无需 `-a`） |
| | `--web` | 启动时同时开启网页调试展示面（Blazor Server；与 `web_open` 工具同一幂等入口，无 MCP 会话时页面可人工调试） |
| | `--web-port <port>` | Web 端口（配合 `--web` 与 `web_open` 的缺省端口；缺省 0 = 自动选空闲端口并拉起默认浏览器） |
| `-v` | `--version` | 版本号 |
| `-h` | `--help` | 帮助 |

## 工具参数

### `dotnetdebugger_decompile`

| 参数 | 说明 | 必填 | 默认 |
| ---- | ---- | ---- | ---- |
| `assembly` | 目标程序集路径 | 是 | — |
| `typeName` | 类型全名 | 是 | — |
| `lines` | 行号范围 `start-end`，缺省前约 8 KB | 否 | — |
| `timeoutSeconds` | 超时秒数 | 否 | 30 |

### `dotnetdebugger_decompile_member`

| 参数 | 说明 | 必填 | 默认 |
| ---- | ---- | ---- | ---- |
| `assembly` | 目标程序集路径 | 是 | — |
| `typeName` | 限定类型内搜索；省略则跨程序集（提供 `token` 时可不填） | 否 | — |
| `memberName` | 成员名子串，忽略大小写（提供 `token` 时可不填） | 否 | — |
| `token` | 成员 token（如 `0x06000005`），提供时忽略 `memberName` | 否 | — |
| `typeToken` | 类型 token（`0x02` 开头），用于歧义消歧 | 否 | — |
| `lines` | 行号范围 | 否 | — |
| `timeoutSeconds` | 超时秒数 | 否 | 30 |

多匹配合并输出、各成员前 `#MEMBER {"name","token","type"}` 分隔行；超过 20 个仅返回签名清单；无匹配时附相近成员名。

### `dotnetdebugger_decompile_il`

| 参数 | 说明 | 必填 | 默认 |
| ---- | ---- | ---- | ---- |
| `assembly` | 目标程序集路径 | 是 | — |
| `token` | 方法定义 token（`0x06` 开头，如 `0x06000005`） | 是 | — |
| `lines` | 行号范围 | 否 | — |
| `timeoutSeconds` | 超时秒数 | 否 | 30 |

按方法 token 反汇编方法体为 IL 文本（ILSpy 风格：头部注释 RVA/Header size/Code size、`.maxstack`/`.locals`、结构化 `.try`/`catch`/`finally`/循环块、每条指令行首 `IL_xxxx` 偏移标签）。行号体系独立于 C# 反编译视图（不与 `decompile`/`decompile_member` 行号对齐）。字段/属性/事件 token（非 `0x06`）返回「不是方法定义」提示。

### `dotnetdebugger_decompile_to_dir` / `dotnetdebugger_decompile_to_project`

| 参数 | 说明 | 必填 | 默认 |
| ---- | ---- | ---- | ---- |
| `assembly` | 目标程序集路径 | 是 | — |
| `outputDir` | 输出目录 | 是 | — |
| `typeName` | （仅 to_dir）限定类型，逗号分隔批量写盘；省略=全量 | 否 | 空=全量 |
| `nestedDirectories` | （仅 to_project）按命名空间嵌套目录 | 否 | true |
| `timeoutSeconds` | 超时秒数 | 否 | 30 |

### `dotnetdebugger_list_types`

| 参数 | 说明 | 必填 | 默认 |
| ---- | ---- | ---- | ---- |
| `assembly` | 目标程序集路径 | 是 | — |
| `categories` | 类别：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 `csi` | 是 | — |
| `nameContains` | 类型名子串过滤，忽略大小写 | 否 | 空=不过滤 |
| `namespaceContains` | 命名空间子串过滤，忽略大小写 | 否 | 空=不过滤 |
| `lines` | 行号范围 | 否 | — |

### `dotnetdebugger_signature`

| 参数 | 说明 | 必填 | 默认 |
| ---- | ---- | ---- | ---- |
| `assembly` | 目标程序集路径 | 是 | — |
| `typeName` | 类型全名 | 是 | — |
| `lines` | 行号范围 | 否 | — |

每行一成员签名，行尾附 token（`0x06`/`0x04`/`0x17`/`0x14`），可直接用于 `decompile_member`（`0x06` 方法 token 亦可用于 `decompile_il`）；未找到时附相近类型名。

### `dotnetdebugger_hierarchy` / `dotnetdebugger_interface_usage` / `dotnetdebugger_generic_instantiations`

| 工具 | 参数 | 说明 |
| ---- | ---- | ---- |
| `dotnetdebugger_hierarchy` | `assembly` + `typeName` 必填，`includeIndirect` 默认 false，`lines` | 基类链/接口/继承实现者；`includeIndirect` 含全部间接后代 |
| `dotnetdebugger_interface_usage` | `assembly` + `typeName`（须为接口）必填，`includeIndirect` 默认 false，`lines` | 实现者 + 调用点 + 签名引用组合视图；非接口提示用 `hierarchy` |
| `dotnetdebugger_generic_instantiations` | `assembly` + `typeName` 必填，`lines` | 签名中 / 方法体中的两段实例化；`typeName` 可带/不带 `` `1``，短名亦命中 |

### `dotnetdebugger_dependencies` / `dotnetdebugger_call_graph`

| 工具 | 参数 | 说明 |
| ---- | ---- | ---- |
| `dotnetdebugger_dependencies` | `assembly` + `typeName` 必填，`includeExternal` 默认 false，`lines` | 签名引用内部类型及反向引用；外部格式 `全名 [程序集名]` |
| `dotnetdebugger_call_graph` | `assembly` 必填，`typeName`（`token` 模式可不填）+ `token`/`includeExternal`/`lines` | 类型级双向调用；`token` 模式反向输出 `类型::成员` 调用点行 |

### `dotnetdebugger_search_string` / `dotnetdebugger_field_access` / `dotnetdebugger_assembly_info` / `dotnetdebugger_cache_stats`

| 工具 | 参数 | 说明 |
| ---- | ---- | ---- |
| `dotnetdebugger_search_string` | `assembly` + `search` 必填，`typeName?` + `includeCompilerGenerated?` + `lines` | 字符串字面量反查，忽略大小写；输出 `类型::成员 字符串值 token`。默认跳过编译器生成类型；`includeCompilerGenerated=true` 含 async 状态机/lambda 闭包里的字面量 |
| `dotnetdebugger_field_access` | `assembly` 必填，`typeName?` + `fieldName`/`fieldToken` + `lines` | 读/写/取地址三段来源，空段 `（无）`；`fieldToken` 为 `0x04` 开头 |
| `dotnetdebugger_assembly_info` | `assembly` 必填，`lines` | 程序集概览（元数据秒回） |
| `dotnetdebugger_cache_stats` | 仅 `lines` | 无 `assembly` 参数；按占用降序列条目 |

> 以上未找到类型时均附相近类型名提示；除 `cache_stats` 外结果均可缓存，命中时头部标注。

### `dotnetdebugger_call_chain`

| 参数 | 说明 | 必填 | 默认 |
| ---- | ---- | ---- | ---- |
| `assembly` | 目标程序集路径 | 是 | — |
| `typeName` | 起始方法所属类型（提供 `token` 时可不填） | 否 | — |
| `memberName` | 起始方法名子串，忽略大小写（多匹配先返 `#MEMBER` 清单） | 否 | — |
| `token` | 起始方法 token，提供时忽略 `memberName` | 否 | — |
| `includeExternal` | 保留并展开跨程序集外部调用 | 否 | false |
| `lines` | 行号范围 | 否 | — |
| `timeoutSeconds` | 超时秒数 | 否 | 30 |

序列行带内部成员 token；被调内部成员超 20 个仅返签名清单。

### 动态调试工具（`dotnetdebugger_debug_launch` / `debug_state` / `debug_output` / `debug_run_to` / `debug_processes` 等）

| 工具 | 参数 | 说明 |
| ---- | ---- | ---- |
| `debug_launch` | `commandLine`（必填） | 目标可执行文件路径（可含参数，如 `DebugTarget.exe 3 8`） |
| | `timeoutSeconds` | 等待目标 CLR 启动秒数上限，默认 30 |
| | `workingDirectory` | 目标进程工作目录，默认空=目标 exe 所在目录（对齐手动启动 exe，目标产物写自己目录）。可显式传目录覆盖；Web 应用等以工作目录定位 appsettings/静态资源的目标应传其 bin 或 publish 目录 |
| | `environment` | 附加环境变量，KEY=VALUE 多行或分号分隔（如 `ASPNETCORE_ENVIRONMENT=Development;MY_KEY=1`），默认空=继承 server 环境 |
| `debug_attach` | `processId`（必填） | 目标进程 id（用 `debug_processes` 查） |
| `debug_state` | `contextLines` | 停点上下文行数预算，默认 100，0=不附。返回含目标 pid、会话状态（Attaching=进程冻结在 Main 前可设断点后 continue 放行）、最近停点；进程已退出时附退出码（launch 会话）；attach 会话且进程在跑时提示「已附加成功、进程独立运行」 |
| `debug_timeline` | `lines` / `filter` / `kind` | 取最近 N 条（默认 100，范围 1-1000）/ 子串过滤（忽略大小写）/ 按源筛选：all/log/act/bp/step/exc/skip/trc/state/engine。返回三源统一时间行（`[HH:mm:ss.fff] tag 内容`）与头部总量/时间范围 |
| `debug_step` | `stepType` | into/over/out，默认 over。返回「已提交 step 命令」——用 `debug_wait` 等停（通常瞬时）或 `debug_state` 确认 Stopped。停在编译器生成的 async 状态机帧时返回附行断点引导（断还原源码的 await 行） |
| `debug_output` | `lines` | 返回最近行数，默认 50，范围 1-2000 |
| | `filter` | 只返回含该子串的行（忽略大小写），默认空=全部。高频日志下筛关键行（如 `filter=Now listening` / `filter=error`） |
| `debug_wait` | `waitSeconds` / `outputLines` / `outputFilter` / `contextLines` | 等停秒数（默认 10）/ 附输出行数（默认 20，0=不附）/ 附输出只留含该子串的行 / 停点上下文预算 |
| `debug_processes` | `filter` | 进程名子串过滤，忽略大小写；缺省空=全部。进程名排序，当前会话目标行标注「← 当前会话」，超 100 条截断提示用 filter。存在当前会话目标时，其 .NET 子孙进程（dbgshim 探测到 CLR 的子/孙进程链）行尾标注「← 会话目标(X) 的子进程/第N代孙进程（父 Y）」，返回尾部附切换引导——**单活动会话**：业务代码跑在子进程时需停当前会话（debug_disconnect/停断点）后 `debug_attach <childPid>` 单独调试；子进程自身输出不在 debug_output 捕获范围（launch 只捕获父进程 stdout/stderr） |
| `debug_breakpoint_set` | `moduleName`+`methodToken`+`ilOffset` / `typeName`+`memberName` / `typeName`+`line` / `sourcePath`+`line` | 四种定位方式；`typeName`+`memberName` 按类型内方法名子串定位（唯一命中即设断点，多方法匹配返回 `#MEMBER` 清单，属性/事件/字段成员提示）；`sourcePath`+`line` 模块未加载/未命中时登记延迟项（模块加载后自动按 PDB 解析绑定）；可选 `hitCount`、`mode`（stop/trace）、`condition`（P6 子集表达式，为真才停/记）；同一 `(模块, token, IL offset)` 重复设置会替换旧断点（不产生同址多断点）。token 方式的 `ilOffset` 须落在 IL 指令边界（否则中文提示须用视图行或入口 0） |
| `debug_run_to` | `typeName`+`line` / `typeName`+`memberName` | 运行到目标位置后停下（对标 VS Run to Cursor）：设一次性临时断点 + 继续运行，命中即停且**自动移除**临时断点。`typeName` 必填，`line`（decompile 输出行号）或 `memberName` 二选一，`moduleName` 可省；`timeoutSeconds` 等待命中上限（默认 30）。超时/命中其它断点/进程退出都返回提示并清理临时断点。只对「目标会被自然执行到」有效 |
| `debug_object` | `path`（必填）+ `depth` + `limit` + `threadId` | **对象结构受控递归下钻（D1）**：`path` 同 `debug_evaluate`（如 `order.Customer`、`$exception.InnerException`，根=栈顶帧局部/参数名 + 字段/下标）；`depth` 递归层数（默认 2，范围 1-6）、`limit` 每层字段/元素上限（默认 32，范围 1-128）；同路径环输出 `<cyclic>` 占位不再下钻；标量/字符串/null 目标返回中文「不是对象/数组」提示。缺省 `threadId=0` 用最近停点线程 |
| `debug_set` | `path`（必填）+ `value`（必填）+ `threadId` | **停点现场改写**：`path` 同 `debug_evaluate`（如 `scores[2]`、`b.A`、`i`、`cfg.Current`，根=栈顶帧局部/参数名，缺省 `threadId=0` 用最近停点线程）；`value` 支持 `null`（引用置空）/ `true`/`false` / 数字（**无符号 `0x` 十六进制**或**带符号十进制**整数/小数/科学计数，可带 `m/f/d` 后缀——按目标类型转换：浮点接受小数/后缀、整型拒后缀、**decimal 目标 v1 不支持**（中文降级提示）；**枚举仅底层 GenericValue 形态可写整数值，enum 对象字段 v1 降级**）/ 同帧对象路径（引用重定向，如 `cfg.Backup`）。返回「原值 → 新值」回显（敏感路径的原值/新值同样经 DB1 脱敏为 `[已脱敏:疑似凭据]`）。不支持改 readonly/const/静态字段、构造新对象、字符串内容（双引号/单引号文本不在文法内，char 用整数码点写）。**风险：写目标进程内存可能使其崩溃，只改确认的变量；目标引用为 null 时的重定向无类型校验，须保证同型** |
| `debug_variables` | `names` + `threadId` | **按名白名单读取（DB2）**：`names` 逗号分隔白名单（空=全量），精确忽略大小写匹配局部/参数展示名——含 PDB 局部名、元数据参数名、无符号名的 `slotN`、异常停点 `$exception` 伪变量；同名跨作用域（locals/arguments 分节）都返回。**最多 50 项**，超限中文拒绝；白名单未知名在返回尾段列出当前帧可用名（零值反馈不静默）。头部注明白名单命中数；命中值照常走 DB1 敏感脱敏。缺省 `threadId=0` 用最近停点线程 |
| `debug_modules` | — | 列出当前调试会话已加载的模块（短名 + 磁盘路径）；确认目标程序集是否已加载（行/成员/源码行断点需模块已加载） |
| `debug_continue` / `debug_disconnect` / `debug_terminate` | `debug_terminate` 可选 `exitCode` | 继续执行（异步返回，停点后 `debug_state` 确认）/ 断开会话（目标继续独立运行）/ 终止目标进程（强制结束并关闭会话，调试/复验收口用） |
| `debug_verify` | `scenarioPath`（必填） | **一键复验（V1）**：场景 JSON 文件路径——含 `target`（启动快照）+ 可选 `build` + `steps`。build 分支自动 `dotnet build`（默认输出、失败绝不启动旧产物）并 `-getProperty:TargetPath` 拿产物启动；断言原语 kind：`breakpointHit`（`breakpointIndex` 0-based 引场景内第 N 个 breakpoint 步骤）/`evaluate`（`path`+`equals`/`contains` 互斥）/`output`（`contains`，`stream`=out/err）/`state`（`expect`）/`noException`；U1A 步骤：`uiAction`（驱动 UI 语义动作/写值）、`uiAssert`（`what`+`equals`/`contains` 断言 UI 状态，失败附期望 vs 实际、实际值经 DB1 脱敏）；`ui`/`set` 步骤预留（依赖 U1/W1，运行时报未就绪）。返回 PASS/FAIL 与失败步骤上下文 |

### UI 自动化工具参数（`ui_find` / `ui_action` / `ui_input` / `ui_get` / `ui_wait`）

| 工具 | 参数 | 说明 |
| ---- | ---- | ---- |
| `ui_find` | `process`（必填）+ `title` + `text` + `type` + `automationId` + `limit` | `process`=pid 或进程名子串（空返回提示）；`title`=窗口标题精确匹配，空=该进程首个顶层窗口；`text`=控件 Name 子串忽略大小写（也匹配 AutomationId）；`type`=UIA 类型名（TextBlock→Text 等别名自动归一）；`automationId`=精确匹配；`limit` 返回上限（默认 50，1-500）。返回 `[index] Type Name=… AutoId=… patterns=… Rect=(x,y w x h)` 行 + 同名成员语义候选 |
| `ui_action` | `process`（必填）+ `verb`（必填）+ `index` + `name` + `type` + `direction` + `lines` + `windowstate` | `verb`=invoke/toggle/select/expand/collapse/focus/scroll/scrollintoview/windowstate；`index`=上次 ui_find 序号（默认 -1=用 name/type）；`name`/`type`=即时唯一·歧义回候选；`direction`=up/down（scroll 用）；`lines` 行数（scroll 用，默认 3，1-100）；`windowstate`=normal/maximized/minimized（按进程定位顶层窗口，忽略 index/name/type）。**无右键/双击**（传 rightclick/doubleclick 明确中文拒绝）。返回注明命中的 pattern；写 AgentActionLog |
| `ui_input` | `process`（必填）+ `value`（必填）+ `index` + `name` + `type` | 按 Value→RangeValue→LegacyIAccessible 写值；定位同 ui_action。只读/无 pattern 中文提示；写 AgentActionLog |
| `ui_get` | `process`（必填）+ `what`（必填）+ `index` + `name` + `type` | `what`=value/name/toggle/selected/expandstate/rangevalue/enabled/offscreen/rect/helptext；定位同 ui_action。读值展示前经 DB1 敏感脱敏 |
| `ui_wait` | `process`（必填）+ `text` + `type` + `textChangedFrom` + `textChangedTo` + `timeoutSeconds` | `text` 与 `textChangedFrom/To` 二选一（前者等控件文本出现，后者等文本从 X 变 Y，可配 `type` 限定）；事件化 + 200ms 轮询兜底；`timeoutSeconds` 最长等待（默认 30，1-300）。命中返回 出现/已变化；超时返回当前状态提示、不报错 |

> 输出捕获仅 `debug_launch` 会话可用（attach 已运行进程无法重定向）；缓冲保留最近 2000 行，被高频日志淹没时用 `filter` 筛关键行。

## 使用示例

在 opencode 对话中直接提出：

**初探程序集**

- > 查看 `bin/Debug/MyApp.dll` 的程序集概览，先摸清引用与类型构成
- > 列出 `bin/Debug/MyApp.dll` 中的所有 class / `csi` 三类

**定位类型与成员**

- > 反编译 `MyApp.Program` / 搜索 `Main` 成员并反编译 / 跨程序集搜索 `Parse`
- > 列出 `MyApp.Program` 的成员签名（API 地图），取行尾 token 反编译单个成员
- > 用 `signature`/`#MEMBER` 行尾的 `0x06` token 反汇编某个方法的方法体 IL（`dotnetdebugger_decompile_il`——async 状态机等编译器生成类型 C# 反编译失败/难读时的兜底）
- > 按行拉取 `MyApp.Program` 第 200-400 行

**继承与引用**

- > 查看 `MyApp.Program` 的基类链、接口与继承者（含间接后代 `includeIndirect=true`）
- > 查询 `MyApp.Program` 的签名引用与反向引用（含外部 `includeExternal=true`）
- > 查询 `MyApp.IWorker` 的实现者与调用点（含间接实现者）

**调用关系**

- > 查询 `MyApp.Program` 的方法体调用了哪些类型 / 哪些类型调用了它（含外部）
- > 查询哪些方法调用了 `MyApp.Program.Parse`（先 `signature` 取 token，再 `dotnetdebugger_call_graph token=...`）
- > 追踪 `MyApp.Program.Parse` 的正向调用序列并反编译被调成员（`dotnetdebugger_call_chain`，`includeExternal=true` 展开同目录/NuGet 可解析的外部调用）

**搜索与字段**

- > 按字符串 `配置Key` / `order by` 反查成员（可限 `typeName`）
- > 追踪字段 `_count` 的读写点（`fieldName` 或先 `signature` 取 `0x04…` 用 `fieldToken`）
- > 查询泛型 `GenericBox` 的具体实例化使用点（`GenericBox` 或 ``GenericBox`1`` 均可）

**写盘与缓存**

- > 反编译 `bin/Debug/MyApp.dll` 到 `src`（全量 / 指定多类型 / 项目形式嵌套目录）
- > 查看缓存占用与命中率（`dotnetdebugger_cache_stats`）

**动态调试：停点改值闭环（W1 debug_set）**

- > 启动 `DebugTarget.exe` 并断点停在某方法入口 → `dotnetdebugger_debug_set` `path=h.N` `value=99` → 返回「路径 h.N 已改：原值 5 → 新值 99」→ `debug_continue` 观察输出出现 `N=99`——「把 X 改成 Y 再跑，看是否复现/消失」的二分定位实验闭环
- > 引用重定向：`debug_set path=cfg.Current value=cfg.Backup`（指向同帧另一对象）；引用置空：`debug_set path=cfg.Current value=null`。**改值有崩目标进程风险，先 `debug_evaluate` 复核当前值再改，改完 `debug_continue` 观察**。边界提醒：数字用无符号 `0x` 十六进制或带符号十进制；小数/后缀适用于 float/double 目标（整型拒小数/后缀）；**decimal 字段目标 v1 不支持写**（如 `order.Total`，引擎给中文降级提示）；枚举仅底层 GenericValue 形态可写整数值、enum 对象字段 v1 降级；敏感路径改写时「原值 → 新值」回显同受 DB1 脱敏（原/新值替换为 `[已脱敏:疑似凭据]`）；目标引用当前为 null 时重定向无类型校验（无 deref 可比对），须保证源与目标同型

**UI 自动化驱动业务操作（U1A ui_*，全 UIA 语义、无物理输入）**

- > 目标程序（如 CoreMes/被测 WinForms）已开着，先在切换按钮 `ui_find process=CoreMes text=手动 type=Button` → 拿到行 `[3] Button Name=手动 AutoId=toggleState patterns=invoke,legacy …` 与语义候选（如 `MainViewModel.SwitchAutoStateCommand` 相关同名成员，可用 `decompile_member` 看实现）→ 想断点停在切换代码里：`debug_attach` 附加后用 `debug_breakpoint_set typeName=… memberName=…` 预埋断点 → `ui_action process=CoreMes index=3 verb=invoke`（语义触发，业务代码自然执行；产线副作用，打 AgentActionLog）→ `debug_wait` 等断点命中看 `debug_stack`/`debug_variables` 现场 → `ui_wait process=CoreMes textChangedFrom=手动 textChangedTo=自动` 确认状态已切（业务闭环）。
- > 按控件能力选动词：复选框 `ui_action verb=toggle`、列表/树/页签项 `verb=select`、下拉/树节点 `verb=expand`/`collapse`、滚动容器 `verb=scroll direction=down lines=5`、屏外项 `verb=scrollintoview`、写文本框/滑块 `ui_input value=…`、读状态 `ui_get what=value/toggle/selected/…`。**无右键/双击**：UIA 无该类入口且物理输入已移除，传 `rightclick`/`doubleclick` 会得到明确中文拒绝——请让目标提供等价的菜单/命令入口再 `verb=invoke`。全 UIA 语义**不移动光标、不注入输入、不抢前台**（仅最小化窗口经 WindowPattern 还原），断开/非交互桌面也能用（只要目标窗口在 UIA 树里）。

**一键复验闭环（V1 debug_verify）——改完 bug 自证修复**

> 把「启动参数快照 + 场景动作 + 期望断言」写成场景 JSON 文件，`debug_verify <文件>` 一条命令跑成 PASS/FAIL：可选先重编译（产物自动拿取），再启动目标、下断点、continue、断言（断点命中/表达式值/输出/状态/无异常），断言失败即停。场景格式（完整示例）：

```jsonc
{
  "name": "Work 计算复验",
  "target": { "commandLine": "CoreMes.exe 3 0" },           // 启动快照（debug_launch 同参）
  "build": { "project": "D:\\proj\\CoreMes\\CoreMes.csproj", "configuration": "Debug" },  // 可选：先重编译
  "steps": [
    { "breakpoint": { "typeName": "CoreMes.Core.Worker", "memberName": "Run", "hit": 1 } },
    { "continue": { "waitSeconds": 30 } },
    { "assert": { "kind": "breakpointHit", "breakpointIndex": 0 } },   // 0-based 引场景内第 1 个 breakpoint 步骤
    { "assert": { "kind": "evaluate", "path": "count", "equals": "3" } },   // equals=精确（字符串按字面值不带引号）
    { "assert": { "kind": "output", "contains": "[CoreMes] done" } },        // output 当前只支持 contains
    { "continue": { "waitSeconds": 30 } },
    { "assert": { "kind": "state", "expect": "Exited" } },
    { "assert": { "kind": "noException" } }
  ]
}
```

> UI 驱动 + 调试断言的闭环示例：`uiAction` 驱动控件、`uiAssert` 断言 UI 状态（`continue.waitSeconds=0` = 放行不等停点，供后续动作在目标运行中执行；`uiAction.verb=input` 时按 `value` 走 `ui_input`）：

```jsonc
{
  "name": "切换按钮复验",
  "target": { "commandLine": "CoreMes.exe" },
  "steps": [
    { "continue": { "waitSeconds": 0 } },                                                       // 0=放行不等待新停点
    { "uiAction": { "process": "CoreMes", "verb": "invoke", "name": "手动", "type": "Button" } },
    { "uiAssert": { "process": "CoreMes", "what": "name", "name": "自动", "contains": "自动" } }
  ]
}
```

- 步骤类型：`breakpoint`（typeName+memberName 成员级定位方法入口，`hit` 第 N 次起）/ `continue`（`waitSeconds`，0=放行不等停点）/ `assert`（kind：breakpointHit/evaluate/output/state/noException）/ `uiAction`（process+verb+定位 index/name/type + 可选 value/direction/lines/windowstate；调用 U1A `ui_action`/`ui_input` 同款语义核心，无物理输入）/ `uiAssert`（process+定位+what + equals|contains，调用 `ui_get` 核心比对；失败附期望 vs 实际、实际值经 DB1 脱敏）/ `ui`·`set`（预留：依赖 U1/W1 未实现，运行时报「步骤类型依赖未就绪」）。
  - 注：早期 spec 草案示例曾用顶层 `{"output": …}`/`{"wait": …}` 作步骤——现统一 canonical：顶层步骤只有 `breakpoint`/`continue`/`assert`/`uiAction`/`uiAssert`/`ui`/`set` 七种，输出/状态/表达式断言一律写成 `assert` 步骤（`kind` 分派）。
- `build` 分支：只重编场景指定工程、**不设置 OutputPath**，产物用项目默认输出路径经 `-getProperty:TargetPath` 自动拿取（SDK 现算，agent 不写路径/TFM）；`commandLine` 首段写**工程入口文件名**（如 `CoreMes.exe`，须与编译产物同名，忽略扩展名/大小写）+ 参数——不一致或 build 失败即 FAIL（绝不启动旧产物）。
- 无 `build`：`commandLine` 用完整路径或相对 server 工作目录（不搜 PATH，同 `debug_launch`）；目标文件不存在 → 中文提示。
- 断言语义：`breakpointHit` 看最近停点是否命中该断点；`evaluate` equals=精确（字符串比字面值，数字/布尔比展示文本，大小写敏感）、contains=展示文本含子串（忽略大小写，也适用于日志串）；`output` **只支持 contains**（`stream`=out/err，缺省全部）；`state` `expect` 可逗号分隔（任一命中即过）；`noException` 场景期间无异常停点。
- 边界与风险：build 需本机 SDK；**无源码的目标（黑盒 dll/exe）不写 build 字段**——直接 `commandLine` 启动，build 只适用于你有源码可改的工程（产物自动定位基于项目默认输出）；每轮复验重新编译/重启目标（有 build 时是唯一真实闭环）；`ui`/`set` 步骤在 U1/W1 落地前报告未就绪；**目标 exe/产物路径含空格 v1 不支持**（启动器按空格切分命令——build 产物落在含空格目录会返回中文边界提示，请用无空格输出目录）；目标输出断言基于 launch 捕获缓冲，进程自然退出后立即断言可能有极短竞态（输出行刚送达）——日志密集应用建议先断点停住再断输出。

## 第三方组件

本项目直接依赖的上游开源项目（完整传递依赖见各包的 NuGet Dependencies 一栏）：

| 组件 | 用途 | 来源 / 许可证 |
|---|---|---|
| ICSharpCode.Decompiler | 反编译引擎 | [ILSpy](https://github.com/icsharpcode/ilspy)（MIT） |
| FlaUI.Core / FlaUI.UIA3 | UI 自动化（ui_* 工具，UIA3） | [FlaUI](https://github.com/FlaUI/FlaUI)（MIT） |
| ClrDebug | ICorDebug 调试封装 | [NuGet: ClrDebug](https://www.nuget.org/packages/ClrDebug) |
| Microsoft.Diagnostics.DbgShim.win-x64 | 调试启动器（dbgshim） | [dotnet/diagnostics](https://github.com/dotnet/diagnostics)（MIT） |
| ModelContextProtocol | MCP C# SDK | [csharp-sdk](https://github.com/modelcontextprotocol/csharp-sdk)（MIT → Apache-2.0 过渡） |
| BootstrapBlazor（含主题/图标扩展） | Web 展示面组件库 | [BootstrapBlazor](https://github.com/dotnetcore/BootstrapBlazor)（Apache-2.0） |
| Monaco Editor | Web 代码视图编辑器（vendored 静态资产随包分发，非 NuGet 依赖，许可证文本随目录分发：`src/DotNetDebugger.Web/wwwroot/lib/monaco-editor/LICENSE.txt`） | [microsoft/monaco-editor](https://github.com/microsoft/monaco-editor)（MIT，Copyright © 2016-present Microsoft Corporation） |
| McMaster.Extensions.Hosting.CommandLine | CLI 参数解析 | [CommandLineUtils](https://github.com/natemcmaster/CommandLineUtils)（Apache-2.0） |

## License

MIT

