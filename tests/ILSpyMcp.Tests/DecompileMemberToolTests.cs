using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;

using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

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
    public async Task typeToken_精确定位类型内搜索成员()
    {
        AppServices.ConfigureForTest();
        try
        {
            using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var handles = MetadataNaming.FindTypes(reader, "ILSpyMcp.Samples.BigClass");
            var handle = Assert.Single(handles);
            var typeToken = $"0x{MetadataTokens.GetToken(handle):x8}";

            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "BigMethod", "", typeToken);

            Assert.Contains("已截断", result);
            Assert.DoesNotContain("有歧义", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task typeToken_非法返回中文提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "BigMethod", "", "0xZZZZ");

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
    public async Task 字段token_按token反编译字段()
    {
        AppServices.ConfigureForTest();
        try
        {
            // Members 类型中按名搜 Name 命中字段（0x04），取其 token 走 token 参数反编译
            var matches = MemberResolver.FindMembers(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Members", "Name").Matches;
            var field = Assert.Single(matches, m => m.Token.StartsWith("0x04"));

            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "", field.Token);

            Assert.Contains("按 token 反编译", result);
            Assert.Contains("Name", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 跨程序集搜索超限_签名清单含字段属性事件()
    {
        AppServices.ConfigureForTest();
        try
        {
            // "e" 跨程序集匹配约 39 个成员（>20）触发超限签名清单，且覆盖字段/属性/事件：
            // Members 类型的 Name 字段（0x04000003）、Changed 事件（0x14000001）与 Props 的 PrivateSet 属性（0x17000006）均在清单内
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "e");

            Assert.Contains("超过上限", result);
            Assert.Contains("0x04000003", result); // 字段 Name
            Assert.Contains("0x17000006", result); // 属性 PrivateSet
            Assert.Contains("0x14000001", result); // 事件 Changed
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
