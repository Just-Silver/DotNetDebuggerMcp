using DotNetDebuggerMcp.Tools.Debugger;
using ModelContextProtocol.Server;
using System.ComponentModel;
using System.Reflection;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// V4 语料回归护栏：反射读各 debug_* 工具方法 [Description]（agent 唯一直接可见的契约面），
/// 断言其保留「提示语 → 期望动作」关键引导片段，防 R1-R8 修复文案与新工具提示语悄悄退化。
/// 纯内存、不触碰静态单例（不触 AppServices 静态状态），故不挂 [Collection("AppServices")]。
/// 只断关键片段 Contains，不做全文精确匹配（高脆）；改关键提示文案必须同步改 ContractData，否则即红。
/// </summary>
public sealed class AgentCopyGuardTests
{
    // 每对 = (工具方法名, Description 必含关键片段) —— 语料契约，改文案须同步改此处（低脆：只锁关键引导词）
    public static TheoryData<string, string> ContractData => new()
    {
        { nameof(DebugControlTool.DebugStep), "debug_wait" },          // step 提交后引导「用 debug_wait 等停」
        { nameof(DebugRunToTool.DebugRunTo), "临时断点" },              // run_to 一次性临时断点语义（命中即移除）
        { nameof(DebugSessionTool.DebugLaunch), "冻结在 Main 前" },     // launch 早期冻结语义（无需目标配合）
        { nameof(DebugSessionTool.DebugState), "Stopped" },             // 读栈/变量前先 debug_state 确认 Stopped
        { nameof(DebugSessionTool.DebugTerminate), "强制结束" },        // debug_terminate 结束目标进程（区别于 disconnect）
        { nameof(DebugSessionTool.DebugModules), "已加载的模块" },      // debug_modules 列已加载模块（断点定位排障）
        { nameof(DebugInspectTool.DebugVariables), "$exception" },      // 异常停点观察口
        { nameof(DebugBreakpointTool.DebugBreakpointList), "绑定" },    // list 返回含绑定状态
        { nameof(DebugExceptionTool.DebugExceptions), "first-chance" }, // 异常断点语义
        // W1/V3/D1 已落地工具补录（描述已含片段，直接固化进契约）：
        { nameof(DebugSetTool.DebugSet), "原值" },                      // 改值返回「原值 → 新值」回显
        { nameof(DebugSetTool.DebugSet), "风险" },                      // 改值副作用风险提示（写内存可能崩溃）
        { nameof(DebugTimelineTool.DebugTimeline), "时间线" },          // 统一时间线语义
        { nameof(DebugObjectTool.DebugObject), "depth" },               // 受控递归下钻参数
        // V1 debug_verify 一键复验（场景文件 + PASS/FAIL 结果语义）
        { nameof(DebugVerifyTool.DebugVerify), "场景" },                // 场景 JSON 文件输入
        { nameof(DebugVerifyTool.DebugVerify), "PASS" },                // 执行到 PASS/FAIL 结果
        { nameof(DebugVerifyTool.DebugVerify), "fail-fast" },           // 断言失败即停语义
        // U1A ui_* 工具补录（语义动词契约：无视觉清单 / 能力清单 / 语义动词与无右键·双击 / 读值 what / 写值 value / 等待超时）
        { nameof(UiTools.UiFind), "控件清单" },                         // ui_find 返回文本控件清单（无视觉）
        { nameof(UiTools.UiFind), "无视觉" },
        { nameof(UiTools.UiFind), "patterns" },                          // ui_find 输出能力清单
        { nameof(UiTools.UiAction), "verb" },                            // ui_action 语义动词
        { nameof(UiTools.UiAction), "右键" },                            // 无右键/双击边界（物理输入已移除）
        { nameof(UiTools.UiAction), "不抢前台" },                        // UIA-only 前台策略
        { nameof(UiTools.UiInput), "value" },                            // ui_input 写入值
        { nameof(UiTools.UiGet), "what" },                               // ui_get 读取状态
        { nameof(UiTools.UiWait), "超时返回当前状态" },                 // ui_wait 超时不报错
    };

    [Theory]
    [MemberData(nameof(ContractData))]
    public void ToolDescription_KeepsCriticalCopy(string methodName, string fragment)
    {
        var assembly = typeof(DebugControlTool).Assembly;
        var method = assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Single(m => m.Name == methodName && m.GetCustomAttribute<McpServerToolAttribute>() is not null);
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.NotNull(description); // 工具方法必须带 [Description]
        Assert.Contains(fragment, description, StringComparison.Ordinal);
    }
}
