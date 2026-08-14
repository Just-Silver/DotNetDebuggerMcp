using ILSpyMcp.Tools;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// call_graph 工具层用例：段标题输出 / 无内部调用（无）占位 / 反向段 / 类型不存在 / 参数校验 / lines 分页 / token 调用点。
/// </summary>
public class CallGraphToolTests
{
    [Fact]
    public async Task CallGraph_Caller_含正向与反向段标题()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller");

        Assert.Contains("方法体调用的内部类型:", result);
        Assert.Contains("程序集内方法体调用此类型的类型:", result);
        Assert.Contains("ILSpyMcp.Samples.Callee", result);
        Assert.DoesNotContain("降级解析", result); // 正常类型方法体 IL 完整解码，头部不含降级行
    }

    [Fact]
    public async Task CallGraph_Uses_方法体无内部调用_输出无占位()
    {
        // Uses.Run 方法体为空、仅默认 ctor 调 Object..ctor（外部），正向段应为（无）占位而非报错
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Uses");

        Assert.Contains("（无）", result);
        Assert.DoesNotContain("at System", result);
    }

    [Fact]
    public async Task CallGraph_WithClosure_编译器生成调用被过滤_输出无占位()
    {
        // Make 只调闭包类型（编译器生成），过滤后正向段为（无）
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.WithClosure");

        Assert.Contains("（无）", result);
    }

    [Fact]
    public async Task CallGraph_类型不存在_返回未找到提示()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "No.Such.Type");

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public async Task CallGraph_类型不存在_附相近类型名()
    {
        // BigClas 短名编辑距离 1 → 提示应附相近类型 BigClass（全名）
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "BigClas");

        Assert.Contains("未找到类型", result);
        Assert.Contains("相近类型", result);
        Assert.Contains("ILSpyMcp.Samples.BigClass", result);
    }

    [Fact]
    public async Task CallGraph_缺typeName_返回必填提示()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "");

        Assert.Contains("请指定 typeName", result);
    }

    [Fact]
    public async Task CallGraph_lines非法格式_返回格式提示()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller", lines: "abc");

        Assert.Contains("lines 参数格式应为", result);
    }

    [Fact]
    public async Task CallGraph_includeExternal_输出外部段()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller", includeExternal: true);

        Assert.Contains("方法体调用的内部类型:", result);
        Assert.Contains("方法体调用的外部类型:", result);
        Assert.Contains("System.Console [System.Console]", result);
        Assert.Contains("ILSpyMcp.Samples.Callee", result);
    }

    [Fact]
    public async Task CallGraph_缺省includeExternal_无外部段()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Caller");

        Assert.DoesNotContain("方法体调用的外部类型:", result);
    }

    [Fact]
    public async Task CallGraph_token_输出方法体调用点()
    {
        var token = TestDataPaths.FirstCalleeMethodToken(TestDataPaths.TestSamplesDll);
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, token: token);

        Assert.Contains("方法体调用此方法的成员:", result);
        Assert.Contains("ILSpyMcp.Samples.Caller::", result);
        Assert.DoesNotContain("方法体调用的内部类型:", result);
    }

    [Fact]
    public async Task CallGraph_token_typeName非空_头部含类型与方法token()
    {
        var token = TestDataPaths.FirstCalleeMethodToken(TestDataPaths.TestSamplesDll);
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Callee", token);

        Assert.Contains($"类型 ILSpyMcp.Samples.Callee 的方法 {token}（调用点）", result);
        Assert.Contains("ILSpyMcp.Samples.Caller::", result);
    }

    [Fact]
    public async Task CallGraph_token_缺省typeName_头部为方法token()
    {
        var token = TestDataPaths.FirstCalleeMethodToken(TestDataPaths.TestSamplesDll);
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, token: token);

        Assert.Contains($"方法 {token}（调用点）", result);
    }

    [Fact]
    public async Task CallGraph_token非法_返回提示()
    {
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, token: "0xZZZZ");

        Assert.Contains("不是有效的元数据 token", result);
    }

    [Fact]
    public async Task CallGraph_token为字段token_输出无占位()
    {
        // 字段 token（0x04 表）非方法定义：FindMethodCallers 返回空，应输出（无）占位而非报错
        var result = await CallGraphTool.CallGraph(TestDataPaths.TestSamplesDll, token: "0x04000001");

        Assert.Contains("方法体调用此方法的成员:", result);
        Assert.Contains("（无）", result);
    }
}
