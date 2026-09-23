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
    /// 但要覆盖**全部能力族**的触发条件（静态分析 / 动态调试 / UI 自动化 / 视觉监视，含 debug_verify 等独立工作流）——**新增/删除能力族
    /// 必须同步补/删对应触发条件，否则该能力对 agent 不可发现**（回归断言见 HandshakeFeatureIntro_覆盖全部能力族触发条件）。
    /// 各块之间空行分隔（Markdown 段落），内部统一用 \n 换行。
    /// </summary>
    public const string HandshakeFeatureIntro =
        "## 服务器简介\n\n" +
        "本服务器面向 .NET 程序集与进程，提供四族能力：**反编译/静态分析**（程序集还原为 C#、探查类型/成员/调用关系）、**动态调试**（启动/附加进程，断点、单步、栈/变量实时值，现场改值、一键复验）、**UI 自动化**（UIA 语义操控桌面控件）与**视觉/监视**（截图、Web 监视器）；反编译与调试引擎均内置，无需外部工具。\n\n" +
        "## 何时使用\n\n" +
        "- **当需要查看 .NET 程序集（.dll/.exe，含无源码或第三方）的 C# 源码、类型结构、成员签名、字符串、调用/引用关系时**，使用反编译与静态分析工具（可先列类型/查签名定位，再按需反编译）。\n" +
        "- **当需要弄清程序运行期行为（为何抛异常、某条件分支是否执行、变量当前值、调用路径）时**，使用动态调试：反编译定位目标方法取 token → 下断点 → 运行至命中 → 观察调用栈与变量 → 单步；想省去反复设断点可直接 debug_run_to 运行到目标位置。\n" +
        "- **当需要验证某个运行期假设（强制走某分支 / 置空 / 换引用）时**，停点后用 debug_set 改写现场变量或字段再继续；取表达式值用 debug_evaluate、下钻对象结构用 debug_object、复盘整段经过用 debug_timeline。\n" +
        "- **当改完 bug 需要自证修复、或回归一段运行期场景时**，用 debug_verify 按场景 JSON（可选重编译 + 断点/求值/输出断言序列）一键跑到 PASS/FAIL。\n" +
        "- **当需要操作桌面 GUI 到某状态（点按钮 / 勾选 / 切下拉 / 填输入框 / 滚动）时**，使用 UI 自动化工具 ui_find（列控件与能力）→ ui_action/ui_input（语义动作/写值）→ ui_wait（等状态变化）→ ui_get（读值）；全 UIA 语义，不移动光标、不注入输入、不抢前台，且不要求活动调试会话（可先摆好 UI 状态再 debug_attach）。\n" +
        "- **当需要向用户或自己实时展示调试现场（网页监视器：断点/单步/变量/动作时间线）时**，调用 web_open 打开（幂等；仅为可选展示，不影响 agent 独立完成调试）。\n" +
        "- **当需要观察 GUI 窗口/屏幕画面（看控件状态、布局冒烟）时**，调用 screenshot 截图（window/screen/region 三模式，返回图片；图片过大会改为落盘返回路径）。\n" +
        "具体工具清单见 MCP 工具目录（`decompile`/`debug`/`ui` 等语义前缀，静态工具另有 `signature`/`list_types`/`call_*`/`hierarchy`/`dependencies`/`interface_usage`/`field_access`/`search_string`，视觉另有 `screenshot`）。\n\n" +
        "## 使用约定\n\n" +
        "程序集/目标文件路径基于当前工作目录；反编译与元数据结果带行号、支持 `lines=\"start-end\"` 分页；动态调试控制类工具异步返回（带默认超时），进程停点信息用查询类工具（`debug_state`/`debug_stack`/`debug_variables`）获取。";

    /// <summary>
    /// 判定提示文本是否以反编译失败前缀开头（转发 Decompiler 库 <see cref="DecompilerText.StartsWithDecompileFailure"/>； InProcessDecompiler.IsErrorResult
    /// 与 CallChainTool 反编译失败判重共用，与 <see cref="DecompileFailurePrefix"/> 同源）。
    /// </summary>
    public static bool StartsWithDecompileFailure(string text)
        => DecompilerText.StartsWithDecompileFailure(text);
}