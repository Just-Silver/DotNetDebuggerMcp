using ILSpyMcp.Validation;
using Xunit;

namespace ILSpyMcp.Tests;

public class ArgumentValidatorsTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"ilspymcp-test-{Guid.NewGuid():N}.dll");

    public ArgumentValidatorsTests()
    {
        File.WriteAllText(_tempFile, "test");
    }

    public void Dispose()
    {
        File.Delete(_tempFile);
    }

    [Fact]
    public void ValidateAssembly_为空_返回错误提示()
    {
        var ok = ArgumentValidators.ValidateAssembly("", out var error);
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ValidateAssembly_文件不存在_返回错误提示()
    {
        var ok = ArgumentValidators.ValidateAssembly(Path.Combine(Path.GetTempPath(), "no-such-file.dll"), out var error);
        Assert.False(ok);
        Assert.Contains("不存在", error!);
    }

    [Fact]
    public void ValidateAssembly_文件存在_校验通过()
    {
        var ok = ArgumentValidators.ValidateAssembly(_tempFile, out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateRequired_为空_返回错误提示()
    {
        var ok = ArgumentValidators.ValidateRequired("", "请指定输出目录。", out var error);
        Assert.False(ok);
        Assert.Equal("请指定输出目录。", error);
    }

    [Fact]
    public void ValidateRequired_非空_校验通过()
    {
        var ok = ArgumentValidators.ValidateRequired("out", "请指定输出目录。", out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateRequired_仅空白_返回错误提示()
    {
        var ok = ArgumentValidators.ValidateRequired("   ", "请指定输出目录。", out var error);
        Assert.False(ok);
        Assert.Equal("请指定输出目录。", error);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(-100)]
    public void ValidateTimeoutSeconds_非正数_返回错误提示(int value)
    {
        var ok = ArgumentValidators.ValidateTimeoutSeconds(value, out var error);
        Assert.False(ok);
        Assert.Contains("不允许永不超时", error!);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(30)]
    [InlineData(300)]
    public void ValidateTimeoutSeconds_正整数_校验通过(int value)
    {
        var ok = ArgumentValidators.ValidateTimeoutSeconds(value, out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateAssembly_路径含非法字符_返回非法提示而非抛异常()
    {
        var ok = ArgumentValidators.ValidateAssembly(@"C:\a|b.dll", out var error);
        Assert.False(ok);
        Assert.Contains("程序集路径非法", error!);
    }

    [Fact]
    public void ValidateAssembly_路径含引号_返回非法提示而非抛异常()
    {
        var ok = ArgumentValidators.ValidateAssembly(@"C:\a""b.dll", out var error);
        Assert.False(ok);
        Assert.Contains("程序集路径非法", error!);
    }

    [Fact]
    public void ValidateAssembly_路径是目录_返回目录而非文件提示()
    {
        var ok = ArgumentValidators.ValidateAssembly(Path.GetTempPath(), out var error);
        Assert.False(ok);
        Assert.Contains("是一个目录而非文件", error!);
    }

    [Fact]
    public void ValidateList_非法字符_返回错误提示()
    {
        var ok = ArgumentValidators.ValidateList("x", out var error);
        Assert.False(ok);
        Assert.Contains("无效的 list 参数", error!);
    }

    [Fact]
    public void ValidateList_合法字符中混入非法字符_返回错误提示()
    {
        var ok = ArgumentValidators.ValidateList("cx", out var error);
        Assert.False(ok);
        Assert.Contains("无效的 list 参数", error!);
    }

    [Fact]
    public void ValidateList_合法组合_校验通过()
    {
        var ok = ArgumentValidators.ValidateList("csi", out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateDecompileTarget_同时指定typeName与member_返回互斥提示()
    {
        var ok = ArgumentValidators.ValidateDecompileTarget("System.String", "M:System.String.Concat", out var error);
        Assert.False(ok);
        Assert.Contains("只能指定其一", error!);
    }

    [Fact]
    public void ValidateDecompileTarget_都为空_返回请指定目标提示()
    {
        var ok = ArgumentValidators.ValidateDecompileTarget("", "", out var error);
        Assert.False(ok);
        Assert.Contains("请指定 typeName 或 member 之一", error!);
    }

    [Fact]
    public void ValidateDecompileTarget_只传typeName_校验通过()
    {
        var ok = ArgumentValidators.ValidateDecompileTarget("System.String", "", out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateDecompileTarget_只传member_校验通过()
    {
        var ok = ArgumentValidators.ValidateDecompileTarget("", "M:System.String.Concat", out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateLanguageVersion_空或空白_视为未指定校验通过(string value)
    {
        var ok = ArgumentValidators.ValidateLanguageVersion(value, out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("CSharp8_0")]
    [InlineData("CSharp12_0")]
    [InlineData("CSharp15_0")]
    [InlineData("CSharp16_0")]
    [InlineData("Preview")]
    [InlineData("Latest")]
    public void ValidateLanguageVersion_合法值_校验通过(string value)
    {
        var ok = ArgumentValidators.ValidateLanguageVersion(value, out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("abc")]
    [InlineData("csharp12_0")]
    [InlineData("CSharpLatest")]
    public void ValidateLanguageVersion_明显非法值_返回错误提示(string value)
    {
        var ok = ArgumentValidators.ValidateLanguageVersion(value, out var error);
        Assert.False(ok);
        Assert.Contains("languageVersion 无效", error!);
    }

    [Fact]
    public void ValidateOutputDir_为空_返回错误提示()
    {
        var ok = ArgumentValidators.ValidateOutputDir("", out var error);
        Assert.False(ok);
        Assert.Contains("请指定 outputDir", error!);
    }

    [Fact]
    public void ValidateOutputDir_路径含非法字符_返回非法提示而非抛异常()
    {
        var ok = ArgumentValidators.ValidateOutputDir(@"C:\a|b", out var error);
        Assert.False(ok);
        Assert.Contains("输出目录路径非法", error!);
    }

    [Fact]
    public void ValidateOutputDir_已存在同名文件_返回错误提示()
    {
        var ok = ArgumentValidators.ValidateOutputDir(_tempFile, out var error);
        Assert.False(ok);
        Assert.Contains("已存在同名文件", error!);
    }

    [Fact]
    public void ValidateOutputDir_合法目录路径_校验通过()
    {
        var dir = Path.Combine(Path.GetTempPath(), $"ilspymcp-out-{Guid.NewGuid():N}");
        var ok = ArgumentValidators.ValidateOutputDir(dir, out var error);
        Assert.True(ok);
        Assert.Null(error);
    }
}