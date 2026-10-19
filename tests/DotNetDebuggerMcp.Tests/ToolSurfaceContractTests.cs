using System.Reflection;
using ModelContextProtocol.Server;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// MCP 工具面契约：agent 看到的工具名/参数名/必填状态是外部契约——改名、漏默认值必须在这里被拦住，
/// 不能靠人眼比对 53 个工具的 schema。
/// </summary>
public sealed class ToolSurfaceContractTests
{
    [Fact]
    public void 所有工具参数都有默认值_缺参由方法体返回中文()
    {
        var offenders = ToolSurface.Tools()
            .SelectMany(t => t.Parameters.Where(p => !p.HasDefault).Select(p => $"{t.Name}.{p.Name}"))
            .ToList();

        Assert.True(offenders.Count == 0,
            "以下参数没有默认值：客户端会在绑定阶段抛英文 Missing key，agent 拿不到原因。\n" +
            string.Join("\n", offenders));
    }

    [Fact]
    public void 工具面快照未被未审核地改动()
    {
        var snapshotPath = Path.Combine(
            TestDataPaths.RepositoryRoot, "tests", "DotNetDebuggerMcp.Tests", "Snapshots", "tool-surface.txt");
        var actual = ToolSurface.Snapshot();

        var updateRequested = Environment.GetEnvironmentVariable("UPDATE_TOOL_SURFACE_SNAPSHOT") == "1";
        if (updateRequested || !File.Exists(snapshotPath))
        {
            Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
            File.WriteAllText(snapshotPath, actual);
            if (!updateRequested)
                Assert.Fail($"工具面快照基线已生成：{snapshotPath}——请复核内容后重新运行。");
            return;
        }

        Assert.Equal(File.ReadAllText(snapshotPath).ReplaceLineEndings("\n"), actual);
    }

    [Fact]
    public void 工具名派生_与已知样本一致()
    {
        // 工具名是 agent 唯一的发现通道：这里固化「方法名 → 工具名」的规则，防止 SDK 升级后静默改名。
        Assert.Equal("assembly_info", ToolSurface.ToSnakeCase("AssemblyInfo"));
        Assert.Equal("debug_breakpoint_set", ToolSurface.ToSnakeCase("DebugBreakpointSet"));
        Assert.Equal("decompile_to_dir", ToolSurface.ToSnakeCase("DecompileToDir"));
    }
}

internal static class ToolSurface
{
    internal sealed record Param(string Name, bool HasDefault);

    internal sealed record Tool(string Name, IReadOnlyList<Param> Parameters);

    internal static IReadOnlyList<Tool> Tools()
    {
        var tools = new List<Tool>();
        foreach (var type in typeof(Configuration.ToolFeatureRegistry).Assembly.GetTypes())
        {
            if (!type.IsClass || type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;

            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                var toolAttr = method.GetCustomAttribute<McpServerToolAttribute>();
                if (toolAttr is null) continue;

                var parameters = method.GetParameters()
                    .Where(p => p.ParameterType != typeof(CancellationToken))
                    .Select(p => new Param(p.Name!, p.HasDefaultValue))
                    .ToList();
                tools.Add(new Tool(toolAttr.Name ?? ToSnakeCase(method.Name), parameters));
            }
        }

        return tools.OrderBy(t => t.Name, StringComparer.Ordinal).ToList();
    }

    internal static string Snapshot()
        => string.Join("\n", Tools().Select(t =>
            $"{t.Name}({string.Join(", ", t.Parameters.Select(p => p.Name))})")) + "\n";

    internal static string ToSnakeCase(string name)
    {
        var builder = new System.Text.StringBuilder(name.Length + 8);
        for (var i = 0; i < name.Length; i++)
        {
            if (char.IsUpper(name[i]) && i > 0) builder.Append('_');
            builder.Append(char.ToLowerInvariant(name[i]));
        }
        return builder.ToString();
    }
}
