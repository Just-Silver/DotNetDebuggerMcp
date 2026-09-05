using DotNetDebugger.Decompiler.Decompiler;
using DotNetDebugger.Decompiler.Metadata;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// 进程内反编译服务用例：DecompileType 命中/未找到、DecompileMember token 非法/越界、DecompileWholeModule、 DecompileToDir
/// 单文件布局、写盘文件名与文件计数、DecompileToProject 写盘、RunWithTimeoutAsync 正常/超时/取消语义。
/// </summary>
public class InProcessDecompilerTests
{
    [Fact]
    public void DecompileType_命中BigClass_包含类声明与方法()
    {
        var result = InProcessDecompiler.DecompileType(TestDataPaths.TestSamplesDll, "ILSpyMcp.Samples.BigClass", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("class BigClass", result);
        Assert.Contains("BigMethod", result);
    }

    [Fact]
    public void DecompileType_未找到类型_返回中文提示()
    {
        var result = InProcessDecompiler.DecompileType(TestDataPaths.TestSamplesDll, "No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("未找到类型", result);
    }

    [Fact]
    public void DecompileType_嵌套类型_点与加号分隔均可命中()
    {
        // 定位改经 MetadataNaming.FindType（+ 归一化为 .），两种分隔写法都应命中同一个嵌套类型
        var assembly = typeof(InProcessDecompilerTests).Assembly.Location;
        var plus = InProcessDecompiler.DecompileType(assembly, "DotNetDebuggerMcp.Tests.InProcessDecompilerTests+TestNested", cancellationToken: TestContext.Current.CancellationToken);
        var dot = InProcessDecompiler.DecompileType(assembly, "DotNetDebuggerMcp.Tests.InProcessDecompilerTests.TestNested", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Equal(plus, dot);
        Assert.Contains("class TestNested", plus);
    }

    [Fact]
    public void DecompileType_程序集路径无效_返回中文错误提示()
    {
        var result = InProcessDecompiler.DecompileType(Path.Combine(Path.GetTempPath(), "no-such-assembly.dll"), "X", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("反编译失败", result);
    }

    [Fact]
    public void DecompileType_非程序集文件_返回中文错误提示()
    {
        var fake = Path.GetTempFileName();
        try
        {
            File.WriteAllText(fake, "not an assembly");
            var result = InProcessDecompiler.DecompileType(fake, "X", cancellationToken: TestContext.Current.CancellationToken);

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

        var result = InProcessDecompiler.DecompileMember(TestDataPaths.TestSamplesDll, token, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("BigMethod", result);
        Assert.DoesNotContain("反编译失败", result);
    }

    [Fact]
    public void DecompileMember_token非十六进制_返回非法token提示()
    {
        var result = InProcessDecompiler.DecompileMember(TestDataPaths.TestSamplesDll, "abc", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("不是有效的元数据 token", result);
    }

    [Fact]
    public void DecompileMember_token越界_返回未引用提示()
    {
        // 0x06FFFFFF：MethodDef 表、row 数远超本程序集方法数，应判越界
        var result = InProcessDecompiler.DecompileMember(TestDataPaths.TestSamplesDll, "0x06FFFFFF", cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("未引用本模块的类型或成员", result);
    }

    [Fact]
    public void DecompileWholeModule_返回包含多个类的文本()
    {
        var result = InProcessDecompiler.DecompileWholeModule(TestDataPaths.TestSamplesDll, cancellationToken: TestContext.Current.CancellationToken);

        Assert.Contains("class BigClass", result);
        Assert.Contains("class Callee", result);
    }

    [Fact]
    public void DecompileToDir_单类型_输出类型名decompiled_cs且单文件()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, "ILSpyMcp.Samples.BigClass", cancellationToken: TestContext.Current.CancellationToken);

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
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, null, cancellationToken: TestContext.Current.CancellationToken);

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
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, "No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("未找到类型", result);
            Assert.Equal(0, Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DecompileToDir_逗号分隔多类型_每类型一个文件()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, "ILSpyMcp.Samples.BigClass,ILSpyMcp.Samples.Circle", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("已写入", result);
            Assert.Contains("2 个文件", result);
            Assert.Contains("来源", result);
            Assert.DoesNotContain("未找到", result);
            Assert.True(File.Exists(Path.Combine(dir, "ILSpyMcp.Samples.BigClass.decompiled.cs")));
            Assert.True(File.Exists(Path.Combine(dir, "ILSpyMcp.Samples.Circle.decompiled.cs")));
            Assert.Contains("class BigClass", File.ReadAllText(Path.Combine(dir, "ILSpyMcp.Samples.BigClass.decompiled.cs")));
            Assert.Contains("class Circle", File.ReadAllText(Path.Combine(dir, "ILSpyMcp.Samples.Circle.decompiled.cs")));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DecompileToDir_逗号分隔部分未找到_已找到仍写盘并附未找到提示()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, "ILSpyMcp.Samples.BigClass,No.Such.Type", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("已写入", result);
            Assert.Contains("1 个文件", result);
            Assert.Contains("未找到：No.Such.Type", result);
            Assert.True(File.Exists(Path.Combine(dir, "ILSpyMcp.Samples.BigClass.decompiled.cs")));
            Assert.Single(Directory.GetFiles(dir));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DecompileToDir_逗号分隔全部未找到_附未找到提示且文件数0()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, dir, "No.A,No.B", cancellationToken: TestContext.Current.CancellationToken);

            Assert.Contains("未找到：No.A、No.B", result);
            Assert.Contains("0 个文件", result);
            Assert.Equal(0, Directory.Exists(dir) ? Directory.GetFiles(dir).Length : 0);
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }
    }

    [Fact]
    public void DecompileToDir_批量写盘提示列出文件路径()
    {
        var outDir = Path.Combine(Path.GetTempPath(), "DotNetDebuggerMcp-tests-" + Guid.NewGuid().ToString("N"));
        try
        {
            var result = InProcessDecompiler.DecompileToDir(TestDataPaths.TestSamplesDll, outDir, "ILSpyMcp.Samples.BigClass,ILSpyMcp.Samples.Members", cancellationToken: TestContext.Current.CancellationToken);
            Assert.Contains("ILSpyMcp.Samples.BigClass.decompiled.cs", result);
            Assert.Contains("ILSpyMcp.Samples.Members.decompiled.cs", result);
        }
        finally { if (Directory.Exists(outDir)) Directory.Delete(outDir, recursive: true); }
    }

    [Fact]
    public void DecompileToProject_写盘生成csproj与源码文件()
    {
        var dir = NewTempDir();
        try
        {
            var result = InProcessDecompiler.DecompileToProject(TestDataPaths.TestSamplesDll, dir, nestedDirectories: false, cancellationToken: TestContext.Current.CancellationToken);

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
            InProcessDecompiler.DecompileToProject(TestDataPaths.TestSamplesDll, dir, nestedDirectories: false, cancellationToken: TestContext.Current.CancellationToken);
            var csproj = Path.Combine(dir, "ILSpyMcp.TestSamples.csproj");
            File.WriteAllText(csproj, new string(' ', 200000) + "GARBAGE_TRAILING_MARKER");

            var result = InProcessDecompiler.DecompileToProject(TestDataPaths.TestSamplesDll, dir, nestedDirectories: false, cancellationToken: TestContext.Current.CancellationToken);

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
            _ => "反编译完成",
            TimeSpan.FromSeconds(5),
            CancellationToken.None,
            "反编译超时，请重试");

        Assert.Equal("反编译完成", result);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_超时_返回超时提示()
    {
        var result = await InProcessDecompiler.RunWithTimeoutAsync(
            _ => { Thread.Sleep(3000); return "迟到结果"; },
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
            _ => { Thread.Sleep(3000); return "迟到结果"; },
            TimeSpan.FromSeconds(30),
            cts.Token,
            "反编译已取消");
        await Task.Delay(100, cancellationToken: TestContext.Current.CancellationToken);
        cts.Cancel();

        Assert.Equal("反编译已取消", await task);
    }

    [Fact]
    public async Task RunWithTimeoutAsync_取消令牌传入work_work收到取消信号返回取消结果()
    {
        // 验证令牌接线而非空跑：work 在收到取消信号前阻塞在 WaitOne，取消后置位 workSawSignal 并返回取消结果。 若令牌未真正传入 work（接线为空跑/误传
        // None），WaitOne 只能靠 30 秒兜底超时才返回， workSawSignal 在 5 秒等待窗口内不可能置位，断言失败；反之则证明 work 确实收到了取消信号。
        using var cts = new CancellationTokenSource();
        var workSawSignal = new ManualResetEventSlim();
        var task = InProcessDecompiler.RunWithTimeoutAsync(
            ct =>
            {
                ct.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                workSawSignal.Set();
                return "反编译已取消";
            },
            TimeSpan.FromSeconds(30),
            cts.Token,
            "反编译已取消");
        await Task.Delay(100, cancellationToken: TestContext.Current.CancellationToken);
        cts.Cancel();

        var result = await task;
        // 取消后 work 线程尚需短暂时间完成置位（Task.WhenAny 可能先选中 delay 直接返回 timeoutHint），轮询等待以观察到信号
        Assert.True(workSawSignal.Wait(TimeSpan.FromSeconds(5), TestContext.Current.CancellationToken), "work 未收到取消信号：取消令牌未真正传入 work（接线为空跑）");
        Assert.Equal("反编译已取消", result);
    }

    /// <summary>
    /// IsErrorResult 必须识别全部错误提示形态（超限/未找到/非法 token/越界 token/反编译失败兜底），且不误判正常反编译文本。 超限分支受 <see
    /// cref="DotNetDebugger.Decompiler.Configuration.DecompilerConfig.MaxOutputBytes"/>（64MB 字符）限制难以在管道层直接触发，此处以真实超限提示文本 覆盖
    /// IsErrorResult 对超限形态的判定（管道层仅依赖本判定做「错误不入缓存」决策）。
    /// </summary>
    [Theory]
    [InlineData("反编译输出超过上限，建议改用 decompile_to_dir", true)]
    [InlineData("反编译已取消", true)]
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

    private static string NewTempDir()
    {
        return Path.Combine(Path.GetTempPath(), "DotNetDebuggerMcp-inproc-" + Guid.NewGuid().ToString("N"));
    }

    /// <summary>
    /// 供 DecompileType 嵌套定位用例使用的主程序集嵌套类型（与 InProcessDecompiler 同程序集，运行时存在）。
    /// </summary>
    private sealed class TestNested
    {
    }
}
