namespace ILSpyMcp.Configuration;

/// <summary>
/// MCP 工具参数级 Description 模板常量：16 个工具多处重复的参数描述文本（assembly/lines/timeoutSeconds/
/// includeExternal/includeIndirect 及工具级页脚句）集中在此，改文案只需改一处。Description 属性参数要求
/// 编译期常量，故均为 const；工具经 [Description(ToolParameterText.Xxx)] 引用（工具级描述可经常量拼接，
/// const+const 仍为编译期常量）。
/// </summary>
internal static class ToolParameterText
{
    /// <summary>
    /// assembly 参数描述（15 个工具共用）。
    /// </summary>
    public const string AssemblyParam =
        "目标程序集文件路径（.dll 或 .exe），可为相对当前工作目录的路径（必填）";

    /// <summary>
    /// lines 参数描述（15 个工具共用）。
    /// </summary>
    public const string LinesParam =
        "按行号范围读取结果，格式 \"start-end\"（1-based 含两端），如 \"200-400\"；缺省返回前约 8 KB";

    /// <summary>
    /// stdout 反编译类工具的 timeoutSeconds 参数描述（decompile/decompile_member/call_chain 共用）。
    /// </summary>
    public const string TimeoutParam =
        "本次反编译超时秒数，默认 30；超时则放弃本次（结果不入缓存），可调大后重试";

    /// <summary>
    /// 写盘工具的 timeoutSeconds 参数描述（decompile_to_dir/decompile_to_project 共用）。
    /// </summary>
    public const string DiskTimeoutParam =
        "本次反编译写盘超时秒数，默认 30；全量写盘大程序集可调大";

    /// <summary>
    /// includeExternal 参数描述（dependencies/call_graph 共用；call_chain 含展开语义，单独写）。
    /// </summary>
    public const string IncludeExternalParam =
        "是否同时输出跨程序集外部类型引用（带程序集归属，格式 全名 [程序集名]，默认 false）";

    /// <summary>
    /// includeIndirect 参数描述（hierarchy/interface_usage 共用）。
    /// </summary>
    public const string IncludeIndirectParam =
        "是否包含间接后代（如接口的所有实现者、基类的所有子孙，默认 false）";

    /// <summary>
    /// 工具级描述末尾的分页页脚句（14 个工具共用，经常量拼接引用；写盘工具与 cache_stats 不含）。
    /// </summary>
    public const string FooterPagination =
        "结果默认返回前约 8 KB，可用 lines 按行号范围拉取后续。";
}
