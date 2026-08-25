namespace ILSpyMcp.Configuration;

/// <summary>
/// 缓存签名契约常量：缓存键的「工具前缀 + 分隔符」统一在此定义，各工具生成 signature 与 CacheStatsTool 展示来源工具名
/// 均引用同一来源，避免新增/改名工具时改一处漏一处（CacheStatsTool 的映射表缺新前缀会显示原始前缀）。 缓存键格式：{Prefix}\u001F{参数...}（无参数时只有 Prefix）。
/// </summary>
internal static class CacheSignatures
{
    /// <summary>
    /// 缓存签名中工具前缀与参数之间的分隔符（agent 不可见，仅缓存键内部）。
    /// </summary>
    public const char SeparatorChar = '\u001F';

    /// <summary>
    /// <see cref="SeparatorChar"/> 的字符串形式。
    /// </summary>
    public const string Separator = "\u001F";

    // ---- 反编译类（ToolPipeline.BuildSignature 按 DecompileKind 生成） ----

    /// <summary>
    /// decompile（类型级）。
    /// </summary>
    public const string Type = "type";

    /// <summary>
    /// decompile_member / call_chain 的成员反编译。
    /// </summary>
    public const string Member = "member";

    /// <summary>
    /// decompile（整模块）。
    /// </summary>
    public const string WholeModule = "whole-module";

    // ---- 元数据类（各工具自行拼接） ----

    /// <summary>
    /// list_types。
    /// </summary>
    public const string ListTypes = "list-types";

    /// <summary>
    /// signature。
    /// </summary>
    public const string Signature = "signature";

    /// <summary>
    /// hierarchy。
    /// </summary>
    public const string Hierarchy = "hierarchy";

    /// <summary>
    /// dependencies。
    /// </summary>
    public const string Dependencies = "dependencies";

    /// <summary>
    /// call_graph（类型级）。
    /// </summary>
    public const string CallGraph = "call-graph";

    /// <summary>
    /// call_graph（token 级反向调用点）。
    /// </summary>
    public const string CallGraphToken = "call-graph-token";

    /// <summary>
    /// assembly_info。
    /// </summary>
    public const string AssemblyInfo = "assembly-info";

    /// <summary>
    /// decompile_member 超限签名清单。
    /// </summary>
    public const string MemberSignatures = "member-signatures";

    /// <summary>
    /// field_access 字段访问扫描（已定位唯一字段）。
    /// </summary>
    public const string FieldAccess = "field-access";

    /// <summary>
    /// field_access 字段名多匹配清单。
    /// </summary>
    public const string FieldAccessList = "field-access-list";

    /// <summary>
    /// search_string。
    /// </summary>
    public const string SearchString = "search-string";

    /// <summary>
    /// interface_usage。
    /// </summary>
    public const string InterfaceUsage = "interface-usage";

    /// <summary>
    /// generic_instantiations。
    /// </summary>
    public const string GenericInstantiations = "generic-instantiations";
}