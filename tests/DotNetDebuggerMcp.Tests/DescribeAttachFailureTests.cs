using DotNetDebuggerMcp.Tools.Debugger;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// P2：附加失败的中文化（把裸 COM/HRESULT 错误转成对 agent 可操作的中文提示）。纯函数，恒跑。
/// </summary>
public sealed class DescribeAttachFailureTests
{
    [Theory]
    [InlineData("Error HRESULT ERROR_INVALID_PARAMETER has been returned from a call to a COM component.")]
    [InlineData("0x80070057")]
    public void 已知占用类错误_给可操作中文与恢复路径(string raw)
    {
        var text = DebugSessionTool.DescribeAttachFailure(new InvalidOperationException(raw));
        Assert.Contains("已被", text);
        Assert.Contains("debug_session(action=disconnect)", text);
        Assert.DoesNotContain("ERROR_INVALID_PARAMETER", text); // 原文不再裸露
    }

    [Fact]
    public void 已有会话的中文提示_原样返回()
    {
        const string msg = "已有活动调试会话（pid=123）。同一时刻仅支持一个调试会话，请先 debug_session(action=disconnect) 或 debug_session(action=terminate) 后再附加。";
        var text = DebugSessionTool.DescribeAttachFailure(new InvalidOperationException(msg));
        Assert.Equal(msg, text);
    }

    [Fact]
    public void 未识别错误_保留原文()
    {
        var text = DebugSessionTool.DescribeAttachFailure(new InvalidOperationException("某种未知失败"));
        Assert.Contains("某种未知失败", text);
        Assert.Contains("附加调试失败", text);
    }
}
