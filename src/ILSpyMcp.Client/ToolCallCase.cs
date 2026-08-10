namespace ILSpyMcp.Client;

/// <summary>
/// 一个端到端验证场景：目标工具 + 说明 + 参数 + 预期结果断言字段。
/// </summary>
/// <param name="Tool">MCP 工具名。</param>
/// <param name="Label">场景说明，打印时区分。</param>
/// <param name="Args">工具调用参数。</param>
/// <param name="ExpectedContains">结果文本必须包含的子串（成功关键词或错误提示关键词）；为 null 时不检查。</param>
/// <param name="MustNotContain">结果文本不得包含的子串（如异常堆栈 "at System"）；为 null 时不检查。</param>
/// <param name="ExpectSuccess">预期调用成功（CallToolAsync 的 IsError 应为 false）；预期返回错误提示文本的场景设为 false。</param>
public sealed record ToolCallCase(
    string Tool,
    string Label,
    IReadOnlyDictionary<string, object?> Args,
    string? ExpectedContains = null,
    string? MustNotContain = null,
    bool ExpectSuccess = true);