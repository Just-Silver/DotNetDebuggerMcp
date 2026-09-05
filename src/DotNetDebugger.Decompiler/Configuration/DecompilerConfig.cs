namespace DotNetDebugger.Decompiler.Configuration;

/// <summary>
/// 能力库自用的内部常量（自原宿主 Configuration.AppConfig 拆分，仅 Decompiler/Metadata 层用到的最小集）：
/// 避免能力库反向依赖宿主程序集。
/// </summary>
internal static class DecompilerConfig
{
    /// <summary>
    /// call_chain 跨程序集调用展开的最大递归深度（原 AppConfig.ExternalExpandMaxDepth）。
    /// </summary>
    public const int ExternalExpandMaxDepth = 5;

    /// <summary>
    /// call_chain 单次跨程序集调用展开最多展开的外部节点数（原 AppConfig.ExternalExpandMaxNodes）。
    /// </summary>
    public const int ExternalExpandMaxNodes = 200;

    /// <summary>
    /// 单次反编译生成文本的字符数上限（原 AppConfig.MaxOutputBytes，值与宿主缓存上限一致）：超过即返回
    /// 「建议改用 decompile_to_dir」提示且该结果不入缓存。
    /// </summary>
    public const long MaxOutputBytes = 64 * 1024 * 1024;
}
