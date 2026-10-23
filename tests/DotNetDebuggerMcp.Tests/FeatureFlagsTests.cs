using DotNetDebuggerMcp.Configuration;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// 能力族开关解析（--features / DOTNETDEBUGGERMCP_FEATURES）纯逻辑。
/// 语义：未指定 = 全部启用；反编译族恒启用、不可关闭；显式空串 = 仅反编译。
/// </summary>
public sealed class FeatureFlagsTests
{
    [Fact]
    public void Parse_未指定_默认全部启用()
    {
        Assert.Equal(FeatureFamily.All, FeatureFlags.Parse(null, null));
        Assert.Equal(FeatureFamily.All, FeatureFlags.Parse(null, "   ")); // env 空白视为未设
    }

    [Fact]
    public void Parse_显式空串_仅反编译()
        => Assert.Equal(FeatureFamily.Decompile, FeatureFlags.Parse("", null));

    [Fact]
    public void Parse_环境变量_生效_且反编译恒在()
        => Assert.Equal(FeatureFamily.Debug | FeatureFamily.Decompile, FeatureFlags.Parse(null, "debug"));

    [Fact]
    public void Parse_CLI优先于环境变量()
    {
        Assert.Equal(FeatureFamily.Screenshot | FeatureFamily.Decompile, FeatureFlags.Parse("screenshot", "debug"));
        // CLI 显式空压过 env：只剩恒启用的反编译
        Assert.Equal(FeatureFamily.Decompile, FeatureFlags.Parse("", "debug"));
    }

    [Theory]
    [InlineData("decompile,debug,ui,screenshot", 15)]  // All
    [InlineData(" DEBUG , Ui ", 7)]                    // Decompile | Debug | Ui
    [InlineData("decompile,decompile", 1)]             // 重复去重
    [InlineData("ui", 5)]                              // 反编译恒在：Ui | Decompile
    public void Parse_组合(string value, int expected)
        => Assert.Equal((FeatureFamily)expected, FeatureFlags.Parse(value, null));

    [Fact]
    public void Parse_任何取值都含反编译族()
    {
        foreach (var value in new[] { "", "debug", "ui", "screenshot", "debug,ui,screenshot" })
            Assert.True(FeatureFlags.Parse(value, null).HasFlag(FeatureFamily.Decompile), $"取值「{value}」缺少反编译族");
    }

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
