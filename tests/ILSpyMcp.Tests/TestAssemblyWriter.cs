using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

namespace ILSpyMcp.Tests;

/// <summary>
/// 测试程序集构造辅助：用 MetadataBuilder 手工写最小程序集，供纯元数据工具测试构造真实场景。
/// </summary>
internal static class TestAssemblyWriter
{
    /// <summary>
    /// 构造名为 ILSpyMcp.TestSamples.dll 的最小程序集（与 TestSamplesExt.dll 的 AssemblyRef 同名、可被 resolver 定位）：
    /// 类型 ILSpyMcp.Samples.Callee 含 .ctor 与 Help 两方法，方法体为 call 指向越界 MemberRef 行——扫描解码时抛
    /// BadImageFormatException，触发 ExternalCallExpander.AbortedBodies 累计（供「降级计数并入」用例）。
    /// </summary>
    public static string WriteCorruptTestSamples(string dir)
    {
        var mb = new MetadataBuilder();
        mb.AddAssembly(mb.GetOrAddString("ILSpyMcp.TestSamples"), new Version(1, 0, 0, 0), default, default, (AssemblyFlags)0, AssemblyHashAlgorithm.Sha1);
        mb.AddModule(0, mb.GetOrAddString("ILSpyMcp.TestSamples.dll"), mb.GetOrAddGuid(Guid.NewGuid()), default, default);

        var corlibRef = mb.AddAssemblyReference(mb.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0), default, default, (AssemblyFlags)0, default);
        var objRef = mb.AddTypeReference(corlibRef, default, mb.GetOrAddString("Object"));

        var sig = new BlobBuilder();
        new BlobEncoder(sig).MethodSignature().Parameters(0, r => r.Void(), p => { });
        var sigHandle = mb.GetOrAddBlob(sig);

        var mbs = new MethodBodyStreamEncoder(new BlobBuilder());
        var ctorBody = AddAbortedBody(mbs);
        var helpBody = AddAbortedBody(mbs);

        var ctor = mb.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            mb.GetOrAddString(".ctor"), sigHandle, ctorBody, MetadataTokens.ParameterHandle(1));
        var help = mb.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.HideBySig,
            MethodImplAttributes.IL | MethodImplAttributes.Managed,
            mb.GetOrAddString("Help"), sigHandle, helpBody, MetadataTokens.ParameterHandle(1));

        mb.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class, mb.GetOrAddString("ILSpyMcp.Samples"),
            mb.GetOrAddString("Callee"), objRef, MetadataTokens.FieldDefinitionHandle(1), ctor);

        var root = new MetadataRootBuilder(mb);
        var peBuilder = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), root, mbs.Builder);
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        var path = Path.Combine(dir, "ILSpyMcp.TestSamples.dll");
        using (var fs = File.Create(path))
        {
            blob.WriteContentTo(fs);
        }
        return path;
    }

    /// <summary>
    /// 写入一个解码必中止的方法体：call 指向不存在的 MemberRef 行（token 越界）——扫描收集时 GetMemberReference 抛
    /// BadImageFormatException，被 IlScanHelper 捕获并累计降级计数。
    /// </summary>
    private static int AddAbortedBody(MethodBodyStreamEncoder mbs)
    {
        var il = new BlobBuilder();
        var encoder = new InstructionEncoder(il);
        encoder.Call(MetadataTokens.MemberReferenceHandle(0xFF));
        encoder.OpCode(ILOpCode.Ret);
        return mbs.AddMethodBody(encoder, maxStack: 8);
    }
}
