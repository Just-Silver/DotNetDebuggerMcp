namespace ILSpyMcp.Formatting;

/// <summary>
/// 段落组装辅助：统一「标题 + 空占位 + 合并」样板（hierarchy/dependencies/call_graph 等元数据工具的分段输出）。
/// 标题恒入 target；items 为空时默认追加「（无）」占位，omitWhenEmpty 为 true 则只输出标题。
/// </summary>
internal static class SectionBuilder
{
    /// <summary>
    /// 追加一个段落：标题恒写入 target；items 非空时逐一写入，为空时按 omitWhenEmpty 决定追加「（无）」占位或仅保留标题。
    /// </summary>
    public static void Append(List<string> target, string title, IReadOnlyCollection<string> items, bool omitWhenEmpty = false)
    {
        target.Add(title);
        if (items.Count == 0)
        {
            if (!omitWhenEmpty) target.Add("（无）");
            return;
        }
        target.AddRange(items);
    }
}
