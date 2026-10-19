using System.Text;

namespace DotNetDebuggerMcp.Configuration;

/// <summary>
/// MCP 握手 <c>ServerInstructions</c> 的按能力族拼装：只注入**已启用族**的触发条件与使用约定。
/// <para>为什么按族拼装：握手是 agent 判断「何时该用本服务器」的唯一通道，客户端常只注入握手、工具目录会被截断；
/// 未启用的族必须在握手里**完全不出现**（工具名、触发词、约定都不提），否则 agent 会把不存在的能力当作可用
/// （护栏 <c>DotNetDebuggerMcpCmdTests.握手_启用族出现_未启用族不出现</c>）。</para>
/// <para>各块之间空行分隔（Markdown 段落），内部统一用 \n 换行；面向 agent，必须简短（常驻上下文且过长会被截断）。</para>
/// </summary>
internal static class HandshakeText
{
    /// <summary>服务器简介段（按族动态列出能力）。</summary>
    private const string IntroHeader = "## 服务器简介\n\n";
    private const string IntroLead = "本服务器面向 .NET 程序集与进程，";

    private const string IntroDecompile = "**反编译/静态分析**（程序集还原为 C#、探查类型/成员/调用关系）";
    private const string IntroDebug = "**动态调试**（启动/附加进程，断点、单步、栈/变量实时值，现场改值、一键复验）";
    private const string IntroUi = "**UI 自动化**（UIA 语义操控桌面控件）";
    private const string IntroScreenshot = "**视觉/监视**（截图、Web 监视器）";

    private const string IntroTail = "；反编译与调试引擎均内置，无需外部工具。";

    private const string UseHeader = "\n\n## 何时使用\n\n";
    private const string ConventionHeader = "\n\n## 使用约定\n\n";

    private const string UseDecompile =
        "- **当需要查看 .NET 程序集（.dll/.exe，含无源码或第三方）的 C# 源码、类型结构、成员签名、字符串、调用/引用关系时**，使用反编译与静态分析工具（可先列类型/查签名定位，再按需反编译）。\n";
    private const string UseDebug =
        "- **当需要弄清程序运行期行为（为何抛异常、某条件分支是否执行、变量当前值、调用路径）时**，使用动态调试：反编译定位目标方法取 token → 下断点 → 运行至命中 → 观察调用栈与变量 → 单步；想省去反复设断点可直接 debug_run_to 运行到目标位置。\n" +
        "- **当需要验证某个运行期假设（强制走某分支 / 置空 / 换引用）时**，停点后用 debug_set 改写现场变量或字段再继续；取表达式值用 debug_evaluate、下钻对象结构用 debug_object、复盘整段经过用 debug_timeline。\n" +
        "- **当改完 bug 需要自证修复、或回归一段运行期场景时**，用 debug_verify 按场景 JSON（可选重编译 + 断点/求值/输出断言序列）一键跑到 PASS/FAIL。含 uiAction/uiAssert 步骤时需服务端已启用 ui 能力族。\n" +
        "- **当需要向用户或自己实时展示调试现场（网页监视器：断点/单步/变量/动作时间线）时**，调用 web_open 打开（幂等；仅为可选展示，不影响 agent 独立完成调试）。\n";
    private const string UseUiLead =
        "- **当需要操作桌面 GUI 到某状态（点按钮 / 勾选 / 切下拉 / 填输入框 / 滚动）时**，使用 UI 自动化工具 ui_find（列控件与能力）→ ui_action/ui_input（语义动作/写值）→ ui_wait（等状态变化）→ ui_get（读值）；全 UIA 语义，不移动光标、不注入输入、不抢前台";
    // 跨族引用（debug_attach / debug_* / 截图 / ui_find）只在对应族也启用时出现——否则会指向未注册的工具（spec §8.1/§8.6）。
    private const string UseUiTailNoDebug = "。单次 UIA 调用默认 5s 护栏，目标响应慢时用 timeoutSeconds 调大（超时≠环境 UIA 不可用）。目标无 Invoke/SelectionItem（如部分 WPF-UI 导航项）时可显式 ui_action clickPoint=true 走真实点击——会移动光标/注入点击/可能抢前台，性能类排查勿用。\n";
    private const string UseUiObserveNoDebug = "- **注意观测扰动**：ui_* 会向目标进程注入 UI Automation 客户端、可能改变其性能特征；性能/卡顿类排查请勿依赖 ui_*。\n";
    private const string UseUiObserveWithScreenshot = "- **注意观测扰动**：ui_* 会向目标进程注入 UI Automation 客户端、可能改变其性能特征；性能/卡顿类排查请优先用 debug_* + 截图。\n";
    private const string UseScreenshot =
        "- **当需要观察 GUI 窗口/屏幕画面（看控件状态、布局冒烟）时**，调用 screenshot 截图：可按全屏/指定显示器（副屏）/窗口（pid、标题、前台、句柄）/窗口客户区/屏幕区域/UIA 单个元素寻址，返回图片；头部给出 原点/帧 坐标元数据（图像为原生 1:1，本服务器不做缩放），可选叠加光标（includeCursor），图片过大会改为落盘返回路径。寻址参数不确定时先列清单：显示器用 `screenshot(mode=list-displays)`、可见窗口用 `screenshot(mode=list-windows)`（hwnd/标题/pid，不限 .NET 进程）。\n";
    private const string UseScreenshotElementUi = "- 按控件截图先 `ui_find` 取 index/名与帧号；`ui_find` 输出的 `帧: frameId=N` 可回传给 `ui_action`/`ui_input`/`ui_get` 与 screenshot，用于拦截引用旧画面的操作。\n";

