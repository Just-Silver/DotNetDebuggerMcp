using DotNetDebuggerMcp.Formatting;
using DotNetDebugger.Decompiler.Metadata;
using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public class SignatureRendererTests
{
    // 主项目程序集（作为测试依赖复制到 bin），含方法/构造函数/字段/属性，纯元数据读取
    private static readonly string AssemblyPath = typeof(OutputFormatter).Assembly.Location;

    [Fact]
    public void 方法签名_含static与返回类型参数()
    {
        // OutputFormatter.FormatHead：public static string FormatHead(List<string> lines)
        var lines = Render("DotNetDebuggerMcp.Formatting.OutputFormatter");
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
        var lines = Render("DotNetDebuggerMcp.Caching.DecompileCache");
        var line = Assert.Single(lines, l => l.Contains("DecompileCache("));
        Assert.Contains("DecompileCache(", line);
        Assert.DoesNotContain(".ctor", line);
        Assert.StartsWith("public ", line);
    }

    [Fact]
    public void 属性_合并get与set访问器()
    {
        // UpdateChecker+UpdateCheckCache：public string Latest { get; set; }
        var lines = Render("DotNetDebuggerMcp.UpdateCheck.UpdateChecker+UpdateCheckCache");
        var line = Assert.Single(lines, l => l.Contains(" Latest { get; set; }"));
        Assert.StartsWith("public ", line);
        Assert.Contains("Latest { get; set; }", line);
    }

    [Fact]
    public void 字段_以访问级别与类型开头()
    {
        // DecompileCache 的私有字段：private readonly long _maxBytes;
        var lines = Render("DotNetDebuggerMcp.Caching.DecompileCache");
        var line = Assert.Single(lines, l => l.Contains("_maxBytes;"));
        Assert.StartsWith("private readonly long _maxBytes;", line);
    }

    [Fact]
    public void 访问器方法不单独输出()
    {
        // UpdateCheckCache 含 { get; set; } 属性，get_/set_ 访问器方法应被合并进属性行、不单独出现
        var lines = Render("DotNetDebuggerMcp.UpdateCheck.UpdateChecker+UpdateCheckCache");
        var text = string.Join("\n", lines);
        Assert.DoesNotContain("get_", text);
        Assert.DoesNotContain("set_", text);
        // 自动属性 backing field（<Latest>k__BackingField）是编译器生成物，API 地图不展示
        Assert.DoesNotContain("k__BackingField", text);
    }

    [Fact]
    public void 每行一个签名_首行以访问级别开头()
    {
        var lines = Render("DotNetDebuggerMcp.Formatting.OutputFormatter");
        Assert.NotEmpty(lines);
        Assert.Matches(@"^(public|internal|protected|private)", lines[0]);
    }

    [Fact]
    public void 每行行尾附成员token()
    {
        // API 地图每行行尾附 ` 0x...` 成员元数据 token，agent 可直接用于 decompile_member 的 token 参数
        var lines = Render("DotNetDebuggerMcp.Formatting.OutputFormatter");
        Assert.NotEmpty(lines);
        Assert.All(lines, line => Assert.Matches(@"0x[0-9a-f]{8}$", line));
    }

    [Fact]
    public void 方法token以06开头()
    {
        // DecompileCache 构造函数（.ctor 渲染为类型名）是方法，MethodDef token 高字节为 0x06
        var lines = Render("DotNetDebuggerMcp.Caching.DecompileCache");
        var line = Assert.Single(lines, l => l.Contains("DecompileCache("));
        Assert.Matches(@"0x06[0-9a-f]{6}$", line);
    }

    [Fact]
    public void 字段token以04开头()
    {
        // _maxBytes 是字段，Field token 高字节为 0x04
        var lines = Render("DotNetDebuggerMcp.Caching.DecompileCache");
        var line = Assert.Single(lines, l => l.Contains("_maxBytes;"));
        Assert.Matches(@"0x04[0-9a-f]{6}$", line);
    }

    [Fact]
    public void 属性token以17开头()
    {
        // Latest 是属性，Property token 高字节为 0x17
        var lines = Render("DotNetDebuggerMcp.UpdateCheck.UpdateChecker+UpdateCheckCache");
        var line = Assert.Single(lines, l => l.Contains(" Latest { get; set; }"));
        Assert.Matches(@"0x17[0-9a-f]{6}$", line);
    }

    [Fact]
    public void RenderSingleMember_字段属性事件按其token渲染()
    {
        // Members 类型：string Name（字段）、int Count { get; set; }（属性）、event Changed（事件）；
        // RenderSingleMember 按句柄 Kind 分发到对应私有渲染器，供 decompile_member 超限签名清单复用
        using var fs = File.OpenRead(TestDataPaths.TestSamplesDll);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var handle = MetadataNaming.FindType(reader, "ILSpyMcp.Samples.Members");
        Assert.True(handle.HasValue, "测试程序集中未找到类型 ILSpyMcp.Samples.Members");
        var type = reader.GetTypeDefinition(handle!.Value);

        var fieldHandle = type.GetFields().Single(h => reader.GetString(reader.GetFieldDefinition(h).Name) == "Name");
        Assert.Equal("public string Name;", SignatureRenderer.RenderSingleMember(reader, type, fieldHandle));

        var propHandle = type.GetProperties().Single(h => reader.GetString(reader.GetPropertyDefinition(h).Name) == "Count");
        Assert.Equal("public int Count { get; set; }", SignatureRenderer.RenderSingleMember(reader, type, propHandle));

        var eventHandle = type.GetEvents().Single(h => reader.GetString(reader.GetEventDefinition(h).Name) == "Changed");
        Assert.Equal("public event System.EventHandler Changed;", SignatureRenderer.RenderSingleMember(reader, type, eventHandle));
    }

    [Fact]
    public void RenderMemberSignature_不含行尾token()
    {
        // RenderMemberSignature 供 decompile_member 超限清单复用，token 已在 #MEMBER JSON 中，行内不得再拼
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var typeHandle = MetadataNaming.FindType(reader, "DotNetDebuggerMcp.Formatting.OutputFormatter");
        Assert.True(typeHandle.HasValue, $"测试程序集中未找到类型 DotNetDebuggerMcp.Formatting.OutputFormatter");
        var type = reader.GetTypeDefinition(typeHandle!.Value);
        var method = type.GetMethods()
            .Select(reader.GetMethodDefinition)
            .Single(m => reader.GetString(m.Name) == "FormatHead");

        var line = SignatureRenderer.RenderMemberSignature(reader, type, method);

        Assert.DoesNotMatch(@"0x[0-9a-f]{8}$", line);
        Assert.Contains("FormatHead(", line);
    }

    private static List<string> Render(string typeFullName)
    {
        using var fs = File.OpenRead(AssemblyPath);
        using var pe = new PEReader(fs);
        var reader = pe.GetMetadataReader();
        var handle = MetadataNaming.FindType(reader, typeFullName);
        Assert.True(handle.HasValue, $"测试程序集中未找到类型 {typeFullName}");
        return SignatureRenderer.RenderTypeSignatures(reader, reader.GetTypeDefinition(handle!.Value)).ToList();
    }

    // TODO(TestData 扩展后补)：主程序集当前无泛型类型定义/泛型方法，泛型参数渲染（List`1 实例化、
    // GetGenericTypeParameter/GetGenericMethodParameter 取名字、方法名后 <T>）暂无可断言的真实成员； 待 tests/TestData 加入泛型样本类型后再补泛型断言。
}