using DotNetDebugger.Decompiler.Document;
using DotNetDebugger.Engine.Models;
using DotNetDebugger.Session;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// async 状态机帧识别/标注助手（C④ + B①，宿主提示层）。
/// Task 2（C）后栈帧 TypeName 是真名（嵌套类型用「+」）；async 方法 Foo 的编译器生成状态机
/// 类型形如「Ns.Outer+&lt;Foo&gt;d__44」/「&lt;Foo&gt;d__44」。本助手从全名解析「&lt;原方法名&gt;d__N」
/// 形态，供 debug_stack 帧标注（C④）、debug_step 引导与停点上下文备注（B①）三处使用。
/// 刻意比 Decompiler 的 CompilerGeneratedFilter（名含「&lt;」即编译器生成）收窄——lambda 闭包
/// &lt;&gt;c__DisplayClass 等不含「&gt;d__」形态，不视为 async 状态机。
/// </summary>
internal static class StateMachineFrameHelper
{
    /// <summary>
    /// 从嵌套类型全名解析 async 状态机对应原方法名（原方法名去掉「&lt;&gt;」）。
    /// 输入 Ns.Outer+&lt;Foo&gt;d__44 / &lt;Foo&gt;d__44 → ("Ns.Outer+"/"" , "Foo")；非状态机返回 null。
    /// 实现按 task-3 brief 原样：不校验「&gt;d__」之后的数字尾缀（&lt;Foo&gt;d__ 也识别为状态机）；
    /// 缺「&lt;」的 Foo&gt;d__44 天然落 LastIndexOf("&lt;") = -1 → 判非状态机。
    /// </summary>
    public static (string Prefix, string MethodName)? TryParseStateMachine(string fullName)
    {
        var idx = fullName.LastIndexOf("<", StringComparison.Ordinal);
        if (idx < 0) return null;
        var close = fullName.IndexOf(">d__", idx, StringComparison.Ordinal);
        if (close < 0) return null;
        return (fullName[..idx], fullName[(idx + 1)..close]);
    }

    /// <summary>
    /// 反查停点顶帧方法所属类型是否为 async 状态机（B① debug_step 引导用）。
    /// 停点帧只有 模块名+方法 token（无类型名）——沿 StopContextRenderer 同款反查：
    /// Session.GetModulePathAsync → DocumentService.FindTypeByToken（返回嵌套「+」全名）→ TryParseStateMachine。
    /// 非状态机 / 任一步失败（模块未登记、动态方法、磁盘读异常）返回 null，调用方静默不加引导
    /// （失败不影响单步本身——引导只是附加提示）。
    /// </summary>
    public static async Task<(string Prefix, string MethodName)?> TryResolveTopFrameStateMachineAsync(
        ActiveDebugSession active,
        FrameLocation? frame,
        CancellationToken cancellationToken = default)
    {
        if (frame is null) return null;
        string? modulePath;
        try
        {
            modulePath = await active.Session.GetModulePathAsync(frame.ModuleName, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            return null;
        }
        if (modulePath is null) return null;
        string? typeFullName;
        try
        {
            typeFullName = DocumentService.FindTypeByToken(modulePath, frame.MethodToken);
        }
        catch
        {
            return null;
        }
        return typeFullName is null ? null : TryParseStateMachine(typeFullName);
    }
}

