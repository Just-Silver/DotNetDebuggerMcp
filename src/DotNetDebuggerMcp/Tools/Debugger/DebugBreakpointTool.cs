using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 调试断点工具：设置/移除/清除断点。断点按 模块名 + 方法 token + IL offset 定位
/// （token 从反编译 signature 行尾或 #MEMBER 取）。
/// </summary>
[McpServerToolType]
public static class DebugBreakpointTool
{
    /// <summary>
    /// 设置断点：按 模块名 + 方法 token（0x06 开头）+ IL offset 定位。模块需已加载
    /// （launch 后等模块加载或先 debug_continue 让模块加载）。返回断点 id。
    /// </summary>
    /// <param name="moduleName">模块名（如 DebugTarget.dll）（必填）。</param>
    /// <param name="methodToken">方法 token（0x06000005，从反编译 signature 行尾取）（必填）。</param>
    /// <param name="ilOffset">IL offset，默认 0（方法入口）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示（断点 id）或错误提示。</returns>
    [McpServerTool]
    [Description("设置断点：按 模块名+方法 token（0x06 开头，从反编译 signature 行尾取）+IL offset 定位。模块需已加载。返回断点 id；设好后 debug_continue 运行至命中。")]
    public static async Task<string> DebugBreakpointSet(
        [Description("模块名（如 DebugTarget.dll）（必填）。")] string moduleName = "",
        [Description("方法 token（0x06000005，从反编译 signature 行尾或 #MEMBER 取）（必填）。")] string methodToken = "",
        [Description("IL offset，默认 0（方法入口）。")] int ilOffset = 0,
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。先用 debug_launch / debug_attach 建立会话。";
        if (string.IsNullOrWhiteSpace(moduleName)) return "请指定模块名（moduleName 必填）。";
        if (string.IsNullOrWhiteSpace(methodToken)) return "请指定方法 token（methodToken 必填，如 0x06000005）。";

        if (!TryParseToken(methodToken, out var token))
            return $"方法 token 格式无效：{methodToken}（应为 0x06000005 形式的十六进制）。";

        try
        {
            var bp = await active.Session.SetBreakpointAsync(moduleName, token, ilOffset, cancellationToken);
            DebugSessionService.Manager.Actions.Log("debug_breakpoint_set", $"{moduleName} {methodToken}+{ilOffset}", $"id={bp.Id}");
            return $"断点已设: id={bp.Id} 位置={bp}。用 debug_continue 运行至命中。";
        }
        catch (Exception ex)
        {
            return $"设置断点失败：{ex.Message}";
        }
    }

    /// <summary>
    /// 移除指定断点。
    /// </summary>
    /// <param name="breakpointId">断点 id（debug_breakpoint_set 返回）（必填）。</param>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示。</returns>
    [McpServerTool]
    [Description("移除指定断点（id 由 debug_breakpoint_set 返回）。")]
    public static async Task<string> DebugBreakpointRemove(
        [Description("断点 id（debug_breakpoint_set 返回）（必填）。")] int breakpointId = 0,
        CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。";
        if (breakpointId <= 0) return "请指定断点 id（breakpointId 必填）。";

        var removed = await active.Session.RemoveBreakpointAsync(breakpointId, cancellationToken);
        DebugSessionService.Manager.Actions.Log("debug_breakpoint_remove", breakpointId.ToString(), removed ? "ok" : "not-found");
        return removed ? $"断点 {breakpointId} 已移除。" : $"未找到断点 {breakpointId}。";
    }

    /// <summary>
    /// 清除全部断点。
    /// </summary>
    /// <param name="cancellationToken">取消令牌。</param>
    /// <returns>中文结果提示。</returns>
    [McpServerTool]
    [Description("清除当前会话的全部断点。")]
    public static async Task<string> DebugBreakpointClear(CancellationToken cancellationToken = default)
    {
        var active = DebugSessionService.Manager.Active;
        if (active is null) return "当前无活动调试会话。";

        await active.Session.ClearBreakpointsAsync(cancellationToken);
        DebugSessionService.Manager.Actions.Log("debug_breakpoint_clear", "", "ok");
        return "已清除全部断点。";
    }

    internal static bool TryParseToken(string text, out int token)
    {
        token = 0;
        var t = text.Trim();
        if (t.StartsWith("0x", StringComparison.OrdinalIgnoreCase)) t = t[2..];
        return int.TryParse(t, System.Globalization.NumberStyles.HexNumber, null, out token);
    }
}
