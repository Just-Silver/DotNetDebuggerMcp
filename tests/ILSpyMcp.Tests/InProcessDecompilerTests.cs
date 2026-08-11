using ILSpyMcp.Decompiler;
using ILSpyMcp.Metadata;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// 进程内反编译服务用例：DecompileType 命中/未找到、DecompileMember token 非法/越界、DecompileWholeModule、
/// DecompileToDir 单文件布局与文件计数、DecompileToProject 写盘、RunWithTimeoutAsync 正常/超时/取消语义。
/// </summary>
public class InProcessDecompilerTests
{
    private static string NewTempDir()
    {
        return Path.Combine(Path.GetTempPath(), "ilspymcp-inproc-" + Guid.NewGuid().ToString("N"));
    }

    [Fact]
    public void DecompileType_命中BigClass_包含类声明与方法()
    {
        var result = InProcessDecompiler.DecompileType(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass");

        Assert.Contains("class BigClass", result);
        Assert.Contains("BigMethod", result);
    }

    [Fact]
    public void DecompileType_未找到类型_返回中文提示()
    {
        var result = InProcessDecompiler.DecompileType(TestDataPaths.TestSamplesDll, "No.Such.Type");

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public void DecompileType_嵌套类型_点与加号分隔均可命中()
    {
        // 定位改经 MetadataNaming.FindType（+ 归一化为 .），两种分隔写法都应命中同一个嵌套类型
        var assembly = typeof(InProcessDecompilerTests).Assembly.Location;
        var plus = InProcessDecompiler.DecompileType(assembly, "ILSpyMcp.Tests.InProcessDecompilerTests+TestNested");
        var dot = InProcessDecompiler.DecompileType(assembly, "ILSpyMcp.Tests.InProcessDecompilerTests.TestNested");

        Assert.Equal(plus, dot);
        Assert.Contains("class TestNested", plus);
    }

    [Fact]
    public void DecompileType_程序集路径无效_返回中文错误提示()
    {
        var result = InProcessDecompiler.DecompileType(Path.Combine(Path.GetTempPath(), "no-such-assembly.dll"), "X");

        Assert.Contains("反编译失败", result);
    }

    [Fact]
    public void DecompileType_非程序集文件_返回中文错误提示()
    {
        var fake = Path.GetTempFileName();
        try
        {
            File.WriteAllText(fake, "not an assembly");
            var result = InProcessDecompiler.DecompileType(fake, "X");

            Assert.Contains("反编译失败", result);
        }
        finally
        {
            File.Delete(fake);
        }
    }

    [Fact]
    public void DecompileMember_合法token_返回成员反编译文本()
    {
        // 经元数据层解析 BigClass.BigMethod 的真实 token（0x06000005 形式），验证 DecompileMember 命中
        var (typeFound, matches, _) = MemberResolver.FindMembers(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", "BigMethod");
        Assert.True(typeFound);
        var token = Assert.Single(matches, m => m.Name == "BigMethod").Token;

        var result = InProcessDecompiler.DecompileMember(TestDataPaths.TestSamplesDll, token);

        Assert.Contains("BigMethod", result);
        Assert.DoesNotContain("反编译失败", result);
    }

    [Fact]
    public void DecompileMember_token非十六进制_返回非法token提示()
    {
        var result = InProcessDecompiler.DecompileMember(TestDataPaths.TestSamplesDll, "abc");

        Assert.Contains("不是有效的元数据 token", result);
    }

    [Fact]
    public void DecompileMember_token越界_返回未引用提示()
    {
        // 0x06FFFFFF：MethodDef 表、row 数远超本程序集方法数，应判越界
        var result = InProcessDecompiler.DecompileMember(TestDataPaths.TestSamplesDll, "0x06FFFFFF");

        Assert.Contains("未引用本模块的类型或成员", result);
    }

    [Fact]
    public void DecompileWholeModule_返回包含多个类的文本()
    {
        var result = InProcessDecompiler.DecompileWholeModule(TestDataPaths.TestSamplesDll);

        Assert.Contains("class BigClass", result);
        Assert.Contains("class Callee", result);
    }

    [Fact]
    public void DecompileToDir_单类型_输出类型名decompiled_cs且单文件()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, "ILSpyMcp.Samples.BigClass");

            Assert.Contains("已写入", result);
            Assert.Contains("1 个文件", result);
            Assert.Contains("来源", result);
            var file = Path.Combine(dir, "ILSpyMcp.Samples.BigClass.decompiled.cs");
            Assert.True(File.Exists(file));
            Assert.Single(Directory.GetFiles(dir));
            Assert.Contains("class BigClass", File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DecompileToDir_全量_输出程序集名decompiled_cs()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, null);

            Assert.Contains("已写入", result);
            Assert.Contains("1 个文件", result);
            var file = Path.Combine(dir, "ILSpyMcp.TestSamples.decompiled.cs");
            Assert.True(File.Exists(file));
            Assert.Single(Directory.GetFiles(dir));
            Assert.Contains("class BigClass", File.ReadAllText(file));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DecompileToDir_类型未找到_不写盘返回中文提示()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, "No.Such.Type");

            Assert.Contains("未找到类型", result);
            Assert.Equal(0, Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DecompileToProject_写盘生成csproj与源码文件()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToProject(TestDataPaths.TestSamplesDll, dir, nestedDirectories: false);

            Assert.Contains("已写入", result);
            Assert.Contains("来源", result);
            Assert.True(File.Exists(Path.Combine(dir, "ILSpyMcp.TestSamples.csproj")));
            var files = Directory.GetFiles(dir, "*.cs", SearchOption.AllDirectories);
            Assert.NotEmpty(files);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DecompileToProject_已存在csproj时重跑_旧尾部被截断XML完整()
    {
        // 回归：File.OpenWrite 的 OpenOrCreate 语义不截断，向已含更长 csproj 的目录重跑会残留旧尾部字节生成损坏 XML。
        // 旧文件刻意做成远超新生成内容的长度并带尾部标记，重跑后断言标记不残留且 XML 完整可解析。
        var dir = NewTempDir();
        try
        {
            InProcessDecompiler.DecompileToProject(TestDataPaths.TestSamplesDll, dir, nestedDirectories: false);
            var csproj = Path.Combine(dir, "ILSpyMcp.TestSamples.csproj");
            File.WriteAllText(csproj, new string(' ', 200000) + "GARBAGE_TRAILING_MARKER");

            var result = InProcessDecompiler.DecompileToProject(TestDataPaths.TestSamplesDll, dir, nestedDirectories: false);

            Assert.Contains("已写入", result);
            var content = File.ReadAllText(csproj);
            Assert.DoesNotContain("GARBAGE_TRAILING_MARKER", content); // 旧尾部字节被截断
            var xml = new System.Xml.XmlDocument();
            xml.Load(csproj); // 若旧尾部字节残留则 XML 解析失败
            Assert.Equal("Project", xml.DocumentElement?.Name);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public async Task RunWithTimeoutAsync_正常完成_返回work结果()
    {
        var result = await InProcessDecompiler.RunWithTimeoutAsync(
            () => "反编译完成",
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            "反编译超时，请重试");

        Assert.Equal("反编译完成", result);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_超时_返回超时提示()
    {
        var result = await InProcessDecompiler.RunWithTimeoutAsync(
            () => { Thread.Sleep(3000); return "迟到结果"; },
            TimeSpan.FromMilliseconds(100),
            CancellationToken.None,
            "反编译超时，请重试");

        Assert.Equal("反编译超时，请重试", result);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_取消_返回超时提示()
    {
        using var cts = new CancellationTokenSource();
        var task = InProcessDecompiler.RunWithTimeoutAsync(
            () => { Thread.Sleep(3000); return "迟到结果"; },
            TimeSpan.FromSeconds(30),
            cts.Token,
            "反编译已取消");
        await Task.Delay(100);
        cts.Cancel();

        Assert.Equal("反编译已取消", await task);
    }

    /// <summary>
    /// IsErrorResult 必须识别全部错误提示形态（超限/未找到/非法 token/越界 token/反编译失败兜底），且不误判正常反编译文本。
    /// 超限分支受 <see cref="ILSpyMcp.Configuration.AppConfig.MaxOutputBytes"/>（64MB 字符）限制难以在管道层直接触发，此处以真实超限提示文本
    /// 覆盖 IsErrorResult 对超限形态的判定（管道层仅依赖本判定做「错误不入缓存」决策）。
    /// </summary>
    [Theory]
    [InlineData("反编译输出超过上限，建议改用 decompile_to_dir", true)]
    [InlineData("未找到类型 No.Such.Type", true)]
    [InlineData("\"abc\" 不是有效的元数据 token，应为 0x 开头的十六进制格式，如 0x06000005", true)]
    [InlineData("元数据 token 0x06FFFFFF 未引用本模块的类型或成员", true)]
    [InlineData("反编译失败：IO 错误（x）", true)]
    [InlineData("反编译失败：无访问权限（x）", true)]
    [InlineData("反编译失败：程序集格式无效（x）", true)]
    [InlineData("反编译失败：x", true)]
    [InlineData("using System;\npublic class A { }", false)]
    [InlineData("namespace Foo { }", false)]
    [InlineData("public class Empty { }", false)]
    [InlineData("", false)]
    public void IsErrorResult_识别全部错误提示形态_不误判反编译结果(string text, bool isError)
    {
        Assert.Equal(isError, InProcessDecompiler.IsErrorResult(text));
    }

    /// <summary>
    /// 供 DecompileType 嵌套定位用例使用的主程序集嵌套类型（与 InProcessDecompiler 同程序集，运行时存在）。
    /// </summary>
    private sealed class TestNested
    {
    }
}
