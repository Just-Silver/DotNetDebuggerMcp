using System.ComponentModel;
using System.Text.Json;
using DotNetDebuggerMcp.Services.Child;
using ModelContextProtocol.Server;

namespace DotNetDebuggerMcp.Tools.Child;

/// <summary>任务生命周期不能绑定一次MCP等待，否则超时会误杀后台程序。</summary>
[McpServerToolType]
public static class ChildProcessTool
{
    /// <summary>取消限定自有Job，避免误杀单实例应用转交请求后的外部进程。</summary>
    [McpServerTool]
    [Description("在子会话内启动/查询/取消任务并读取增量输出。start 按调用端解析后的原路径在 child 内启动程序或脚本（必须给 instance 与 executable，缺一直接拒绝，绝不退化为父侧 shell 执行），只等启动登记结果、**不等应用退出**——timeoutSeconds 不是运行时限，超时/取消只表示放弃等待、不杀进程；要终止任务必须显式 cancel，cancel 只结束该任务自有的 Windows Job，不按进程名杀进程、不注销整个 child（已退出时幂等返回）。list 必须给 instance；status/output/cancel 必须给 instance 与 taskId。output 按字节游标读取持续排空的 stdout/stderr（默认约 8 KiB，最大 32 KiB，截断会标注）。")]
    public static Task<string> ChildProcess(
        [Description("操作类型（默认 status）：start / list / status / output / cancel。")] string action = "status",
        [Description("实例标识（默认空）；本工具所有 action 都必须给实例，空值返回提示，不回退主会话。")] string instance = "",
        [Description("任务标识（status / output / cancel 必填，默认空=返回提示）。")] string taskId = "",
        [Description("目标可执行文件或脚本解释器路径（start 必填，默认空）；相对路径按调用端当前目录转绝对路径，裸命令名按调用端 PATH 解析，在 child 内启动。")] string executable = "",
        [Description("参数 JSON 字符串数组（默认 []），元素逐个传给目标，不自动拼接 shell。")] string arguments = "[]",
        [Description("工作目录（默认空=executable 所在目录）；相对路径按调用端当前目录转绝对路径。")] string workingDirectory = "",
        [Description("环境变量 JSON 字符串字典（默认 {}），只覆盖显式提供的项。")] string environment = "{}",
        [Description("输出字节游标（仅 action=output）：上次返回的 nextCursor，默认 0=从当前最早保留位置开始。")] long cursor = 0,
        [Description("输出读取字节上限（仅 action=output），默认 8192（约 8 KiB），最大 32768，在完整 UTF-8 字符边界切分。")] int limit = 8192,
        [Description("等待秒数上限，默认 5：status/output/cancel 的响应等待；start 只等启动登记结果、不等应用退出，超时仅放弃等待、不杀进程。")] int timeoutSeconds = 5,
        CancellationToken cancellationToken = default)
        => ChildServices.CallTextAsync("child_process",
            JsonSerializer.SerializeToElement(new
            {
                action, instance, taskId, executable, arguments, workingDirectory, environment, cursor, limit, timeoutSeconds
            }),
            cancellationToken);
}
