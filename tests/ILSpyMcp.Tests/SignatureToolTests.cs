using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using ILSpyMcp.Tools;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

public class SignatureToolTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含 OutputFormatter 等公开类型；纯元数据读取，无需 TestData 路径
    private static readonly string MainAssembly = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public async Task Signature_BigClass_含方法签名且不含访问器()
    {
        // BigClass 只含方法（BigMethod/BigHelper/BigHelper2 + 隐式构造函数），无字段/属性/事件，故无 get_ 访问器
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass");

        Assert.Contains("BigMethod(", result);
        Assert.Contains("static", result);
        Assert.Contains("BigHelper()", result);
        Assert.DoesNotContain("get_", result);
    }

    [Fact]
    public async Task Signature_OutputFormatter_含FormatHead且为static()
    {
        var result = await SignatureTool.Signature(MainAssembly, "ILSpyMcp.Formatting.OutputFormatter");

        Assert.Contains("FormatHead(", result);
        Assert.Contains("static", result);
    }

    [Fact]
    public async Task Signature_类型不存在_返回未找到提示()
    {
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "No.Such.Type");

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public async Task Signature_缺typeName_返回必填提示()
    {
        var result = await SignatureTool.Signature(TestDataPaths.TestSamplesDll, "");

        Assert.Contains("请指定 typeName", result);
    }

    [Fact]
    public void RenderMemberSignature_OutputFormatter的FormatHead_含static与名字()
    {
        using var fs = File.OpenRead(MainAssembly);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var typeHandle = MetadataNaming.FindType(reader, "ILSpyMcp.Formatting.OutputFormatter");
        Assert.True(typeHandle.HasValue, "测试程序集中未找到类型 ILSpyMcp.Formatting.OutputFormatter");
        var type = reader.GetTypeDefinition(typeHandle!.Value);
        var method = type.GetMethods()
            .Select(reader.GetMethodDefinition)
            .Single(m => reader.GetString(m.Name) == "FormatHead");

        var line = SignatureRenderer.RenderMemberSignature(reader, type, method);

        Assert.Contains("static", line);
        Assert.Contains("FormatHead(", line);
    }
}
