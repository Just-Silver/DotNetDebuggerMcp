# Changelog

本文件记录 ILSpyMcp 各版本的变更，格式遵循 [Keep a Changelog](https://keepachangelog.com/zh-CN/1.1.0/)，版本遵循[语义化版本](https://semver.org/lang/zh-CN/)。

版本号与 `src/ILSpyMcp/ILSpyMcp.csproj` 的 `<Version>` 保持一致；发布时 `<PackageReleaseNotes>` 自动提取当前版本对应段落。

## [1.1.0] - 2026-08-10

### Added

- MCP 工具（decompile / list_types）输出前置头部信息块（程序集/目标/参数/内容），明确代码归属与当前切片位置，缓存命中时同样携带
- decompile_to_dir 成功提示并入来源程序集

### Fixed

- 修复 list_types 空结果（如列出不存在的实体类别）静默无提示的问题
