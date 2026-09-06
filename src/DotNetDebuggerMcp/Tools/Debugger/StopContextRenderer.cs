using System.Text;
using DotNetDebugger.Decompiler.Document;
using DotNetDebugger.Session;
using DotNetDebuggerMcp.Services;

namespace DotNetDebuggerMcp.Tools.Debugger;

/// <summary>
/// 停点上下文渲染（P4）：把停点帧（模块+方法 token+IL）映射到反编译视图，附当前语句周边代码。
/// 行号与 decompile 输出同系（P3 渲染统一），agent 可直接用该行号走 debug_breakpoint_set(typeName, line)。
/// 预算语义（AppConfig.DefaultStopContextBudgetLines / contextLines 参数）：方法完整行数 ≤ 预算显示整个函数；
/// 超出按当前语句截取预算行并注明。任何映射失败降级为一句说明，不影响工具主返回。
/// </summary>
internal static class StopContextRenderer
{
    public static async Task<string?> RenderAsync(ActiveDebugSession active, int budgetLines)
    {
        if (budgetLines <= 0) return null;
        if (active.Buffer.LastStop?.TopFrame is not { } frame) return null;

        try
        {
            var modulePath = await active.Session.GetModulePathAsync(frame.ModuleName);
            if (modulePath is null) return "停点上下文不可用：模块路径未登记。";
            var typeFullName = DocumentService.FindTypeByToken(modulePath, frame.MethodToken);
            if (typeFullName is null) return "停点上下文不可用：无法从方法 token 反查类型（动态/生成方法）。";
            var doc = DebugSessionService.Documents.GetOrLoad(modulePath, typeFullName);
            if (doc.Error is not null) return $"停点上下文不可用：{doc.Error}";

            // 当前语句行：精确映射缺失（如停在方法首指令前）落方法首条语句行
            var currentLine = DocumentService.GetLineForIlOffset(doc, frame.MethodToken, frame.IlOffset)
                ?? DocumentService.GetMethodFirstLine(doc, frame.MethodToken);
            if (currentLine is null) return "停点上下文不可用：该方法无语句映射。";

            var (start, end, truncation) = SelectWindow(doc, frame.MethodToken, currentLine.Value, budgetLines);

            var sb = new StringBuilder();
            sb.Append($"停点上下文（{typeFullName} 第 {currentLine} 行");
            if (truncation.Length > 0) sb.Append($"，{truncation}");
            sb.Append("）:");
            sb.AppendLine();
            for (var i = start; i <= end; i++)
            {
                sb.Append($"{i}\t{doc.Lines[i - 1]}");
                if (i == currentLine) sb.Append("  ← 当前语句");
                sb.AppendLine();
            }
            return sb.ToString().TrimEnd();
        }
        catch (Exception ex)
        {
            return $"停点上下文不可用：{ex.Message}";
        }
    }

    /// <summary>选窗口：方法行区间在预算内显示整个函数；超出按当前语句截取预算行（clamp 到文档范围）。</summary>
    private static (int Start, int End, string Truncation) SelectWindow(SourceDocument doc, int methodToken, int currentLine, int budgetLines)
    {
        var range = DocumentService.GetMethodLineRange(doc, methodToken);
        if (range is { } r && r.End - r.Start + 1 <= budgetLines)
            return (r.Start, r.End, "");

        var half = budgetLines / 2;
        var start = Math.Max(1, currentLine - half);
        var end = Math.Min(doc.Lines.Length, start + budgetLines - 1);
        start = Math.Max(1, end - budgetLines + 1);
        var note = range is null
            ? $"超出预算 {budgetLines}，按当前语句截取"
            : $"方法共 {range.Value.End - range.Value.Start + 1} 行，超出预算 {budgetLines}，按当前语句截取";
        return (start, end, note);
    }
}
