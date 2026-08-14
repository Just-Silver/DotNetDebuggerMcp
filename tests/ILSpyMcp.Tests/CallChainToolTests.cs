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
    public async Task CallChain_includeExternal_跨程序集调用展开()
    {
        // ExtCaller.Run 调 TestSamples.dll 的 Callee：includeExternal=true 时展开到被调方法体（含 System.Object::.ctor）
        AppServices.ConfigureForTest();
        try
        {
            var result = await CallChainTool.CallChain(TestDataPaths.TestSamplesExtDll, token: ExtCallerRunToken(), includeExternal: true);

            Assert.Contains("ILSpyMcp.TestSamples::ILSpyMcp.Samples.Callee::.ctor 调用:", result);
            Assert.Contains("System.Object::.ctor", result);
            Assert.DoesNotContain("未找到程序集", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task CallChain_includeExternal_外部程序集无法解析_标注终止()
    {
        // 把 Ext dll 复制到临时目录（同目录无 TestSamples.dll，CWD 也无）：resolver 定位失败 → 行尾标注终止提示
        AppServices.ConfigureForTest();
        var tempDir = Path.Combine(Path.GetTempPath(), "ilspymcp-callchain-ext-test");
        Directory.CreateDirectory(tempDir);
        var tempDll = Path.Combine(tempDir, "ILSpyMcp.TestSamplesExt.dll");
        try
        {
            File.Copy(TestDataPaths.TestSamplesExtDll, tempDll, overwrite: true);
            var result = await CallChainTool.CallChain(tempDll, token: ExtCallerRunToken(), includeExternal: true);

            Assert.Contains("（未找到程序集 ILSpyMcp.TestSamples，视为框架/外部调用未展开）", result);
        }
        finally
        {
            AppServices.ResetForTest();
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
        }
    }

    [Fact]
    public async Task CallChain_includeExternal_展开方法体解码中止_降级计数并入()
    {
        // 合成同名 ILSpyMcp.TestSamples.dll（Callee 方法体 IL 截断 → 解码中止）置于主 dll 同目录：ExtCaller.Run 对
        // Callee..ctor 与 Callee.Help 的跨程序集调用均展开中止（2 处）——头部降级提示须并入 expander 计数，
        // 证明展开完成后再合并 FormatContext.Degraded（展开前读取恒为 0）。
        AppServices.ConfigureForTest();
        var tempDir = Path.Combine(Path.GetTempPath(), "ilspymcp-callchain-abort-test");
        Directory.CreateDirectory(tempDir);
        try
        {
            TestAssemblyWriter.WriteCorruptTestSamples(tempDir);
            var tempMain = Path.Combine(tempDir, "ILSpyMcp.TestSamplesExt.dll");
            File.Copy(TestDataPaths.TestSamplesExtDll, tempMain, overwrite: true);
            var result = await CallChainTool.CallChain(tempMain, token: ExtCallerRunToken(), includeExternal: true);

            Assert.Contains("ILSpyMcp.TestSamples::ILSpyMcp.Samples.Callee::.ctor 调用:", result);
            Assert.Contains("本结果含 2 处降级解析", result);
        }
        finally
        {
            AppServices.ResetForTest();
            try { Directory.Delete(tempDir, recursive: true); } catch (IOException) { }
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
            Assert.DoesNotContain("反编译失败：反编译失败", result); // 底层已带「反编译失败：」前缀，不得重复包装
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

    /// <summary>
    /// 取跨程序集测试程序集 ExtCaller.Run 的元数据 token，供跨程序集展开用例。
    /// </summary>
    private static string ExtCallerRunToken()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesExtDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != "ExtCaller") continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) == "Run")
                    return $"0x{MetadataTokens.GetToken(methodHandle):x8}";
            }
        }
        throw new InvalidOperationException("TestSamplesExt 未找到 ExtCaller.Run");
    }
}
