namespace ILSpyMcp.Client;

/// <summary>
/// check_status 工具的全部端到端验证场景：环境自检应返回中文报告（不抛框架错误、无异常堆栈）。
/// NuGet 新版本检查结果受网络影响不定，不断言其具体内容。
/// </summary>
public static class CheckCases
{
    public static IEnumerable<ToolCallCase> All()
    {
        yield return new ToolCallCase(
            "check_status",
            "环境自检",
            new Dictionary<string, object?>(),
            ExpectedContains: "ilspycmd",
            MustNotContain: "at System");
    }
}
