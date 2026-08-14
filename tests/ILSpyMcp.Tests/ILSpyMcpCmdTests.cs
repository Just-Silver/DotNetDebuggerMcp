using ILSpyMcp;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// CLI 入口测试：版本号与 -cg/-cc/-tk 分发。分发改用 AppServices 集合串行化（-cc 走 call_chain 进程内反编译，
/// 触碰 AppServices 静态状态）；命令行分发复用对应工具（已在工具层测试覆盖），端到端 CLI 行为由 dotnet run 手工验证。
/// </summary>
[Collection("AppServices")]
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
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: true, callChain: false, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            token: token, typeToken: "", lines: "", timeoutSeconds: 30, check: false);

        Assert.Contains("ILSpyMcp.Samples.Caller::", result);
    }

    [Fact]
    public async Task DispatchCliAsync_cc_输出调用序列与反编译()
    {
        // -cc -tk 分发走 call_chain 的 token 分支（进程内反编译，串行化使用 AppServices）
        var token = ChainTopRunToken();
        var result = await ILSpyMcpCmd.DispatchCliAsync(
            assembly: TestDataPaths.TestSamplesDll, typeName: "", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: false, callChain: true, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            token: token, typeToken: "", lines: "", timeoutSeconds: 30, check: false);

        Assert.Contains("方法体调用序列:", result);
        Assert.Contains("ChainMid::", result);
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

    /// <summary>
    /// 取测试程序集 ChainTop.Run 的元数据 token，供 -cc 分发用例。
    /// </summary>
    private static string ChainTopRunToken()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "ChainTop") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                if (reader.GetString(reader.GetMethodDefinition(methodHandle).Name) == "Run")
                    return $"0x{MetadataTokens.GetToken(methodHandle):x8}";
            }
        }
        throw new InvalidOperationException("TestSamples 未找到 ChainTop.Run");
    }
}
