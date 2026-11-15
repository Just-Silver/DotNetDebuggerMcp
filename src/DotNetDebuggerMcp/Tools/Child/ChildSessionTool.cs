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
    [Description("管理子会话实例与按请求找回结果。需要在不干扰父会话鼠标/键盘/前台的情况下操作 GUI 时，先 start 拿到实例，再把 instance 传给 child_process / child_pointer / child_keyboard 或 ui_* / screenshot。action（默认 status）：probe=只读探测开关、父会话、已有 child 与是否已保存自动登录凭据；start=创建实例并等待就绪（默认等 30 秒；有已保存凭据时自动注入登录，无凭据时先尝试系统自动登录、认证持续等待时弹出一次 Windows 安全凭据窗口并保存到当前用户凭据管理器，之后不再询问；每个实例最多主动询问一次；Ready 需完成系统登录并收到 worker 握手，控件连接不等于 Ready，认证未完成时返回实例与状态，继续用 status 查；原持有器失效时：无 child 则结束旧实例重建，自有 child 仍在则重连同一实例，归属不明则拒绝）；status=查状态（instance 空=列当前父会话的本产品实例；返回 health 区分持有器/worker/child 会话是否存活与 GUI 是否可交互）；login=打开凭据窗口保存或更新自动登录凭据（首次配置用；用户名按账户实际登录名填写：本地账户 `计算机名\\用户` 或 `域名\\用户`，Microsoft 账户用 `MicrosoftAccount\\邮箱`；会等待用户在桌面输入，超时只放弃等待、窗口不会被强制关闭）；logout=删除本产品保存的凭据（不动 RDP 自己的 TERMSRV 凭据，不影响正在连接的实例，下次 start 会重新询问）；stop=注销整个 child，会结束其中全部应用（包括用户手动在该 child 打开的应用），不自动改系统 child 开关；requests=列已受理请求摘要（无需 requestId）；result=按 requestId 找回原请求结果，不重放、不执行新的 GUI 动作。超时/取消只表示放弃等待：请求仍可能运行并保持 GUI Busy（拒绝新 GUI 动作，允许状态查询与 stop），未收到响应就用 requests 找 requestId 再用 result 对账；unknown 不能当作未执行或已完成。")]
    public static Task<string> ChildSession(
        [Description("操作类型（默认 status）：probe / start / status / stop / login / logout / requests / result。")] string action = "status",
        [Description("实例标识（默认空）；stop / requests / result 必须给实例，status 空值=列当前父会话的本产品实例。")] string instance = "",
        [Description("请求标识（仅 action=result 必填，默认空=返回提示）。")] string requestId = "",
        [Description("请求列表游标（仅 action=requests）：上次返回的单调序号，默认 0=从当前最早保留记录开始。")] long cursor = 0,
        [Description("请求列表条数上限（仅 action=requests），默认 20，范围 1-100。")] int limit = 20,
        [Description("等待秒数上限，默认 30：start/stop 为等待就绪/停止的上限，login 为等待用户完成凭据窗口的上限；超时仅放弃等待并返回当前状态（凭据窗口仍可能继续），result 为等待该请求结束的上限，范围 0-300（0=立即查询），其他 action 1-300。")] int timeoutSeconds = 30,
        CancellationToken cancellationToken = default)
        => ChildServices.CallTextAsync("child_session",
            JsonSerializer.SerializeToElement(new { action, instance, requestId, cursor, limit, timeoutSeconds }),
            cancellationToken);
}
