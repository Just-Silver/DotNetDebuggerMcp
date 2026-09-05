// 调试相关测试需串行执行（attach 真实目标进程，ICorDebug 会话相互干扰）。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
