using ILSpyMcp;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// CLI 入口测试。仅覆盖不触碰 AppServices 单例的纯逻辑（版本号、-cg -tk 分发），避免并行测试相互污染；
/// 命令行分发复用 DecompileTool/ListTypesTool/DecompileToDirTool（已在工具层测试覆盖），
/// 端到端 CLI 行为由 dotnet run 手工验证。
/// </summary>
public class ILSpyMcpCmdTests
{
    [Fact]
    public void Version_返回工具名加三位版本号()
    {
        var cmd = new ILSpyMcpCmd();
        Assert.StartsWith("ilspymcp ", cmd.Version);
        Assert.Matches(@"^ilspymcp \d+\.\d+\.\d+$", cmd.Version);
    }

    [Fact]
    public async Task DispatchCliAsync_cg加tk_输出方法级调用点()
    {
        // -cg -tk 分发走 call_graph 的 token 分支（纯元数据，不触碰 AppServices），应输出 Caller:: 调用点行
        var token = TestDataPaths.FirstCalleeMethodToken(TestDataPaths.TestSamplesDll);
        var result = await ILSpyMcpCmd.DispatchCliAsync(
            assembly: TestDataPaths.TestSamplesDll, typeName: "", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: true, external: false, indirect: false, assemblyInfo: false,
            token: token, typeToken: "", lines: "", timeoutSeconds: 30, check: false);

        Assert.Contains("ILSpyMcp.Samples.Caller::", result);
    }

    [Fact]
    public void BuildServerInstructions_含更新报告_首行为CWD行后接报告()
    {
        const string report = "ilspymcp 已是最新版本";
        var text = ILSpyMcpCmd.BuildServerInstructions(report);

        var lines = text.Split([Environment.NewLine], StringSplitOptions.None);
        Assert.Equal($"当前工作目录: {Environment.CurrentDirectory}", lines[0]);
        Assert.Contains(report, text);
    }

    [Fact]
    public void BuildServerInstructions_报告为空_仅CWD行()
    {
        Assert.Equal($"当前工作目录: {Environment.CurrentDirectory}", ILSpyMcpCmd.BuildServerInstructions(null));
        Assert.Equal($"当前工作目录: {Environment.CurrentDirectory}", ILSpyMcpCmd.BuildServerInstructions(""));
    }
}
