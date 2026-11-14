using SharpSight.ChildSession.Lifecycle;

namespace DotNetDebuggerMcp.Services.Child;

internal static class ChildServices
{
    internal static string Root => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "DotNetDebuggerMcp", "child");
    internal static ChildHostCommand Command
    {
        get
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("无法定位当前宿主。");
            var prefix = string.Equals(Path.GetFileNameWithoutExtension(executable), "dotnet", StringComparison.OrdinalIgnoreCase)
                ? new[] { typeof(DotNetDebuggerMcpCmd).Assembly.Location } : Array.Empty<string>();
            return new ChildHostCommand(executable, prefix);
        }
    }
    internal static ChildSessionClient CreateClient() => new(new ChildInstanceStore(Root), Command);
}
