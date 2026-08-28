namespace ILSpyMcp.Configuration;

/// <summary>
/// 跨层共享的用户可见文案常量：Decompiler/Pipeline/Tools 三层多处重复拼写的中文提示前缀与模板集中在此， 修改文案只需改一处，避免「反编译失败」等字面量在多文件散落导致改一处漏多处。
/// </summary>
internal static class AppText
{
    /// <summary>
    /// 反编译失败提示统一前缀（InProcessDecompiler 各异常兜底、ToolPipeline 回源失败、CallChainTool 判重共用； 改文案时此处唯一，判重逻辑经
    /// <see cref="StartsWithDecompileFailure"/> 同步感知）。
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
    /// MCP 握手 ServerInstructions 注入的服务器功能简介（首行 CWD 之后）：面向 agent 的能力概述与使用约定， 必须保持简短——ServerInstructions
    /// 常驻 agent 上下文且过长会被截断。新增工具若改变能力类别需同步本常量。
    /// </summary>
    public const string HandshakeFeatureIntro =
        "本服务器是进程内 ILSpy 反编译 MCP（引擎随包内置，无需外部安装反编译工具）：可反编译类型/成员并批量写盘（全量或项目形式），" +
        "并可元数据秒回查询列类型/成员签名/继承层级/依赖/调用图/程序集信息/字符串反查/字段访问/接口实现/泛型实例化/调用链/缓存状态。" +
        "assembly 与 outputDir 相对路径基于上方「当前工作目录」；结果带行号，长输出用 lines=\"start-end\" 分页获取。";

    /// <summary>
    /// 判定提示文本是否以反编译失败前缀开头（InProcessDecompiler.IsErrorResult 与 CallChainTool 反编译失败判重共用， 与 <see
    /// cref="DecompileFailurePrefix"/> 同源，改前缀无需改此处）。
    /// </summary>
    public static bool StartsWithDecompileFailure(string text)
        => text.StartsWith(DecompileFailurePrefix, StringComparison.Ordinal);
}