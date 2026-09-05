namespace DotNetDebugger.Decompiler.Metadata;

/// <summary>
/// 相近名匹配：编辑距离 ≤ 2 或与查询名共享 ≥ 4 字符公共前缀视为相近，供「未找到类型/未找到成员」时给 agent 相近名提示。 算法集中于此，MemberResolver（成员名）与 MetadataNaming（类型名）共用同一套判定，避免两处实现漂移。
/// </summary>
public static class SimilarNameMatcher
{
    /// <summary>
    /// 在候选中查找与查询名相近的项：编辑距离 ≤ 2 或共享公共前缀 ≥ 4 字符，按名序排序取前 max 个。
    /// </summary>
    /// <param name="candidates">候选名集合。</param>
    /// <param name="query">查询名。</param>
    /// <param name="max">最多返回个数（默认 5）。</param>
    /// <returns>相近项列表，可能为空。</returns>
    public static IReadOnlyList<string> FindSimilar(IEnumerable<string> candidates, string query, int max = 5)
        => candidates
            .Where(n => IsSimilar(n, query))
            .OrderBy(n => n, StringComparer.Ordinal)
            .Take(max)
            .ToList();

    /// <summary>
    /// 判定单个候选名是否与查询名相近：编辑距离 ≤ 2 或共享公共前缀 ≥ 4 字符。 类型名匹配时全名与短名（最后一段）分别调用本方法，兼容只输短名的查询。
    /// </summary>
    public static bool IsSimilar(string candidate, string query)
    {
        // 长度差 >2 时编辑距离必 >2，跳过 Levenshtein 矩阵计算仅查公共前缀（与原判定严格等价）
        if (Math.Abs(candidate.Length - query.Length) > 2) return CommonPrefixLength(candidate, query) >= 4;
        return LevenshteinDistance(candidate, query) <= 2 || CommonPrefixLength(candidate, query) >= 4;
    }

    /// <summary>
    /// Levenshtein 编辑距离：插入/删除/替换各计 1，用于相近名判定（≤ 2 视为相近）。
    /// </summary>
    public static int LevenshteinDistance(string a, string b)
    {
        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];
        for (var j = 0; j <= b.Length; j++) prev[j] = j;
        for (var i = 1; i <= a.Length; i++)
        {
            curr[0] = i;
            for (var j = 1; j <= b.Length; j++)
            {
                var cost = a[i - 1] == b[j - 1] ? 0 : 1;
                curr[j] = Math.Min(Math.Min(curr[j - 1] + 1, prev[j] + 1), prev[j - 1] + cost);
            }
            (prev, curr) = (curr, prev);
        }
        return prev[b.Length];
    }

    /// <summary>
    /// 两个字符串的最长公共前缀长度。
    /// </summary>
    private static int CommonPrefixLength(string a, string b)
    {
        var n = Math.Min(a.Length, b.Length);
        var i = 0;
        while (i < n && a[i] == b[i]) i++;
        return i;
    }
}