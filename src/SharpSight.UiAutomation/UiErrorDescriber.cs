using FlaUI.Core.Tools;

using System.Runtime.Versioning;

using FlaUICom = FlaUI.Core.Tools.Com;

namespace SharpSight.UiAutomation;

/// <summary>
/// UIA 调用失败的<b>成因说明 + 排查引导</b>（纯函数，可单测）。
/// <para>FlaUI 的 <c>Exceptions.*</c> 系列全部用 <c>base(String.Empty, innerException)</c> 构造，<c>Message</c> 恒为空串，
/// 真正的成因（如 <c>UIA_E_ELEMENTNOTENABLED</c>）只保留在 <c>InnerException.HResult</c>。旧实现只取 <c>ex.Message</c>，
/// 于是「控件禁用」这类明确错误对外表现为「调用失败：」——错误码被丢弃，调用方只能猜（2026-10-26 误诊报告的根因）。</para>
/// <para>另一处旧缺陷：所有 pattern 失败都被追加同一句「后台/不可见」引导，与被禁用等成因矛盾。本类按成因分流引导，
/// 仅在确实无法识别时才退回该通用提示。</para>
/// </summary>
[SupportedOSPlatform("windows7.0")]
internal static class UiErrorDescriber
{
    /// <summary>成因无法从错误码识别时的兜底引导（沿用原「后台/不可见」话术）。</summary>
    internal const string FallbackHint =
        "；若为目标后台/不可见导致 provider 拒绝，可用 ui_get what=offscreen 确认，必要时先 ui_action verb=windowstate windowstate=normal 还原最小化窗口。";

    /// <summary>已知错误码 → 中文成因；未知返回 null（交由调用方用原始消息兜底）。</summary>
    internal static string? DescribeCode(uint hresult) => hresult switch
    {
        FlaUICom.UIA_E_ELEMENTNOTENABLED => "控件已禁用（IsEnabled=false），UIA 拒绝在禁用控件上执行该操作",
        FlaUICom.UIA_E_ELEMENTNOTAVAILABLE => "元素已失效（界面已重建或控件已销毁）",
        FlaUICom.UIA_E_NOCLICKABLEPOINT => "该控件没有可点击点（不在屏幕内或被完全遮挡）",
        FlaUICom.UIA_E_PROXYASSEMBLYNOTLOADED => "UIA 代理程序集加载失败（目标框架的自定义 Automation 代理缺失）",
        FlaUICom.UIA_E_NOTSUPPORTED => "目标控件的 UIA provider 明确不支持该属性/操作",
        FlaUICom.UIA_E_TIMEOUT => "UIA 调用超时（目标未在约定时间内响应）",
        FlaUICom.UIA_E_INVALIDOPERATION => "当前状态不允许该操作（常见于控件禁用、不可聚焦，或所在窗口状态异常）",
        0x80004001u => "目标控件的 UIA provider 未实现该操作（E_NOTIMPL）",
        0x80070005u => "权限不足（目标以更高权限运行，需同权限访问）",
        _ => null,
    };

    /// <summary>
    /// 一条可读的失败说明：优先用错误码成因，其次用原始消息，最后用异常类型兜底。
    /// 尾部恒附「异常类型 + 主要错误码」——信息不丢失，agent 据以归因而非猜测。
    /// </summary>
    internal static string Describe(Exception ex)
    {
        var codes = Codes(ex);
        var cause = KnownCause(codes)
            ?? (string.IsNullOrWhiteSpace(ex.Message) ? "UIA 调用失败" : ex.Message.Trim());
        var code = PrimaryCode(codes);
        var codeText = code is { } c ? $" 0x{c:X8}" : "";
        return $"{cause}（{ex.GetType().Name}{codeText}）";
    }

    /// <summary>与成因匹配的排查引导：禁用不再引导去查「后台/不可见」，而是引导确认可用性。</summary>
    internal static string Hint(Exception ex)
    {
        var codes = Codes(ex);
        if (codes.Contains(FlaUICom.UIA_E_ELEMENTNOTENABLED))
            return "；该控件当前禁用——先让它变为可用（常见前置：先选择设备/勾选开关/完成上一步），并用 ui_get what=enabled 确认后再操作。";
        if (codes.Contains(0x80004001u))
            return "；该控件不支持这条驱动路径，可改用其它入口（如 ui_action clickPoint=true 真实点击），或改驱动其容器控件。";
        if (codes.Contains(FlaUICom.UIA_E_ELEMENTNOTAVAILABLE))
            return "；界面已重建，请重新 ui_find 拿新的 index。";
        if (codes.Contains(FlaUICom.UIA_E_INVALIDOPERATION))
            return "；先用 ui_get what=enabled 确认控件可用——禁用控件、不可聚焦的容器控件都会拒绝该操作。";
        return FallbackHint;
    }

    private static List<uint> Codes(Exception ex)
    {
        var codes = new List<uint>(4);
        for (var cur = ex; cur is not null; cur = cur.InnerException)
        {
            var code = unchecked((uint)cur.HResult);
            if (!codes.Contains(code)) codes.Add(code);
        }
        return codes;
    }

    private static string? KnownCause(List<uint> codes)
    {
        foreach (var code in codes)
            if (DescribeCode(code) is { } known)
                return known;
        return null;
    }

    /// <summary>展示用错误码：优先取能被识别的那个（外层 FlaUI 包装码 0x80131500 本身无信息量）。</summary>
    private static uint? PrimaryCode(List<uint> codes)
    {
        foreach (var code in codes)
            if (DescribeCode(code) is not null)
                return code;
        return codes.Count > 0 ? codes[0] : null;
    }
}
