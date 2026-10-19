using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Tools.Decompile;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// decompile_to_dir 的 project 模式：同一个工具既能写单文件布局，也能写可编译项目（含 csproj），
/// 避免 agent 在两个近义工具间选择。
/// </summary>
[Collection("AppServices")]
public sealed class DecompileToDirProjectModeTests
{
    [Fact]
    public async Task 项目模式_写出工程文件与源码()
    {
        AppServices.ConfigureForTest();
        var dir = NewTempDir();
        try
        {
            var result = await DecompileToDirTool.DecompileToDir(
                TestDataPaths.TestSamplesDll, dir, project: true,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("个文件", result);
            Assert.True(File.Exists(Path.Combine(dir, TestDataPaths.TestSamplesAssemblyName + ".csproj")),
                $"项目模式应写出 {TestDataPaths.TestSamplesAssemblyName}.csproj，实际：{result}");
            Assert.NotEmpty(Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories));
        }
        finally
        {
            Cleanup(dir);
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 默认模式_不写工程文件()
    {
        AppServices.ConfigureForTest();
        var dir = NewTempDir();
        try
        {
            var result = await DecompileToDirTool.DecompileToDir(
                TestDataPaths.TestSamplesDll, dir,
                cancellationToken: TestContext.Current.CancellationToken);

            Assert.False(File.Exists(Path.Combine(dir, TestDataPaths.TestSamplesAssemblyName + ".csproj")));
            Assert.NotEmpty(Directory.GetFiles(dir, "*.decompiled.cs"));
        }
        finally
        {
            Cleanup(dir);
            AppServices.ResetForTest();
        }
    }

    private static string NewTempDir()
        => Path.Combine(Path.GetTempPath(), "dnd-projmode-" + Guid.NewGuid().ToString("N"));

    private static void Cleanup(string dir)
    {
        try { if (Directory.Exists(dir)) Directory.Delete(dir, recursive: true); } catch (IOException) { }
    }
}
