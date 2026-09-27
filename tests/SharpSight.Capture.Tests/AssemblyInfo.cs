// Capture 测试共享 UiSampleApp 子进程（UiSampleAppProcess 的 KillLeftovers 按进程名清理全部同名进程）
// 与全局屏幕/DPI 状态，并发运行会互相杀掉目标窗口 → 必须串行（与迁出前 Engine.Tests 行为一致）。
[assembly: Xunit.v3.Parallelization(Mode = Xunit.Sdk.ParallelMode.None)]
