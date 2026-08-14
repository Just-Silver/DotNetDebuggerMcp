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

    /// <summary>
    /// 自引用深链链长（M0..M6 共 7 个方法）：M0→M1→...→M6 的外部调用链深度超过
    /// <see cref="ILSpyMcp.Configuration.AppConfig.ExternalExpandMaxDepth"/>，供 ExternalCallExpander 深度上限用例。
    /// </summary>
    private const int ChainLength = 7;

    /// <summary>
    /// 构造名为 DeepChain.dll 的自引用深链程序集：类型 ILSpyMcp.Deep.Chain 含 M0..M6 七个方法，每个方法经 MemberRef
    /// （parent 为指向自身程序集 AssemblyRef 的 TypeRef，被 CallChainScanner 判为外部调用）调用下一个方法，构成
    /// M0→M1→...→M6 的外部调用链。主 dll 同目录可解析自身（AssemblyRef 与自身同名），供展开深度超限用例
    /// （预修复展开全部 7 层，修复后最深层 M6 不再展开）。
    /// </summary>
    public static string WriteDeepChain(string dir)
    {
        const string asmName = "DeepChain";
        const string typeNs = "ILSpyMcp.Deep";
        const string typeName = "Chain";
        var mb = new MetadataBuilder();
        mb.AddAssembly(mb.GetOrAddString(asmName), new Version(1, 0, 0, 0), default, default, (AssemblyFlags)0, AssemblyHashAlgorithm.Sha1);
        mb.AddModule(0, mb.GetOrAddString("DeepChain.dll"), mb.GetOrAddGuid(Guid.NewGuid()), default, default);

        var selfRef = mb.AddAssemblyReference(mb.GetOrAddString(asmName), new Version(1, 0, 0, 0), default, default, (AssemblyFlags)0, default);
        var selfTypeRef = mb.AddTypeReference(selfRef, mb.GetOrAddString(typeNs), mb.GetOrAddString(typeName));
        var corlibRef = mb.AddAssemblyReference(mb.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0), default, default, (AssemblyFlags)0, default);
        var objRef = mb.AddTypeReference(corlibRef, default, mb.GetOrAddString("Object"));

        var sig = new BlobBuilder();
        new BlobEncoder(sig).MethodSignature().Parameters(0, r => r.Void(), p => { });
        var sigHandle = mb.GetOrAddBlob(sig);

        var mrefs = new MemberReferenceHandle[ChainLength];
        for (var i = 0; i < ChainLength; i++)
            mrefs[i] = mb.AddMemberReference(selfTypeRef, mb.GetOrAddString($"M{i}"), sigHandle);

        var mbs = new MethodBodyStreamEncoder(new BlobBuilder());
        var bodies = new int[ChainLength];
        for (var i = 0; i < ChainLength; i++)
        {
            var il = new BlobBuilder();
            var encoder = new InstructionEncoder(il);
            if (i < ChainLength - 1) encoder.Call(mrefs[i + 1]);
            encoder.OpCode(ILOpCode.Ret);
            bodies[i] = mbs.AddMethodBody(encoder, maxStack: 8);
        }

        var methods = new MethodDefinitionHandle[ChainLength];
        for (var i = 0; i < ChainLength; i++)
        {
            methods[i] = mb.AddMethodDefinition(MethodAttributes.Public | MethodAttributes.HideBySig,
                MethodImplAttributes.IL | MethodImplAttributes.Managed,
                mb.GetOrAddString($"M{i}"), sigHandle, bodies[i], MetadataTokens.ParameterHandle(1));
        }

        mb.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class,
            mb.GetOrAddString(typeNs), mb.GetOrAddString(typeName), objRef, MetadataTokens.FieldDefinitionHandle(1), methods[0]);

        var root = new MetadataRootBuilder(mb);
        var peBuilder = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), root, mbs.Builder);
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        var path = Path.Combine(dir, "DeepChain.dll");
        using (var fs = File.Create(path))
        {
            blob.WriteContentTo(fs);
        }
        return path;
    }
}
