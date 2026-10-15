using DotNetDebuggerMcp.Configuration;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// 能力族开关解析（--features / DOTNETDEBUGGERMCP_FEATURES）纯逻辑。
/// 语义：McMaster 缺参 → null（未指定，回落默认/env）；--features= → ""（显式零能力）。
/// </summary>
public sealed class FeatureFlagsTests
{
    [Fact]
    public void Parse_未指定_默认仅反编译()
    {
        Assert.Equal(FeatureFamily.Decompile, FeatureFlags.Parse(null, null));
        Assert.Equal(FeatureFamily.Decompile, FeatureFlags.Parse(null, "   ")); // env 空白视为未设
    }

    [Fact]
    public void Parse_显式空串_报错_因零能力服务器不可用()
        => Assert.Throws<InvalidOperationException>(() => FeatureFlags.Parse("", null));

    [Fact]
    public void Parse_环境变量_生效()
        => Assert.Equal(FeatureFamily.Debug, FeatureFlags.Parse(null, "debug"));

    [Fact]
    public void Parse_CLI优先于环境变量()
    {
        Assert.Equal(FeatureFamily.Screenshot, FeatureFlags.Parse("screenshot", "debug"));
        // CLI 显式空压过 env（但零能力本身报错——见上一条）
        Assert.Throws<InvalidOperationException>(() => FeatureFlags.Parse("", "debug"));
    }

    [Theory]
    [InlineData("decompile,debug,ui,screenshot", 15)]  // All
    [InlineData(" DEBUG , Ui ", 6)]                    // Debug | Ui
    [InlineData("decompile,decompile", 1)]             // 重复去重
    public void Parse_组合(string value, int expected)
        => Assert.Equal((FeatureFamily)expected, FeatureFlags.Parse(value, null));

    [Theory]
    [InlineData("debugg")]
    [InlineData("decompile,nope")]
    public void TryParse_未知取值_返回中文错误并列出合法值(string value)
    {
        var (family, error) = FeatureFlags.TryParse(value);
        Assert.Equal(FeatureFamily.None, family);
        Assert.Contains("decompile", error);
        Assert.Contains("screenshot", error);
    }

    [Fact]
    public void Resolve_未知取值_带出错误供启动路径退出()
    {
        var (family, error) = FeatureFlags.Resolve("debbug", null);
        Assert.Equal(FeatureFamily.None, family);
        Assert.False(string.IsNullOrEmpty(error));
    }

    [Fact]
    public void Resolve_环境变量未知取值_同样带出错误()
    {
        var (_, error) = FeatureFlags.Resolve(null, "nope");
        Assert.False(string.IsNullOrEmpty(error));
    }
}
