using DotNetDebuggerMcp.Services;
using DotNetDebuggerMcp.Tools.Decompile;
using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;

using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// decompile_il 工具端到端用例：按方法 token 反汇编方法体为 IL（经共享管道缓存/lines 分页），
/// 非方法 token/非法 token 的中文提示，二次调用命中缓存。串行化使用 AppServices 静态状态
/// （与 DecompileMemberToolTests/ToolPipelineTests 同一集合）。
/// </summary>
[Collection("AppServices")]
public class DecompileIlToolTests
{
    [Fact]
    public async Task 提供方法token_返回IL反汇编文本()
    {
        AppServices.ConfigureForTest();
        try
        {
            // TestSamples.BigClass.BigHelper：空方法体（最小 IL），token 经元数据动态读防漂移
            var token = FindMethodToken("BigClass", "BigHelper");

            var result = await DecompileIlTool.DecompileIl(TestDataPaths.TestSamplesDll, token, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("IL 反汇编", result);
            Assert.Contains("// Method begins at RVA", result);
            Assert.Contains("IL_0000: ret", result);
            Assert.Contains("1\t", result); // 带行号输出
            Assert.DoesNotContain("反汇编失败", result);
            Assert.DoesNotContain("缓存:", result); // 首调回源不标注缓存
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 二次调用_命中缓存()
    {
        AppServices.ConfigureForTest();
        try
        {
            var token = FindMethodToken("BigClass", "BigHelper");

            var first = await DecompileIlTool.DecompileIl(TestDataPaths.TestSamplesDll, token, cancellationToken: TestContext.Current.CancellationToken);
            Assert.DoesNotContain("缓存:", first);

            var second = await DecompileIlTool.DecompileIl(TestDataPaths.TestSamplesDll, token, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("缓存:   命中（重复查询成本低）", second);
            // 正文一致（仅头部缓存标注行不同）
            var body1 = FirstBody(first);
            var body2 = FirstBody(second);
            Assert.Equal(body1, body2);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 非方法token_返回中文提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            // Members.Name 字段 token（0x04 开头）：非方法 token 应被拒绝
            var fieldToken = FindMemberToken("Members", "Name", HandleKind.FieldDefinition);
            Assert.StartsWith("0x04", fieldToken);

            var result = await DecompileIlTool.DecompileIl(TestDataPaths.TestSamplesDll, fieldToken, cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("不是方法定义", result);
            Assert.Contains("反汇编失败", result);
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
            var result = await DecompileIlTool.DecompileIl(TestDataPaths.TestSamplesDll, "0xZZZZ", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("不是有效的元数据 token", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 缺token_返回必填提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await DecompileIlTool.DecompileIl(TestDataPaths.TestSamplesDll, "", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("请指定 token", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    [Fact]
    public async Task 缺assembly_返回必填提示()
    {
        AppServices.ConfigureForTest();
        try
        {
            var result = await DecompileIlTool.DecompileIl("", "0x060004b1", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("请指定 assembly", result);
        }
        finally
        {
            AppServices.ResetForTest();
        }
    }

    /// <summary>取格式化结果中头部信息块之后的正文（按 --- 分隔线切分），供比较不同头部标注时的正文一致性。</summary>
    private static string FirstBody(string text)
    {
        var sep = text.IndexOf("\n---\n", StringComparison.Ordinal);
        return sep < 0 ? text : text[(sep + 5)..];
    }

    /// <summary>取 TestSamples 中指定类型指定名方法的方法定义 token（0x06 开头十六进制文本）。</summary>
    private static string FindMethodToken(string typeName, string methodName)
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
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

    /// <summary>取 TestSamples 中指定类型指定名成员的 token（按 Kind 过滤，0x 开头十六进制文本）。</summary>
    private static string FindMemberToken(string typeName, string memberName, HandleKind kind)
    {
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
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
