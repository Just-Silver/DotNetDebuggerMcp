namespace DotNetDebugger.Engine.Session;

/// <summary>
/// first-chance 异常断点过滤器：空 = 全部异常；否则按类型名匹配——
/// 全名相等 或 全名以「.过滤名」结尾（短名匹配，如 "DivideByZeroException" 命中 "System.DivideByZeroException"），
/// 忽略大小写。刻意不做任意子串（如 "Exception" 会命中一切，语义陷阱）；不匹配的异常由回调跳过并计数反馈。
/// </summary>
public sealed class ExceptionBreakpointFilter
{
    /// <summary>要捕获的异常类型名（全名或短名）；空/空串 = 全部 first-chance 异常。</summary>
    public string? TypeName { get; }

    public ExceptionBreakpointFilter(string? typeName = null) => TypeName = string.IsNullOrWhiteSpace(typeName) ? null : typeName.Trim();

    /// <summary>判断是否命中：过滤器为空(全部)；否则全名相等或以「.过滤名」结尾，忽略大小写。</summary>
    public bool Matches(string exceptionTypeFullName)
    {
        if (TypeName is null) return true;
        if (string.IsNullOrEmpty(exceptionTypeFullName)) return false;
        return exceptionTypeFullName.Equals(TypeName, StringComparison.OrdinalIgnoreCase)
               || exceptionTypeFullName.EndsWith("." + TypeName, StringComparison.OrdinalIgnoreCase);
    }
}
