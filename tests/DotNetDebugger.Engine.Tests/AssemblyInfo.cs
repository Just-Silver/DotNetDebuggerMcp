// 进程内调试测试（attach 真实 .NET 目标进程、设断点/单步/异常）必须串行执行：
// 并行 attach 多个目标进程会导致 ICorDebug/dbgshim 会话相互干扰（实测断点类测试并发时集体超时）。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
