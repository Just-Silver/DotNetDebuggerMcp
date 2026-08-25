using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// IlScanHelper 共享 IL 解码用例：回调驱动解码 Caller.Run 方法体，验证 Call 族指令与原始 token 被正确提取。
/// </summary>
public class IlScanHelperTests
{
    [Fact]
    public void DecodeMethodBody_解码CallerRun的call指令_回调收到Call族()
    {
        // Run 含 newobj Callee..ctor 与 callvirt Callee.Help，回调应收到 Call/Callvirt/Newobj 指令且带原始 token
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var type = GetType(reader, "ILSpyMcp.Samples.Caller");
        var run = type.GetMethods().First(m => reader.GetString(reader.GetMethodDefinition(m).Name) == "Run");
        var body = pe.GetMethodBody(reader.GetMethodDefinition(run).RelativeVirtualAddress);
        Assert.NotNull(body);

        var calls = new List<IlScanHelper.IlInstruction>();
        IlScanHelper.DecodeMethodBody(body!.GetILReader(), instr =>
        {
            if (instr.Opcode is ILOpCode.Call or ILOpCode.Callvirt or ILOpCode.Newobj) calls.Add(instr);
        });

        Assert.NotEmpty(calls);
        Assert.All(calls, c => Assert.True(c.RawToken > 0));
    }

    private static TypeDefinition GetType(MetadataReader reader, string typeFullName)
    {
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        return reader.GetTypeDefinition(handle!.Value);
    }
}