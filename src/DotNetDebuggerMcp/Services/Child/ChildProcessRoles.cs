using SharpSight.ChildSession.Connection;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Lifecycle;

namespace DotNetDebuggerMcp.Services.Child;

internal static class ChildProcessRoles
{
    internal static async Task<int?> TryRunAsync(string[] args, CancellationToken ct)
    {
        if (args.Length == 0 || args[0] != "--child-internal") return null;
        try
        {
            if (args.Length != 4) throw new ArgumentException("内部角色需要角色名、私有目录和实例/设置参数。");
            var store = new ChildInstanceStore(args[2]);
            switch (args[1])
            {
                case "holder":
                    await new ChildHolderRuntime(store, ChildServices.Command).RunAsync(args[3], ct);
                    break;
                case "worker":
                    await new ChildWorkerRuntime(store).RunAsync(args[3], (_, request, _) =>
                        Task.FromResult(ChildReply.Create(request, "not-executed", "该worker操作尚未接线。")), ct);
                    break;
                case "setting":
                    if (args[3] is not ("enable" or "disable")) throw new ArgumentException("设置仅允许enable/disable。");
                    await new ChildCoordinator(store, new ChildSessionPlatform(ChildServices.Command))
                        .ChangeSettingAsync(args[3] == "enable", ct);
                    break;
                default: throw new ArgumentException("未知内部角色。");
            }
            return 0;
        }
        catch (Exception ex) { Console.Error.WriteLine($"子会话内部角色启动失败：{ex.Message}"); return 1; }
    }
}
