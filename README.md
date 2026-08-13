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

配置完成后重启 opencode，工具以 `ilspy_*` 前缀暴露。

## 命令行调试

`ilspymcp` 直接运行即进入 MCP 服务器模式；也可传入参数以命令行形式执行与 MCP 工具相同的功能，便于调试：

```bash
ilspymcp -v                                  # 查看版本号（等价 --version）
ilspymcp -h                                  # 查看帮助（等价 --help）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program      # 反编译单个类型（带行号，等价 ilspy_decompile）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -mn Main  # 按成员名子串搜成员（等价 ilspy_decompile_member）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -s   # 输出成员签名（等价 ilspy_signature）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -hc  # 继承/接口关系（等价 ilspy_hierarchy）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -d   # 成员签名内部引用（等价 ilspy_dependencies）
ilspymcp -a bin/Debug/MyApp.dll -t MyApp.Program -cg  # 方法体调用关系（等价 ilspy_call_graph）
ilspymcp -a bin/Debug/MyApp.dll -l csi               # 列出实体类型（等价 ilspy_list_types）
ilspymcp -a bin/Debug/MyApp.dll -o src                 # 反编译写盘（等价 ilspy_decompile_to_dir，单文件输出）
ilspymcp -a bin/Debug/MyApp.dll -o src -p --nested-directories   # 项目形式反编译写盘（等价 ilspy_decompile_to_project）
ilspymcp -c                                          # 检查 ilspymcp 是否有新版本（CLI 调试用，无需 -a；MCP 会话握手时自动注入报告）
```

常用参数：`-a|--assembly`（程序集）、`-t|--type`（类型）、`-mn|--membername`（按名搜索成员）、`-s|--signatures`（成员签名，配合 `-t`）、`-hc|--hierarchy`（继承/接口，配合 `-t`）、`-d|--dependencies`（内部引用，配合 `-t`）、`-cg|--callgraph`（方法体调用关系，配合 `-t`）、`-l|--list`（类型类别）、`-o|--outputdir`（输出目录，单文件输出）、`-p|--project`（项目形式，需配合 `-o`）、`--nested-directories`（项目形式下按命名空间嵌套目录，仅对 `-p` 生效）、`-ln|--lines`（行号分页）、`--timeout`（超时秒数）、`-c|--check`（检查 ilspymcp 是否有新版本）。

## 工具

| MCP 工具 | 用途 |
| ---- | ---- |
| `ilspy_decompile` | 反编译单个类型的完整源码到标准输出（类型级入口），输出带行号标注，支持按行号范围分页拉取 |
| `ilspy_decompile_member` | 反编译单个或多个成员的实现体（成员级入口），按成员名子串定位或按 token 定位，输出带行号标注，支持分页拉取 |
| `ilspy_decompile_to_dir` | 将程序集反编译写入指定目录（全量或单个类型，单文件输出） |
| `ilspy_decompile_to_project` | 以可编译项目形式将整个程序集反编译写入指定目录（每个类型一个源码文件，按命名空间嵌套目录） |
| `ilspy_list_types` | 列出程序集中的实体类型（class/interface/struct/delegate/enum，可组合指定），输出带行号标注 |
| `ilspy_signature` | 输出指定类型全部成员（字段/方法/属性/事件）每成员一行 C# 签名，作 API 地图 |
| `ilspy_hierarchy` | 输出指定类型的基类链（上溯到 System.Object）、实现的接口与程序集内继承/实现它的类型 |
| `ilspy_dependencies` | 输出指定类型成员签名引用的程序集内部类型及反向引用 |
| `ilspy_call_graph` | 输出指定类型方法体调用的程序集内部类型及反向调用者（执行流级，与签名级引用互补） |

`ilspy_decompile`、`ilspy_decompile_member`、`ilspy_list_types`、`ilspy_signature`、`ilspy_hierarchy`、`ilspy_dependencies` 与 `ilspy_call_graph` 默认仅输出前约 8 KB，均可用 `lines` 参数按行号分页拉取；`ilspy_decompile_to_dir`/`ilspy_decompile_to_project` 结果写盘、不做输出量截断。全部工具均使用内置反编译引擎，开箱即用；其中 `ilspy_list_types`/`ilspy_signature`/`ilspy_hierarchy`/`ilspy_dependencies`/`ilspy_call_graph` 为元数据读取，秒回。结果按「程序集 + 参数」缓存在内存，程序集更新后自动失效；反编译结果命中缓存时头部标注「缓存: 命中（重复查询成本低）」，agent 可知重复查询低成本、可放心多问。`list_types` 输出行首类别前缀（如 `class Foo.Bar`）可直接复制作 `typeName` 使用，无需去掉前缀。MCP 会话启动握手时自动检查 ilspymcp 是否有新版本：检测到新版本时注入指令式提示，要求 agent 在会话开始的回复中主动告知用户并提供升级命令；已是最新时仅注入状态行，不打扰用户。无需单独调用检查工具。

