# ilspymcp

基于 [ilspycmd](https://github.com/icsharpcode/ilspy) 的反编译 MCP 服务器，使用官方 [ModelContextProtocol](https://www.nuget.org/packages/ModelContextProtocol) C# SDK 构建，通过 stdio 传输与 MCP 客户端通信。前置条件：用户已安装 `ilspycmd`（`dotnet tool install --global ilspycmd`），本服务器内部仍通过子进程调用它，不内置反编译库。

## 构建

```bash
dotnet build -c Release
```

## 本地运行（源码方式）

```bash
dotnet run -c Release
```

在 opencode 中配置：

```json
{
  "mcp": {
    "ilspy": {
      "type": "local",
      "command": ["dotnet", "run", "--project", "src/ILSpyMcp"]
    }
  }
}
```

## 发布到 NuGet.org

1. 运行 `dotnet pack -c Release` 生成 nupkg
2. 推送到 NuGet.org：

```bash
dotnet nuget push bin/Release/*.nupkg --api-key <your-api-key> --source https://api.nuget.org/v3/index.json
```

发布成功后用户安装：

```bash
dotnet tool install --global ilspymcp
```

在 opencode 中配置：

```json
{
  "mcp": {
    "ilspy": {
      "type": "local",
      "command": ["ilspymcp"]
    }
  }
}
```

## 工具

| 工具 | 说明 |
| ---- | ---- |
| `decompile` | 反编译单个类型/成员到标准输出，支持 `lines` 行号分页 |
| `list_types` | 列出程序集中的实体类型（class/interface/struct/delegate/enum） |
| `decompile_to_dir` | 将程序集反编译写入指定目录（全量/项目/单类型） |

三个工具参数语义与行为详见仓库根目录 `README.md`。

## 注意事项

- 本服务器为框架依赖（framework-dependent）应用，安装机器需具备 .NET 10 运行时。
- 日志输出到 stderr，不污染 stdout 上的 MCP 协议消息。
