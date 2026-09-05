using DotNetDebuggerMcp.Validation;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public class ArgumentValidatorsTests : IDisposable
{
    private readonly string _tempFile = Path.Combine(Path.GetTempPath(), $"DotNetDebuggerMcp-test-{Guid.NewGuid():N}.dll");

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
    public void ValidateMemberNameSearch_缺typeName_校验通过()
    {
        // typeName 允许为空：省略时跨程序集搜索
        var ok = ArgumentValidators.ValidateMemberNameSearch("", "SerializeAsync", out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Fact]
    public void ValidateMemberNameSearch_缺memberName_返回提示()
    {
        var ok = ArgumentValidators.ValidateMemberNameSearch("System.Text.Json.JsonSerializer", "", out var error);
        Assert.False(ok);
        Assert.Contains("memberName", error!);
        Assert.Contains("跨程序集", error!); // 提示说明省略 typeName 时跨程序集搜索
    }

    [Fact]
    public void ValidateMemberNameSearch_都提供_校验通过()
    {
        var ok = ArgumentValidators.ValidateMemberNameSearch("System.Text.Json.JsonSerializer", "SerializeAsync", out var error);
        Assert.True(ok);
        Assert.Null(error);
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
        var dir = Path.Combine(Path.GetTempPath(), $"DotNetDebuggerMcp-out-{Guid.NewGuid():N}");
        var ok = ArgumentValidators.ValidateOutputDir(dir, out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("0x06000005")]
    [InlineData("0X06000005")] // 前缀大小写不敏感
    public void ValidateToken_合法token_校验通过(string token)
    {
        var ok = ArgumentValidators.ValidateToken(token, out var error);
        Assert.True(ok);
        Assert.Null(error);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    public void ValidateToken_空或空白_返回必填提示(string token)
    {
        var ok = ArgumentValidators.ValidateToken(token, out var error);
        Assert.False(ok);
        Assert.Contains("请指定 token 参数", error!);
    }

    [Theory]
    [InlineData("06000005")] // 缺 0x 前缀
    [InlineData("0x")]       // 仅前缀无内容
    [InlineData("0xGGGG")]   // 非十六进制
    [InlineData("0x123456789")] // 超出 int 范围
    public void ValidateToken_格式非法_返回格式提示(string token)
    {
        var ok = ArgumentValidators.ValidateToken(token, out var error);
        Assert.False(ok);
        Assert.Contains("不是有效的元数据 token", error!);
    }
}
