namespace DotNetDebugger.Engine.Models;

/// <summary>求值结果种类：标量（含字符串）/ null / 对象 / 数组。</summary>
public enum DebugEvalKind
{
    Scalar,
    Null,
    Object,
    Array,
}

/// <summary>
/// 表达式路径求值结果（P6 纯读值）：Display/Children 与 debug_variables 同款渲染；
/// ScalarValue 供比较与布尔判定（仅标量非 null：bool/char/整型/浮点/string），非标量为 null。
/// </summary>
public sealed record DebugEvalResult(
    string Display,
    string? TypeName,
    DebugEvalKind Kind,
    IReadOnlyList<DebugVariable>? Children,
    object? ScalarValue);
