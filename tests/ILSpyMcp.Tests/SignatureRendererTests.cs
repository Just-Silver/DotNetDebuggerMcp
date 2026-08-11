using ILSpyMcp.Formatting;
using ILSpyMcp.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace ILSpyMcp.Tests;

public class SignatureRendererTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含方法/构造函数/字段/属性，纯元数据读取
    private static readonly string AssemblyPath = typeof(OutputFormatter).Assembly.Location;

    private static List<string> Render(string typeFullName)
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        return SignatureRenderer.RenderTypeSignatures(reader, reader.GetTypeDefinition(handle!.Value)).ToList();
    }

    [Fact]
    public void 方法签名_含static与返回类型参数()
    {
        // OutputFormatter.FormatHead：public static string FormatHead(List<string> lines)
        var lines = Render("ILSpyMcp.Formatting.OutputFormatter");
        var line = Assert.Single(lines, l => l.Contains("FormatHead("));
        Assert.Contains("static", line);
        Assert.Contains("FormatHead(", line);
        Assert.Contains("List<string>", line);
        Assert.Contains("string ", line);
    }

    [Fact]
    public void 构造函数_渲染为类型名而非ctor()
    {
        // DecompileCache 的构造函数：public DecompileCache(long maxBytes = ...)
        var lines = Render("ILSpyMcp.Caching.DecompileCache");
        var line = Assert.Single(lines, l => l.Contains("DecompileCache("));
        Assert.Contains("DecompileCache(", line);
        Assert.DoesNotContain(".ctor", line);
        Assert.StartsWith("public ", line);
    }

    [Fact]
    public void 属性_合并get与set访问器()
    {
        // UpdateChecker+UpdateCheckCache：public string Latest { get; set; }
        var lines = Render("ILSpyMcp.UpdateCheck.UpdateChecker+UpdateCheckCache");
        var line = Assert.Single(lines, l => l.Contains(" Latest { get; set; }"));
        Assert.StartsWith("public ", line);
        Assert.Contains("Latest { get; set; }", line);
    }

    [Fact]
    public void 字段_以访问级别与类型开头()
    {
        // DecompileCache 的私有字段：private readonly long _maxBytes;
        var lines = Render("ILSpyMcp.Caching.DecompileCache");
        var line = Assert.Single(lines, l => l.Contains("_maxBytes;"));
        Assert.StartsWith("private readonly long _maxBytes;", line);
    }

    [Fact]
    public void 访问器方法不单独输出()
    {
        // UpdateCheckCache 含 { get; set; } 属性，get_/set_ 访问器方法应被合并进属性行、不单独出现
        var lines = Render("ILSpyMcp.UpdateCheck.UpdateChecker+UpdateCheckCache");
        var text = string.Join("\n", lines);
        Assert.DoesNotContain("get_", text);
        Assert.DoesNotContain("set_", text);
        // 自动属性 backing field（<Latest>k__BackingField）是编译器生成物，API 地图不展示
        Assert.DoesNotContain("k__BackingField", text);
    }

    [Fact]
    public void 每行一个签名_首行以访问级别开头()
    {
        var lines = Render("ILSpyMcp.Formatting.OutputFormatter");
        Assert.NotEmpty(lines);
        Assert.Matches(@"^(public|internal|protected|private)", lines[0]);
    }

    // TODO(TestData 扩展后补)：主程序集当前无泛型类型定义/泛型方法，泛型参数渲染（List`1 实例化、
    // GetGenericTypeParameter/GetGenericMethodParameter 取名字、方法名后 <T>）暂无可断言的真实成员；
    // 待 tests/TestData 加入泛型样本类型后再补泛型断言。
}
