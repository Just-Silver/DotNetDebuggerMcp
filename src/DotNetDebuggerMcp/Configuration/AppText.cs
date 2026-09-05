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
    /// MCP 握手 ServerInstructions 注入的服务器功能简介（Markdown：服务器简介/何时使用/使用约定三块标题分节）： 面向 agent 的触发条件
    /// 与使用约定，必须保持简短——ServerInstructions 常驻 agent 上下文且过长会被截断。**不逐条列举工具**（agent 经 MCP 工具目录发现），
    /// 只写「何时使用本服务器」的触发条件（反编译 vs 动态调试场景）。各块之间空行分隔（Markdown 段落），内部统一用 \n 换行。
    /// </summary>
    public const string HandshakeFeatureIntro =
        "## 服务器简介\n\n" +
        "本服务器是 .NET 程序集分析工具，提供两类能力：**反编译/静态分析**（将 .NET 程序集还原为 C#，探查类型/成员/调用关系，引擎内置无需外部工具）与**动态调试**（启动/附加 .NET 进程，下断点、单步、观察调用栈与变量实时值）。\n\n" +
        "## 何时使用\n\n" +
        "- **当需要查看 .NET 程序集（.dll/.exe，含无源码或第三方）的 C# 源码、类型结构、成员签名、字符串、调用/引用关系时**，使用本服务器的反编译与静态分析工具（可先列类型/查签名定位，再按需反编译）。\n" +
        "- **当需要弄清程序运行期行为（为何抛异常、某条件分支是否执行、变量当前值、调用路径）时**，使用本服务器的动态调试：反编译定位目标方法取 token → 下断点 → 运行至命中 → 观察调用栈与变量 → 单步。\n" +
        "具体工具清单见 MCP 工具目录（名称带 `decompile`/`debug` 等语义前缀）。\n\n" +
        "## 使用约定\n\n" +
        "程序集/目标文件路径基于当前工作目录；反编译与元数据结果带行号、支持 `lines=\"start-end\"` 分页；动态调试控制类工具异步返回（带默认超时），进程停点信息用查询类工具（`debug_state`/`debug_stack`/`debug_variables`）获取。";

    /// <summary>
    /// 判定提示文本是否以反编译失败前缀开头（转发 Decompiler 库 <see cref="DecompilerText.StartsWithDecompileFailure"/>； InProcessDecompiler.IsErrorResult
    /// 与 CallChainTool 反编译失败判重共用，与 <see cref="DecompileFailurePrefix"/> 同源）。
    /// </summary>
    public static bool StartsWithDecompileFailure(string text)
        => DecompilerText.StartsWithDecompileFailure(text);
}