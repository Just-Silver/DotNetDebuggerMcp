using System.Reflection.Metadata;
using System.Reflection.Metadata.Ecma335;
using System.Reflection.PortableExecutable;
using DotNetDebugger.Engine.Session;
using DotNetDebugger.Session;
using Xunit;

namespace DotNetDebugger.Session.Tests;

/// <summary>
/// SourceLineBreakpointResolver（R6）真实 PDB 链路测试：在 DebugTarget.dll 旁的真实 portable PDB 上，
/// 用「DebugTarget.cs」文件末段匹配解析源行 → 得到 Work 方法的 token+IL——验证 Session resolver
/// （包 Decompiler SourceLineResolver）在真实模块上的解析能力（Engine 测试用桩 resolver 只测机制，
/// 真实 PDB 解析链路由本测试覆盖）。
/// </summary>
public sealed class SourceLineBreakpointResolverTests
{
    private static string DebugTargetDll => Path.Combine(LocateRepo(), "tests", "TestData", "DebugTarget.dll");

    private static string LocateRepo()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "DotNetDebuggerMcp.slnx"))) break;
            dir = dir.Parent;
        }
        Assert.NotNull(dir);
        return dir!.FullName;
    }

    [Fact]
    public void Resolve_RealPdb_FindsWorkMethodTokenMatchingMetadata()
    {
        var dll = DebugTargetDll;
        Assert.True(File.Exists(dll), $"DebugTarget.dll 不存在：{dll}（先跑 generate-testdata.ps1）");
        Assert.True(File.Exists(Path.ChangeExtension(dll, ".pdb")), "DebugTarget.pdb 不存在（先跑 generate-testdata.ps1）");

        var workToken = ReadMethodToken(dll, "Work");
        Assert.True(workToken > 0, "DebugTarget 中未找到 Work 方法");

        var resolver = SourceLineBreakpointResolver.Instance;
        // 遍历行号找 Work 的源行映射（脚本内嵌源码，行号不硬编码——落在 Work 方法区间即命中）
        SourceLineResolveResult? hit = null;
        for (var line = 1; line <= 100 && hit is null; line++)
        {
            var r = resolver.Resolve(dll, "DebugTarget.cs", line);
            if (r is not null && r.MethodToken == workToken) hit = r;
        }

        Assert.NotNull(hit);
        Assert.Equal("DebugTarget.dll", hit!.ModuleName);
        Assert.Equal(workToken, hit.MethodToken);
        Assert.True(hit.IlOffset >= 0);
        Assert.True(hit.ActualLine > 0);
    }

    [Fact]
    public void Resolve_NoPdb_OrWrongFile_ReturnsNull()
    {
        var dll = DebugTargetDll;
        var resolver = SourceLineBreakpointResolver.Instance;

        // 无此源文件（末段不匹配）→ null
        Assert.Null(resolver.Resolve(dll, "NoSuchFile.cs", 10));
    }

    private static int ReadMethodToken(string dllPath, string methodName)
    {
        using var fs = File.OpenRead(dllPath);
        using var pe = new PEReader(fs);
        var mr = pe.GetMetadataReader();
        foreach (var th in mr.TypeDefinitions)
        {
            var td = mr.GetTypeDefinition(th);
            foreach (var mh in td.GetMethods())
            {
                var md = mr.GetMethodDefinition(mh);
                if (mr.GetString(md.Name) == methodName)
                    return MetadataTokens.GetToken(mh);
            }
        }
        return 0;
    }
}