    private const string ToolListPrefix = "具体工具清单见 MCP 工具目录（";
    private const string ToolListDecompile = "`decompile` 等语义前缀，静态工具另有 `signature`/`list_types`/`call_*`/`hierarchy`/`dependencies`/`interface_usage`/`field_access`/`search_literals`";
    private const string ToolListDebug = "调试 `debug_*`";
    private const string ToolListUi = "UI `ui_*`";
    private const string ToolListScreenshot = "视觉 `screenshot`（含 `mode=list-displays` / `list-windows` 只读清单）";
    private const string ToolListSuffix = "）。\n";

    private const string ConventionStatic =
        "程序集/目标文件路径基于当前工作目录；反编译与元数据结果带行号、支持 `lines=\"start-end\"` 分页。";
    private const string ConventionDebug =
        "动态调试控制类工具异步返回（带默认超时），进程停点信息用查询类工具（`debug_wait`/`debug_frame`）获取。";
    private const string ConventionDebugFrozen =
        "attach/launch 后进程即冻结，可直接读栈。";

    /// <summary>
    /// 按启用族拼装握手文本。<paramref name="enabled"/> 为 <see cref="FeatureFamily.None"/> 时**不注入能力简介**
    /// （连「## 何时使用」也不出现），只返回空串——由调用方决定是否再拼更新报告段。
    /// </summary>
    internal static string Build(FeatureFamily enabled, string? updateReport)
    {
        var sb = new StringBuilder();
        if (enabled != FeatureFamily.None)
        {
            sb.Append(IntroHeader).Append(IntroLead).Append(JoinFamilies(enabled)).Append(IntroTail);
            sb.Append(UseHeader);
            if (enabled.HasFlag(FeatureFamily.Decompile)) sb.Append(UseDecompile);
            if (enabled.HasFlag(FeatureFamily.Debug)) sb.Append(UseDebug);
            if (enabled.HasFlag(FeatureFamily.Ui)) sb.Append(BuildUseUi(enabled));
            if (enabled.HasFlag(FeatureFamily.Screenshot)) sb.Append(BuildUseScreenshot(enabled));
            sb.Append('\n').Append(BuildToolList(enabled));
            sb.Append(ConventionHeader).Append(BuildConvention(enabled));
        }
        if (!string.IsNullOrEmpty(updateReport))
        {
            if (sb.Length > 0) sb.Append("\n\n");
            sb.Append(updateReport);
        }
        return sb.ToString();
    }

    /// <summary>按族列出工具前缀（只列已启用族，避免未启用族的工具名泄漏进握手）。</summary>
    private static string BuildToolList(FeatureFamily enabled)
    {
        var parts = new List<string>(4);
        if (enabled.HasFlag(FeatureFamily.Decompile)) parts.Add(ToolListDecompile);
        if (enabled.HasFlag(FeatureFamily.Debug)) parts.Add(ToolListDebug);
        if (enabled.HasFlag(FeatureFamily.Ui)) parts.Add(ToolListUi);
        if (enabled.HasFlag(FeatureFamily.Screenshot)) parts.Add(ToolListScreenshot);
        return ToolListPrefix + string.Join("，", parts) + ToolListSuffix;
    }

    /// <summary>使用约定：静态约定恒在；调试约定只在调试族启用时出现。</summary>
    private static string BuildConvention(FeatureFamily enabled)
        => enabled.HasFlag(FeatureFamily.Debug)
            ? $"{ConventionStatic}{ConventionDebugFrozen}{ConventionDebug}"
            : ConventionStatic;


    /// <summary>ui 段：仅当 debug / screenshot 也启用时才引用它们（否则会指向未注册的工具）。</summary>
    private static string BuildUseUi(FeatureFamily enabled)
    {
        var sb = new StringBuilder(UseUiLead);
        if (enabled.HasFlag(FeatureFamily.Debug)) sb.Append("，且不要求活动调试会话（可先摆好 UI 状态再 debug_attach）");
        sb.Append(UseUiTailNoDebug);
        sb.Append(enabled.HasFlag(FeatureFamily.Screenshot) ? UseUiObserveWithScreenshot : UseUiObserveNoDebug);
        return sb.ToString();
    }

    /// <summary>screenshot 段：仅在 ui 也启用时才提「按控件截图用 ui_find 取 index/frameId」。</summary>
    private static string BuildUseScreenshot(FeatureFamily enabled)
        => enabled.HasFlag(FeatureFamily.Ui) ? UseScreenshot + UseScreenshotElementUi : UseScreenshot;

    /// <summary>按固定顺序列出启用族的中文能力短语（顿号分隔）。</summary>
    private static string JoinFamilies(FeatureFamily enabled)
    {
        var parts = new List<string>(4);
        if (enabled.HasFlag(FeatureFamily.Decompile)) parts.Add(IntroDecompile);
        if (enabled.HasFlag(FeatureFamily.Debug)) parts.Add(IntroDebug);
        if (enabled.HasFlag(FeatureFamily.Ui)) parts.Add(IntroUi);
        if (enabled.HasFlag(FeatureFamily.Screenshot)) parts.Add(IntroScreenshot);
        return string.Join("、", parts);
    }
}
