using DotNetDebugger.Decompiler.Document;
using Xunit;

namespace DotNetDebugger.Decompiler.Tests;

/// <summary>P4：DocumentService.FindTypeByToken（方法 token → 类型全名，自 Web 提升为公共实现）。</summary>
public sealed class FindTypeByTokenTests
{
    [Fact]
    public void Resolve_KnownMethodToken_ReturnsTypeFullName()
    {
        // DebugTarget.dll 的 Work 方法 token（元数据动态读，防脚本漂移）
        var dll = Path.ChangeExtension(TestDataPaths.DebugTargetExe, ".dll");
        var workToken = DocumentServiceTests.FindMethodToken(dll, "DebugTarget.Program", "Work");
        Assert.True(workToken > 0);

        var type = DocumentService.FindTypeByToken(dll, workToken);
        Assert.Equal("DebugTarget.Program", type);
    }

    [Fact]
    public void Resolve_UnknownToken_ReturnsNull()
    {
        var dll = Path.ChangeExtension(TestDataPaths.DebugTargetExe, ".dll");
        Assert.Null(DocumentService.FindTypeByToken(dll, 0x06000001 | unchecked((int)0xFFFF0000)));
    }

    [Fact]
    public void Resolve_MissingFile_ReturnsNull()
    {
        Assert.Null(DocumentService.FindTypeByToken("Z:\\no\\such.dll", 0x06000001));
    }
}
