using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Ipc;

namespace SharpSight.ChildSession.Lifecycle;

public sealed record ChildBootstrap(ChildIdentity Identity, string HolderPipe, string WorkerPipe,
    string Secret, ChildPeer Holder, ChildPeer? Worker, string State, string Reason);

public sealed class ChildInstanceStore(string root)
{
    public string Root { get; } = Path.GetFullPath(root);

    public string Create(string instance)
    {
        var path = InstancePath(instance);
        var directory = Directory.CreateDirectory(path);
        using var user = WindowsIdentity.GetCurrent();
        var acl = new DirectorySecurity();
        acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { user.User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
                InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(acl);
        return path;
    }

    public async Task WriteAsync(ChildBootstrap bootstrap, CancellationToken ct = default)
    {
        var path = Path.Combine(Create(bootstrap.Identity.Instance), "instance.json");
        var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            await File.WriteAllTextAsync(temp, JsonSerializer.Serialize(bootstrap, ChildWire.Json), ct);
            File.Move(temp, path, true);
        }
        finally { if (File.Exists(temp)) File.Delete(temp); }
    }

    public async Task<ChildBootstrap> ReadAsync(string instance, CancellationToken ct = default)
    {
        var path = Path.Combine(InstancePath(instance), "instance.json");
        if (new FileInfo(path).Length > ChildWire.MaximumBytes) throw new InvalidOperationException("实例记录过大。");
        return JsonSerializer.Deserialize<ChildBootstrap>(await File.ReadAllTextAsync(path, ct), ChildWire.Json)
            ?? throw new InvalidOperationException("实例记录不可用。");
    }

    public async Task<byte[]> ReadArtifactAsync(string instance, ChildArtifact artifact, CancellationToken ct = default)
    {
        var directory = InstancePath(instance);
        var relative = artifact.RelativePath;
        if (Path.IsPathRooted(relative) || relative.Split('/', '\\').Any(p => p is ".." or "." or ""))
            throw new InvalidOperationException("产物路径超出实例范围。");
        var path = Path.GetFullPath(Path.Combine(directory, relative));
        if (!path.StartsWith(directory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("产物不属于本实例。");
        RejectLinks(directory, relative);
        var info = new FileInfo(path);
        if (info.Length != artifact.Length || info.Length > 64 * 1024 * 1024)
            throw new InvalidOperationException("产物长度不符或超过64 MiB。");
        return await File.ReadAllBytesAsync(path, ct);
    }

    private string InstancePath(string instance)
    {
        if (string.IsNullOrWhiteSpace(instance) || instance.Any(c => !char.IsAsciiLetterOrDigit(c) && c != '-'))
            throw new InvalidOperationException("实例标识格式无效。");
        return Path.Combine(Root, instance);
    }

    private static void RejectLinks(string directory, string relative)
    {
        var path = directory;
        if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
            throw new InvalidOperationException("实例目录不可为重解析点。");
        foreach (var part in relative.Split('/', '\\'))
        {
            path = Path.Combine(path, part);
            if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidOperationException("产物不可经过重解析点。");
        }
    }
}
