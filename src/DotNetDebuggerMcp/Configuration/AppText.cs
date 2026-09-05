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
    /// 匹配数量超过上限时「仅列出签名」的头部标注（decompile_member / call_chain 共用）。
    /// </summary>
    public const string OverLimitOnlySignatures = "超过上限，仅列出签名";

    /// <summary>
    /// call_chain 跨程序集调用解析失败时行尾标注模板（{0} 为程序集短名；Description 侧引用同文案需自行拼写）。
    /// </summary>
    public const string UnresolvedAssemblyAnnotation = "未找到程序集 {0}，视为框架/外部调用未展开";

    /// <summary>
    /// MCP 握手 ServerInstructions 注入的服务器功能简介（Markdown：服务器简介/工具一览/使用约定三块标题分节）： 面向 agent 的触发条件、
    /// 全量工具一览与使用约定，必须保持简短——ServerInstructions 常驻 agent 上下文且过长会被截断。 各块之间空行分隔（Markdown 段落），
    /// 内部统一用 \n 换行（与 BuildServerInstructions 拼接 Environment.NewLine 混用由渲染端统一处理）。 新增工具必须同步本常量
    /// 的工具一览，否则 agent 无法在握手期得知新工具。
    /// </summary>
    public const string HandshakeFeatureIntro =
        "## 服务器简介\n\n" +
        "本服务器内置反编译引擎（ICSharpCode.Decompiler，随包分发，无需外部安装反编译工具），提供 .NET 程序集反编译与静态分析能力。**当需要反编译或分析 .NET 程序集（.dll/.exe，含第三方/无源码程序集）时使用本服务器**。\n\n" +
        "## 工具一览\n\n" +
        "- **`decompile`**：反编译指定类型的完整实现\n" +
        "- **`decompile_member`**：按成员名子串或 token 反编译成员（多匹配前插 `#MEMBER` JSON 分隔行取 token）\n" +
        "- **`decompile_to_dir`**：指定类型或全程序集反编译写盘为 `.cs` 文件（超限场景唯一出路）\n" +
        "- **`decompile_to_project`**：全程序集以可编译项目形式写盘（嵌套目录）\n" +
        "- **`list_types`**：列类型（`nameContains`/`namespaceContains` 过滤）\n" +
        "- **`signature`**：成员签名 API 地图（行尾 token 可直接反编译该成员）\n" +
        "- **`hierarchy`**：基类链/接口/继承实现者（`includeIndirect` 含间接后代）\n" +
        "- **`dependencies`**：类型引用的内部/外部与反向引用\n" +
        "- **`call_graph`**：方法体调用的内部/外部与反向调用者\n" +
        "- **`assembly_info`**：程序集名/版本/引用/入口点概览\n" +
        "- **`search_string`**：按字符串字面量反查成员\n" +
        "- **`field_access`**：追踪字段读写位置\n" +
        "- **`interface_usage`**：接口实现者与调用点\n" +
        "- **`generic_instantiations`**：泛型实例化使用点\n" +
        "- **`call_chain`**：调用序列 + 被调成员反编译\n" +
        "- **`cache_stats`**：共享缓存占用/命中率\n\n" +
        "## 使用约定\n\n" +
        "`assembly` 与 `outputDir` 相对路径基于当前工作目录；结果带行号，长输出用 `lines=\"start-end\"` 分页获取。";

    /// <summary>
    /// 判定提示文本是否以反编译失败前缀开头（转发 Decompiler 库 <see cref="DecompilerText.StartsWithDecompileFailure"/>； InProcessDecompiler.IsErrorResult
    /// 与 CallChainTool 反编译失败判重共用，与 <see cref="DecompileFailurePrefix"/> 同源）。
    /// </summary>
    public static bool StartsWithDecompileFailure(string text)
        => DecompilerText.StartsWithDecompileFailure(text);
}