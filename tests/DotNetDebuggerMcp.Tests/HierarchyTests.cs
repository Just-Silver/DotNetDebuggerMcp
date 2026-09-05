using DotNetDebuggerMcp.Formatting;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;
using DotNetDebuggerMcp.Tools.Metadata;

namespace DotNetDebuggerMcp.Tests;

[Collection("AppServices")]
public class HierarchyTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含 class/struct/enum 等；纯元数据读取
    private static readonly string AssemblyPath = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public void GetInterfaces_Dog_含IAnimal()
    {
        // Dog : IAnimal，接口为同程序集 TypeDefinition
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.Dog");
        Assert.Contains("ILSpyMcp.Samples.IAnimal", Hierarchy.GetInterfaces(scope.Reader, type));
    }

    [Fact]
    public void GetDescendants_IAnimal_含Dog()
    {
        // 反向：程序集内实现 IAnimal 接口的类型（直接接口相等）
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.IAnimal");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        Assert.Contains("ILSpyMcp.Samples.Dog", Hierarchy.GetDescendants(scope.Reader, type, fullName));
    }

    [Fact]
    public void GetBaseChain_顶层类_首元素为自身并以SystemObject结尾()
    {
        // OutputFormatter 直接继承 System.Object，基类链 = [自身, System.Object]
        using var scope = new MetadataScope(AssemblyPath);
        var type = Resolve(scope.Reader, "DotNetDebuggerMcp.Formatting.OutputFormatter");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        var chain = Hierarchy.GetBaseChain(scope.Reader, type);
        Assert.Equal(fullName, chain[0]);
        Assert.Equal("System.Object", chain[^1]);
    }

    [Fact]
    public void GetBaseChain_接口_仅含自身()
    {
        // 接口的 BaseType 为 nil，基类链只含接口自身
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.IAnimal");
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

    [Fact]
    public void GetDescendantsIncludingIndirect_Level1_含全部间接后代()
    {
        // Level1 → Level2 → Level3 → Level4：间接后代应收集 Level2/Level3/Level4 整条链
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.Level1");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        var descendants = Hierarchy.GetDescendantsIncludingIndirect(scope.Reader, type, fullName);

        Assert.Contains("ILSpyMcp.Samples.Level2", descendants);
        Assert.Contains("ILSpyMcp.Samples.Level3", descendants);
        Assert.Contains("ILSpyMcp.Samples.Level4", descendants);
    }

    [Fact]
    public void GetDescendantsIncludingIndirect_接口_含多层实现者()
    {
        // IWorker 被 WorkerBase 直接实现，WorkerDerived : WorkerBase 间接实现；间接实现者应含两者
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.IWorker");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        var descendants = Hierarchy.GetDescendantsIncludingIndirect(scope.Reader, type, fullName);

        Assert.Contains("ILSpyMcp.Samples.WorkerBase", descendants);
        Assert.Contains("ILSpyMcp.Samples.WorkerDerived", descendants);

        // 直接实现者仅 WorkerBase
        var direct = Hierarchy.GetDescendants(scope.Reader, type, fullName);
        Assert.Contains("ILSpyMcp.Samples.WorkerBase", direct);
        Assert.DoesNotContain("ILSpyMcp.Samples.WorkerDerived", direct);
    }

    [Fact]
    public void GetDescendantsIncludingIndirect_泛型基类_含间接后代()
    {
        // GenericRoot<T> 被 GenericMid : GenericRoot<int> 直接继承，GenericLeaf : GenericMid 间接继承；
        // 泛型实例化比较走底层定义全名（与 GetDescendants 一致），间接后代应含两者
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var type = Resolve(scope.Reader, "ILSpyMcp.Samples.GenericRoot`1");
        var fullName = MetadataNaming.FullName(scope.Reader, type);
        var descendants = Hierarchy.GetDescendantsIncludingIndirect(scope.Reader, type, fullName);

        Assert.Contains("ILSpyMcp.Samples.GenericMid", descendants);
        Assert.Contains("ILSpyMcp.Samples.GenericLeaf", descendants);

        // 直接后代仅 GenericMid
        var direct = Hierarchy.GetDescendants(scope.Reader, type, fullName);
        Assert.Contains("ILSpyMcp.Samples.GenericMid", direct);
        Assert.DoesNotContain("ILSpyMcp.Samples.GenericLeaf", direct);
    }

    [Fact]
    public async Task Hierarchy_Empty_无接口无后代_空段输出无占位()
    {
        // Empty 为 public class Empty { }（仅默认构造）：无接口实现、无程序集内后代/继承者，但基类链非空（[自身, System.Object]）。
        // 防回归：hierarchy 空段应与同族工具（dependencies/call_graph/interface_usage）一致输出「（无）」占位， 不得整段省略——否则
        // agent 无法区分「确实没有」与「输出不完整」。
        var result = await HierarchyTool.Hierarchy(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.Empty", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("基类链:", result);
        Assert.Contains("接口:", result);
        Assert.Contains("程序集内继承/实现此类型的类型:", result);
        Assert.Contains("（无）", result);
    }

    [Fact]
    public void GetDescendantsIncludingIndirect_无间接后代_与直接后代一致()
    {
        // AbstractShape → Circle → SealedCircle：AbstractShape 的间接后代含 SealedCircle（间接）， 与
        // GetDescendants（直接）不一致；而 Circle 无更深的链，间接=直接
        using var scope = new MetadataScope(TestDataPaths.TestSamplesDll);
        var shape = Resolve(scope.Reader, "ILSpyMcp.Samples.AbstractShape");
        var shapeFullName = MetadataNaming.FullName(scope.Reader, shape);
        Assert.Contains("ILSpyMcp.Samples.SealedCircle",
            Hierarchy.GetDescendantsIncludingIndirect(scope.Reader, shape, shapeFullName));

        var circle = Resolve(scope.Reader, "ILSpyMcp.Samples.Circle");
        var circleFullName = MetadataNaming.FullName(scope.Reader, circle);
        var indirectCircle = Hierarchy.GetDescendantsIncludingIndirect(scope.Reader, circle, circleFullName);
        var directCircle = Hierarchy.GetDescendants(scope.Reader, circle, circleFullName);
        Assert.Equal(directCircle.OrderBy(n => n), indirectCircle.OrderBy(n => n));
    }

    private static TypeDefinition Resolve(MetadataReader reader, string typeFullName)
    {
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        return reader.GetTypeDefinition(handle!.Value);
    }

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
}