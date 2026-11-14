using DotNetDebuggerMcp;
using McMaster.Extensions.CommandLineUtils;
using Microsoft.Extensions.Hosting;
using DotNetDebuggerMcp.ChildCli;
using DotNetDebuggerMcp.Services.Child;

// CLI 缺值/未知选项等解析错误统一兜底为中文提示 + 非零退出码，与 MCP 工具「缺参返回提示、不抛异常」的行为对齐； 错误走 stderr（stdout 只承载结果），避免崩溃堆栈直接暴露给用户
try
{
    var roleExit = await ChildProcessRoles.TryRunAsync(args, CancellationToken.None);
    if (roleExit is not null) return roleExit.Value;
    if (args.Length > 0 && args[0] == "child")
        return await ChildCliCommand.RunAsync(args[1..], CancellationToken.None);
    return await new HostBuilder().RunCommandLineApplicationAsync<DotNetDebuggerMcpCmd>(args);
}
catch (CommandParsingException ex)
{
    Console.Error.WriteLine($"{ex.Message} 参数不完整或用法错误，可用 -h/--help 查看所有选项。");
    return 1;
}
// MCP 装配期（HostBuilder/WithToolsFromAssembly/DI）异常兜底：避免崩溃堆栈直接暴露给用户，统一走 stderr 中文提示 + 非零退出码
catch (Exception ex)
{
    Console.Error.WriteLine($"启动失败：{ex.Message}");
    return 1;
}
