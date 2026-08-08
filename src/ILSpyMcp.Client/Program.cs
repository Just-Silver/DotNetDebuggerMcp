using ILSpyMcp.Client;

// 向上查找仓库根目录（含 ILSpyMcp.slnx），路径随仓库整体移动而自动适配
static string FindRepoRoot()
{
    var dir = new DirectoryInfo(AppContext.BaseDirectory);
    while (dir is not null)
    {
        if (File.Exists(Path.Combine(dir.FullName, "ILSpyMcp.slnx"))) return dir.FullName;
        dir = dir.Parent;
    }
    throw new DirectoryNotFoundException("未找到仓库根目录（缺少 ILSpyMcp.slnx）");
}

var root = FindRepoRoot();
var serverProject = Path.Combine(root, "src", "ILSpyMcp", "ILSpyMcp.csproj");
var dll = Path.Combine(root, "tests", "TestData", "System.Linq.dll");
var outDir = Path.Combine(root, "tests", ".ilspymcp-client-out");

var runner = await ClientRunner.ConnectAsync(serverProject);
// decompile_to_dir 产物断言的失败单独统计（不属于工具调用断言）
var artifactFailures = 0;

try
{
    await runner.ListToolsAsync();
    await runner.RunAsync(DecompileCases.All(dll));
    await runner.RunAsync(ListTypesCases.All(dll));
    await runner.RunAsync(DecompileToDirCases.All(dll, outDir));

    // 产物断言：decompile_to_dir 场景执行后、清理前校验 outDir 下确实写入了 .cs 文件
    var csCount = Directory.Exists(outDir)
        ? Directory.GetFiles(outDir, "*.cs", SearchOption.AllDirectories).Length
        : 0;
    if (csCount > 0)
    {
        Console.WriteLine($"\n[PASS] decompile_to_dir 产物校验：{outDir} 下共 {csCount} 个 .cs 文件");
    }
    else
    {
        Console.WriteLine($"\n[FAIL] decompile_to_dir 产物校验：{outDir} 下未发现 .cs 文件");
        artifactFailures++;
    }
}
finally
{
    // 无论断言结果如何都清理写盘验证产物，避免污染 tests/
    if (Directory.Exists(outDir))
    {
        Directory.Delete(outDir, recursive: true);
        Console.WriteLine($"\n已清理验证产物: {outDir}");
    }
}

var totalFailures = runner.Failures + artifactFailures;
if (totalFailures > 0)
{
    Console.WriteLine($"\n共 {totalFailures} 个场景未通过（工具调用 {runner.Failures} 个 + 产物断言 {artifactFailures} 个）。");
    Environment.ExitCode = 1;
}
else
{
    Console.WriteLine("\n全部场景通过。");
}
