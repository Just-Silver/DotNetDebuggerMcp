using DotNetDebugger.Decompiler.Configuration;

namespace DotNetDebuggerMcp.Configuration;

/// <summary>
/// 跨层共享的用户可见文案常量：Pipeline/Tools 两层多处重复拼写的中文提示前缀与模板集中在此， 修改文案只需改一处，避免「反编译失败」等字面量在多文件散落导致改一处漏多处。
/// 反编译失败前缀与判定归 Decompiler 库（<see cref="DecompilerText"/>）单一来源，此处转发。
/// </summary>
internal static class AppText
{
    /// <summary>
    /// 反编译失败提示统一前缀（转发 Decompiler 库 <see cref="DecompilerText.DecompileFailurePrefix"/>，单一来源； ToolPipeline
    /// 回源失败、CallChainTool 判重共用，判重逻辑经 <see cref="StartsWithDecompileFailure"/> 同步感知）。
    /// </summary>
    public const string DecompileFailurePrefix = DecompilerText.DecompileFailurePrefix;

    /// <summary>
    /// IL 反汇编失败提示前缀（转发 Decompiler 库 <see cref="DecompilerText.IlFailurePrefix"/>，单一来源； ToolPipeline
    /// 错误包装判重共用——decompile_il 的库内错误自带此前缀，包装时不得二次加「反编译失败：」）。
    /// </summary>
    public const string IlFailurePrefix = DecompilerText.IlFailurePrefix;

    /// <summary>
    /// 匹配数量超过上限时「仅列出签名」的头部标注（decompile_member / call_sequence 共用）。
    /// </summary>
    public const string OverLimitOnlySignatures = "超过上限，仅列出签名";

    /// <summary>
    /// call_sequence 跨程序集调用解析失败时行尾标注模板（{0} 为程序集短名；Description 侧引用同文案需自行拼写）。
    /// </summary>
    public const string UnresolvedAssemblyAnnotation = "未找到程序集 {0}，视为框架/外部调用未展开";

    /// <summary>
    /// 判定提示文本是否以反编译失败前缀开头（转发 Decompiler 库 <see cref="DecompilerText.StartsWithDecompileFailure"/>； InProcessDecompiler.IsErrorResult
    /// 与 CallChainTool 反编译失败判重共用，与 <see cref="DecompileFailurePrefix"/> 同源）。
    /// </summary>
    public static bool StartsWithDecompileFailure(string text)
        => DecompilerText.StartsWithDecompileFailure(text);
}