# ilspymcp

基于 [ilspycmd](https://github.com/icsharpcode/ilspy) 的 .NET 反编译 MCP 服务器。在 [opencode](https://opencode.ai) 等 MCP 客户端中可直接将 .NET 程序集（dll/exe）反编译为 C# 源码、列出类型，或把整个程序集反编译写入指定目录。

## 环境要求

- [.NET SDK 10](https://dotnet.microsoft.com/download)

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

## 工具

| MCP 工具 | 用途 |
| ---- | ---- |
| `ilspy_decompile` | 反编译单个类型/成员到标准输出，输出带行号标注，支持按行号范围分页拉取 |
| `ilspy_list_types` | 列出程序集中的实体类型（class/interface/struct/delegate/enum，可组合指定），输出带行号标注 |
| `ilspy_decompile_to_dir` | 将程序集反编译写入指定目录（全量/项目/单类型） |

`ilspy_decompile` 与 `ilspy_list_types` 默认仅输出前 200 行，可用 `lines` 参数按行号分页拉取；`ilspy_decompile_to_dir` 结果写盘、不做行数截断。结果按「程序集 + 参数」缓存在内存，程序集更新后自动失效；`ilspycmd` 未安装时仅提示，不代为执行。

### 工具参数

| 工具 | 参数 | 说明 | 必填 |
| ---- | ---- | ---- | ---- |
| `ilspy_decompile` | `assembly` | 程序集文件路径（.dll/.exe），可为相对当前工作目录的路径 | 是 |
| | `typeName` | 仅反编译指定全限定类型名，例如 `System.String` | 与 `member` 至少其一 |
| | `member` | 反编译单个成员：XML 文档 ID（如 `M:System.String.Concat(System.String,System.String)`）或元数据 token（如 `0x06000005`） | 与 `typeName` 至少其一 |
| | `languageVersion` | C# 语言版本，如 `CSharp8_0`、`CSharp12_0`、`CSharp13_0`、`Latest` | 否 |
| | `lines` | 按行号范围读取结果，格式 `start-end`（1-based 含两端，单次最多 500 行），如 `200-400` | 否 |
| `ilspy_list_types` | `assembly` | 程序集文件路径 | 是 |
| | `list` | 实体类型类别组合：c=class, i=interface, s=struct, d=delegate, e=enum，可组合如 `csi` | 是 |
| | `lines` | 按行号范围读取结果，格式 `start-end` | 否 |
| `ilspy_decompile_to_dir` | `assembly` | 程序集文件路径 | 是 |
| | `outputDir` | 输出目录；结果写入磁盘而非标准输出 | 是 |
| | `project` | 以可编译项目形式反编译（每个类型一个源码文件） | 否 |
| | `typeName` | 仅反编译指定全限定类型名；省略则反编译整个程序集 | 否 |
| | `nestedDirectories` | 输出到目录时按命名空间使用嵌套目录 | 否 |
| | `languageVersion` | C# 语言版本 | 否 |

## 使用示例

在 opencode 对话中直接提出：

- **列出所有类**：> 列出 `bin/Debug/MyApp.dll` 中的所有 class 类型
- **列出多类类型**：> 列出 `bin/Debug/MyApp.dll` 中的 class、interface、struct 类型（`list="csi"`）
- **全量反编译**：> 反编译 `bin/Debug/MyApp.dll` 到 `src` 目录（必须指定 `outputDir`）
- **项目形式 + 嵌套目录**：> 以可编译项目形式反编译 `bin/Debug/MyApp.dll` 到 `src`，并按命名空间嵌套目录
- **单个类型**：> 反编译 `bin/Debug/MyApp.dll` 中的 `MyApp.Program` 类型
- **单个成员**：> 反编译 `bin/Debug/MyApp.dll` 中 `MyApp.Program` 的 `Main` 方法
- **按行拉取**：> 反编译 `bin/Debug/MyApp.dll` 中的 `MyApp.Program`，读取第 200-400 行

## License

MIT
