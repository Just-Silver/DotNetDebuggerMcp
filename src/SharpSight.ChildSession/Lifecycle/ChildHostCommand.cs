using System.Diagnostics;
using System.Text;

namespace SharpSight.ChildSession.Lifecycle;

public sealed record ChildHostCommand(string Executable, IReadOnlyList<string> PrefixArguments)
{
    public ProcessStartInfo StartInfo(params string[] arguments)
    {
        var info = new ProcessStartInfo(Executable) { UseShellExecute = false, CreateNoWindow = true };
        foreach (var argument in PrefixArguments.Concat(arguments)) info.ArgumentList.Add(argument);
        return info;
    }

    internal string ArgumentString(params string[] arguments)
        => string.Join(" ", PrefixArguments.Concat(arguments).Select(Quote));

    private static string Quote(string argument)
    {
        var output = new StringBuilder("\"");
        var slashes = 0;
        foreach (var c in argument)
        {
            if (c == '\\') { slashes++; continue; }
            output.Append('\\', c == '"' ? slashes * 2 + 1 : slashes);
            output.Append(c);
            slashes = 0;
        }
        output.Append('\\', slashes * 2).Append('"');
        return output.ToString();
    }
}
