namespace DotNetDebugger.Engine.Models;

/// <summary>
/// 表达式路径段（P6 表达式读值子集）：从栈顶帧根变量起逐段定位的坐标。
/// 引擎按段直读（每段解一次引用），绕开 MaxChildren 截断——数组任意下标、深层链都可靠。
/// </summary>
public abstract record PathSegment
{
    /// <summary>路径段数上限（防失控长链；解析层与引擎双侧校验，宿主文案数字为受此常量约束的副本）。</summary>
    public const int MaxSegments = 8;

    /// <summary>字段段：按名取实例字段。属性不可直接读（getter 是目标进程代码），引擎按
    /// X → _x → _X → &lt;X&gt;k__BackingField 约定降级，全未命中报错附可用字段清单。</summary>
    public sealed record Field(string Name) : PathSegment;

    /// <summary>索引段：非负下标（负索引 v1 不支持）。仅作用于数组/字符串（字符串索引得单字符字符串）。</summary>
    public sealed record Index(int Position) : PathSegment;
}
