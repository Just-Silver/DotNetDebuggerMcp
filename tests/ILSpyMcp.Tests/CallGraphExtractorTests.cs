using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// CallGraphExtractor 方法体调用图提取用例：内部方法调用 / 构造调用 / 泛型实例化 / 跨程序集排除 /
/// 编译器生成 target 过滤 / 访问器调用计入 / 字段访问不计入 / 反向调用者扫描。
/// 素材：生成测试程序集（tests/TestData）中的 Caller/Callee/GenericCaller/GenericHelper/PropReader/FieldUser 等类型。
/// </summary>
public class CallGraphExtractorTests
{
    /// <summary>
    /// 持有打开的 PEReader 与元数据读取器，保证 reader 在断言期间有效（PE 释放后 reader 访问会崩）。
    /// </summary>
    private sealed class MetadataScope : IDisposable
    {
        private readonly FileStream _fs;
        private readonly PEReader _pe;

        public MetadataScope()
        {
            _fs = File.OpenRead(TestDataPaths.TestSamplesDll);
            _pe = new PEReader(_fs);
            Reader = _pe.GetMetadataReader();
        }

        public PEReader Pe => _pe;

        public MetadataReader Reader { get; }

        public void Dispose()
        {
            _pe.Dispose();
            _fs.Dispose();
        }
    }

    private static TypeDefinition GetType(MetadataReader reader, string typeFullName)
    {
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        return reader.GetTypeDefinition(handle!.Value);
    }

    private static List<string> Extract(MetadataScope scope, string typeFullName)
        => CallGraphExtractor.ExtractMethodBodyCallTypes(scope.Pe, GetType(scope.Reader, typeFullName)).ToList();

    private static (List<string> Internal, List<string> External) ExtractWithExternal(MetadataScope scope, string typeFullName)
    {
        var (internalSet, external) = CallGraphExtractor.ExtractMethodBodyCallTypesWithExternal(
            scope.Pe, GetType(scope.Reader, typeFullName));
        return (internalSet.ToList(), external.ToList());
    }

    [Fact]
    public void Caller_方法体调用内部方法_收集Callee()
    {
        // Run 含 newobj Callee..ctor + callvirt Callee.Help，RunStatic 含 call Callee.StaticHelp
        using var scope = new MetadataScope();
        var result = Extract(scope, "ILSpyMcp.Samples.Caller");
        Assert.Contains("ILSpyMcp.Samples.Callee", result);
    }

    [Fact]
    public void Caller_跨程序集调用_不收集System类型()
    {
        // External 调 System.Console.WriteLine，属跨程序集 TypeRef/MemberRef，不应收集
        using var scope = new MetadataScope();
        var result = Extract(scope, "ILSpyMcp.Samples.Caller");
        Assert.DoesNotContain("System.Console", result);
        Assert.DoesNotContain("System.String", result);
    }

    [Fact]
    public void Caller_WithExternal_外部集合含SystemConsole_内部集合不变()
    {
        // External 调 System.Console.WriteLine：WithExternal 收集 System.Console [System.Console]；
        // 默认 ctor 调基类 Object..ctor 亦为 MemberRef 外部引用（System.Object）。内部集合与缺省 API 一致
        using var scope = new MetadataScope();
        var (internalSet, external) = ExtractWithExternal(scope, "ILSpyMcp.Samples.Caller");

        Assert.Equal(Extract(scope, "ILSpyMcp.Samples.Caller"), internalSet);
        Assert.Contains("System.Console [System.Console]", external);
        Assert.Contains("System.Object [System.Runtime]", external);
        Assert.DoesNotContain("System.Console", internalSet);
    }

    [Fact]
    public void WithClosure_WithExternal_仅内部闭包调用_外部集合含Func()
    {
        // Make 返回 () => x + 1：闭包类型（编译器生成）过滤后内部为空，构造 System.Func<> 走外部收集
        using var scope = new MetadataScope();
        var (internalSet, external) = ExtractWithExternal(scope, "ILSpyMcp.Samples.WithClosure");

        Assert.Empty(internalSet);
        Assert.Contains("System.Func`1 [System.Runtime]", external);
    }

