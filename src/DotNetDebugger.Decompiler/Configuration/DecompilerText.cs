namespace DotNetDebugger.Decompiler.Configuration;

/// <summary>
/// 能力库自用的用户可见文案常量（自原宿主 Configuration.AppText 拆分，仅 Decompiler 层用到的最小集）。
/// </summary>
internal static class DecompilerText
{
    /// <summary>
    /// 反编译失败提示统一前缀（InProcessDecompiler 各异常兜底、ToolPipeline 回源失败判重共用；改文案时此处唯一，
    /// 判重逻辑经 <see cref="StartsWithDecompileFailure"/> 同步感知）。
    /// </summary>
    public const string DecompileFailurePrefix = "反编译失败：";

    /// <summary>
    /// 判定提示文本是否以反编译失败前缀开头（与 <see cref="DecompileFailurePrefix"/> 同源）。
    /// </summary>
    public static bool StartsWithDecompileFailure(string text)
        => text.StartsWith(DecompileFailurePrefix, StringComparison.Ordinal);
}
