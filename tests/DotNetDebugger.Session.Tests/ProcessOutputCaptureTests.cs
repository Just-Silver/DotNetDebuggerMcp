using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>ProcessOutputCapture 纯内存测试：环形上限/尾部顺序/空行忽略（无进程，快速）。</summary>
public sealed class ProcessOutputCaptureTests
{
    [Fact]
    public void Append_ExceedsMaxLines_DropsOldest()
    {
        var capture = new ProcessOutputCapture();
        var before = DateTimeOffset.Now.AddSeconds(-5);
        for (var i = 1; i <= ProcessOutputCapture.MaxLines + 10; i++)
            capture.Append(ProcessOutputStream.Stdout, $"line-{i}");

        Assert.Equal(ProcessOutputCapture.MaxLines, capture.Count);
        var tail = capture.Tail(ProcessOutputCapture.MaxLines);
        // 逐最旧：最前是 line-11，最后是 line-510；Sequence 连续；Timestamp 在锁外入口取、落在合理窗口
        Assert.Equal("line-11", tail[0].Text);
        Assert.Equal($"line-{ProcessOutputCapture.MaxLines + 10}", tail[^1].Text);
        Assert.Equal(tail[^1].Sequence, tail[0].Sequence + tail.Count - 1);
        Assert.InRange(tail[^1].Timestamp, before, DateTimeOffset.Now.AddSeconds(5));
    }

    [Fact]
    public void Tail_PreservesOldToNewOrder_AndStreamKind()
    {
        var capture = new ProcessOutputCapture();
        capture.Append(ProcessOutputStream.Stdout, "a-out");
        capture.Append(ProcessOutputStream.Stderr, "b-err");
        capture.AppendSystem("c-exit");

        var tail = capture.Tail(10);
        Assert.Equal(3, tail.Count);
        Assert.Equal([ "a-out", "b-err", "c-exit" ], tail.Select(l => l.Text).ToArray());
        Assert.Equal(ProcessOutputStream.Stdout, tail[0].Stream);
        Assert.Equal(ProcessOutputStream.Stderr, tail[1].Stream);
        Assert.Equal(ProcessOutputStream.Stdout, tail[2].Stream);
    }

    [Fact]
    public void Append_EmptyText_Ignored()
    {
        var capture = new ProcessOutputCapture();
        capture.Append(ProcessOutputStream.Stdout, "");
        capture.Append(ProcessOutputStream.Stderr, null!);

        Assert.Equal(0, capture.Count);
        Assert.Empty(capture.Tail(10));
    }
}