    [Fact]
    public void UsesShared1_字段初始化构造调用_收集Shared()
    {
        // 字段 S = new Shared()：方法体含 newobj Shared..ctor，属方法调用边
        using var scope = new MetadataScope();
        var result = Extract(scope, "ILSpyMcp.Samples.UsesShared1");
        Assert.Contains("ILSpyMcp.Samples.Shared", result);
    }

    [Fact]
    public void GenericCaller_泛型方法实例化调用_收集GenericHelper()
    {
        // Echo(1) 编译为 MethodSpec(MethodDef GenericHelper.Echo, int32)，应归约到 GenericHelper
        using var scope = new MetadataScope();
        var result = Extract(scope, "ILSpyMcp.Samples.GenericCaller");
        Assert.Contains("ILSpyMcp.Samples.GenericHelper", result);
    }

    [Fact]
    public void PropReader_属性访问器调用_收集PropHolder()
    {
        // p.Value 编译为 callvirt PropHolder.get_Value，访问器调用计入调用边
        using var scope = new MetadataScope();
        var result = Extract(scope, "ILSpyMcp.Samples.PropReader");
        Assert.Contains("ILSpyMcp.Samples.PropHolder", result);
    }

    [Fact]
    public void FieldUser_仅字段访问_不收集FieldHolder()
    {
        // h.Data 编译为 ldfld FieldHolder::Data，字段访问不是方法调用边，不应收集
        using var scope = new MetadataScope();
        var result = Extract(scope, "ILSpyMcp.Samples.FieldUser");
        Assert.DoesNotContain("ILSpyMcp.Samples.FieldHolder", result);
    }

    [Fact]
    public void WithClosure_仅调用编译器生成类型_结果为空()
    {
        // Make 只调用闭包 <>c__DisplayClass0_0（编译器生成类型）与 System.Func（外部），均被过滤
        using var scope = new MetadataScope();
        var result = Extract(scope, "ILSpyMcp.Samples.WithClosure");
        Assert.Empty(result);
    }

    [Fact]
    public void Callee_反向扫描_含Caller()
    {
        using var scope = new MetadataScope();
        var type = GetType(scope.Reader, "ILSpyMcp.Samples.Callee");
        var callers = CallGraphExtractor.FindCallers(scope.Pe, type, "ILSpyMcp.Samples.Callee");
        Assert.Contains("ILSpyMcp.Samples.Caller", callers);
    }

    [Fact]
    public void GenericHelper_反向扫描_含GenericCaller()
    {
        using var scope = new MetadataScope();
        var type = GetType(scope.Reader, "ILSpyMcp.Samples.GenericHelper");
        var callers = CallGraphExtractor.FindCallers(scope.Pe, type, "ILSpyMcp.Samples.GenericHelper");
        Assert.Contains("ILSpyMcp.Samples.GenericCaller", callers);
    }

    [Fact]
    public void FindMethodCallers_反向定位调用该方法的方法体()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        // 取 Callee 首个方法（Help，被 Caller.Run 的 c.Help() 调用）token
        var callee = GetType(reader, "ILSpyMcp.Samples.Callee");
        var token = $"0x{MetadataTokens.GetToken(callee.GetMethods().First()):x8}";
        var callers = CallGraphExtractor.FindMethodCallers(pe, token);
        Assert.Contains(callers, c => c.StartsWith("ILSpyMcp.Samples.Caller::"));
    }

    [Fact]
    public void FindMethodCallers_泛型实例化调用_解包MethodSpec()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        // GenericCaller.Run 调 GenericHelper.Echo(1) 编译为 MethodSpec，应解包归约到 Echo
        var helper = GetType(reader, "ILSpyMcp.Samples.GenericHelper");
        var echo = helper.GetMethods().First(h => reader.GetString(reader.GetMethodDefinition(h).Name) == "Echo");
        var token = $"0x{MetadataTokens.GetToken(echo):x8}";
        var callers = CallGraphExtractor.FindMethodCallers(pe, token);
        Assert.Contains(callers, c => c.StartsWith("ILSpyMcp.Samples.GenericCaller::"));
    }

    [Fact]
    public void FindMethodCallers_非方法token_返回空()
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        // 0x02000000 是 TypeDef 表起始 token，非方法定义 → 返回空
        var callers = CallGraphExtractor.FindMethodCallers(pe, "0x02000000");
        Assert.Empty(callers);
    }
}
