using ILSpyMcp.Infrastructure;

namespace ILSpyMcp.Tests;

/// <summary>测试用 fake 进程执行器，可配置退出码/输出/延迟并统计调用次数。</summary>
internal sealed class FakeProcessRunner : IProcessRunner
{
    public int Code = 0;
    public string Stdout = "";
    public string Stderr = "";
    public TimeSpan? Timeout;
    public TimeSpan? Delay;

    public int CallCount { get; private set; }
    public string? LastExecutable { get; private set; }
    public IReadOnlyList<string>? LastArgs { get; private set; }
    public CancellationToken LastToken { get; private set; }

    public async Task<ProcessResult> RunAsync(string executable, IReadOnlyList<string> args, string cwd, TimeSpan? timeout = null, CancellationToken cancellationToken = default)
    {
        CallCount++;
        Timeout = timeout;
        LastExecutable = executable;
        LastArgs = args;
        LastToken = cancellationToken;
        if (Delay is { } d) await Task.Delay(d, cancellationToken);
        return new ProcessResult(Code, Stdout, Stderr);
    }
}
