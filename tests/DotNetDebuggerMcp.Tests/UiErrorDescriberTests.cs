using FlaUI.Core.Exceptions;
using FlaUI.Core.Tools;

using SharpSight.UiAutomation;

using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using Xunit;

using FlaUICom = FlaUI.Core.Tools.Com;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// <see cref="UiErrorDescriber"/> 业务规则（纯函数，不触碰 COM）：
/// ① 已知 UIA 错误码必须译成可操作的中文成因（禁用控件不得再表现为「调用失败：」空消息）；
/// ② 排查引导按成因分流——禁用不再被引去「后台/不可见」；
/// ③ 无法识别时不得丢信息（保留原始消息与异常类型）。
/// </summary>
[SupportedOSPlatform("windows7.0")]
public sealed class UiErrorDescriberTests
{
    /// <summary>禁用的 TextBox 上 SetValue 的真实异常形状：外层 FlaUI 包装（空消息）+ 内层 COM 错误码。</summary>
    private static ElementNotEnabledException DisabledElementError()
        => new(new COMException("0x80040200", unchecked((int)FlaUICom.UIA_E_ELEMENTNOTENABLED)));

    [Fact]
    public void Describe_DisabledControl_NamesTheRealCauseInsteadOfEmptyMessage()
    {
        var text = UiErrorDescriber.Describe(DisabledElementError());

        // 旧实现只取 ex.Message（FlaUI 该系列恒为空串）→ 输出「SetValue 调用失败：」。
        Assert.Contains("禁用", text);
        Assert.Contains("IsEnabled", text);
        Assert.DoesNotContain("调用失败：", text);
    }

    [Fact]
    public void Describe_KeepsErrorCodeAndExceptionTypeForDiagnosis()
    {
        var text = UiErrorDescriber.Describe(DisabledElementError());

        // 错误码是唯一能闭环归因的字段，必须透出（外层包装码 0x80131500 无信息量，取内层成因码）。
        Assert.Contains("0x80040200", text);
        Assert.Contains(nameof(ElementNotEnabledException), text);
    }

    [Fact]
    public void Hint_DisabledControl_PointsAtEnablingRatherThanOffscreen()
    {
        var hint = UiErrorDescriber.Hint(DisabledElementError());

        Assert.Contains("enabled", hint);
        // 2026-10-26 误诊链：恒追加的「后台/不可见」把排查引向窗口状态。
        Assert.DoesNotContain("offscreen", hint);
    }

    [Fact]
    public void Hint_UnimplementedProvider_SuggestsAnotherDrivePath()
    {
        var hint = UiErrorDescriber.Hint(new NotImplementedException());

        Assert.Contains("clickPoint", hint);
        Assert.DoesNotContain("offscreen", hint);
    }

    [Fact]
    public void Hint_StaleElement_AsksForFreshFind()
    {
        var hint = UiErrorDescriber.Hint(new ElementNotAvailableException(
            new COMException("gone", unchecked((int)FlaUICom.UIA_E_ELEMENTNOTAVAILABLE))));

        Assert.Contains("ui_find", hint);
        Assert.DoesNotContain("offscreen", hint);
    }

    [Fact]
    public void Hint_UnrecognizedCause_FallsBackToOffscreenGuidance()
    {
        // 成因确实无法识别时，「后台/不可见」仍是合理猜测——只有已知成因才分流。
        // 注意不能用 InvalidOperationException：它的默认 HResult 恰是 0x80131509（== UIA_E_INVALIDOPERATION），
        // 会命中「状态不允许」分支。
        var hint = UiErrorDescriber.Hint(new IOException("某框架自定义错误"));

        Assert.Equal(UiErrorDescriber.FallbackHint, hint);
        Assert.Contains("offscreen", hint);
    }

    [Fact]
    public void Hint_InvalidOperation_CheckedAsDisabledOrUnfocusable()
    {
        // focus 对禁用控件抛的正是 InvalidOperationException（HResult 0x80131509）——必须引导去查可用性，
        // 而不是「后台/不可见」（2026-10-26 误诊链的起点）。
        var hint = UiErrorDescriber.Hint(new InvalidOperationException("Operation is not valid due to the current state of the object."));

        Assert.Contains("enabled", hint);
        Assert.DoesNotContain("offscreen", hint);
    }

    [Fact]
    public void Describe_UnrecognizedCause_KeepsOriginalMessage()
    {
        var text = UiErrorDescriber.Describe(new IOException("目标窗口已关闭"));

        Assert.Contains("目标窗口已关闭", text);
        Assert.Contains(nameof(IOException), text);
    }

    [Fact]
    public void DescribeCode_MapsEveryDocumentedUiaFailureCode()
    {
        // 逐个码表项都必须是可操作中文，不得漏成 null（漏了就会退化成「调用失败：」）。
        uint[] codes =
        [
            FlaUICom.UIA_E_ELEMENTNOTENABLED, FlaUICom.UIA_E_ELEMENTNOTAVAILABLE, FlaUICom.UIA_E_NOCLICKABLEPOINT,
            FlaUICom.UIA_E_PROXYASSEMBLYNOTLOADED, FlaUICom.UIA_E_NOTSUPPORTED, FlaUICom.UIA_E_TIMEOUT,
            FlaUICom.UIA_E_INVALIDOPERATION, 0x80004001u, 0x80070005u,
        ];

        foreach (var code in codes)
            Assert.False(string.IsNullOrWhiteSpace(UiErrorDescriber.DescribeCode(code)), $"错误码 0x{code:X8} 未翻译");
    }
}
