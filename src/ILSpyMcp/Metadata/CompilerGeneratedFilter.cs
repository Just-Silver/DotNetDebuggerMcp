using System.Reflection.Metadata;

namespace ILSpyMcp.Metadata;

/// <summary>
/// 编译器生成类型判定：list_types 默认过滤、hierarchy/dependencies 跳过共用同一规则。
/// 判定依据：C# 编译器生成的类型名必然含 '&lt;'（&lt;Module&gt;、&lt;&gt;c 显示类、&lt;PrivateImplementationDetails&gt;、
/// &lt;M&gt;d__N 状态机、&lt;&lt;Main&gt;$&gt;d__0），且 C# 标识符不允许 '&lt;'，因此「名含 &lt;」双向精确。
/// 刻意不用 CompilerGeneratedAttribute 兜底——顶层语句生成的 Program 类带该特性但是用户代码入口（ilspycmd -l 也列出），
/// 过滤会误杀用户入口；也刻意不用 "__" 前缀过滤——合法类型名（如 __ComObject）可含双下划线。
/// </summary>
public static class CompilerGeneratedFilter
{
    /// <summary>
    /// 判定类型是否为编译器生成类型。
    /// </summary>
    /// <param name="reader">元数据读取器。</param>
    /// <param name="type">待判定的类型定义。</param>
    /// <returns>编译器生成类型返回 true。</returns>
    public static bool IsCompilerGenerated(MetadataReader reader, TypeDefinition type)
        => reader.GetString(type.Name).Contains('<');
}
