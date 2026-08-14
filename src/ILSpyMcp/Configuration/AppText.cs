namespace ILSpyMcp.Configuration;

/// <summary>
/// 跨层共享的用户可见文案常量：Decompiler/Pipeline/Tools 三层多处重复拼写的中文提示前缀与模板集中在此，
/// 修改文案只需改一处，避免「反编译失败」等字面量在多文件散落导致改一处漏多处。
/// </summary>
internal static class AppText
{
    /// <summary>
    /// 反编译失败提示统一前缀（InProcessDecompiler 各异常兜底、ToolPipeline 回源失败、CallChainTool 判重共用；
    /// 改文案时此处唯一，判重逻辑经 <see cref="StartsWithDecompileFailure"/> 同步感知）。
    /// </summary>
    public const string DecompileFailurePrefix = "反编译失败：";

    /// <summary>
    /// 匹配数量超过上限时「仅列出签名」的头部标注（decompile_member / call_chain 共用）。
    /// </summary>
    public const string OverLimitOnlySignatures = "超过上限，仅列出签名";

    /// <summary>
    /// call_chain 跨程序集调用解析失败时行尾标注模板（{0} 为程序集短名；Description 侧引用同文案需自行拼写）。
    /// </summary>
    public const string UnresolvedAssemblyAnnotation = "未找到程序集 {0}，视为框架/外部调用未展开";

    /// <summary>
    /// 判定提示文本是否以反编译失败前缀开头（InProcessDecompiler.IsErrorResult 与 CallChainTool 反编译失败判重共用，
    /// 与 <see cref="DecompileFailurePrefix"/> 同源，改前缀无需改此处）。
    /// </summary>
    public static bool StartsWithDecompileFailure(string text)
        => text.StartsWith(DecompileFailurePrefix, StringComparison.Ordinal);
}
