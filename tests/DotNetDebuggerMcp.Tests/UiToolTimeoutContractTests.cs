using SharpSight.UiAutomation;
using DotNetDebuggerMcp.Tools.Debugger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// UI 工具超时契约护栏（确定性、与桌面是否交互无关，CI 必过/必败）：
/// ui_* 五工具都必须暴露可调的单次 UIA 调用超时 <c>timeoutSeconds</c>（此前固定 5s 不可调，
/// 目标响应慢时 agent 无处加大超时，只能看到「UIA 调用超过 5s」而误判「环境 UIA 不可用」）。
/// 另测 <see cref="UiAutomationService.ResolveTimeout"/> 的默认值与上下界 clamp。
/// </summary>
public sealed class UiToolTimeoutContractTests
{
    [Theory]
    [InlineData(nameof(UiTools.UiFind))]
    [InlineData(nameof(UiTools.UiAction))]
    [InlineData(nameof(UiTools.UiInput))]
    [InlineData(nameof(UiTools.UiGet))]
    [InlineData(nameof(UiTools.UiWait))]
    public void UiTool_ExposesAdjustableTimeout(string methodName)
    {
        var method = typeof(UiTools).GetMethods(BindingFlags.Public | BindingFlags.Static)
            .Single(m => m.Name == methodName && m.GetCustomAttribute<McpServerToolAttribute>() is not null);

        var p = method.GetParameters().SingleOrDefault(p => p.Name == "timeoutSeconds");
        Assert.NotNull(p);
        Assert.Equal(typeof(int), p!.ParameterType);
        Assert.True(p.HasDefaultValue && p.DefaultValue is int def && def >= 1,
            $"ui_* 工具 {methodName} 的 timeoutSeconds 必须有正整数默认值（agent 可见的可调超时）。");
    }

    [Fact]
    public void ResolveTimeout_DefaultsTo5_AndClampsToRange()
    {
        Assert.Equal(TimeSpan.FromSeconds(5), UiAutomationService.ResolveTimeout(0));    // <=0 → 默认 5
        Assert.Equal(TimeSpan.FromSeconds(5), UiAutomationService.ResolveTimeout(-3));   // 负值同样回落默认 5
        Assert.Equal(TimeSpan.FromSeconds(5), UiAutomationService.ResolveTimeout(5));
        Assert.Equal(TimeSpan.FromSeconds(1), UiAutomationService.ResolveTimeout(1));
        Assert.Equal(TimeSpan.FromSeconds(300), UiAutomationService.ResolveTimeout(9999)); // clamp 上界
    }
}
