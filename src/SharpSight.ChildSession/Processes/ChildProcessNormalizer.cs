using System.Text.Json;

namespace SharpSight.ChildSession.Processes;

public static class ChildProcessNormalizer
{
    public static JsonElement Normalize(JsonElement arguments)
    {
        var values = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(arguments.GetRawText())
            ?? throw new ArgumentException("参数必须为JSON对象。");
        if (!values.TryGetValue("action", out var action) || action.GetString() != "start") return arguments;
        var executable = values.TryGetValue("executable", out var file) ? file.GetString() ?? "" : "";
        if (string.IsNullOrWhiteSpace(executable)) throw new ArgumentException("executable不能为空。");
        var path = Resolve(executable);
        var directory = values.TryGetValue("workingDirectory", out var cwd) ? cwd.GetString() ?? "" : "";
        values["executable"] = JsonSerializer.SerializeToElement(path);
        values["workingDirectory"] = JsonSerializer.SerializeToElement(
            string.IsNullOrWhiteSpace(directory) ? Path.GetDirectoryName(path) : Path.GetFullPath(directory));
        return JsonSerializer.SerializeToElement(values);
    }

    private static string Resolve(string executable)
    {
        if (File.Exists(executable)) return Path.GetFullPath(executable);
        if (executable.Contains(Path.DirectorySeparatorChar) || executable.Contains(Path.AltDirectorySeparatorChar))
            throw new ArgumentException("目标程序不存在。");
        var extensions = Path.HasExtension(executable) ? new[] { "" } :
            (Environment.GetEnvironmentVariable("PATHEXT") ?? ".EXE").Split(';');
        foreach (var directory in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(';'))
            foreach (var extension in extensions)
            {
                var candidate = Path.Combine(directory.Trim('"'), executable + extension);
                if (File.Exists(candidate)) return Path.GetFullPath(candidate);
            }
        throw new ArgumentException("未能从调用端PATH解析目标程序。");
    }
}
