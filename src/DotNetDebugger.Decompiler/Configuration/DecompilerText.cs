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
    /// IL 反汇编失败提示前缀（InProcessDecompiler.DecompileIl 的 token 校验失败/非方法 token/异常兜底统一前缀，
    /// 语义有别于「反编译失败」——IL 反汇编针对已定位的成员方法体，token 无效属于参数问题而非反编译引擎失败，
    /// 独立前缀便于 agent 区分「给错了 token」与「引擎/程序集问题」）。
    /// </summary>
    public const string IlFailurePrefix = "反汇编失败：";

    /// <summary>
    /// 判定提示文本是否以反编译失败前缀开头（与 <see cref="DecompileFailurePrefix"/> 同源）。
    /// </summary>
    public static bool StartsWithDecompileFailure(string text)
        => text.StartsWith(DecompileFailurePrefix, StringComparison.Ordinal);

    /// <summary>
    /// 判定提示文本是否以 IL 反汇编失败前缀开头（与 <see cref="IlFailurePrefix"/> 同源）。
    /// </summary>
    public static bool StartsWithIlFailure(string text)
        => text.StartsWith(IlFailurePrefix, StringComparison.Ordinal);
}
