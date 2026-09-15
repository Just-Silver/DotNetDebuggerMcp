using DotNetDebugger.Decompiler.Decompiler;
using DotNetDebugger.Decompiler.Document;
using Xunit;

namespace DotNetDebugger.Decompiler.Tests;

/// <summary>
/// 反编译文件句柄释放回归：反编译入口（含解析引用程序集）不得把 OS 文件句柄持有到调用结束之后。
/// Windows 下残留句柄会阻止构建工具删除/覆盖 bin 目录中的 dll（MSB3021/MSB3027/MSB3061），
/// 用户侧表现为「MCP 把 dll 锁定占用」。依赖程序集经 UniversalAssemblyResolver 加载，
/// 解析器流选项必须是 PrefetchMetadata（DecompilerConfig.DependencyStreamOptions，构造时读完即关流）。
/// </summary>
public sealed class AssemblyFileHandleTests
{
    [Fact]
    public void 反编译类型完成后_主程序集与依赖程序集均无残留句柄()
    {
        RunWithCopiedSamples(temp =>
        {
            // ExtCaller.Run 方法体引用主样本 Callee 类型 → 反编译必然经 UniversalAssemblyResolver 加载依赖程序集
            var result = InProcessDecompiler.DecompileType(temp.Ext,
                TestDataPaths.SamplesExtNamespace + ".ExtCaller", TestContext.Current.CancellationToken);

            Assert.DoesNotContain("反编译失败", result);
            // 依赖元数据仍可解析（PrefetchMetadata 只放弃句柄，不牺牲依赖类型/成员签名解析）
            Assert.Contains("Callee", result);
            Assert.Contains("Help", result);

            AssertNoResidualHandle(temp.Ext, temp.Main);
        });
    }

    [Fact]
    public void 反编译写盘完成后_主程序集与依赖程序集均无残留句柄()
    {
        RunWithCopiedSamples(temp =>
        {
            var outputDir = Path.Combine(temp.Dir, "out");
            var result = InProcessDecompiler.DecompileToProject(temp.Ext, outputDir, nestedDirectories: false,
                TestContext.Current.CancellationToken);

            Assert.DoesNotContain("反编译失败", result);
            Assert.True(File.Exists(Path.Combine(outputDir, TestDataPaths.TestSamplesExtAssemblyName + ".csproj")));

            AssertNoResidualHandle(temp.Ext, temp.Main);
        });
    }

    [Fact]
    public void 反编译文档完成后_主程序集与依赖程序集均无残留句柄()
    {
        RunWithCopiedSamples(temp =>
        {
            // Web 文档服务（DocumentService）与反编译工具同源使用时也必须即时释放依赖句柄
            var doc = DocumentService.GetTypeDocument(temp.Ext, TestDataPaths.SamplesExtNamespace + ".ExtCaller");

            Assert.True(doc.IsSuccess, doc.Error);
            Assert.Contains("Callee", doc.Text);

            AssertNoResidualHandle(temp.Ext, temp.Main);
        });
    }

    /// <summary>把主样本 + Ext 样本复制到独立临时目录后执行用例（同目录保证解析器可解析依赖），结束后清理。</summary>
    private static void RunWithCopiedSamples(Action<(string Dir, string Main, string Ext)> act)
    {
        var temp = Path.Combine(Path.GetTempPath(), "dotnetdebugger-handle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            var main = Path.Combine(temp, TestDataPaths.TestSamplesAssemblyName + ".dll");
            var ext = Path.Combine(temp, TestDataPaths.TestSamplesExtAssemblyName + ".dll");
            File.Copy(TestDataPaths.TestSamplesDll, main);
            File.Copy(Path.Combine(Path.GetDirectoryName(TestDataPaths.TestSamplesDll)!,
                TestDataPaths.TestSamplesExtAssemblyName + ".dll"), ext);

            act((temp, main, ext));
        }
        finally
        {
            try { Directory.Delete(temp, recursive: true); } catch { /* 句柄未释放时清理失败不掩盖断言失败 */ }
        }
    }

    /// <summary>独占打开断言：任何未释放的句柄（含 FileShare.Read 共享读）都会让本次读写打开抛 IOException。</summary>
    private static void AssertNoResidualHandle(params string[] paths)
    {
        foreach (var path in paths)
        {
            try
            {
                using var fs = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            }
            catch (IOException ex)
            {
                Assert.Fail($"{Path.GetFileName(path)} 在反编译调用结束后仍被句柄占用：{ex.Message}");
            }
        }
    }
}
