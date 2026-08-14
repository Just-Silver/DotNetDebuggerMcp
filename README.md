# ilspymcp

内置 [ICSharpCode.Decompiler](https://github.com/icsharpcode/ilspy) 反编译引擎的 .NET 反编译 MCP 服务器。在 [opencode](https://opencode.ai) 等 MCP 客户端中可直接将 .NET 程序集（dll/exe）反编译为 C# 源码、列出类型，或把整个程序集反编译写入指定目录。反编译引擎随包内置，安装本工具即可开箱即用，无需额外安装其他反编译工具。

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## 安装

### 安装 `ilspymcp`

```bash
dotnet tool install --global ilspymcp
```

升级：

```bash
dotnet tool update --global ilspymcp
```

卸载：

```bash
dotnet tool uninstall --global ilspymcp
```

## opencode 接入

在 `opencode.json` 中注册本地 MCP 服务器：

```json
{
  "$schema": "https://opencode.ai/config.json",
  "mcp": {
    "ilspy": {
      "type": "local",
      "command": ["ilspymcp"]
    }
  }
}
```

配置完成后重启 opencode，工具以 `ilspy_*` 前缀暴露。握手时 server 会在会话上下文注入当前工作目录，`assembly`/`outputDir` 的相对路径以此解析。

## 命令行调试

`ilspymcp` 直接运行即进入 MCP 服务器模式；也可传入参数以命令行形式执行与 MCP 工具相同的功能，便于调试：

```bash
ilspymcp -v                                  # 查看版本号（等价 --version）
ilspymcp -h                                  # 查看帮助（等价 --help）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program      # 反编译单个类型（带行号，等价 ilspy_decompile）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -mn Main  # 按成员名子串搜成员（等价 ilspy_decompile_member）
ilspymcp -a bin/Debug/MyApp.dll -mn Main      # 省略 -t 跨程序集按成员名搜成员（等价 ilspy_decompile_member，typeName 缺省）
ilspymcp -a bin/Debug/MyApp.dll -tt 0x02000004 -mn Main  # typeName 有歧义时按类型 token 精确定位类型后搜成员（等价 ilspy_decompile_member typeToken=...）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -s   # 输出成员签名（等价 ilspy_signature）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -hc  # 继承/接口关系（等价 ilspy_hierarchy）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -hc -i  # 继承/接口关系含全部间接后代（等价 ilspy_hierarchy includeIndirect=true）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -d   # 成员签名内部引用（等价 ilspy_dependencies）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -d -x  # 成员签名引用含跨程序集外部类型（等价 ilspy_dependencies includeExternal=true）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -cg  # 方法体调用关系（等价 ilspy_call_graph）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -cg -x  # 方法体调用含跨程序集外部类型（等价 ilspy_call_graph includeExternal=true）
ilspymcp -a bin/Debug/MyApp.dll -cg -tk 0x06000005  # 按方法 token 反向定位程序集内调用它的成员（等价 ilspy_call_graph token=...，typeName 可不填）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.IWorker -iu  # 接口实现者与调用点组合视图（等价 ilspy_interface_usage）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.IWorker -iu -i  # 含全部间接实现者（等价 ilspy_interface_usage includeIndirect=true）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.GenericBox -gi  # 泛型实例化使用点（等价 ilspy_generic_instantiations，typeName 可带 arity 如 GenericBox`1 也可省略）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -mn Parse -cc  # 起始方法 Parse 的调用序列 + 被调用成员反编译（等价 ilspy_call_chain）
ilspymcp -a bin/Debug/MyApp.dll -cc -tk 0x06000010  # 按方法 token 直接定位起始方法（等价 ilspy_call_chain token=...，typeName/memberName 可不填）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -mn Parse -cc -x  # 调用序列含跨程序集外部调用行并展开可解析的外部调用（同目录/CWD/NuGet 可解析则展开为被调方法体子序列，找不到标注终止；等价 ilspy_call_chain includeExternal=true）
ilspymcp -a bin/Debug/MyApp.dll -ai      # 输出程序集概览（等价 ilspy_assembly_info）
ilspymcp -a bin/Debug/MyApp.dll -l csi               # 列出实体类型（等价 ilspy_list_types）
ilspymcp -a bin/Debug/MyApp.dll -l c -nc Box        # 列出类型且名称含 Box（等价 ilspy_list_types 的 nameContains 参数，忽略大小写）
ilspymcp -a bin/Debug/MyApp.dll -l c -ns ILSpyMcp   # 列出类型且命名空间含 ILSpyMcp（等价 ilspy_list_types 的 namespaceContains 参数，忽略大小写）
ilspymcp -a bin/Debug/MyApp.dll -ss "配置Key"        # 按字符串字面量子串反查成员（等价 ilspy_search_string，忽略大小写）
ilspymcp -a bin/Debug/MyApp.dll -ss "order by" -t MyApp.Program  # 限定类型内按字符串字面量反查（等价 ilspy_search_string 的 typeName 参数）
ilspymcp -a bin/Debug/MyApp.dll -fa -t MyApp.Program -fn _count  # 追踪字段 _count 的读取/写入/取地址位置（等价 ilspy_field_access）
ilspymcp -a bin/Debug/MyApp.dll -fa -tk 0x04000005   # 按字段 token 追踪读写点（等价 ilspy_field_access fieldToken=...，typeName 可不填）
ilspymcp -a bin/Debug/MyApp.dll -o src                 # 反编译写盘（等价 ilspy_decompile_to_dir，单文件输出）
ilspymcp -a bin/Debug/MyApp.dll -o src -t "MyApp.IWorker,MyApp.Worker"   # 写盘指定多个类型（typeName 逗号分隔，每类型一个文件）
ilspymcp -a bin/Debug/MyApp.dll -o src -p --nested-directories   # 项目形式反编译写盘（等价 ilspy_decompile_to_project）
ilspymcp -c                                          # 检查 ilspymcp 是否有新版本（CLI 调试用，无需 -a；MCP 会话握手时自动注入报告）
```

常用参数：`-a|--assembly`（程序集）、`-t|--type`（类型）、`-mn|--membername`（按名搜索成员）、`-tt|--typetoken`（类型 token，配合 `-mn` 在 typeName 有歧义时精确定位类型）、`-s|--signatures`（成员签名，配合 `-t`）、`-hc|--hierarchy`（继承/接口，配合 `-t`）、`-ai|--assembly-info`（程序集概览，配合 `-a`）、`-i|--indirect`（hierarchy 含全部间接后代、interface_usage 含全部间接实现者，配合 `-hc`/`-iu`）、`-d|--dependencies`（内部引用，配合 `-t`）、`-cg|--callgraph`（方法体调用关系，配合 `-t`）、`-iu|--interfaceusage`（接口实现者与调用点组合视图，配合 `-t`）、`-gi|--genericinstantiations`（泛型实例化使用点，配合 `-t`）、`-cc|--callchain`（起始方法的调用序列 + 被调用成员反编译，配合 `-t -mn` 或 `-tk`）、`-tk|--token`（方法 token，配合 `-cg` 反向定位调用它的成员、配合 `-cc` 直接定位起始方法）、`-x|--external`（同时输出跨程序集外部类型引用/调用行，配合 `-d`/`-cg`/`-cc`）、`-l|--list`（类型类别）、`-nc|--namecontains`（类型名子串过滤，配合 `-l`）、`-ns|--namespacecontains`（命名空间子串过滤，配合 `-l`）、`-ss|--searchstring`（按字符串字面量子串反查成员，可选 `-t` 限定类型）、`-fa|--fieldaccess`（追踪字段读写点，可选 `-t` 限定类型、`-fn` 指定字段名、`-tk` 指定字段 token）、`-o|--outputdir`（输出目录，单文件输出）、`-p|--project`（项目形式，需配合 `-o`）、`--nested-directories`（项目形式下按命名空间嵌套目录，仅对 `-p` 生效）、`-ln|--lines`（行号分页）、`--timeout`（超时秒数）、`-c|--check`（检查 ilspymcp 是否有新版本）。

## 工具

| MCP 工具 | 用途 |
| ---- | ---- |
| `ilspy_decompile` | 反编译单个类型的完整源码到标准输出（类型级入口），输出带行号标注，支持按行号范围分页拉取；未找到类型时附相近类型名 |
| `ilspy_decompile_member` | 反编译单个或多个成员的实现体（成员级入口），按成员名子串定位（字段/方法/属性/事件均可，`typeName` 省略时跨程序集搜索）或按 token 定位，输出带行号标注，支持分页拉取；`typeName` 有歧义时返回歧义提示（列出候选类型及 typeToken，可用 `typeToken` 精确定位）；未找到类型时附相近类型名 |
| `ilspy_decompile_to_dir` | 将程序集反编译写入指定目录（全量或指定类型，`typeName` 支持逗号分隔多个类型批量写盘，单文件输出；未找到的类型在结果中提示） |
| `ilspy_decompile_to_project` | 以可编译项目形式将整个程序集反编译写入指定目录（每个类型一个源码文件，按命名空间嵌套目录） |
| `ilspy_list_types` | 列出程序集中的实体类型（class/interface/struct/delegate/enum，可组合指定），支持按类型名/命名空间子串过滤（嵌套类型按最外层声明类型命名空间归属），输出带行号标注 |
| `ilspy_signature` | 输出指定类型全部成员（字段/方法/属性/事件）每成员一行 C# 签名，作 API 地图；每行行尾附成员 token，可直接用于 `ilspy_decompile_member` 的 `token` 参数；未找到类型时附相近类型名 |
| `ilspy_hierarchy` | 输出指定类型的基类链（上溯到 System.Object）、实现的接口与程序集内继承/实现它的类型；`includeIndirect=true` 时后代段含全部间接后代（接口的所有实现者、基类的所有子孙）；未找到类型时附相近类型名 |
| `ilspy_dependencies` | 输出指定类型成员签名引用的程序集内部类型及反向引用；`includeExternal=true` 时追加输出跨程序集外部类型（带程序集归属，格式 `全名 [程序集名]`）；未找到类型时附相近类型名 |
| `ilspy_call_graph` | 输出指定类型方法体调用的程序集内部类型及反向调用者（执行流级，与签名级引用互补）；`includeExternal=true` 时追加输出方法体调用的跨程序集外部类型（带程序集归属）；`token` 参数按方法 token 反向定位程序集内调用该具体方法的成员（方法级调用点）；未找到类型时附相近类型名 |
| `ilspy_call_chain` | 输出起始方法的方法级正向调用序列，并对被调用的程序集内部成员反编译组合输出（方法级执行流视图）：按 `token` 或 `typeName`+`memberName`（匹配多个方法时返回 `#MEMBER` 签名清单）定位起始方法，序列行带内部成员 token（可直接用于 `ilspy_decompile_member` 反编译）；`includeExternal=true` 时保留跨程序集外部调用行（格式 `全名::成员名 [程序集名]`）并展开可解析的外部调用（经 UniversalAssemblyResolver 定位主 dll 同目录/CWD/NuGet 缓存/共享框架/GAC 的磁盘程序集，外部调用行后缩进输出 `程序集::类型::成员 调用:` + 被调方法体子序列，子序列内跨程序集调用递归展开），解析失败的框架/外部调用在行尾标注 `（未找到程序集 X，视为框架/外部调用未展开）`；被调用成员各成员体前有 `#MEMBER` JSON 分隔行 |
| `ilspy_interface_usage` | 输出指定接口的使用情况组合视图：程序集内实现该接口的类型（`includeIndirect=true` 时含全部间接实现者，如接口的子接口、实现者及其子类）、方法体调用接口成员的调用点（`类型全名::成员名 → 接口成员名` 行，反扫全部类型方法体调用指令，覆盖内部接口与跨程序集外部接口、含泛型实例化调用解包）与成员签名引用该接口的类型（签名级引用，与调用点互补）；三段空段输出 `（无）` 占位，未找到类型时附相近类型名 |
| `ilspy_generic_instantiations` | 输出指定泛型类型在程序集内被具体实例化的使用点两段：成员签名中的泛型实例化（扫描全部类型的字段/方法/属性/事件签名，凡签名用具体类型参数实例化该泛型类型时输出 `类型全名::成员签名 → GenericType<arg, arg>` 行）与方法体调用中的泛型实例化（扫描全部类型方法体调用指令，凡调用该泛型类型的泛型方法输出 `来源类型::来源方法签名 → Echo<int>` 行、经成员引用实例化该泛型类型时输出对应行）；两段空段输出 `（无）` 占位，`typeName` 可带 arity（``GenericBox`1``）也可省略（`GenericBox`，短名亦命中），未找到类型时附相近类型名 |
| `ilspy_search_string` | 按字符串字面量子串反查成员：扫描全部（或 `typeName` 指定）类型方法体的 `ldstr` 指令，按子串（忽略大小写）匹配业务文案/SQL 片段/配置 Key 等用户字符串，输出每行 `类型全名::成员签名` + 转义后的字符串值 + 成员 token（可直接用于 `ilspy_decompile_member` 的 `token` 参数反编译对应成员）；未找到类型时附相近类型名 |
| `ilspy_field_access` | 追踪指定字段的读取/写入/取地址位置：按 `fieldToken`（`0x04` 开头字段 token）或 `typeName`+`fieldName`（忽略大小写，省略 `typeName` 时跨程序集搜索）定位字段后，反向扫描全部类型方法体的字段访问指令（`ldfld`/`ldsfld` 读取、`stfld`/`stsfld` 写入、`ldflda`/`ldsflda` 取地址），输出三段 `类型全名::成员签名` 来源成员（空段 `（无）` 占位）；字段名匹配多个字段时返回 `#MEMBER` 签名清单，用其中 token 作 `fieldToken` 精确定位 |
| `ilspy_assembly_info` | 输出程序集概览：程序集名与版本、目标框架、引用的程序集清单（名+版本）、实体类型计数（class/interface/struct/delegate/enum，过滤编译器生成类型）与入口点；元数据读取秒回，适合作为接触陌生程序集的第一站 |
| `ilspy_cache_stats` | 输出进程内共享缓存状态：当前占用/上限（据此判断缓存大小设置是否合适）、条目数、命中率（会话启动以来的累计命中/未命中）与逐条目占用明细（按占用降序，含来源工具、参数与程序集），定位缓存大头；无程序集参数 |

`ilspy_decompile`、`ilspy_decompile_member`、`ilspy_call_chain`、`ilspy_list_types`、`ilspy_signature`、`ilspy_hierarchy`、`ilspy_dependencies`、`ilspy_call_graph`、`ilspy_interface_usage`、`ilspy_generic_instantiations`、`ilspy_search_string`、`ilspy_field_access` 与 `ilspy_assembly_info` 默认仅输出前约 8 KB，均可用 `lines` 参数按行号分页拉取；`ilspy_decompile_to_dir`/`ilspy_decompile_to_project` 结果写盘、不做输出量截断。全部工具均使用内置反编译引擎，开箱即用；其中 `ilspy_list_types`/`ilspy_signature`/`ilspy_hierarchy`/`ilspy_dependencies`/`ilspy_call_graph`/`ilspy_interface_usage`/`ilspy_generic_instantiations`/`ilspy_search_string`/`ilspy_field_access`/`ilspy_assembly_info` 为元数据读取，秒回，`ilspy_call_chain` 定位起始方法为元数据秒回、被调用成员反编译走共享缓存。除写盘工具外全部工具结果按「程序集 + 参数」缓存在内存（共用同一缓存，程序集更新后自动失效）；命中缓存时头部标注「缓存: 命中（重复查询成本低）」，agent 可知重复查询低成本、可放心多问。`list_types` 输出行首类别前缀（如 `class Foo.Bar`）可直接复制作 `typeName` 使用，无需去掉前缀。MCP 会话启动握手时自动检查 ilspymcp 是否有新版本：检测到新版本时注入指令式提示，要求 agent 在会话开始的回复中主动告知用户并提供升级命令；已是最新时仅注入状态行，不打扰用户。无需单独调用检查工具。

### 工具参数

| 工具 | 参数 | 说明 | 必填 |
| ---- | ---- | ---- | ---- |
| `ilspy_decompile` | `assembly` | 程序集文件路径（.dll/.exe），可为相对当前工作目录的路径 | 是 |
| | `typeName` | 仅反编译指定全限定类型名，例如 `System.String` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`（1-based 含两端，单次最多约 32 KB），如 `200-400`；省略返回前约 8 KB | 否 |
| | `timeoutSeconds` | 本次反编译等待超时秒数（默认 30）；超时即放弃本次反编译、结果不入缓存，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_decompile_member` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 在指定类型内搜索成员，全限定类型名，例如 `System.Text.Json.JsonSerializer`；省略时跨程序集搜索全部类型的成员（提供 `token` 时可不填） | 否 |
| | `memberName` | 成员名子串（忽略大小写），例如 `SerializeAsync`；字段/方法/属性/事件均可按名搜索，匹配到的成员全部反编译，匹配数超过 20 时仅返回成员签名清单（提供 `token` 时可不填） | 否 |
| | `token` | 按元数据 token 直接反编译单个成员（如超限签名清单或多成员分隔行 `#MEMBER` JSON 中的 `0x06000005`）；提供时忽略 `memberName`，`typeName` 可不填 | 否 |
| | `typeToken` | 按类型定义 token（`0x02` 开头，如歧义提示中列出的 token）精确定位类型（`typeName` 歧义消歧），再在类型内按 `memberName` 搜索；提供时 `typeName` 可不填 | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| | `timeoutSeconds` | 本次反编译等待超时秒数（默认 30）；超时即放弃本次反编译、结果不入缓存，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_decompile_to_dir` | `assembly` | 程序集文件路径 | 是 |
| | `outputDir` | 输出目录；结果写入磁盘而非标准输出 | 是 |
| | `typeName` | 仅反编译指定全限定类型名；支持逗号分隔多个类型批量写盘（每个类型一个 `.decompiled.cs` 文件），如 `A.B.C1,A.B.C2`；未找到的类型在结果中提示（单个未找到时附相近类型名），部分成功也算成功；省略则反编译整个程序集 | 否 |
| | `timeoutSeconds` | 本次反编译写盘等待超时秒数（默认 30，全量写盘大程序集可调大）；超时即放弃本次写盘，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_decompile_to_project` | `assembly` | 程序集文件路径 | 是 |
| | `outputDir` | 输出目录；结果写入磁盘而非标准输出 | 是 |
| | `nestedDirectories` | 输出到目录时按命名空间使用嵌套目录（默认 true） | 否 |
| | `timeoutSeconds` | 本次反编译写盘等待超时秒数（默认 30，全量写盘大程序集可调大）；超时即放弃本次写盘，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_list_types` | `assembly` | 程序集文件路径 | 是 |
| | `list` | 实体类型类别组合：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 `csi` | 是 |
| | `nameContains` | 类型名子串过滤（忽略大小写，默认空=不过滤），如 `Box` 只返回名称含 Box 的类型；大型程序集按名定位类型免分页 | 否 |
| | `namespaceContains` | 命名空间子串过滤（忽略大小写，默认空=不过滤），如 `ILSpyMcp` 只返回命名空间含 ILSpyMcp 的类型；嵌套类型按最外层声明类型命名空间归属；可与 `nameContains` 组合使用 | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_signature` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Formatting.OutputFormatter` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_hierarchy` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Formatting.OutputFormatter` | 是 |
| | `includeIndirect` | 是否包含间接后代（如接口的所有实现者、基类的所有子孙），一次返回全部后代、免递归多次调用；默认 `false` | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_dependencies` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Caching.DecompileCache` | 是 |
| | `includeExternal` | 是否同时输出跨程序集外部类型引用（如 BCL/NuGet，带程序集归属，格式 `全名 [程序集名]`，如 `System.Console [System.Console]`）；默认 `false` | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_call_graph` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Caching.DecompileCache`；类型级反查必填，提供 `token` 时可不填 | 否 |
| | `token` | 方法元数据 token（取 `signature` 行尾或 `#MEMBER` 分隔行的 token，如 `0x06000005`）：按 token 反向定位程序集内哪些方法体调用了这个具体方法，输出 `类型全名::成员签名` 调用点行；提供时 `typeName` 可不填、`includeExternal` 忽略 | 否 |
| | `includeExternal` | 是否同时输出跨程序集外部类型引用（如 BCL/NuGet，带程序集归属，格式 `全名 [程序集名]`，如 `System.Console [System.Console]`）；默认 `false` | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_call_chain` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 起始方法所属类型全名，格式与 list_types 输出一致，例如 `ILSpyMcp.Samples.ChainTop`；提供 `token` 时可不填 | 否 |
| | `memberName` | 起始方法名子串（忽略大小写），如 `Run`；匹配多个方法时返回 `#MEMBER` 签名清单（含 token）供精确定位；提供 `token` 时可不填 | 否 |
| | `token` | 起始方法元数据 token（取 `signature` 行尾或 `#MEMBER` 分隔行的 token，如 `0x06000005`）：按 token 直接定位起始方法，忽略 `memberName`，`typeName` 可不填；默认空=不使用 | 否 |
| | `includeExternal` | 是否在调用序列中保留跨程序集外部调用行并展开（格式 `全名::成员名 [程序集名]`，如 `System.Console::WriteLine [System.Console]`）；为 `true` 时经 UniversalAssemblyResolver 将可解析的外部调用（主 dll 同目录/CWD/NuGet 缓存/共享框架/GAC）展开为被调方法体子序列（外部调用行后缩进输出，子序列内跨程序集调用递归展开），找不到的外部调用在行尾标注 `（未找到程序集 X，视为框架/外部调用未展开）`；默认 `false` | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| | `timeoutSeconds` | 本次反编译等待超时秒数（默认 30）；被调用成员较多时可调大；超时即放弃本次反编译、结果不入缓存，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_interface_usage` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标接口的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Samples.IWorker` | 是 |
| | `includeIndirect` | 是否包含全部间接实现者（接口的子接口、实现者及其子类等间接后代），一次返回全部、免递归多次调用；默认 `false` | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_generic_instantiations` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标泛型类型的全限定名，格式与 list_types 输出一致；泛型类型可带 arity（``GenericBox`1``）也可省略 arity（`GenericBox`，短名如 `GenericBox` 亦命中），例如 `ILSpyMcp.Samples.GenericBox` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_search_string` | `assembly` | 程序集文件路径 | 是 |
| | `search` | 要搜索的字符串字面量子串（忽略大小写），如 `配置Key`、`order by` | 是 |
| | `typeName` | 限定在指定类型内反查，全限定类型名（格式与 list_types 输出一致）；省略时跨程序集全部类型 | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_field_access` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 字段所属类型全名，格式与 list_types 输出一致，例如 `ILSpyMcp.Samples.FieldHolder`；省略时跨程序集按 `fieldName` 搜索全部类型（提供 `fieldToken` 时可不填） | 否 |
| | `fieldName` | 字段名子串（忽略大小写），如 `Data`；匹配多个字段时返回 `#MEMBER` 签名清单，用其中 token 作 `fieldToken` 精确定位（提供 `fieldToken` 时可不填） | 否 |
| | `fieldToken` | 字段元数据 token（`0x04` 开头，取 `signature` 行尾或 `#MEMBER` 分隔行的 token，如 `0x04000005`）：提供时按 token 直接定位字段，忽略 `fieldName`，`typeName` 可不填 | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_assembly_info` | `assembly` | 程序集文件路径 | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_cache_stats` | `lines` | 按行号范围读取条目明细，格式 `start-end`；省略返回前约 8 KB | 否 |

## 使用示例

在 opencode 对话中直接提出：

- **程序集概览**：> 查看 `bin/Debug/MyApp.dll` 的程序集概览（程序集名与版本、目标框架、引用清单、类型构成、入口点），先摸清陌生程序集再深入
- **列出所有类**：> 列出 `bin/Debug/MyApp.dll` 中的所有 class 类型
- **列出多类类型**：> 列出 `bin/Debug/MyApp.dll` 中的 class、interface、struct 类型（`list="csi"`）
- **全量反编译**：> 反编译 `bin/Debug/MyApp.dll` 到 `src` 目录（必须指定 `outputDir`）
- **项目形式 + 嵌套目录**：> 以可编译项目形式反编译 `bin/Debug/MyApp.dll` 到 `src`，并按命名空间嵌套目录
- **单个类型**：> 反编译 `bin/Debug/MyApp.dll` 中的 `MyApp.Program` 类型
- **按名搜索成员**：> 在 `bin/Debug/MyApp.dll` 的 `MyApp.Program` 中搜索名称包含 `Main` 的成员并反编译
- **跨程序集搜索成员**：> 在 `bin/Debug/MyApp.dll` 的全部类型中搜索名称包含 `Parse` 的成员并反编译（省略 `typeName`；多匹配时 `#MEMBER` JSON 带 `type` 字段标注各成员所属类型）
- **按 token 反编译单个成员**：> 在 `bin/Debug/MyApp.dll` 的 `MyApp.Program` 中搜索名称包含 `Parse` 的成员；若匹配超过 20 个仅返回 `#MEMBER` 签名清单，则取目标行 JSON 中的 `token` 字段（如 `0x06000010`）用 `token` 参数直接反编译该成员
- **成员签名（API 地图）**：> 列出 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的全部成员签名（每行行尾为成员 token，如 `  0x06000005`，可直接用作 `token` 参数反编译对应成员）
- **继承关系**：> 查看 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的基类链、接口与继承者
- **内部引用**：> 查询 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的成员签名引用了哪些内部类型、以及哪些类型引用了它（加 `includeExternal=true` 同时列出 BCL/NuGet 外部类型及所属程序集，如 `System.Console [System.Console]`）
- **方法体调用关系**：> 查询 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的方法体调用了哪些内部类型、以及哪些类型的方法体调用了它（加 `includeExternal=true` 同时列出方法体调用的外部类型及所属程序集）
- **方法级调用点**：> 查询 `bin/Debug/MyApp.dll` 中哪些方法体调用了 `MyApp.Program` 的 `Parse` 方法（先用 `signature` 取该成员行尾 token 如 `0x06000010`，再以 `token` 参数调用 `ilspy_call_graph`，输出 `类型全名::成员签名` 调用点行）
- **接口使用情况**：> 查询 `bin/Debug/MyApp.dll` 中 `MyApp.IWorker` 接口的实现者、方法体调用该接口成员的调用点（输出 `类型全名::成员名 → 接口成员名` 行）与成员签名引用它的类型（加 `includeIndirect=true` 一次列出全部间接实现者，如接口的子接口、实现者及其子类）
- **泛型实例化使用点**：> 查询 `bin/Debug/MyApp.dll` 中 `MyApp.GenericBox` 泛型类型在程序集内被具体实例化的使用点（`typeName` 传 `GenericBox` 短名或 ``GenericBox`1`` 全名均可，输出两段：成员签名中的泛型实例化 `类型全名::成员签名 → GenericBox<int>` 行与方法体调用中的泛型实例化行）
- **方法调用序列**：> 追踪 `bin/Debug/MyApp.dll` 中 `MyApp.Program.Parse` 方法体的正向调用序列，并反编译被调用的内部成员（`typeName`+`memberName` 定位起始方法；`memberName` 匹配多个方法时返回 `#MEMBER` 签名清单，取目标行 `token` 用 `token` 参数精确定位；`includeExternal=true` 时保留跨程序集外部调用行，如 `System.Console::WriteLine [System.Console]`，并把同目录/CWD/NuGet 缓存/共享框架可解析的外部调用展开为被调方法体子序列（行尾标注 `（未找到程序集 X，视为框架/外部调用未展开）` 表示该外部调用无法解析、未展开）；序列行带内部成员 token，可直接用于 `ilspy_decompile_member` 反编译）
- **字符串反查**：> 在 `bin/Debug/MyApp.dll` 中按字符串字面量子串 `配置Key` 反查引用它的成员（忽略大小写，输出 `类型全名::成员签名` + 转义后的字符串值 + 成员 token；可加 `typeName` 限定在指定类型内，命中行 token 可直接用于 `ilspy_decompile_member` 反编译对应成员）
- **字段读写点**：> 追踪 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 字段 `_count` 的读取/写入/取地址位置（输出三段 `类型全名::成员签名` 来源成员；若字段名匹配多个字段会返回 `#MEMBER` 签名清单，取目标字段的 token 用 `fieldToken` 参数精确定位；也可先用 `signature` 取该字段行尾 token 如 `0x04000010` 直接定位）
- **按行拉取**：> 反编译 `bin/Debug/MyApp.dll` 中的 `MyApp.Program`，读取第 200-400 行
- **缓存状态**：> 查看当前会话的缓存占用与命中率（`cache_stats`），评估缓存大小设置是否合适、定位占用大头

## License

MIT
