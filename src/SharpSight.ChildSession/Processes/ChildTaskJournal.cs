namespace SharpSight.ChildSession.Processes;

internal sealed class ChildTaskJournal(string id, WindowsJob job, ChildOutputStore output)
{
    internal string Id { get; } = id;
    internal WindowsJob Job { get; } = job;
    internal ChildOutputStore Output { get; } = output;
    internal Task Completion { get; set; } = Task.CompletedTask;
    internal bool Cancelled;
    internal string? Failure;

    internal object Snapshot() => new
    {
        taskId = Id, processId = Job.Process.Id, startedTicks = Job.StartedTicks,
        running = Job.HasProcesses || !Completion.IsCompleted,
        exitCode = Job.Process.HasExited ? (int?)Job.Process.ExitCode : null,
        cancelled = Cancelled, error = Failure,
        note = "仅报告本任务自有Job；启动器退出不证明被转交的可见应用已退出。"
    };
}