### 工具参数

| 工具 | 参数 | 说明 | 必填 |
| ---- | ---- | ---- | ---- |
| `ilspy_decompile` | `assembly` | 程序集文件路径（.dll/.exe），可为相对当前工作目录的路径 | 是 |
| | `typeName` | 仅反编译指定全限定类型名，例如 `System.String` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`（1-based 含两端，单次最多约 32 KB），如 `200-400`；省略返回前约 8 KB | 否 |
| | `timeoutSeconds` | 本次反编译等待超时秒数（默认 30）；超时即放弃本次反编译、结果不入缓存，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_decompile_member` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 在指定类型内搜索成员，全限定类型名，例如 `System.Text.Json.JsonSerializer`（提供 `token` 时可不填） | 否 |
| | `memberName` | 成员名子串（忽略大小写），例如 `SerializeAsync`；匹配到的成员全部反编译，匹配数超过 20 时仅返回成员签名清单（提供 `token` 时可不填） | 否 |
| | `token` | 按元数据 token 直接反编译单个成员（如超限签名清单或多成员分隔行中的 `0x06000005`）；提供时忽略 `memberName`，`typeName` 可不填 | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| | `timeoutSeconds` | 本次反编译等待超时秒数（默认 30）；超时即放弃本次反编译、结果不入缓存，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_decompile_to_dir` | `assembly` | 程序集文件路径 | 是 |
| | `outputDir` | 输出目录；结果写入磁盘而非标准输出 | 是 |
| | `typeName` | 仅反编译指定全限定类型名；省略则反编译整个程序集 | 否 |
| | `timeoutSeconds` | 本次反编译写盘等待超时秒数（默认 30，全量写盘大程序集可调大）；超时即放弃本次写盘，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_decompile_to_project` | `assembly` | 程序集文件路径 | 是 |
| | `outputDir` | 输出目录；结果写入磁盘而非标准输出 | 是 |
| | `nestedDirectories` | 输出到目录时按命名空间使用嵌套目录（默认 true） | 否 |
| | `timeoutSeconds` | 本次反编译写盘等待超时秒数（默认 30，全量写盘大程序集可调大）；超时即放弃本次写盘，可调大后重试；超时后中断后台反编译，不再继续占用 CPU | 否 |
| `ilspy_list_types` | `assembly` | 程序集文件路径 | 是 |
| | `list` | 实体类型类别组合：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 `csi` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_signature` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Formatting.OutputFormatter` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_hierarchy` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Formatting.OutputFormatter` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_dependencies` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Caching.DecompileCache` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |
| `ilspy_call_graph` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Caching.DecompileCache` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前约 8 KB | 否 |

## 使用示例

在 opencode 对话中直接提出：

- **列出所有类**：> 列出 `bin/Debug/MyApp.dll` 中的所有 class 类型
- **列出多类类型**：> 列出 `bin/Debug/MyApp.dll` 中的 class、interface、struct 类型（`list="csi"`）
- **全量反编译**：> 反编译 `bin/Debug/MyApp.dll` 到 `src` 目录（必须指定 `outputDir`）
- **项目形式 + 嵌套目录**：> 以可编译项目形式反编译 `bin/Debug/MyApp.dll` 到 `src`，并按命名空间嵌套目录
- **单个类型**：> 反编译 `bin/Debug/MyApp.dll` 中的 `MyApp.Program` 类型
- **按名搜索成员**：> 在 `bin/Debug/MyApp.dll` 的 `MyApp.Program` 中搜索名称包含 `Main` 的成员并反编译
- **按 token 反编译单个成员**：> 在 `bin/Debug/MyApp.dll` 的 `MyApp.Program` 中搜索名称包含 `Parse` 的成员；若匹配超过 20 个仅返回签名清单，则取目标行末尾 token（如 `0x06000010`）用 `token` 参数直接反编译该成员
- **成员签名（API 地图）**：> 列出 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的全部成员签名
- **继承关系**：> 查看 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的基类链、接口与继承者
- **内部引用**：> 查询 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的成员签名引用了哪些内部类型、以及哪些类型引用了它
- **方法体调用关系**：> 查询 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的方法体调用了哪些内部类型、以及哪些类型的方法体调用了它
- **按行拉取**：> 反编译 `bin/Debug/MyApp.dll` 中的 `MyApp.Program`，读取第 200-400 行

## License

MIT
