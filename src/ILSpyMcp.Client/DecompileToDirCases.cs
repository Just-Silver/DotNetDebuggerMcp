namespace ILSpyMcp.Client;

/// <summary>
/// decompile_to_dir 工具的全部端到端验证场景：全量 / project / typeName / nestedDirectories / languageVersion /
/// 缺参与非法参数校验。 每个场景写独立输出子目录，避免互相覆盖；最终由入口统一清理并校验产物。
/// </summary>
public static class DecompileToDirCases
{
    public static IReadOnlyList<ToolCallCase> All(string dll, string outDir) => new[]
    {
        // 成功场景：均应以「已写入」提示收尾且不出现异常堆栈
        new ToolCallCase("decompile_to_dir", "全量写盘",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "full") },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        new ToolCallCase("decompile_to_dir", "project 项目形式",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "project"), ["project"] = true },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        new ToolCallCase("decompile_to_dir", "typeName 单类型",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "single"), ["typeName"] = TestDataHelper.TypeName },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        new ToolCallCase("decompile_to_dir", "nestedDirectories 嵌套目录",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "nested"), ["nestedDirectories"] = true },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        new ToolCallCase("decompile_to_dir", "languageVersion",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "lv"), ["languageVersion"] = "Latest" },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        new ToolCallCase("decompile_to_dir", "timeoutSeconds（自定义超时）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "timeout"), ["timeoutSeconds"] = 120 },
            ExpectedContains: "已写入", MustNotContain: "at System"),
        // 非法语言版本应返回中文校验提示而非异常堆栈
        new ToolCallCase("decompile_to_dir", "非法 languageVersion（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll, ["outputDir"] = Path.Combine(outDir, "lv-invalid"), ["languageVersion"] = "Foo" },
            ExpectedContains: "languageVersion 无效", MustNotContain: "at System", ExpectSuccess: false),
        // 缺 outputDir：返回「请指定 outputDir」校验提示
        new ToolCallCase("decompile_to_dir", "缺 outputDir（应返回校验提示）",
            new Dictionary<string, object?> { ["assembly"] = dll },
            ExpectedContains: "请指定 outputDir", MustNotContain: "at System", ExpectSuccess: false),
    };
}