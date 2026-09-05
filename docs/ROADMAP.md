# ROADMAP（远期待办）

> 记录「暂不做、以后再评估」的功能想法，防止丢失。当前迭代范围见 `CHANGELOG.md` 的 `[Unreleased]` 段；进行中的 P4-2 WebUI 待办见 `docs/planning/open-questions.md` #7。

## v2 候选（vision §4.3「后做」，不阻塞当前）
- **表达式求值安全子集**（调试器 watch 表达式，参考 debug-mcp AST 求值路线，research/01 §4）
- **PDB 行断点**（有 PDB 程序集按源行断点；当前反编译无 PDB 用 IL offset 断点）
- **模块延迟绑定断点**（模块未加载时设 pending 断点，Engine v2）
- **EventPipe / ClrMD 旁路**（轻量运行期观察，不走 ICorDebug 全 attach）
- **多调试会话并行**（当前 v1 单活动会话）

## WebUI 后续（P4 收尾后的体验项）
- **停点模块路径查询**（Engine 暴露模块短名→全路径，让断点命中无条件跳到停点类型/方法——当前只当 agent 正在看停点模块时才跟随）
- **断点红点显示**（Monaco 编辑器 glyph 区显示断点）
- **刷新后代码编辑器内容保持**（Blazor Server 组件状态持久化）
- **调试面板增强**（可选）：调用栈/局部变量/线程面板的实时性提升、加字段（如对象/数组查看、表达式输入）——当前已能展示停点快照，增强留待后续
- **事件日志 / agent 轨迹时间线**（P4.2 可选，用户拍板列为待办）
- **`web_open` 幂等 MCP 工具**（agent 按需开 Web 可视化；MCP server 默认不应带 `--web`）
- **razor 双文件拆分**（`X.razor` + `X.razor.cs` code-behind，Blazor 规范）

## 其它远期
- 全功能表达式求值 / 动态 EnC 排除项 / Mono 目标
