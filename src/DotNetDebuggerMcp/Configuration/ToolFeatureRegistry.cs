using System.Reflection;
using ModelContextProtocol.Server;

namespace DotNetDebuggerMcp.Configuration;

/// <summary>
/// 工具类型 → 能力族 的**显式注册表**：MCP 启动时按其过滤注册（未启用族不进工具目录）；覆盖率回归测试断言每个
/// <c>[McpServerToolType]</c> 类型都在表里——**未登记即测试失败**（防新增工具静默不可发现或被误归族）。
/// <para>归属按命名空间显式判定、**不设兜底**：不认识的命名空间返回 <see cref="FeatureFamily.None"/>，
/// 由覆盖率测试兜住（旧实现「其余一律归 Decompile」会让漏登记的新工具默认对外暴露）。</para>
/// </summary>
internal static class ToolFeatureRegistry
{
    private static readonly Type[] All = BuildAll();

    /// <summary>程序集内全部工具类型（测试覆盖率断言用）。</summary>
    internal static IReadOnlyList<Type> AllToolTypes() => All;

    /// <summary>该类型所属能力族；**未登记返回 <see cref="FeatureFamily.None"/>**（覆盖率测试据此报错）。</summary>
    internal static FeatureFamily FamilyOf(Type toolType)
    {
        var ns = toolType.Namespace ?? "";
        if (ns.EndsWith(".Tools.Screenshot", StringComparison.Ordinal)) return FeatureFamily.Screenshot;
        if (ns.EndsWith(".Tools.Debugger", StringComparison.Ordinal))
            return toolType.Name == "UiTools" ? FeatureFamily.Ui : FeatureFamily.Debug;
        if (ns.EndsWith(".Tools.Decompile", StringComparison.Ordinal)
            || ns.EndsWith(".Tools.Metadata", StringComparison.Ordinal)
            || ns.EndsWith(".Tools", StringComparison.Ordinal))   // CacheStatsTool 在 Tools 根
            return FeatureFamily.Decompile;
        return FeatureFamily.None;   // 未登记：覆盖率测试失败（不静默归类）
    }

    /// <summary>已启用族对应的工具类型（顺序稳定，便于快照断言）。</summary>
    internal static IReadOnlyList<Type> EnabledToolTypes(FeatureFamily enabled)
        => All.Where(t => FamilyOf(t) != FeatureFamily.None && enabled.HasFlag(FamilyOf(t))).ToList();

    private static Type[] BuildAll()
        => typeof(ToolFeatureRegistry).Assembly.GetTypes()
            // 工具类是 static class（IL 层 abstract+sealed），故不能用 !IsAbstract 过滤；只排除非类与接口/枚举。
            .Where(t => t.IsClass && t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
}
