using DotNetDebuggerMcp.Formatting;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using DotNetDebuggerMcp.Tools.Metadata;

namespace DotNetDebuggerMcp.Tests;

public class SignatureToolTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含 OutputFormatter 等公开类型；纯元数据读取，无需 TestData 路径
    private static readonly string MainAssembly = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public async Task Signature_BigClass_含方法签名且不含访问器()
    {
        // BigClass 只含方法（BigMethod/BigHelper/BigHelper2 + 隐式构造函数），无字段/属性/事件，故无 get_ 访问器
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("BigMethod(", result);
        Assert.Contains("static", result);
        Assert.Contains("BigHelper()", result);
        Assert.DoesNotContain("get_", result);
    }

    [Fact]
    public async Task Signature_OutputFormatter_含FormatHead且为static()
    {
        var result = await SignatureTool.Signature(MainAssembly, "DotNetDebuggerMcp.Formatting.OutputFormatter", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("FormatHead(", result);
        Assert.Contains("static", result);
    }

    [Fact]
    public async Task Signature_Members_每行行尾附方法属性token()
    {
        // Members 含方法（Raise/隐式 ctor，token 0x06）、属性（Count，token 0x17），行尾应附 token 供 agent 直接反编译
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Members", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("  0x06", result);
        Assert.Contains("  0x17", result);
    }

    [Fact]
    public async Task Signature_类型不存在_返回未找到提示()
    {
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public async Task Signature_类型不存在_附相近类型名()
    {
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "BigClas", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("未找到类型", result);
        Assert.Contains("相近类型", result);
        Assert.Contains("ILSpyMcp.Samples.BigClass", result);
    }

    [Fact]
    public async Task Signature_缺typeName_返回必填提示()
    {
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("请指定 typeName", result);
    }

    [Fact]
    public void RenderMemberSignature_OutputFormatter的FormatHead_含static与名字()
    {
        using var fs = File.OpenRead(MainAssembly);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var typeHandle = MetadataNaming.FindType(reader, "DotNetDebuggerMcp.Formatting.OutputFormatter");
        Assert.True(typeHandle.HasValue, "测试程序集中未找到类型 DotNetDebuggerMcp.Formatting.OutputFormatter");
        var type = reader.GetTypeDefinition(typeHandle!.Value);
        var method = type.GetMethods()
            .Select(reader.GetMethodDefinition)
            .Single(m => reader.GetString(m.Name) == "FormatHead");

        var line = SignatureRenderer.RenderMemberSignature(reader, type, method);

        Assert.Contains("static", line);
        Assert.Contains("FormatHead(", line);
    }

    [Fact]
    public async Task Signature_Dog_接口实现方法不渲染sealed()
    {
        // C# 编译器把隐式接口实现标为 sealed virtual newslot，但源码是普通方法，渲染时不得出现 sealed
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Dog", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("public void Speak();", result);
        Assert.DoesNotContain("sealed", result);
    }

    [Fact]
    public async Task Signature_Props_静态属性带static且索引器渲染this()
    {
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Props", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("public static string StaticProp { get; set; }", result);
        Assert.Contains("public int this[int] { get; set; }", result);
        Assert.Contains("public int this[string] { get; }", result);
    }

    [Fact]
    public async Task Signature_GenericBox_泛型构造函数名不含arity()
    {
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.GenericBox`1", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("public GenericBox<T>();", result);
        Assert.DoesNotContain("GenericBox`1<T>", result);
    }

    [Fact]
    public async Task Signature_ThingImpl_显式接口访问器不重复渲染()
    {
        // 显式接口属性访问器（Ns.IThing.get_Value）既不能被当方法行重复输出，也不能丢失属性行
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.ThingImpl", cancellationToken: TestContext.Current.CancellationToken);

        Assert.DoesNotContain("get_Value", result);
        Assert.DoesNotContain("get_", result);
        Assert.Contains("private int ILSpyMcp.Samples.IThing.Value { get; }", result);
    }

    [Fact]
    public async Task Signature_lines合法范围_返回切片行号()
    {
        // BigClass 含 BigMethod/BigHelper/BigHelper2 等多个成员签名，lines="1-2" 应返回带行号的前两行且不提示截断
        // 头部信息块前置，body 首行为 "1\t..."
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", lines: "1-2", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("\n1\t", result);
        Assert.DoesNotContain("已截断", result);
    }

    [Fact]
    public async Task Signature_lines非法格式_返回格式提示()
    {
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", lines: "abc", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("lines 参数格式应为", result);
    }
}