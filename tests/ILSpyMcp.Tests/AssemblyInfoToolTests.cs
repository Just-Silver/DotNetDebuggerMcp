using ILSpyMcp.Formatting;
using ILSpyMcp.Tools;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// assembly_info 工具端到端单元用例：程序集概览各字段、引用清单、入口点（类库为无或无法解析）与参数校验。
/// 纯元数据读取，直接调工具方法（与 MCP 调用同逻辑）。
/// </summary>
public class AssemblyInfoToolTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含 TargetFrameworkAttribute 与引用清单；纯元数据读取，无需 TestData 路径
    private static readonly string MainAssembly = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public async Task AssemblyInfo_测试程序集_含程序集名版本目标框架类型计数引用与入口点()
    {
        var result = await AssemblyInfoTool.AssemblyInfo(TestDataPaths.TestSamplesDll);

        Assert.Contains("程序集: ILSpyMcp.TestSamples", result);
        Assert.Contains("版本: ", result);
        Assert.Contains("目标框架: ", result);
        Assert.Contains("类型总数: ", result);
        Assert.Contains("class:", result);
        Assert.Contains("interface:", result);
        Assert.Contains("引用的程序集:", result);
        Assert.Contains("入口点:", result);
        Assert.DoesNotContain("at System", result);
    }

    [Fact]
    public async Task AssemblyInfo_测试程序集_类型计数与类别自洽()
    {
        var result = await AssemblyInfoTool.AssemblyInfo(TestDataPaths.TestSamplesDll);

        // 601 个 class（Class0001-0600 + BigClass）→ 概览中 class 计数应 >= 600
        var match = System.Text.RegularExpressions.Regex.Match(result, @"class:\s*(\d+)");
        Assert.True(match.Success, $"未找到 class 计数，实际结果：{result}");
        Assert.True(int.Parse(match.Groups[1].Value) >= 600, $"class 计数应 >= 600，实际 {match.Groups[1].Value}");
        // 类型总数（实体 + 编译器生成）应 > class 计数（含 <Module> 等编译器生成类型）
        var totalMatch = System.Text.RegularExpressions.Regex.Match(result, @"类型总数:\s*(\d+)");
        Assert.True(totalMatch.Success, "未找到类型总数");
        Assert.True(int.Parse(totalMatch.Groups[1].Value) > int.Parse(match.Groups[1].Value), "类型总数应大于 class 计数（含编译器生成类型）");
    }

    [Fact]
    public async Task AssemblyInfo_主程序集_引用清单含SystemRuntime且目标框架可读()
    {
        var result = await AssemblyInfoTool.AssemblyInfo(MainAssembly);

        Assert.Contains("引用的程序集:", result);
        Assert.Contains("System.Runtime", result);
        Assert.Contains("目标框架: ", result);
        Assert.DoesNotContain("<未知>", result); // 主程序集带 TargetFrameworkAttribute，应可解析
        Assert.Contains("入口点:", result);
    }

    [Fact]
    public async Task AssemblyInfo_缺assembly_返回必填提示()
    {
        var result = await AssemblyInfoTool.AssemblyInfo("");

        Assert.Contains("请指定 assembly", result);
    }

    [Fact]
    public async Task AssemblyInfo_assembly不存在_返回文件不存在提示()
    {
        var result = await AssemblyInfoTool.AssemblyInfo(@"C:\no\such\file.dll");

        Assert.Contains("程序集文件不存在", result);
    }

    [Fact]
    public async Task AssemblyInfo_lines分页_返回带行号切片()
    {
        var result = await AssemblyInfoTool.AssemblyInfo(TestDataPaths.TestSamplesDll, lines: "1-3");

        // 头部信息块前置，body 首行为 "1\t程序集: ..."
        Assert.Contains("程序集信息", result); // 头部目标行
        Assert.Contains("1\t程序集: ILSpyMcp.TestSamples", result);
        Assert.DoesNotContain("已截断", result);
    }

    [Fact]
    public async Task AssemblyInfo_lines非法格式_返回格式提示()
    {
        var result = await AssemblyInfoTool.AssemblyInfo(TestDataPaths.TestSamplesDll, lines: "abc");

        Assert.Contains("lines 参数格式应为", result);
    }
}
