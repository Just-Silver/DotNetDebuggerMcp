using System.ComponentModel;
using System.Text.Json;
using DotNetDebuggerMcp.Configuration;
using DotNetDebuggerMcp.Services.Child;
using ModelContextProtocol.Server;

namespace DotNetDebuggerMcp.Tools.Child;

/// <summary>真实输入没有主会话路径，避免实例不可用时干扰用户操作。</summary>
[McpServerToolType]
public static class ChildInputTools
{
    /// <summary>每轮动作必须完整释放按下状态，不能跨请求继承按钮所有权。</summary>
    [McpServerTool]
    [Description("在子会话内执行真实鼠标动作。**坐标固定为 child 虚拟屏物理像素坐标**（允许负原点；原生 1:1，截图像素点 = 截图返回原点 + 图内像素），不是父侧连接窗坐标或客户区坐标。没有主会话模式：缺 instance 或 processId 直接拒绝，失败不回退父侧输入。action（必填）：move / click / doubleclick / drag / scroll；目标 PID 必填，hwnd 空时要求该进程唯一可选顶层窗口；参数不适用时拒绝，不静默忽略。每个动作是完整有界序列（drag=按下→分段移动→抬起），实例内 GUI 操作串行。observationId 可省略，但**省略后不保证坐标来自新画面**（实例/会话/目标/前台检查始终执行）。")]
    public static Task<string> ChildPointer(
        [Description("实例标识（默认空）；本工具没有主会话模式，空值直接拒绝、不回退父侧输入。" + ToolParameterText.ChildInstanceRestoreHint)] string instance = "",
        [Description("目标进程 id（必填，默认 0=未指定）；必须是 child 内进程。")] int processId = 0,
        [Description("目标窗口句柄（默认空=要求该进程唯一可选顶层窗口；多窗口时必须显式指定）。")] string hwnd = "",
        [Description("动作类型（默认空=返回合法值提示）：move / click / doubleclick / drag / scroll。")] string action = "",
        [Description("起点 X，child 虚拟屏物理像素（允许负原点），默认 0。")] int x = 0,
        [Description("起点 Y，child 虚拟屏物理像素（允许负原点），默认 0。")] int y = 0,
        [Description("drag 终点 X，child 虚拟屏物理像素，默认 0（仅 drag 使用）。")] int endX = 0,
        [Description("drag 终点 Y，child 虚拟屏物理像素，默认 0（仅 drag 使用）。")] int endY = 0,
        [Description("按键（默认 left）：left / right / middle。")] string button = "left",
        [Description("滚轮单位（仅 scroll，默认 0，可正可负）。")] int delta = 0,
        [Description("动作持续毫秒（drag 分段移动等），默认 300，范围 50-5000。")] int durationMs = 300,
        [Description("观察引用（默认空）；可省略，但省略后不保证坐标来自新画面，实例/会话/目标/前台检查仍始终执行。")] string observationId = "",
        [Description("单次动作等待秒数上限（含排队），默认 5，范围 1-30。")] int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
        => ChildServices.CallTextAsync("child_pointer",
            JsonSerializer.SerializeToElement(new
            {
                instance, processId, hwnd, action, x, y, endX, endY, button, delta, durationMs, observationId, timeoutSeconds
            }),
            cancellationToken);

    /// <summary>仅释放本轮提交的键，避免清理组合键时改变其他来源的物理键态。</summary>
    [McpServerTool]
    [Description("在子会话内执行真实键盘输入。action（必填）：text=Unicode 系统输入，不自动追加 Enter、不关闭 IME——IME 提交需显式再发 Enter；key=单键、chord=组合键（如 Ctrl+K，恰一个非修饰键），用键码名（Ctrl/Alt/Shift/Win、A-Z、0-9、F1-F24、Enter/Tab/Escape/Space/Backspace/Delete/Insert/Home/End/PageUp/PageDown/Up/Down/Left/Right）。不跨调用保留按下状态，也不释放并非本轮按下的键。没有主会话模式：缺 instance 直接拒绝，不回退父侧输入。**输入成功只表示事件已提交，不表示目标业务已消费**——请用 ui_get / screenshot 确认结果。observationId 可省略，但省略后不保证目标仍是先前观察的画面。")]
    public static Task<string> ChildKeyboard(
        [Description("实例标识（默认空）；本工具没有主会话模式，空值直接拒绝、不回退父侧输入。" + ToolParameterText.ChildInstanceRestoreHint)] string instance = "",
        [Description("目标进程 id（必填，默认 0=未指定）；必须是 child 内进程。")] int processId = 0,
        [Description("目标窗口句柄（默认空=要求该进程唯一可选顶层窗口；多窗口时必须显式指定）。")] string hwnd = "",
        [Description("动作类型（默认空=返回合法值提示）：text / key / chord。")] string action = "",
        [Description("要输入的文本（action=text 必填，默认空）；Unicode 系统输入，不自动追加 Enter、不关闭 IME。")] string text = "",
        [Description("键名（action=key/chord 必填，默认空）：Ctrl/Alt/Shift/Win、A-Z、0-9、F1-F24、Enter/Tab/Escape/Space/Backspace/Delete/Insert/Home/End/PageUp/PageDown/Up/Down/Left/Right。")] string keys = "",
        [Description("观察引用（默认空）；可省略，但省略后不保证目标仍是先前观察的画面，实例/会话/目标/前台检查仍始终执行。")] string observationId = "",
        [Description("单次输入等待秒数上限（含排队），默认 5，范围 1-30。")] int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
        => ChildServices.CallTextAsync("child_keyboard",
            JsonSerializer.SerializeToElement(new { instance, processId, hwnd, action, text, keys, observationId, timeoutSeconds }),
            cancellationToken);
}
