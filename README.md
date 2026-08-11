# ilspymcp

基于 [ilspycmd](https://github.com/icsharpcode/ilspy) 的 .NET 反编译 MCP 服务器。在 [opencode](https://opencode.ai) 等 MCP 客户端中可直接将 .NET 程序集（dll/exe）反编译为 C# 源码、列出类型，或把整个程序集反编译写入指定目录。

## 环境要求

- [.NET 10 SDK](https://dotnet.microsoft.com/download)

## 安装

### 前置条件：安装 `ilspycmd`

本工具运行期依赖 `ilspycmd`，需先安装：

```bash
dotnet tool install --global ilspycmd
```

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
ilspymcp -a bin/Debug/MyApp.dll -o src --nested-directories   # 反编译写盘（等价 ilspy_decompile_to_dir）
ilspymcp -a bin/Debug/MyApp.dll -o src -p --nested-directories   # 项目形式反编译写盘（等价 ilspy_decompile_to_project）
ilspymcp -c                                          # 环境自检（CLI 调试用，无需 -a；MCP 会话起始已自动注入环境状态报告）
```

常用参数：`-a|--assembly`（程序集）、`-t|--type`（类型）、`-mn|--membername`（按名搜索成员）、`-s|--signatures`（成员签名，配合 `-t`）、`-hc|--hierarchy`（继承/接口，配合 `-t`）、`-d|--dependencies`（内部引用，配合 `-t`）、`-cg|--callgraph`（方法体调用关系，配合 `-t`）、`-l|--list`（类型类别）、`-o|--outputdir`（输出目录）、`-p|--project`（项目形式，需配合 `-o`）、`--nested-directories`（嵌套目录）、`-ln|--lines`（行号分页）、`--timeout`（超时秒数）、`-c|--check`（环境自检）。

## 工具

| MCP 工具 | 用途 |
| ---- | ---- |
| `ilspy_decompile` | 反编译单个类型到标准输出，输出带行号标注，支持按行号范围分页拉取 |
| `ilspy_decompile_member` | 按成员名子串在指定类型内搜索并反编译匹配的成员，输出带行号标注，支持分页拉取 |
| `ilspy_decompile_to_dir` | 将程序集反编译写入指定目录（全量或单个类型） |
| `ilspy_decompile_to_project` | 以可编译项目形式将整个程序集反编译写入指定目录（每个类型一个源码文件） |
| `ilspy_list_types` | 列出程序集中的实体类型（class/interface/struct/delegate/enum，可组合指定），输出带行号标注 |
| `ilspy_signature` | 输出指定类型全部成员（字段/方法/属性/事件）每成员一行 C# 签名，作 API 地图 |
| `ilspy_hierarchy` | 输出指定类型的基类链（上溯到 System.Object）、实现的接口与程序集内继承/实现它的类型 |
| `ilspy_dependencies` | 输出指定类型成员签名引用的程序集内部类型及反向引用 |
| `ilspy_call_graph` | 输出指定类型方法体调用的程序集内部类型及反向调用者（执行流级，与签名级引用互补） |

`ilspy_decompile`、`ilspy_decompile_member`、`ilspy_list_types`、`ilspy_signature`、`ilspy_hierarchy`、`ilspy_dependencies` 与 `ilspy_call_graph` 默认仅输出前 200 行，均可用 `lines` 参数按行号分页拉取；`ilspy_decompile_to_dir`/`ilspy_decompile_to_project` 结果写盘、不做行数截断。`ilspy_list_types`/`ilspy_signature`/`ilspy_hierarchy`/`ilspy_dependencies`/`ilspy_call_graph` 为纯元数据读取，秒回且无需安装 ilspycmd；反编译类工具依赖 ilspycmd，未安装时仅提示，不代为执行。结果按「程序集 + 参数」缓存在内存，程序集更新后自动失效。MCP 会话启动握手时自动执行环境自检（ilspycmd 安装/版本、ilspymcp 更新状态）并注入会话起始提示，无需单独调用检查工具。

### 工具参数

| 工具 | 参数 | 说明 | 必填 |
| ---- | ---- | ---- | ---- |
| `ilspy_decompile` | `assembly` | 程序集文件路径（.dll/.exe），可为相对当前工作目录的路径 | 是 |
| | `typeName` | 仅反编译指定全限定类型名，例如 `System.String` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`（1-based 含两端，单次最多 500 行），如 `200-400`；省略返回前 200 行 | 否 |
| | `timeoutSeconds` | 本次反编译回源超时秒数（默认 30） | 否 |
| `ilspy_decompile_member` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 在指定类型内搜索成员，全限定类型名，例如 `System.Text.Json.JsonSerializer` | 是 |
| | `memberName` | 成员名子串（忽略大小写），例如 `SerializeAsync`；匹配到的成员全部反编译，匹配数超过 20 时仅返回成员签名清单 | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前 200 行 | 否 |
| | `timeoutSeconds` | 本次反编译回源超时秒数（默认 30） | 否 |
| `ilspy_decompile_to_dir` | `assembly` | 程序集文件路径 | 是 |
| | `outputDir` | 输出目录；结果写入磁盘而非标准输出 | 是 |
| | `typeName` | 仅反编译指定全限定类型名；省略则反编译整个程序集 | 否 |
| | `nestedDirectories` | 输出到目录时按命名空间使用嵌套目录（默认 true） | 否 |
| | `timeoutSeconds` | 本次反编译写盘超时秒数（默认 30，全量写盘大程序集可调大） | 否 |
| `ilspy_decompile_to_project` | `assembly` | 程序集文件路径 | 是 |
| | `outputDir` | 输出目录；结果写入磁盘而非标准输出 | 是 |
| | `nestedDirectories` | 输出到目录时按命名空间使用嵌套目录（默认 true） | 否 |
| | `timeoutSeconds` | 本次反编译写盘超时秒数（默认 30，全量写盘大程序集可调大） | 否 |
| `ilspy_list_types` | `assembly` | 程序集文件路径 | 是 |
| | `list` | 实体类型类别组合：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 `csi` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前 200 行 | 否 |
| `ilspy_signature` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Formatting.OutputFormatter` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前 200 行 | 否 |
| `ilspy_hierarchy` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Processes.ProcessRunner` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前 200 行 | 否 |
| `ilspy_dependencies` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Caching.DecompileCache` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前 200 行 | 否 |
| `ilspy_call_graph` | `assembly` | 程序集文件路径 | 是 |
| | `typeName` | 目标类型的全限定名，格式与 list_types 输出一致，例如 `ILSpyMcp.Caching.DecompileCache` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end`；省略返回前 200 行 | 否 |

## 使用示例

在 opencode 对话中直接提出：

- **列出所有类**：> 列出 `bin/Debug/MyApp.dll` 中的所有 class 类型
- **列出多类类型**：> 列出 `bin/Debug/MyApp.dll` 中的 class、interface、struct 类型（`list="csi"`）
- **全量反编译**：> 反编译 `bin/Debug/MyApp.dll` 到 `src` 目录（必须指定 `outputDir`）
- **项目形式 + 嵌套目录**：> 以可编译项目形式反编译 `bin/Debug/MyApp.dll` 到 `src`，并按命名空间嵌套目录
- **单个类型**：> 反编译 `bin/Debug/MyApp.dll` 中的 `MyApp.Program` 类型
- **按名搜索成员**：> 在 `bin/Debug/MyApp.dll` 的 `MyApp.Program` 中搜索名称包含 `Main` 的成员并反编译
- **成员签名（API 地图）**：> 列出 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的全部成员签名
- **继承关系**：> 查看 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的基类链、接口与继承者
- **内部引用**：> 查询 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的成员签名引用了哪些内部类型、以及哪些类型引用了它
- **方法体调用关系**：> 查询 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的方法体调用了哪些内部类型、以及哪些类型的方法体调用了它
- **按行拉取**：> 反编译 `bin/Debug/MyApp.dll` 中的 `MyApp.Program`，读取第 200-400 行

## License

MIT
