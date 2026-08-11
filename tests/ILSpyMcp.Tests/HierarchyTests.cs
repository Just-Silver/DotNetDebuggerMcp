using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

public class HierarchyTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含接口实现 ProcessRunner : IProcessRunner；纯元数据读取
    private static readonly string AssemblyPath = typeof(OutputFormatter).Assembly.Location;

    /// <summary>
    /// 持有打开的 PEReader 与元数据读取器，保证 reader 在断言期间有效（PE 释放后 reader 访问会崩）。
    /// </summary>
    private sealed class MetadataScope : IDisposable
    {
        private readonly FileStream _fs;
        private readonly PEReader _pe;

        public MetadataScope(string path)
        {
            _fs = File.OpenRead(path);
            _pe = new PEReader(_fs);
            Reader = _pe.GetMetadataReader();
        }

        public MetadataReader Reader { get; }

        public void Dispose()
        {
            _pe.Dispose();
            _fs.Dispose();
        }
    }

    private static TypeDefinition Resolve(MetadataReader reader, string typeFullName)
    {
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        return reader.GetTypeDefinition(handle!.Value);
    }

    [Fact]
    public void GetInterfaces_ProcessRunner_含IProcessRunner()
    {
        // ProcessRunner : IProcessRunner，接口为同程序集 TypeDefinition
        using var scope = new MetadataScope(AssemblyPath);
        var type = Resolve(scope.Reader, "ILSpyMcp.Processes.ProcessRunner");
        Assert.Contains("ILSpyMcp.Processes.IProcessRunner", Hierarchy.GetInterfaces(scope.Reader, type));
    }

    [Fact]
    public void GetDescendants_IProcessRunner_含ProcessRunner()
    {
        // 反向：程序集内实现 IProcessRunner 接口的类型（直接接口相等）
        using var scope = new MetadataScope(AssemblyPath);
        var type = Resolve(scope.Reader, "ILSpyMcp.Processes.IProcessRunner");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        Assert.Contains("ILSpyMcp.Processes.ProcessRunner", Hierarchy.GetDescendants(scope.Reader, type, fullName));
    }

    [Fact]
    public void GetBaseChain_顶层类_首元素为自身并以SystemObject结尾()
    {
        // OutputFormatter 直接继承 System.Object，基类链 = [自身, System.Object]
        using var scope = new MetadataScope(AssemblyPath);
        var type = Resolve(scope.Reader, "ILSpyMcp.Formatting.OutputFormatter");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        var chain = Hierarchy.GetBaseChain(scope.Reader, type);
        Assert.Equal(fullName, chain[0]);
        Assert.Equal("System.Object", chain[^1]);
    }

    [Fact]
    public void GetBaseChain_接口_仅含自身()
    {
        // 接口的 BaseType 为 nil，基类链只含接口自身
        using var scope = new MetadataScope(AssemblyPath);
        var type = Resolve(scope.Reader, "ILSpyMcp.Processes.IProcessRunner");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        Assert.Equal(new[] { fullName }, Hierarchy.GetBaseChain(scope.Reader, type));
    }

    [Fact]
    public void GetBaseChain_多层链_无重复元素()
    {
        // DerivedClass -> BaseClass -> System.Object：三层链，中间基类在程序集内（TypeDef）不能重复出现
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.DerivedClass");
        var chain = Hierarchy.GetBaseChain(scope.Reader, type);

        Assert.Equal(
            new[] { "ILSpyMcp.Samples.DerivedClass", "ILSpyMcp.Samples.BaseClass", "System.Object" },
            chain);
        Assert.Equal(chain.Count, chain.Distinct().Count());
    }

    [Fact]
    public void GetBaseChain_Level4_四层链完整()
    {
        // Level4 -> Level3 -> Level2 -> Level1 -> System.Object
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.Level4");

        Assert.Equal(
            new[]
            {
                "ILSpyMcp.Samples.Level4",
                "ILSpyMcp.Samples.Level3",
                "ILSpyMcp.Samples.Level2",
                "ILSpyMcp.Samples.Level1",
                "System.Object",
            },
            Hierarchy.GetBaseChain(scope.Reader, type));
    }

    [Fact]
    public void GetInterfaces_IntComparer_泛型接口实例化被解析()
    {
        // IntComparer : IMyComparer<int>——接口是 TypeSpecification 泛型实例化，必须能被解析而非丢弃
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.IntComparer");

        Assert.Contains("ILSpyMcp.Samples.IMyComparer<int>", Hierarchy.GetInterfaces(scope.Reader, type));
    }

    [Fact]
    public void GetDescendants_AbstractShape_直接子类为Circle()
    {
        // 后代语义为「直接继承」：AbstractShape 的直接子类是 Circle；SealedCircle : Circle 不在其列
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.AbstractShape");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        var descendants = Hierarchy.GetDescendants(scope.Reader, type, fullName);

        Assert.Contains("ILSpyMcp.Samples.Circle", descendants);
        Assert.DoesNotContain("ILSpyMcp.Samples.SealedCircle", descendants);

        // Circle 的直接子类才是 SealedCircle
        var circle = Resolve(scope.Reader, "ILSpyMcp.Samples.Circle");
        var circleFullName = MetadataNaming.FullName(scope.Reader, circle);
        Assert.Contains("ILSpyMcp.Samples.SealedCircle", Hierarchy.GetDescendants(scope.Reader, circle, circleFullName));
    }
}
