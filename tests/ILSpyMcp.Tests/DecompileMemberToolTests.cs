using ILSpyMcp.Metadata;
using ILSpyMcp.Services;
using ILSpyMcp.Tools;

using System.Reflection;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// decompile_member 工具 token 参数路径：按元数据 token 直接反编译单个成员，以及非法 token 的中文提示。 串行化使用 AppServices 静态状态（与
/// CheckToolTests/ToolPipelineTests 同一集合）。
/// </summary>
[Collection("AppServices")]
public class DecompileMemberToolTests
{
    [Fact]
    public async Task 提供token_按token反编译单个成员()
    {
        AppServices.ConfigureForTest();
        try
        {
            // 经 MemberResolver 拿 BigClass.BigMethod 的真实 token（与超限清单 token 同源）
            var matches = MemberResolver.FindMembers(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "BigMethod").Matches;
            Assert.NotEmpty(matches);
            var token = matches[0].Token;
            Assert.StartsWith("0x", token);

            // typeName/memberName 均缺省，仅靠 token 反编译
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "", token, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("按 token 反编译", result);
            Assert.Contains("BigMethod", result);
            Assert.DoesNotContain("超过上限", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 非法token_返回中文提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "", "0xZZZZ", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("不是有效的元数据 token", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task typeToken_精确定位类型内搜索成员()
    {
        AppServices.ConfigureForTest();
        try
        {
            using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
            using var pe = new PEReader(fs);
            var reader = pe.GetMetadataReader();
            var handles = MetadataNaming.FindTypes(reader, "ILSpyMcp.Samples.BigClass");
            var handle = Assert.Single(handles);
            var typeToken = $"0x{MetadataTokens.GetToken(handle):x8}";

            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "BigMethod", "", typeToken, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("已截断", result);
            Assert.DoesNotContain("有歧义", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task typeToken_非法返回中文提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "BigMethod", "", "0xZZZZ", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("不是有效的元数据 token", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task typeToken_非类型token_返回中文提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            // 方法 token（0x06 开头）当作 typeToken：Kind 非 TypeDefinition，应返回「不是类型定义」提示而非定位到类型
            var methodToken = MemberResolver.FindMembers(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "BigMethod").Matches[0].Token;

            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "BigMethod", "", methodToken, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("不是类型定义的元数据 token", result);
            Assert.DoesNotContain("BigClass 的成员", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task typeName_歧义_返回歧义提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            // 构造归一化同名类型对（嵌套 Holder+Item 与顶层 Holder.Item 归一化后均为 Probe.Ambiguity.Holder.Item）
            var dir = Path.Combine(Path.GetTempPath(), "ilspymcp-ambig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dll = WriteAmbiguousAssembly(dir);
            try
            {
                var result = await DecompileMemberTool.DecompileMember(dll, "Probe.Ambiguity.Holder.Item", "M", cancellationToken: TestContext.Current.CancellationToken);

                Assert.Contains("有歧义", result);
                Assert.Contains("Probe.Ambiguity.Holder+Item", result);
                Assert.Contains("Probe.Ambiguity.Holder.Item", result);
                Assert.Contains("typeToken", result);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task typeName_歧义_提供typeToken消歧后定位到该类型()
    {
        AppServices.ConfigureForTest();
        try
        {
            var dir = Path.Combine(Path.GetTempPath(), "ilspymcp-ambig-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(dir);
            var dll = WriteAmbiguousAssembly(dir);
            try
            {
                // 顶层 Holder.Item（归一化同名对中的第二个候选）的 token 作 typeToken → 在该类型内搜索成员； 断言消歧后定位到该类型（未找到提示用解析出的类型全名），且不再有歧义提示
                var typeToken = $"0x{TopItemToken(dll):x8}";

                var result = await DecompileMemberTool.DecompileMember(dll, "", "NoSuch", "", typeToken, cancellationToken: TestContext.Current.CancellationToken);

                Assert.Contains("类型 Probe.Ambiguity.Holder.Item 中未找到", result);
                Assert.DoesNotContain("有歧义", result);
            }
            finally
            {
                Directory.Delete(dir, recursive: true);
            }
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 省略typeName_跨程序集搜索并反编译匹配成员()
    {
        AppServices.ConfigureForTest();
        try
        {
            // typeName 为空：跨程序集按成员名搜索，BigMethod 命中 BigClass.BigMethod
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "BigMethod", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("跨程序集", result);
            Assert.Contains("#MEMBER", result);
            Assert.Contains("ILSpyMcp.Samples.BigClass", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 字段token_按token反编译字段()
    {
        AppServices.ConfigureForTest();
        try
        {
            // Members 类型中按名搜 Name 命中字段（0x04），取其 token 走 token 参数反编译
            var matches = MemberResolver.FindMembers(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Members", "Name").Matches;
            var field = Assert.Single(matches, m => m.Token.StartsWith("0x04"));

            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "", field.Token, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("按 token 反编译", result);
            Assert.Contains("Name", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 跨程序集搜索超限_签名清单含字段属性事件()
    {
        AppServices.ConfigureForTest();
        try
        {
            // "e" 跨程序集匹配约 39 个成员（>20）触发超限签名清单，且覆盖字段/属性/事件： Members 类型的 Name 字段（0x04000003）、Changed
            // 事件（0x14000001）与 Props 的 PrivateSet 属性（0x17000006）均在清单内
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "", "e", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("超过上限", result);
            Assert.Contains("0x04000003", result); // 字段 Name
            Assert.Contains("0x17000006", result); // 属性 PrivateSet
            Assert.Contains("0x14000001", result); // 事件 Changed
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 缺token且缺memberName_返回校验提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await DecompileMemberTool.DecompileMember(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "", "", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("请指定 memberName", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    /// <summary>
    /// 用 MetadataBuilder 构造一个含归一化同名类型对的最小程序集：命名空间 Probe.Ambiguity 的 Holder（嵌套 Item， 全名
    /// Probe.Ambiguity.Holder+Item）与命名空间 Probe.Ambiguity.Holder 的顶层 Item（全名
    /// Probe.Ambiguity.Holder.Item）， 两者 + 归一化为 . 后均为 Probe.Ambiguity.Holder.Item，构成真实歧义输入（C# 源码无法表达该碰撞）。
    /// </summary>
    private static string WriteAmbiguousAssembly(string dir)
    {
        var mb = new MetadataBuilder();
        mb.AddAssembly(mb.GetOrAddString("Probe.Ambiguous"), new Version(1, 0, 0, 0), default, default, (AssemblyFlags)0, AssemblyHashAlgorithm.Sha1);
        mb.AddModule(0, mb.GetOrAddString("Probe.Ambiguous.dll"), mb.GetOrAddGuid(Guid.NewGuid()), default, default);
        var asmRef = mb.AddAssemblyReference(mb.GetOrAddString("System.Runtime"), new Version(10, 0, 0, 0), default, default, (AssemblyFlags)0, default);
        var objRef = mb.AddTypeReference(asmRef, default, mb.GetOrAddString("Object"));

        var holder = mb.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class,
            mb.GetOrAddString("Probe.Ambiguity"), mb.GetOrAddString("Holder"), objRef,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        var nestedItem = mb.AddTypeDefinition(TypeAttributes.NestedPublic | TypeAttributes.Class,
            default, mb.GetOrAddString("Item"), objRef,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));
        mb.AddNestedType(nestedItem, holder);
        var topItem = mb.AddTypeDefinition(TypeAttributes.Public | TypeAttributes.Class,
            mb.GetOrAddString("Probe.Ambiguity.Holder"), mb.GetOrAddString("Item"), objRef,
            MetadataTokens.FieldDefinitionHandle(1), MetadataTokens.MethodDefinitionHandle(1));

        var root = new MetadataRootBuilder(mb);
        var peBuilder = new ManagedPEBuilder(PEHeaderBuilder.CreateLibraryHeader(), root, new BlobBuilder());
        var blob = new BlobBuilder();
        peBuilder.Serialize(blob);
        var path = Path.Combine(dir, "Probe.Ambiguous.dll");
        using (var fs = File.Create(path))
        {
            blob.WriteContentTo(fs);
        }
        return path;
    }

    /// <summary>
    /// 取构造程序集中顶层 Item（归一化同名对第二个候选）的类型定义 token。
    /// </summary>
    private static int TopItemToken(string dll)
    {
        using var fs = File.OpenRead(dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var handle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(handle);
            if (reader.GetString(type.Name) != "Item") continue;
            if (!type.IsNested) return MetadataTokens.GetToken(handle);
        }
        throw new InvalidOperationException("未找到顶层 Item 类型");
    }
}