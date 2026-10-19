using DotNetDebuggerMcp;
using DotNetDebuggerMcp.Configuration;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// CLI 入口测试：版本号与 -cg/-cc/-tk 分发。分发改用 AppServices 集合串行化（-cc 走 call_sequence 进程内反编译，
/// 触碰 AppServices 静态状态）；命令行分发复用对应工具（已在工具层测试覆盖），端到端 CLI 行为由 dotnet run 手工验证。
/// </summary>
[Collection("AppServices")]
public class DotNetDebuggerMcpCmdTests
{
    [Fact]
    public void Version_返回工具名加三位版本号()
    {
        var cmd = new DotNetDebuggerMcpCmd();
        Assert.StartsWith("DotNetDebuggerMcp ", cmd.Version);
        Assert.Matches(@"^DotNetDebuggerMcp \d+\.\d+\.\d+$", cmd.Version);
    }

    [Fact]
    public async Task DispatchCliAsync_cg加tk_输出方法级调用点()
    {
        // -cg -tk 分发走 call_edges 的 token 分支（纯元数据，不触碰 AppServices），应输出 Caller:: 调用点行
        var token = TestDataPaths.FirstCalleeMethodToken(TestDataPaths.TestSamplesDll);
        var result = await DotNetDebuggerMcpCmd.DispatchCliAsync(
            assembly: TestDataPaths.TestSamplesDll, typeName: "", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: true, callChain: false, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            interfaceUsage: false, genericInstantiations: false,
            token: token, typeToken: "", lines: "", timeoutSeconds: 30, check: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains($"{TestDataPaths.SamplesNamespace}.Caller::", result);
    }

    [Fact]
    public async Task DispatchCliAsync_cc_输出调用序列与反编译()
    {
        // -cc -tk 分发走 call_sequence 的 token 分支（进程内反编译，串行化使用 AppServices）
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
            assembly: TestDataPaths.TestSamplesDll, typeName: $"{TestDataPaths.SamplesNamespace}.IAnimal", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: false, callChain: false, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            interfaceUsage: true, genericInstantiations: false,
            token: "", typeToken: "", lines: "", timeoutSeconds: 30, check: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("实现该接口的类型:", result);
        Assert.Contains($"{TestDataPaths.SamplesNamespace}.Dog", result);
        Assert.Contains($"{TestDataPaths.SamplesNamespace}.AnimalCaller::Run → Speak", result);
    }

    [Fact]
    public async Task DispatchCliAsync_gi_输出泛型实例化两段()
    {
        // -gi 分发走 generic_instantiations（纯元数据，经共享缓存），GenericBox 应含成员签名段与 GenericUser 命中
        var result = await DotNetDebuggerMcpCmd.DispatchCliAsync(
            assembly: TestDataPaths.TestSamplesDll, typeName: $"{TestDataPaths.SamplesNamespace}.GenericBox", memberName: "", entityTypes: "", nameContains: "", namespaceContains: "",
            searchString: "", fieldName: "",
            outputDir: "", project: false, nestedDirectories: false, signatures: false, hierarchy: false,
            dependencies: false, callGraph: false, callChain: false, fieldAccess: false, external: false, indirect: false, assemblyInfo: false,
            interfaceUsage: false, genericInstantiations: true,
            token: "", typeToken: "", lines: "", timeoutSeconds: 30, check: false,
            cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("成员签名中的泛型实例化:", result);
        Assert.Contains($"{TestDataPaths.SamplesNamespace}.GenericUser::", result);
        Assert.Contains("GenericBox<int>", result);
    }

    [Fact]
    public void BuildServerInstructions_含更新报告_简介后接更新报告()
    {
        const string report = "DotNetDebuggerMcp 已是最新版本";
        var text = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.All, report);

        Assert.StartsWith("## 服务器简介", text);
        Assert.Contains("## 何时使用", text);
        Assert.Contains("## 使用约定", text);
        Assert.EndsWith(report, text);
    }

    [Fact]
    public void BuildServerInstructions_报告为空_仅功能简介()
    {
        var text = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.All, null);
        Assert.EndsWith("获取。", text); // 简介以使用约定末句收尾，无更新报告段
        Assert.Equal(text, DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.All, ""));
    }

    [Fact]
    public void 握手_启用族出现_未启用族不出现()
    {
        var all = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.All, null);
        Assert.Contains("动态调试", all);
        Assert.Contains("ui_find", all);
        Assert.Contains("screenshot", all);
        Assert.Contains("debug_verify", all);

        var minimal = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.Decompile, null);
        Assert.Contains("反编译", minimal);
        Assert.DoesNotContain("debug_", minimal);
        Assert.DoesNotContain("ui_find", minimal);
        Assert.DoesNotContain("ui_action", minimal);
        Assert.DoesNotContain("screenshot", minimal);
        Assert.DoesNotContain("web_open", minimal);
    }

    [Fact]
    public void 握手_未启用族的工具名不出现_各组合()
    {
        // spec §8.1/§8.6：disabled 族的工具名/触发词完全不出现（不只是单族 Decompile 情形）
        var uiOnly = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.Ui, null);
        Assert.DoesNotContain("debug_attach", uiOnly);
        Assert.DoesNotContain("debug_", uiOnly);
        Assert.DoesNotContain("截图", uiOnly);
        Assert.DoesNotContain("screenshot", uiOnly);

        var screenshotOnly = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.Screenshot, null);
        Assert.DoesNotContain("ui_find", screenshotOnly);
        Assert.DoesNotContain("ui_action", screenshotOnly);
        Assert.DoesNotContain("debug_", screenshotOnly);

        var debugOnly = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.Debug, null);
        Assert.DoesNotContain("ui_find", debugOnly);
        Assert.DoesNotContain("screenshot", debugOnly);

        // 启用时对应触发词出现
        var all = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.All, null);
        Assert.Contains("debug_attach", all);
        Assert.Contains("ui_find", all);
        Assert.Contains("截图", all);
    }

    [Fact]
    public void 握手_全关_不注入能力简介()
    {
        var none = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.None, null);
        Assert.DoesNotContain("## 何时使用", none);
    }

    [Fact]
    public void 握手_启用族触发条件齐备()
    {
        var text = DotNetDebuggerMcpCmd.BuildServerInstructions(FeatureFamily.All, null);
        // 触发条件导向：逐族断言「何时使用」，新增能力族漏进握手时本测试失败；仍不逐条列工具
        Assert.Contains("反编译", text);       // 静态分析族
        Assert.Contains("动态调试", text);     // 调试核心
        Assert.Contains("debug_run_to", text); // 调试扩展：运行到目标
        Assert.Contains("debug_set", text);    // 调试扩展：现场改值
        Assert.Contains("debug_evaluate", text);
        Assert.Contains("debug_verify", text); // 独立工作流：一键复验
        Assert.Contains("ui_find", text);      // UI 自动化族
        Assert.Contains("ui_action", text);
        Assert.Contains("screenshot", text);   // 视觉/监视
        // 视觉族细分触发条件（阶段一 screenshot 通用化：漏一族即失败，spec §9/R20）
        Assert.Contains("显示器", text);        // 多显示器寻址
        Assert.Contains("前台", text);          // foreground / windowTitle=@active
        Assert.Contains("客户区", text);        // clientArea
        Assert.Contains("句柄", text);          // hwnd
        Assert.Contains("元素", text);          // mode=element（UIA 元素级截图）
        Assert.Contains("includeCursor", text); // 光标
        Assert.Contains("原点", text);          // origin/scale 坐标元数据
        Assert.Contains("frameId", text);       // 代际护栏（ui_find ↔ ui_action/screenshot）
        Assert.Contains("screenshot(mode=list-displays)", text); // 视觉族发现：显示器清单（喂 display）
        Assert.Contains("screenshot(mode=list-windows)", text);  // 视觉族发现：窗口清单（喂 hwnd/windowTitle/processId）
        Assert.Contains("web_open", text);
        // P1–P6（2026-10-15）：冻结态可读栈 / clickPoint 物理输入声明 / ui_* 观测扰动
        Assert.Contains("冻结", text);          // P1：attach/launch 冻结态即可读栈
        Assert.Contains("clickPoint", text);   // P3：显式真实点击开关
        Assert.Contains("观测扰动", text);     // P6：ui_* 注入 UIA 客户端声明
        Assert.Contains("## 何时使用", text);
        Assert.DoesNotContain("## 工具一览", text);
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
