using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Decompiler.Configuration;
using DotNetDebugger.Decompiler.Decompiler;
using Xunit;

namespace DotNetDebugger.Decompiler.Tests;

/// <summary>
/// InProcessDecompiler.DecompileIl（按方法 token 反汇编方法体为 IL）用例：方法 token → IL 文本、async 外壳方法
/// 状态机线索、空方法体输出；非法 token / 非方法 token / 越界方法 token → 「反汇编失败：」中文提示；
/// IsErrorResult 必须识别新前缀且不误判正常 IL 文本。
/// </summary>
public sealed class DecompileIlTests
{
    private static string Dll => TestDataPaths.TestSamplesDll;

    [Fact]
    public void 方法token_返回IL文本()
    {
        // BigHelper：空方法体（最小 IL），断言输出含 ILSpy 反汇编骨架（RVA 注释/.maxstack/指令行号标签）
        var token = FindMethodToken("BigClass", "BigHelper");
        Assert.StartsWith("0x06", token);

        var result = InProcessDecompiler.DecompileIl(Dll, token, TestContext.Current.CancellationToken);

        Assert.Contains("// Method begins at RVA", result);
        Assert.Contains(".maxstack", result);
        Assert.Contains("IL_0000: ret", result);
        Assert.DoesNotContain("反汇编失败", result);
    }

    [Fact]
    public void async外壳方法_IL含状态机类型引用()
    {
        // WithAsync.Go：async 外壳方法体（编译器生成 <Go>d__0 状态机的创建/启动），
        // IL 文本应含状态机类型全名——这是 async 状态机 IL 兜底（B②）的典型场景
        var token = FindMethodToken("WithAsync", "Go");
        Assert.StartsWith("0x06", token);

        var result = InProcessDecompiler.DecompileIl(Dll, token, TestContext.Current.CancellationToken);

        Assert.Contains("WithAsync/'<Go>d__0'", result);
        Assert.Contains("AsyncTaskMethodBuilder", result);
        Assert.Contains("IL_", result);
    }

    [Fact]
    public void token非法_返回反汇编失败中文提示()
    {
        var result = InProcessDecompiler.DecompileIl(Dll, "abc", TestContext.Current.CancellationToken);

        Assert.StartsWith(DecompilerText.IlFailurePrefix, result);
        Assert.Contains("不是有效的元数据 token", result);
    }

    [Fact]
    public void token非方法定义_返回反汇编失败中文提示()
    {
        // Members.Name 字段 token（0x04 开头）：非方法 token，IL 反汇编应拒绝
        var fieldToken = FindMemberToken("Members", "Name", HandleKind.FieldDefinition);
        Assert.StartsWith("0x04", fieldToken);

        var result = InProcessDecompiler.DecompileIl(Dll, fieldToken, TestContext.Current.CancellationToken);

        Assert.StartsWith(DecompilerText.IlFailurePrefix, result);
        Assert.Contains("不是方法定义", result);
    }

    [Fact]
    public void token越界_返回反汇编失败中文提示()
    {
        // 0x06FFFFFF：MethodDef 表、row 数远超本程序集方法数
        var result = InProcessDecompiler.DecompileIl(Dll, "0x06FFFFFF", TestContext.Current.CancellationToken);

        Assert.StartsWith(DecompilerText.IlFailurePrefix, result);
        Assert.Contains("未引用本模块的方法", result);
    }

    [Theory]
    [InlineData("反汇编失败：\"abc\" 不是有效的元数据 token", true)]
    [InlineData("反汇编失败：元数据 token 0x04000003 不是方法定义", true)]
    [InlineData("反汇编失败：元数据 token 0x06FFFFFF 未引用本模块的方法", true)]
    [InlineData("反汇编失败：IO 错误（x）", true)]
    [InlineData("// Method begins at RVA 0x37c0\n.maxstack 4\nIL_0000: ret", false)]
    [InlineData("IL_0000: ldarg.0", false)]
    public void IsErrorResult_识别反汇编失败提示_不误判IL文本(string text, bool isError)
    {
        Assert.Equal(isError, InProcessDecompiler.IsErrorResult(text));
    }

    /// <summary>
    /// 取指定类型中指定名方法的方法定义 token（0x06 开头十六进制文本）。
    /// </summary>
    internal static string FindMethodToken(string typeName, string methodName)
    {
        using var fs = File.OpenRead(Dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != typeName) continue;
            foreach (var methodHandle in type.GetMethods())
            {
                var method = reader.GetMethodDefinition(methodHandle);
                if (reader.GetString(method.Name) == methodName)
                {
                    return $"0x{MetadataTokens.GetToken(methodHandle):x8}";
                }
            }
        }
        throw new InvalidOperationException($"TestSamples 未找到类型 {typeName} 的方法 {methodName}");
    }

    /// <summary>
    /// 取指定类型中指定名成员的 token（按 Kind 过滤，返回 0x 开头十六进制文本）。
    /// </summary>
    private static string FindMemberToken(string typeName, string memberName, HandleKind kind)
    {
        using var fs = File.OpenRead(Dll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        foreach (var typeHandle in reader.TypeDefinitions)
        {
            var type = reader.GetTypeDefinition(typeHandle);
            if (reader.GetString(type.Name) != typeName) continue;
            IEnumerable<EntityHandle> handles = kind switch
            {
                HandleKind.FieldDefinition => type.GetFields().Select(h => (EntityHandle)h),
                HandleKind.MethodDefinition => type.GetMethods().Select(h => (EntityHandle)h),
                HandleKind.PropertyDefinition => type.GetProperties().Select(h => (EntityHandle)h),
                HandleKind.EventDefinition => type.GetEvents().Select(h => (EntityHandle)h),
                _ => throw new ArgumentOutOfRangeException(nameof(kind)),
            };
            foreach (var handle in handles)
            {
                var name = kind switch
                {
                    HandleKind.FieldDefinition => reader.GetString(reader.GetFieldDefinition((FieldDefinitionHandle)handle).Name),
                    HandleKind.MethodDefinition => reader.GetString(reader.GetMethodDefinition((MethodDefinitionHandle)handle).Name),
                    HandleKind.PropertyDefinition => reader.GetString(reader.GetPropertyDefinition((PropertyDefinitionHandle)handle).Name),
                    HandleKind.EventDefinition => reader.GetString(reader.GetEventDefinition((EventDefinitionHandle)handle).Name),
                    _ => "",
                };
                if (name == memberName) return $"0x{MetadataTokens.GetToken(handle):x8}";
            }
        }
        throw new InvalidOperationException($"TestSamples 未找到类型 {typeName} 的成员 {memberName}");
    }
}
