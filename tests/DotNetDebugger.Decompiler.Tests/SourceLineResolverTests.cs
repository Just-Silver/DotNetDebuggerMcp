using DotNetDebugger.Decompiler.Document;
using Xunit;

namespace DotNetDebugger.Decompiler.Tests;

/// <summary>
/// P3-3b：SourceLineResolver（PDB 源码行 → 方法 token + IL offset）测试。
/// DebugTarget 由 generate-testdata.ps1 产出（exe+dll+pdb，源为脚本内嵌 DebugTarget.cs）。
/// 元数据/PDB 一律用 dll（exe 是 apphost 无托管元数据）；断言做语义级校验，不硬编码源码行号防脚本漂移。
/// </summary>
public sealed class SourceLineResolverTests
{
    private static string Dll => Path.ChangeExtension(TestDataPaths.DebugTargetExe, ".dll");

    [Fact]
    public void Resolve_末段文件名_返回有效落点()
    {
        // 动态找首个可解析行（注释/空行/usings 无映射是合法错误路径），不断言行号防脚本漂移
        SourceLineResolver.SourceLineTarget? target = null;
        string? error = null;
        for (var line = 1; line <= 80 && target is null; line++)
            target = SourceLineResolver.Resolve(Dll, "DebugTarget.cs", line, out error);

        Assert.True(target is not null, error);
        Assert.True(target!.MethodToken > 0);
        Assert.True(target.ActualLine >= 1);
    }

    [Fact]
    public void Resolve_任意行_落点行不小于请求行()
    {
        for (var line = 1; line <= 40; line += 7)
        {
            var target = SourceLineResolver.Resolve(Dll, "DebugTarget.cs", line, out var error);
            if (target is null) continue; // 该行无映射（如 using 区）合法
            Assert.True(target.ActualLine >= line, $"line={line} 落点 {target.ActualLine} 早于请求行");
        }
    }

    [Fact]
    public void Resolve_未找到文档_返回中文提示()
    {
        var target = SourceLineResolver.Resolve(Dll, "NoSuchSource.cs", 10, out var error);
        Assert.Null(target);
        Assert.Contains("未找到源文件", error);
    }

    [Fact]
    public void Resolve_无PDB模块_返回中文提示()
    {
        // TestSamples.dll 无旁置 PDB
        var target = SourceLineResolver.Resolve(TestDataPaths.TestSamplesDll, "Any.cs", 1, out var error);
        Assert.Null(target);
        Assert.Contains("无 PDB", error);
    }

    [Fact]
    public void Resolve_行号非法_返回提示()
    {
        var target = SourceLineResolver.Resolve(Dll, "DebugTarget.cs", 0, out var error);
        Assert.Null(target);
        Assert.Contains("行号", error);
    }
}
