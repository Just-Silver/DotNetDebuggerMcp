using DotNetDebuggerMcp;
using DotNetDebuggerMcp.Configuration;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// CLI 入口测试：版本号与 -cg/-cc/-tk 分发。分发改用 AppServices 集合串行化（-cc 走 call_chain 进程内反编译，
/// 触碰 AppServices 静态状态）；命令行分发复用对应工具（已在工具层测试覆盖），端到端 CLI 行为由 dotnet run 手工验证。
/// </summary>
[Collection("AppServices")]
public class DotNetDebuggerMcpCmdTests
{
    [Fact]
    public void Version_返回工具名加三位版本号()
    {
        var cmd = new DotNetDebuggerMcpCmd();
        Assert.StartsWith("ilspymcp ", cmd.Version);
        Assert.Matches(@"^ilspymcp \d+\.\d+\.\d+$", cmd.Version);
    }

    [Fact]
    public async Task DispatchCliAsync_cg加tk_输出方法级调用点()
    {
        // -cg -tk 分发走 call_graph 的 token 分支（纯元数据，不触碰 AppServices），应输出 Caller:: 调用点行
        var token = TestDataPaths.FirstCalleeMethodToken(TestDataPaths.TestSamplesDll);
        var result = await DotNetDebuggerMcpCmd.DispatchCliAsync(
            assembly: TestDataPaths.TestSamplesDll, typeName: "", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: true, callChain: false, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            interfaceUsage: false, genericInstantiations: false,
            token: token, typeToken: "", lines: "", timeoutSeconds: 30, check: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("ILSpyMcp.Samples.Caller::", result);
    }

    [Fact]
    public async Task DispatchCliAsync_cc_输出调用序列与反编译()
    {
        // -cc -tk 分发走 call_chain 的 token 分支（进程内反编译，串行化使用 AppServices）
        var token = ChainTopRunToken();
        var result = await DotNetDebuggerMcpCmd.DispatchCliAsync(
            assembly: TestDataPaths.TestSamplesDll, typeName: "", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: false, callChain: true, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            interfaceUsage: false, genericInstantiations: false,
            token: token, typeToken: "", lines: "", timeoutSeconds: 30, check: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("方法体调用序列:", result);
        Assert.Contains("ChainMid::", result);
    }

    [Fact]
    public async Task DispatchCliAsync_iu_输出接口实现者与调用点()
    {
        // -iu 分发走 interface_usage（纯元数据，经共享缓存），IAnimal 应含实现者 Dog 与调用点 AnimalCaller::Run → Speak
        var result = await DotNetDebuggerMcpCmd.DispatchCliAsync(
            assembly: TestDataPaths.TestSamplesDll, typeName: "ILSpyMcp.Samples.IAnimal", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: false, callChain: false, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            interfaceUsage: true, genericInstantiations: false,
            token: "", typeToken: "", lines: "", timeoutSeconds: 30, check: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("实现该接口的类型:", result);
        Assert.Contains("ILSpyMcp.Samples.Dog", result);
        Assert.Contains("ILSpyMcp.Samples.AnimalCaller::Run → Speak", result);
    }

    [Fact]
    public async Task DispatchCliAsync_gi_输出泛型实例化两段()
    {
        // -gi 分发走 generic_instantiations（纯元数据，经共享缓存），GenericBox 应含成员签名段与 GenericUser 命中
        var result = await DotNetDebuggerMcpCmd.DispatchCliAsync(
            assembly: TestDataPaths.TestSamplesDll, typeName: "ILSpyMcp.Samples.GenericBox", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: false, callChain: false, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            interfaceUsage: false, genericInstantiations: true,
            token: "", typeToken: "", lines: "", timeoutSeconds: 30, check: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("成员签名中的泛型实例化:", result);
        Assert.Contains("ILSpyMcp.Samples.GenericUser::", result);
        Assert.Contains("GenericBox<int>", result);
    }

    [Fact]
    public void BuildServerInstructions_含更新报告_简介后接更新报告()
    {
        const string report = "ilspymcp 已是最新版本";
        var text = DotNetDebuggerMcpCmd.BuildServerInstructions(report);

        Assert.StartsWith("## 服务器简介", text);
        Assert.Contains("## 工具一览", text);
        Assert.Contains("## 使用约定", text);
        Assert.Contains(AppText.HandshakeFeatureIntro, text);
        Assert.EndsWith(report, text);
    }

    [Fact]
    public void BuildServerInstructions_报告为空_仅功能简介()
    {
        Assert.Equal(AppText.HandshakeFeatureIntro, DotNetDebuggerMcpCmd.BuildServerInstructions(null));
        Assert.Equal(AppText.HandshakeFeatureIntro, DotNetDebuggerMcpCmd.BuildServerInstructions(""));
    }

    [Fact]
    public void BuildServerInstructions_工具一览_包含全部16个工具()
    {
        var text = AppText.HandshakeFeatureIntro;
        foreach (var tool in AllHandshakeTools)
        {
            Assert.Contains($"**`{tool}`**", text);
        }
    }

    /// <summary>
    /// 握手功能简介「工具一览」应覆盖的全部 MCP 工具（与 Tools 目录工具类一一对应；新增工具需同步 <see cref="AppText.HandshakeFeatureIntro"/>）。
    /// </summary>
    private static readonly string[] AllHandshakeTools =
    [
        "decompile", "decompile_member", "decompile_to_dir", "decompile_to_project",
        "list_types", "signature", "hierarchy", "dependencies", "call_graph", "assembly_info",
        "search_string", "field_access", "interface_usage", "generic_instantiations", "call_chain", "cache_stats",
    ];

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
