using DotNetDebuggerMcp.Services;
using ModelContextProtocol.Server;

using System.ComponentModel;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// debug_verify 一键复验工具：读场景 JSON 文件并执行到 PASS/FAIL（薄包装 VerifyService；
/// 场景解析/build/launch/步骤断言/汇总逻辑在 Services 层，不走 MCP 往返）。
/// </summary>
[McpServerToolType]
public static class DebugVerifyTool
{
    /// <summary>
    /// 一键复验：读场景 JSON 文件（target 启动快照 + 可选 build 重编译 + steps 断言序列）并执行到 PASS/FAIL。
    /// 场景文件绝对路径（必填）；build 只重编场景指定工程、产物自动拿取（项目默认输出，不设置 OutputPath），
    /// commandLine 写工程入口 exe 文件名+参数（首段文件名须与产物同名）；无 build 时 commandLine 用完整路径/相对
    /// server 工作目录。启动/断点/output/evaluate 断言走本进程 Session；断言失败即停（fail-fast）。场景格式见 README。
    /// 改完源码后用它自证修复。
    /// </summary>
    [McpServerTool]
    [Description("一键复验：读场景 JSON 文件（target 启动快照 + 可选 build 重编译 + steps 断言序列）并执行到 PASS/FAIL。build 分支自动重编译场景工程并按项目默认输出路径拿产物（不设置 OutputPath），commandLine 写工程入口 exe 文件名+参数（首段文件名须与编译产物同名）；无 build 时 commandLine 用完整路径。启动/断点/evaluate/output 断言走本进程调试会话，断言失败即停（fail-fast），返回 PASS/FAIL 汇总与失败步骤实际上下文。改完 bug 后用它自证修复。场景格式示例见 README（debug_verify 段）。")]
    public static async Task<string> DebugVerify(
        [Description("场景 JSON 文件绝对路径（必填）。")] string scenarioPath = "",
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(scenarioPath))
            return "请指定场景 JSON 文件绝对路径（scenarioPath 必填）。";
        if (!File.Exists(scenarioPath))
            return $"场景文件不存在：{scenarioPath}（debug_verify 需场景 JSON 文件路径）。";

        try
        {
            return await VerifyService.VerifyAsync(scenarioPath, cancellationToken);
        }
        catch (Exception ex)
        {
            // VerifyService 内部已兜底中文提示；此处兜住取消等系统性异常
            return cancellationToken.IsCancellationRequested
                ? "debug_verify 执行已取消（场景未跑完，可重试）。"
                : $"debug_verify 执行失败：{ex.Message}";
        }
    }
}
