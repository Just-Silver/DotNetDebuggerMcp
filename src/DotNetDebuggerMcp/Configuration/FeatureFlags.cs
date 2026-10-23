namespace DotNetDebuggerMcp.Configuration;

/// <summary>
/// MCP 能力族开关（位标志）。**只作用于 MCP 面**——控制哪些工具注册进工具目录、握手简介注入哪些族的触发条件；
/// CLI 执行路径（-a/-l/-dbg/-c）不受影响。
/// </summary>
[Flags]
internal enum FeatureFamily
{
    None = 0,
    Decompile = 1,
    Debug = 2,
    Ui = 4,
    Screenshot = 8,
    All = Decompile | Debug | Ui | Screenshot,
}

/// <summary>
/// 能力族开关解析：CLI <c>--features</c> 优先于环境变量 <c>DOTNETDEBUGGERMCP_FEATURES</c>；两者都未指定则
/// **默认启用全部能力族**。该参数是**白名单**：列出的族才注册，未列出的族不注册、握手不提。
/// <para><b>反编译族恒启用、不可关闭</b>（它是本服务器的基础能力，且必须有工具可暴露）——传空串
/// <c>--features=</c> 等价于「只启用反编译」。语义要点：McMaster 下「不给参数」是 <c>null</c>（未指定，回落
/// 默认/env）、<c>"--features="</c> 是空串 <c>""</c>（只留反编译），两者不同。</para>
/// </summary>
internal static class FeatureFlags
{
    /// <summary>环境变量名（MCP 客户端在 mcp.json 的 env 配置）。</summary>
    internal const string EnvVar = "DOTNETDEBUGGERMCP_FEATURES";

    /// <summary>合法取值（错误文案与调用方共用）。</summary>
    internal static readonly string[] FeatureNames = ["decompile", "debug", "ui", "screenshot"];

    /// <summary>
    /// 解析取值串：空/空白 → <see cref="FeatureFamily.None"/>（由 <see cref="Resolve"/> 补上恒启用的反编译族）；
    /// 逗号分隔、忽略大小写与空白、忽略重复；未知 token → 中文错误（列出合法取值）。
    /// </summary>
    internal static (FeatureFamily Family, string? Error) TryParse(string value)
    {
        var family = FeatureFamily.None;
        var unknown = new List<string>();
        foreach (var raw in value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            var token = raw.ToLowerInvariant();
            switch (token)
            {
                case "decompile": family |= FeatureFamily.Decompile; break;
                case "debug": family |= FeatureFamily.Debug; break;
                case "ui": family |= FeatureFamily.Ui; break;
                case "screenshot": family |= FeatureFamily.Screenshot; break;
                default: unknown.Add(raw); break;
            }
        }
        if (unknown.Count > 0)
            return (FeatureFamily.None,
                $"未知的能力族：{string.Join("、", unknown)}。合法取值为 {string.Join("/", FeatureNames)}（逗号分隔，如 --features=decompile,debug）。");
        return (family, null);
    }

    /// <summary>
    /// CLI 优先于环境变量并应用默认：<paramref name="cliValue"/> 非 null 即权威（含空串=只启用反编译）；
    /// 为 null 时才看环境变量（非空白才解析）；两者皆空 → **启用全部能力族**。未知取值抛
    /// <see cref="InvalidOperationException"/>（消息即中文提示）——启动路径应改用 <see cref="Resolve"/> 以报错退出。
    /// </summary>
    internal static FeatureFamily Parse(string? cliValue, string? envValue)
    {
        var (family, error) = Resolve(cliValue, envValue);
        if (error is not null) throw new InvalidOperationException(error);
        return family;
    }

    /// <summary>
    /// 同 <see cref="Parse"/> 但不抛异常：未知取值时返回 <c>(None, 中文错误)</c>，供启动路径 <c>Console.Error</c> + 非零退出。
    /// <para>返回值恒含 <see cref="FeatureFamily.Decompile"/>（不可关闭）；因此不存在「零能力」配置——一个不注册任何
    /// 工具的 MCP 服务器会让客户端 <c>tools/list</c> 拿协议错误（实测）。</para>
    /// </summary>
    internal static (FeatureFamily Family, string? Error) Resolve(string? cliValue, string? envValue)
    {
        var (family, error) = cliValue is not null ? TryParse(cliValue)
            : !string.IsNullOrWhiteSpace(envValue) ? TryParse(envValue)
            : (FeatureFamily.All, null);
        if (error is not null) return (FeatureFamily.None, error);
        return (family | FeatureFamily.Decompile, null);
    }
}
