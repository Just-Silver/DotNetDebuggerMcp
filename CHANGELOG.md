# Changelog

本文件记录 ILSpyMcp 各版本的变更，格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循[语义化版本](https://semver.org/lang/zh-CN/)。

版本号与 `src/ILSpyMcp/ILSpyMcp.csproj` 的 `<Version>` 保持一致；发布时 `<PackageReleaseNotes>` 自动提取当前版本对应段落。未发布的变更统一记录在 `[Unreleased]`，发布时再转为带日期的版本段落。

## [1.1.2] - 2026-08-11

### Added

- 环境自检新增 NuGet 更新检查：检查 ilspymcp 是否有新版本，结果落盘跨进程共享（成功 TTL 24h、失败 1h 退避、失败保留旧值），避免每次会话都联网复查
- MCP 握手期经 `ServerInstructions` 注入完整环境自检报告（ilspycmd 安装/版本 + NuGet 更新状态），agent 会话起始即可感知环境；握手后台异步刷新 NuGet 磁盘缓存供下次会话
- 版本比较共用 `IsNewerThanCurrent` 静态方法（环境自检报告与握手注入两处调同一规则），防版本比较规则漂移

### Changed

- **check_status 不再暴露为 MCP 工具**（破坏性变更）：环境自检报告改由握手期注入 `ServerInstructions`，agent 无需手动调用；`CheckStatus` 保留供 CLI `-c/--check` 调试
- 源码按功能拆分命名空间（原 `Infrastructure` 拆为 `Configuration`/`Services`/`Pipeline`/`Processes`/`Caching`/`Formatting`/`Metadata`/`UpdateCheck`），消除 `Infrastructure` 大杂烩
- 解除命名空间循环依赖：`UpdateChecker` 构造注入查询委托、`EnvironmentChecker` 依赖经参数传入、`ToolExecutor` 移入 Services 层、`InstallChecker` 改用 `AppConfig.IlspyCmdExecutable` 常量，依赖方向单向化（Services → 各功能层 → Tools）
- CLI `-c/--check` 调用前先刷新 NuGet 缓存（TTL/退避内不联网），无缓存记录时 NuGet 段不再永远留白
- 多行提示/报告文本统一改用 `Environment.NewLine`（环境自检报告、ilspycmd 退出码错误提示、Client 终端输出），跨平台换行正确

### Fixed

- 握手期 `StatusReport` 环境自检异常不再阻断 MCP 启动：降级为空注入提示，核心反编译功能不受影响
- `GetCachedNuGetLine` 复用已解析版本（`IsNewerThanCurrent` 新增 `Version` 重载），消除重复 TryParse
- 修复 `AppConfig` XML cref 无法解析（改用全限定名）与文件尾缺失换行

## [1.1.1] - 2026-08-10

### Changed

- decompile_to_dir 的 `nestedDirectories` 默认改为 `true`（省略即按命名空间嵌套目录输出）
- 全部工具参数描述补齐默认值说明（`lines` 缺省返回前 200 行、`project` 默认 false、`nestedDirectories` 默认 true、`languageVersion` 省略使用 ilspycmd 默认），消除 agent 对默认行为的盲区
- decompile_to_dir 的 `typeName` 参数描述注明「仅 project=false 时生效；project=true 时项目模式忽略并全量输出」，消除描述与实现不符（ilspycmd 项目模式会静默忽略 `-t`）

## [1.1.0] - 2026-08-10

### Added

- 新增 check_status 工具（CLI 同步 `-c/--check`）：环境自检——ilspycmd 是否安装、版本是否满足要求（>= 11.0，-m 单成员反编译所需）、当前 ilspymcp 是否有新版本；结果会话内缓存（仅首次真实检查），NuGet 网络失败/超时静默跳过该检查项
- MCP 工具（decompile / decompile_member / list_types / decompile_to_dir）调用支持取消：客户端取消一次调用时立即终止 ilspycmd 子进程，避免孤儿进程占用资源（内部逐层传递 CancellationToken）
- MCP 工具（decompile / list_types）输出前置头部信息块（程序集/目标/内容），明确代码归属与当前切片位置，缓存命中时同样携带
- decompile_to_dir 成功提示并入来源程序集

### Changed

- 消除 Infrastructure → Tools 交叉依赖：环境自检报告组装下沉至 Infrastructure 的 EnvironmentChecker，AppServices 不再反向引用工具层
- ToolCommand 显式持有 Assembly 属性，执行管道签名收敛，杜绝「管道实参」与「命令内路径」双份程序集数据导致缓存 key 错配
- 工具执行样板收敛至共享 ToolExecutor（统一路径解析与管道/子进程调用），消除各工具重复代码与细节漂移
- check_status 环境自检：InstallChecker 的安装状态与版本解析合并为同一检查任务，消除先读版本为空的时序契约

### Fixed

- 移除头部信息块的「参数」行：agent 面对的是 MCP 命名参数，ilspycmd 内部命令行参数（如 `-m token`、`-t`、`-l`）对 agent 无意义且会误导
- 修复 list_types 空结果（如列出不存在的实体类别）静默无提示的问题
- 修复 .mcp/server.json 缩进错乱
- Program.cs 增加 MCP 装配期异常兜底（启动失败时 stderr 中文提示 + 非零退出码，不再暴露崩溃堆栈）
- 删除冗余的 AppServices.DefaultTimeout（统一使用 AppConfig.DefaultTimeout）
- 测试仓库根探测改为逐级上溯查找 ILSpyMcp.slnx，消除硬编码上溯层数
