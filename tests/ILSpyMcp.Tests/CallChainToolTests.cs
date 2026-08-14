using ILSpyMcp.Pipeline;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// call_chain 工具层用例：token 定位起始方法输出调用序列与成员反编译、typeName+memberName 定位、
/// includeExternal 外部调用行保留/过滤、多匹配签名清单提示、未找到/参数校验。
/// 串行化使用 AppServices 静态状态（与 CheckToolTests/ToolPipelineTests 同一集合）。
/// </summary>
[Collection("AppServices")]
public class CallChainToolTests
{
    [Fact]
    public async Task CallChain_token定位ChainTopRun_输出调用序列与反编译()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, token: ChainTopRunToken());

            Assert.Contains("方法体调用序列:", result);
            Assert.Contains("被调用成员反编译:", result);
            Assert.Contains("#MEMBER", result);
            Assert.Contains("ChainMid::", result);
            Assert.DoesNotContain("at System", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_typeName加memberName定位_输出调用序列与反编译()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.ChainTop", "Run");

            Assert.Contains("方法体调用序列:", result);
            Assert.Contains("ILSpyMcp.Samples.ChainMid::Mid()", result);
            Assert.Contains("ILSpyMcp.Samples.ChainMid::StaticMid()", result);
            Assert.Contains("#MEMBER", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_缺省includeExternal_外部调用行不输出()
    {
        // Mid 仅调 System.Console.WriteLine（外部）：缺省 includeExternal=false 时序列段无外部行
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, token: ChainMidMidToken());

            Assert.Contains("方法体调用序列:", result);
            Assert.DoesNotContain("System.Console::WriteLine", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_includeExternal_外部调用行带程序集归属()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, token: ChainMidMidToken(), includeExternal: true);

            Assert.Contains("System.Console::WriteLine", result);
            Assert.Contains("[System.Console]", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_多匹配_返回签名清单提示用token()
    {
        // BigClass 中 Big 命中 BigMethod/BigHelper/BigHelper2 3 个 → 返回 #MEMBER 清单而非反编译
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "Big");

            Assert.Contains("#MEMBER", result);
            Assert.Contains("3 个匹配", result);
            Assert.DoesNotContain("方法体调用序列:", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_未找到成员_返回相近成员提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "NoSuchMethod");

            Assert.Contains("未找到名称包含", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_类型不存在_返回未找到提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, "No.Such.Type", "Run");

            Assert.Contains("未找到类型", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_缺typeName_返回必填提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, "", "Run");

            Assert.Contains("请指定 typeName", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_token非方法_返回提示()
    {
        // 字段 token（0x04 表）非方法定义：应返回「不是方法」提示而非扫描
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, token: "0x04000001");

            Assert.Contains("不是方法的元数据 token", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_token非法_返回校验提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, token: "0xZZZZ");

            Assert.Contains("不是有效的元数据 token", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_成员反编译失败_返回提示文本不抛异常()
    {
        // 反编译探针抛异常模拟成员反编译失败：工具应返回中文提示文本而非把异常逸出为 Tool Error/崩溃
        AppServices.ConfigureForTest();
        AppServices.Pipeline = new ToolPipeline(AppServices.Cache,
            (_, _) => throw new InvalidOperationException("模拟反编译失败"));
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, token: ChainTopRunToken());

            Assert.Contains("反编译失败", result);
            Assert.Contains("模拟反编译失败", result);
            Assert.DoesNotContain("方法体调用序列:", result); // 任一成员失败即整体返回提示，丢弃已拼部分
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_成员反编译超时_返回可重试提示()
    {
        // 反编译探针经门闩阻塞模拟慢反编译：timeoutSeconds=1 必超时，返回可重试提示文本而非抛异常
        var gate = new ManualResetEventSlim(initialState: true);
        var probe = new Func<ToolCommand, CancellationToken, string>((_, _) =>
        {
            gate.Wait();
            return "public class Ok { }";
        });
        AppServices.ConfigureForTest();
        AppServices.Pipeline = new ToolPipeline(AppServices.Cache, probe);
        gate.Reset(); // 阻塞探针：成员反编译必超时
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesDll, token: ChainTopRunToken(), timeoutSeconds: 1);

            Assert.Contains("反编译超时", result);
            Assert.Contains("可调大 timeoutSeconds", result);
            Assert.DoesNotContain("方法体调用序列:", result);
        }
        finally
        {
            gate.Set(); // 放行后台探针，避免残留阻塞线程
            AppServices.ResetForTest();
        }
    }

    /// <summary>
    /// 取测试程序集 ChainTop.Run 的元数据 token，供 token 定位用例。
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

    /// <summary>
    /// 取测试程序集 ChainMid.Mid（仅调 System.Console.WriteLine 的外部方法）的元数据 token，供 includeExternal 用例。
    /// </summary>
    private static string ChainMidMidToken()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "ChainMid") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) == "Mid")
                    return $"0x{MetadataTokens.GetToken(methodHandle):x8}";
            }
        }
        throw new InvalidOperationException("TestSamples 未找到 ChainMid.Mid");
    }
}
