namespace ILSpyMcp.Client;

/// <summary>
/// decompile_to_dir / decompile_to_project 工具的全部端到端验证场景：全量 / 项目形式 / typeName / timeoutSeconds /
/// 缺参校验。 每个场景写独立输出子目录，避免互相覆盖；最终由入口统一清理并校验产物。
/// </summary>
public static class DecompileToDirCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll, string outDir) => new[]
    {
        // 成功场景：均应以「已写入」提示收尾且不出现异常堆栈
        new ToolCallCase("decompile_to_dir", "全量写盘",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "full") },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        // 项目形式由独立工具承担：写盘 600+ 类型文件较慢，超时放宽避免慢机器误杀
        new ToolCallCase("decompile_to_project", "project 项目形式",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "project"), ["timeoutSeconds"] = 600 },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        new ToolCallCase("decompile_to_dir", "typeName 单类型",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "single"), ["typeName"] = TestDataHelper.TypeName },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        new ToolCallCase("decompile_to_dir", "typeName 逗号分隔多类型",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "multi"), ["typeName"] = TestDataHelper.TypeName + "," + TestDataHelper.DerivedTypeName },
            ExpectedContains: "2 个文件", MustNotContain: "at System"),
        new ToolCallCase("decompile_to_dir", "timeoutSeconds（自定义超时）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "timeout"), ["timeoutSeconds"] = 120 },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        // 缺 outputDir：返回「请指定 outputDir」校验提示
        new ToolCallCase("decompile_to_dir", "缺 outputDir（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "请指定 outputDir", MustNotContain: "at System", ExpectSuccess: false),
    };
}