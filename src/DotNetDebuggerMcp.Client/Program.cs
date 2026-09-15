using DotNetDebuggerMcp.Client;

var root = TestDataHelper.RepoRoot;
var serverProject = Path.Combine(root, "src", "DotNetDebuggerMcp", "DotNetDebuggerMcp.csproj");
var dll = TestDataHelper.Dll;
var outDir = Path.Combine(root, "tests", ".dotnetdebugger-client-out");

var runner = await ClientRunner.ConnectAsync(serverProject);
// decompile_to_dir 产物断言的失败单独统计（不属于工具调用断言）
var artifactFailures = 0;

try
{
    await runner.ListToolsAsync();
    await runner.RunAsync(DecompileCases.All(dll));
    await runner.RunAsync(DecompileCases.CrossAssembly(TestDataHelper.ExtDll));
    // 文件句柄释放断言（server 进程仍存活，紧随 Ext 反编译之后）：Ext 反编译让 server 解析了同目录依赖 TestSamples.dll——
    // 两个 dll 都必须可独占打开；残留句柄会锁住构建输出目录，用户侧 dotnet build/clean 报 MSB3061/MSB3021
    artifactFailures += LockProbe(TestDataHelper.ExtDll, TestDataHelper.Dll);
    await runner.RunAsync(DecompileMemberCases.All(dll));
    await runner.RunAsync(DecompileIlCases.All(dll));
    await runner.RunAsync(ListTypesCases.All(dll));
    await runner.RunAsync(AssemblyInfoCases.All(dll));
    await runner.RunAsync(SignatureCases.All(dll));
    await runner.RunAsync(HierarchyCases.All(dll));
    await runner.RunAsync(DependenciesCases.All(dll));
    await runner.RunAsync(CallGraphCases.All(dll));
    await runner.RunAsync(InterfaceUsageCases.All(dll));
    await runner.RunAsync(GenericInstantiationCases.All(dll));
    await runner.RunAsync(CallChainCases.All(dll));
    await runner.RunAsync(CallChainCases.CrossAssembly(TestDataHelper.ExtDll));
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

// 独占打开探测：任何未释放的句柄（含 FileShare.Read 共享读）都会让本次读写打开抛 IOException。
// 探测在 Client 进程执行，命中即说明 server 进程仍持有该文件句柄。
static int LockProbe(params string[] paths)
{
    var failures = 0;
    foreach (var path in paths)
    {
        try
        {
            using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Console.WriteLine($"{Environment.NewLine}[PASS] 文件句柄释放：{Path.GetFileName(path)} 可独占打开（server 进程仍存活）");
        }
        catch (IOException ex)
        {
            Console.WriteLine($"{Environment.NewLine}[FAIL] 文件句柄释放：{Path.GetFileName(path)} 仍被 server 进程占用（{ex.Message}）");
            failures++;
        }
    }
    return failures;
}
