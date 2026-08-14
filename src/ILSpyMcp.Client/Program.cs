using ILSpyMcp.Client;

var root = TestDataHelper.RepoRoot;
var serverProject = Path.Combine(root, "src", "ILSpyMcp", "ILSpyMcp.csproj");
var dll = TestDataHelper.Dll;
var outDir = Path.Combine(root, "tests", ".ilspymcp-client-out");

var runner = await ClientRunner.ConnectAsync(serverProject);
// decompile_to_dir 产物断言的失败单独统计（不属于工具调用断言）
var artifactFailures = 0;

try
{
    await runner.ListToolsAsync();
    await runner.RunAsync(DecompileCases.All(dll));
    await runner.RunAsync(DecompileMemberCases.All(dll));
    await runner.RunAsync(ListTypesCases.All(dll));
    await runner.RunAsync(AssemblyInfoCases.All(dll));
    await runner.RunAsync(SignatureCases.All(dll));
    await runner.RunAsync(HierarchyCases.All(dll));
    await runner.RunAsync(DependenciesCases.All(dll));
    await runner.RunAsync(CallGraphCases.All(dll));
    await runner.RunAsync(SearchStringCases.All(dll));
    await runner.RunAsync(FieldAccessCases.All(dll));
    await runner.RunAsync(DecompileToDirCases.All(dll, outDir));

    // 产物断言：decompile_to_dir / decompile_to_project 场景执行后、清理前校验 outDir 下确实写入了 .cs 文件
    var csCount = Directory.Exists(outDir)
        ? Directory.GetFiles(outDir, "*.cs", SearchOption.AllDirectories).Length
        : 0;
    if (csCount > 0)
    {
        Console.WriteLine($"{Environment.NewLine}[PASS] decompile_to_dir/decompile_to_project 产物校验：{outDir} 下共 {csCount} 个 .cs 文件");
    }
    else
    {
        Console.WriteLine($"{Environment.NewLine}[FAIL] decompile_to_dir/decompile_to_project 产物校验：{outDir} 下未发现 .cs 文件");
        artifactFailures++;
    }
}
finally
{
    // 无论断言结果如何都清理写盘验证产物，避免污染 tests/
    if (Directory.Exists(outDir))
    {
        Directory.Delete(outDir, recursive: true);
        Console.WriteLine($"{Environment.NewLine}已清理验证产物: {outDir}");
    }
}

var totalFailures = runner.Failures + artifactFailures;
if (totalFailures > 0)
{
    Console.WriteLine($"{Environment.NewLine}共 {totalFailures} 个场景未通过（工具调用 {runner.Failures} 个 + 产物断言 {artifactFailures} 个）。");
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine($"{Environment.NewLine}全部场景通过。");
}