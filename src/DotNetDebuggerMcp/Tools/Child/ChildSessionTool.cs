using System.ComponentModel;
using System.Text.Json;
using DotNetDebuggerMcp.Services.Child;
using ModelContextProtocol.Server;

namespace DotNetDebuggerMcp.Tools.Child;

/// <summary>执行实例由连接持有进程共享，避免MCP与CLI各建一份会话。</summary>
[McpServerToolType]
public static class ChildSessionTool
{
    /// <summary>stop会结束整个child，不能把它当作关闭单个任务。</summary>
    [McpServerTool]
    [Description("管理子会话实例与按请求找回结果。需要在不干扰父会话鼠标/键盘/前台的情况下操作 GUI 时，先 start 拿到实例，再把 instance 传给 child_process / child_pointer / child_keyboard 或 ui_* / screenshot。action（默认 status）：probe=只读探测开关、父会话与已有 child 状态；start=创建实例并等待就绪（默认等 30 秒，认证未完成时返回实例与状态，继续用 status 查）；status=查状态（instance 空=列当前父会话的本产品实例）；stop=注销整个 child，会结束其中全部应用（包括用户手动在该 child 打开的应用），不自动改系统 child 开关；requests=列已受理请求摘要（无需 requestId）；result=按 requestId 找回原请求结果，不重放、不执行新的 GUI 动作。超时/取消只表示放弃等待：请求仍可能运行并保持 GUI Busy（拒绝新 GUI 动作，允许状态查询与 stop），未收到响应就用 requests 找 requestId 再用 result 对账；unknown 不能当作未执行或已完成。")]
    public static Task<string> ChildSession(
        [Description("操作类型（默认 status）：probe / start / status / stop / requests / result。")] string action = "status",
        [Description("实例标识（默认空）；stop / requests / result 必须给实例，status 空值=列当前父会话的本产品实例。")] string instance = "",
        [Description("请求标识（仅 action=result 必填，默认空=返回提示）。")] string requestId = "",
        [Description("请求列表游标（仅 action=requests）：上次返回的单调序号，默认 0=从当前最早保留记录开始。")] long cursor = 0,
        [Description("请求列表条数上限（仅 action=requests），默认 20，范围 1-100。")] int limit = 20,
        [Description("等待秒数上限，默认 30：start/stop 为等待就绪/停止的上限，超时仅放弃等待并返回当前状态；result 为等待该请求结束的上限，范围 0-300（0=立即查询），其他 action 1-300。")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
        => ChildServices.CallTextAsync("child_session",
            JsonSerializer.SerializeToElement(new { action, instance, requestId, cursor, limit, timeoutSeconds }),
            cancellationToken);
}
