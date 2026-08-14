namespace ILSpyMcp.Formatting;

/// <summary>
/// 段落组装辅助：统一「标题 + 空占位 + 合并」样板（hierarchy/dependencies/call_graph 等元数据工具的分段输出）。
/// items 非空时写标题 + 内容；items 为空时默认追加「（无）」占位，omitWhenEmpty 为 true 时空段整段省略（标题与占位都不写）。
/// </summary>
internal static class SectionBuilder
{
    /// <summary>空段占位文本（各元数据工具分段输出的空段统一用此占位，避免手写（无）造成文案漂移）。</summary>
    public const string EmptyPlaceholder = "（无）";

    /// <summary>
    /// 追加一个段落：items 非空时写标题后逐一写入内容；items 为空时默认写标题 + 「（无）」占位，
    /// omitWhenEmpty 为 true 则整段省略——标题与占位均不写入 target（供空段需整体消失的段落使用）。
    /// </summary>
    public static void Append(List<string> target, string title, IReadOnlyCollection<string> items, bool omitWhenEmpty = false)
    {
        if (items.Count == 0 && omitWhenEmpty) return;
        target.Add(title);
        if (items.Count == 0)
        {
            target.Add(EmptyPlaceholder);
            return;
        }
        target.AddRange(items);
    }
}
