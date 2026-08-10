# Changelog

本文件记录 ILSpyMcp 各版本的变更，格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循[语义化版本](https://semver.org/lang/zh-CN/)。

版本号与 `src/ILSpyMcp/ILSpyMcp.csproj` 的 `<Version>` 保持一致；发布时 `<PackageReleaseNotes>` 自动提取当前版本对应段落。未发布的变更统一记录在 `[Unreleased]`，发布时再转为带日期的版本段落。

## [1.1.0] - 2026-08-10

### Added

- 新增 check_status 工具（CLI 同步 `-c/--check`）：环境自检——ilspycmd 是否安装、版本是否满足要求（>= 11.0，-m 单成员反编译所需）、当前 ilspymcp 是否有新版本；结果会话内缓存（仅首次真实检查），NuGet 网络失败/超时静默跳过该检查项
- MCP 工具（decompile / decompile_member / list_types / decompile_to_dir）调用支持取消：客户端取消一次调用时立即终止 ilspycmd 子进程，避免孤儿进程占用资源（内部逐层传递 CancellationToken）
- MCP 工具（decompile / list_types）输出前置头部信息块（程序集/目标/内容），明确代码归属与当前切片位置，缓存命中时同样携带
- decompile_to_dir 成功提示并入来源程序集
- CI 新增端到端验证步骤（安装 ilspycmd + 运行 Client 走全部工具），端到端验证不再依赖手工执行

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
