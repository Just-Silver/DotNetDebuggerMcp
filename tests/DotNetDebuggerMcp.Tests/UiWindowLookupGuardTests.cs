using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// UIA 窗口定位确定性回归护栏（与桌面是否交互无关，CI 必过/必败）：
/// 禁止窗口定位回退到「UIA 桌面根全量枚举」——<c>desktop.FindAllChildren(...)</c> 必须走完全部顶层窗口，
/// 会跨过 shell 的 <c>Progman</c>（Program Manager）窗口，实测单次可阻塞 ~7.5s，超过 5s 护栏后
/// <c>ui_find</c>/<c>ui_action</c>/<c>ui_input</c>/<c>ui_get</c>/<c>ui_wait</c> 全族超时失败。
/// 正解：Win32 主窗口句柄直转 UIA 元素（<c>FromHandle</c>，O(1)）优先，或 <c>FindFirstChild</c>（命中即返回）。
/// 环境相关（依赖本机 shell 窗口响应速度），e2e 在桌面枚举快的机器上不红，故此处用源码扫描做确定性兜底。
/// </summary>
public sealed class UiWindowLookupGuardTests
{
    [Fact]
    public void LocatorSource_DoesNotFullEnumerateDesktop()
    {
        var file = Path.Combine(TestDataPaths.RepositoryRoot,
            "src", "DotNetDebuggerMcp", "Services", "Ui", "UiElementLocator.cs");
        Assert.True(File.Exists(file), $"定位器源码不存在：{file}");

        var text = File.ReadAllText(file);
        Assert.True(
            !text.Contains(".FindAllChildren(", StringComparison.Ordinal),
            "UiElementLocator 不得用 .FindAllChildren( 枚举窗口——桌面全量枚举会跨过 shell 的 Progman 窗口阻塞 ~7.5s，"
            + "超 5s 护栏致 ui_* 全族超时。请用 MainWindowHandle+FromHandle 快速路径，或 FindFirstChild（命中即返回）。");
    }
}
