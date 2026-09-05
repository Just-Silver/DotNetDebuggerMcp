namespace DotNetDebugger.Engine.Session;

/// <summary>first-chance 异常断点：按异常类型名过滤（空 = 全部）。v1 支持单类型或空。</summary>
public sealed class ExceptionBreakpointFilter
{
    /// <summary>要捕获的异常类型全名；空/空串 = 全部 first-chance 异常。</summary>
    public string? TypeName { get; }

    public ExceptionBreakpointFilter(string? typeName = null) => TypeName = typeName;

    /// <summary>判断是否命中：过滤器为空(全部)或异常类型名匹配（全名或子串）。</summary>
    public bool Matches(string exceptionTypeFullName)
        => string.IsNullOrEmpty(TypeName)
           || exceptionTypeFullName.Equals(TypeName, StringComparison.Ordinal)
           || exceptionTypeFullName.Contains(TypeName, StringComparison.Ordinal);
}
