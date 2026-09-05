namespace DotNetDebugger.Engine.Models;

/// <summary>
/// 变量值（标量渲染 + 简单对象首层字段）。v1 覆盖：primitive/string/值类型标量 → Scalar；
/// 引用类型非字符串 → 展开首层字段（深度 1）；数组/复杂结构降级为摘要文本。
/// </summary>
public sealed record DebugValue(string Kind, string Display, IReadOnlyList<DebugVariable>? Children = null)
{
    public static DebugValue Scalar(string display) => new("scalar", display);
    public static DebugValue Summary(string kind, string display) => new(kind, display);
    public static DebugValue Object(string display, IReadOnlyList<DebugVariable> children) => new("object", display, children);
}
