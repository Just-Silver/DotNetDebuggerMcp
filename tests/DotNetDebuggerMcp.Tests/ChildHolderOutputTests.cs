using System.Diagnostics;
using System.Text.Json;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildHolderOutputTests
{
    [Fact]
    public async Task 调用端退出后仍存活的持有器不能阻止调用端输出读到结尾()
    {
        var root = Path.Combine(Path.GetTempPath(), "child-holder-output-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "output-probe"));
        var executable = Path.Combine(TestDataPaths.RepositoryRoot, "tests", "TestData", "ChildSessionTarget", "ChildSessionTarget.exe");
        using var parent = Process.Start(new ProcessStartInfo(executable)
        {
            UseShellExecute = false, RedirectStandardOutput = true, RedirectStandardError = true,
            ArgumentList = { "holder-parent", root }
        })!;
        Process? holder = null;
        var output = parent.StandardOutput;
        var error = parent.StandardError.ReadToEndAsync(TestContext.Current.CancellationToken);
        try
        {
            var first = await output.ReadLineAsync(TestContext.Current.CancellationToken);
            using var value = JsonDocument.Parse(first!);
            holder = Process.GetProcessById(value.RootElement.GetProperty("ProcessId").GetInt32());
            Assert.Equal(value.RootElement.GetProperty("StartedTicks").GetInt64(), holder.StartTime.ToUniversalTime().Ticks);
            await parent.WaitForExitAsync(TestContext.Current.CancellationToken);
            Assert.False(holder.HasExited);
            var tail = await output.ReadToEndAsync(TestContext.Current.CancellationToken)
                .WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken);
            Assert.Empty(tail);
            Assert.Empty(await error.WaitAsync(TimeSpan.FromSeconds(2), TestContext.Current.CancellationToken));
            Assert.False(holder.HasExited);
        }
        finally
        {
            if (holder is not null)
            {
                if (!holder.HasExited) holder.Kill(true);
                await holder.WaitForExitAsync(TestContext.Current.CancellationToken);
                holder.Dispose();
            }
            if (!parent.HasExited) { parent.Kill(true); await parent.WaitForExitAsync(TestContext.Current.CancellationToken); }
            if (Directory.Exists(root)) Directory.Delete(root, true);
        }
    }
}
