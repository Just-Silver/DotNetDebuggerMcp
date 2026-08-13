using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;

using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// decompile_member 工具 token 参数路径：按元数据 token 直接反编译单个成员，以及非法 token 的中文提示。
/// 串行化使用 AppServices 静态状态（与 CheckToolTests/ToolPipelineTests 同一集合）。
/// </summary>
[Collection("AppServices")]
public class DecompileMemberToolTests
{
    [Fact]
    public async Task 提供token_按token反编译单个成员()
    {
        AppServices.ConfigureForTest();
        try
        {
            // 经 MemberResolver 拿 BigClass.BigMethod 的真实 token（与超限清单 token 同源）
            var matches = MemberResolver.FindMembers(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "BigMethod").Matches;
            Assert.NotEmpty(matches);
            var token = matches[0].Token;
            Assert.StartsWith("0x", token);

            // typeName/memberName 均缺省，仅靠 token 反编译
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "", token);

            Assert.Contains("按 token 反编译", result);
            Assert.Contains("BigMethod", result);
            Assert.DoesNotContain("超过上限", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 非法token_返回中文提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "", "0xZZZZ");

            Assert.Contains("不是有效的元数据 token", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 省略typeName_跨程序集搜索并反编译匹配成员()
    {
        AppServices.ConfigureForTest();
        try
        {
            // typeName 为空：跨程序集按成员名搜索，BigMethod 命中 BigClass.BigMethod
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "BigMethod");

            Assert.Contains("跨程序集", result);
            Assert.Contains("#MEMBER", result);
            Assert.Contains("ILSpyMcp.Samples.BigClass", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 缺token且缺memberName_返回校验提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "", "");

            Assert.Contains("请指定 memberName", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }
}
