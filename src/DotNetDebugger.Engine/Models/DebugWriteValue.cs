namespace DotNetDebugger.Engine.Models;

/// <summary>
/// 停点写值指令（W1 debug_set 引擎底座）：描述「末段值对象」要写成什么。
/// Null=引用目标置 null（仅引用/字符串引用有效）；Scalar=值类型目标按文本转换后写
/// （数字/bool，enum 目标按底层整型——引擎按目标元素类型解析）；CopyPath=引用目标
/// 重定向到「同一停点现场」另一条路径解析出的对象引用。
/// </summary>
public abstract record DebugWriteValue
{
    /// <summary>引用置 null（SetValue(地址 0)）。</summary>
    public sealed record Null() : DebugWriteValue;

    /// <summary>标量字面量文本（文法判定在 Session，目标类型转换在 Engine）。</summary>
    public sealed record Scalar(string Text) : DebugWriteValue;

    /// <summary>引用重定向：把目标引用改为指向源路径（Root+Segments，同帧解析）解出的对象。</summary>
    public sealed record CopyPath(string Root, IReadOnlyList<PathSegment> Segments) : DebugWriteValue;
}

/// <summary>停点写值结果（原值 → 新值回显，防 agent 误判改写是否触及原因）。</summary>
public sealed record DebugWriteResult(
    string OldDisplay,
    string NewDisplay,
    string? TypeName);
