using ILSpyMcp;
using ILSpyMcp.Tools;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// 工具前置检查（安装检测 + assembly 校验）与工具层注入 fake 的用例；所有用例在 finally 中恢复 AppServices 默认实现。
/// </summary>
public class ToolPreflightTests
{
    private static readonly string AssemblyPath = typeof(ToolPreflightTests).Assembly.Location;

    [Fact]
    public async Task 安装检测失败_返回安装提示()
    {
        var fake = new FakeProcessRunner { Code = 1 };
        AppServices.ConfigureForTest(fake);
        try
        {
            var result = await ToolPreflight.CheckAsync(AssemblyPath);
            Assert.Equal(InstallChecker.InstallHint, result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 安装成功但程序集不存在_返回文件不存在提示()
    {
        var fake = new FakeProcessRunner { Code = 0 };
        AppServices.ConfigureForTest(fake);
        try
        {
            var missing = Path.Combine(Path.GetTempPath(), $"ilspymcp-missing-{Guid.NewGuid():N}.dll");
            var result = await ToolPreflight.CheckAsync(missing);
            Assert.NotNull(result);
            Assert.Contains("程序集文件不存在", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 安装成功且程序集存在_返回null()
    {
        var fake = new FakeProcessRunner { Code = 0 };
        AppServices.ConfigureForTest(fake);
        try
        {
            var result = await ToolPreflight.CheckAsync(AssemblyPath);
            Assert.Null(result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task Decompile_注入fake_正常返回格式化结果()
    {
        var fake = new FakeProcessRunner { Code = 0, Stdout = "a\nb\n" };
        AppServices.ConfigureForTest(fake);
        try
        {
            var result = await DecompileTool.Decompile(assembly: AssemblyPath, typeName: "System.String");
            Assert.Equal("1\ta\n2\tb", result);
            Assert.Equal(2, fake.CallCount); // 1 次安装检测 + 1 次反编译
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task Decompile_指定timeoutSeconds_透传超时并返回结果()
    {
        var fake = new FakeProcessRunner { Code = 0, Stdout = "a\n" };
        AppServices.ConfigureForTest(fake);
        try
        {
            var result = await DecompileTool.Decompile(assembly: AssemblyPath, typeName: "System.String", timeoutSeconds: 99);
            Assert.Equal("1\ta", result);
            Assert.Equal(TimeSpan.FromSeconds(99), fake.Timeout);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task Decompile_非法timeoutSeconds_返回校验提示()
    {
        AppServices.ConfigureForTest(new FakeProcessRunner { Code = 0 });
        try
        {
            var result = await DecompileTool.Decompile(assembly: AssemblyPath, typeName: "System.String", timeoutSeconds: 0);
            Assert.Contains("timeoutSeconds 必须为正整数", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task ListTypes_指定timeoutSeconds_透传超时并返回结果()
    {
        var fake = new FakeProcessRunner { Code = 0, Stdout = "a\n" };
        AppServices.ConfigureForTest(fake);
        try
        {
            var result = await ListTypesTool.ListTypes(assembly: AssemblyPath, list: "c", timeoutSeconds: 45);
            Assert.Equal("1\ta", result);
            Assert.Equal(TimeSpan.FromSeconds(45), fake.Timeout);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task DecompileToDir_指定timeoutSeconds_透传超时并返回写入提示()
    {
        var fake = new FakeProcessRunner { Code = 0 };
        AppServices.ConfigureForTest(fake);
        var output = Path.Combine(Path.GetTempPath(), $"ilspymcp-out-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(output);
            var result = await DecompileToDirTool.DecompileToDir(assembly: AssemblyPath, outputDir: output, typeName: "System.String", timeoutSeconds: 300);
            Assert.Contains("已写入", result);
            Assert.Equal(TimeSpan.FromSeconds(300), fake.Timeout);
        }
        finally
        {
            AppServices.ResetForTest();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }

    [Fact]
    public async Task ListTypes_注入fake_返回类型列表()
    {
        var fake = new FakeProcessRunner { Code = 0, Stdout = "a\n" };
        AppServices.ConfigureForTest(fake);
        try
        {
            var result = await ListTypesTool.ListTypes(assembly: AssemblyPath, list: "c");
            Assert.Equal("1\ta", result);
            Assert.Equal(2, fake.CallCount); // 1 次安装检测 + 1 次类型列表
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task DecompileToDir_注入fake_成功返回写入提示()
    {
        var fake = new FakeProcessRunner { Code = 0 };
        AppServices.ConfigureForTest(fake);
        var output = Path.Combine(Path.GetTempPath(), $"ilspymcp-out-{Guid.NewGuid():N}");
        try
        {
            Directory.CreateDirectory(output);
            var result = await DecompileToDirTool.DecompileToDir(assembly: AssemblyPath, outputDir: output, typeName: "System.String");
            Assert.Contains("已写入", result);
            Assert.Equal(2, fake.CallCount); // 1 次安装检测 + 1 次反编译写盘
        }
        finally
        {
            AppServices.ResetForTest();
            if (Directory.Exists(output)) Directory.Delete(output, recursive: true);
        }
    }
}
