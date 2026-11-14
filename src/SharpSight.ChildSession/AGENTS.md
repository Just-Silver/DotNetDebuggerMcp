<!-- 职责：维护子会话能力库的依赖边界、所有权与验证入口；不写：产品工具用法或历史实验结果。 -->

# SharpSight.ChildSession

Windows 子会话执行能力库，不依赖 MCP、宿主或调试 Session 库。生命周期、请求、任务和输入分别管理资源；不得回退父会话输入。

- 请求结果未知时保留 requestId，禁止自动重放变更；执行中的请求不得因结果记录限额被淘汰。
- 真实输入必须核对实例、连接、实际会话、input desktop 和目标身份；停止只注销核实属于本实例的 child。
- 新代码单方法尽量不超过 60 行，超过 80 行必须拆分。
- 行为测试位于 `tests/DotNetDebuggerMcp.Tests/Child*Tests.cs`；真实 child 集成验收仅显式授权运行，不自动修改系统开关。
- 定向 MTP 验证优先传完整测试类名，并核对发现数；本机 `--filter-class "*Child*Tests"` 曾运行零个测试并退出 5，不能当作通过。已验证完整类名 `DotNetDebuggerMcp.Tests.ChildRequestTests` / `ChildIpcTests` 可用。
- 正式设计与任务见根规划地图中的子会话 MCP/CLI 设计及实施计划。

## IPC 与产物边界

管道使用 CurrentUserOnly，并在收发两端以原生对端 PID 核对创建时间、SID、Session 和映像；客户端自报字段不算证据。实例密钥仅放受 ACL 限制的实例目录，不进参数或正常日志。长度前缀在分配正文前校验，单帧至多 1 MiB；截断帧只结束当前连接。

核验 API：[GetNamedPipeClientProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeclientprocessid)、[GetNamedPipeServerProcessId](https://learn.microsoft.com/en-us/windows/win32/api/winbase/nf-winbase-getnamedpipeserverprocessid)。这不是同账户恶意程序的安全沙箱。

产物不能越过实例目录或经过重解析点；父侧读取核对长度，单个落盘产物读取上限 64 MiB，避免伪造长度造成无界内存分配。PNG 真正内容及其新鲜性仍由截图业务验证。

## 任务进程

先挂起创建、归入自有 Job、再恢复；不能先让应用运行再追认所有权。每任务 Job 使用关闭即结束标志，仅取消本任务进程树，不按进程名结束应用。任务日志达到限额后继续排空管道；stdin 的父写端不可被 child 继承，否则 child 会因自己的写端而永远收不到 EOF。

原生契约：[AssignProcessToJobObject](https://learn.microsoft.com/en-us/windows/win32/api/jobapi2/nf-jobapi2-assignprocesstojobobject)（Job 内进程必须同 Session）、[扩展限额结构](https://learn.microsoft.com/en-us/windows/win32/api/winnt/ns-winnt-jobobject_extended_limit_information)。

## GUI 执行边界

实例共享 `ChildGuiExecutionGate`。已开始的 UIA COM 调用必须等待真实后台 Task 结束，期间保留客户端和门；截止只让调用方取回 running，不将其登记为终态。超预算仍未返回时拒绝新 GUI 操作，控制查询与进程任务不经过此门。

`ui_wait` 仅在订阅、探测和退订时取门，等待信号或轮询睡眠时放门；不推进帧、不替换清单。门内顺序调用允许同一执行上下文重入，不能把重入上下文用于并发派生 GUI 工作。
