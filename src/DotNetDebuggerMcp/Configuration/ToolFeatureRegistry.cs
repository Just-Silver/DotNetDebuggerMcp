using System.Reflection;
using ModelContextProtocol.Server;

namespace DotNetDebuggerMcp.Configuration;

/// <summary>
/// 工具类型 → 能力族 的**单一注册表**：MCP 启动时按其过滤注册的工具（未启用族不进工具目录）；覆盖率回归测试
/// 断言每个 <c>[McpServerToolType]</c> 类型都在此登记，新增工具漏登记即失败——否则它会静默不可被发现。
/// <para>归属以**命名空间/目录**为准（族边界与目录边界一致）：反编译/元数据 + cache_stats → Decompile；
/// <c>Tools.Debugger</c> 下 <c>Debug*</c> + <c>Tools.Web</c> 的 web_open → Debug；<c>UiTools</c> → Ui；
/// <c>Tools.Screenshot</c> → Screenshot。</para>
/// </summary>
internal static class ToolFeatureRegistry
{
    private static readonly Type[] All = BuildAll();

    /// <summary>程序集内全部工具类型（测试覆盖率断言用）。</summary>
    internal static IReadOnlyList<Type> AllToolTypes() => All;

    /// <summary>该类型所属能力族（未登记返回 <see cref="FeatureFamily.None"/>，由覆盖率测试兜住）。</summary>
    internal static FeatureFamily FamilyOf(Type toolType)
    {
        var ns = toolType.Namespace ?? "";
        if (ns.EndsWith(".Tools.Screenshot", StringComparison.Ordinal)) return FeatureFamily.Screenshot;
        if (ns.EndsWith(".Tools.Web", StringComparison.Ordinal)) return FeatureFamily.Debug;
        if (ns.EndsWith(".Tools.Debugger", StringComparison.Ordinal))
            return toolType.Name == "UiTools" ? FeatureFamily.Ui : FeatureFamily.Debug;
        return FeatureFamily.Decompile; // Tools.Decompile / Tools.Metadata / CacheStatsTool(根 Tools)
    }

    /// <summary>已启用族对应的工具类型（顺序稳定，便于快照断言）。</summary>
    internal static IReadOnlyList<Type> EnabledToolTypes(FeatureFamily enabled)
        => All.Where(t => enabled.HasFlag(FamilyOf(t))).ToList();

    private static Type[] BuildAll()
        => typeof(ToolFeatureRegistry).Assembly.GetTypes()
            // 工具类是 static class（IL 层 abstract+sealed），故不能用 !IsAbstract 过滤；只排除非类与接口/枚举。
            .Where(t => t.IsClass && t.GetCustomAttribute<McpServerToolTypeAttribute>() is not null)
            .OrderBy(t => t.FullName, StringComparer.Ordinal)
            .ToArray();
}
