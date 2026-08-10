using ILSpyMcp;
using Xunit;

namespace ILSpyMcp.Tests;

/// <summary>
/// CLI 入口测试。仅覆盖不触碰 AppServices 单例的纯逻辑（版本号），避免并行测试相互污染；
/// 命令行分发复用 DecompileTool/ListTypesTool/DecompileToDirTool（已在 ToolPreflightTests 覆盖），
/// 端到端 CLI 行为由 dotnet run 手工验证。
/// </summary>
public class ILSpyMcpCmdTests
{
    [Fact]
    public void Version_返回工具名加三位版本号()
    {
        var cmd = new ILSpyMcpCmd();
        Assert.StartsWith("ilspymcp ", cmd.Version);
        Assert.Matches(@"^ilspymcp \d+\.\d+\.\d+$", cmd.Version);
    }
}
