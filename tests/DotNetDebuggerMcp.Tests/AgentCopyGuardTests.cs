using DotNetDebuggerMcp.Tools.Debugger;
using DotNetDebuggerMcp.Tools.Screenshot;
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
        // 阶段一 screenshot 通用化补录（agent 唯一直接可见的契约面：固定 PNG / 模式推断 / 坐标元数据）
        { nameof(ScreenshotTool.Screenshot), "固定 PNG" },         // 输出格式恒为 PNG（无 format/quality）
        { nameof(ScreenshotTool.Screenshot), "auto" },             // mode 默认 auto 按参数推断
        { nameof(ScreenshotTool.Screenshot), "原点" },             // 头部 origin/scale 坐标元数据
        { nameof(ScreenshotTool.Screenshot), "缩放" },
        // 视觉族发现工具（只为 screenshot 寻址服务；只读）
        { nameof(ScreenshotDisplaysTool.ScreenshotDisplays), "显示器" },              // 列显示器清单
        { nameof(ScreenshotWindowsTool.ScreenshotWindows), "可见顶层窗口" },          // 列窗口清单
    };

    // 每项 = (工具方法名, 参数名, 该参数 Description 必含关键片段) —— 参数级契约。
    // 锁「agent 据以正确调用」的关键事实：默认值、取值域、护栏语义；改参数说明必须同步改此处。
    public static TheoryData<string, string, string> ParamContractData => new()
    {
        { nameof(ScreenshotTool.Screenshot), "mode", "auto" },             // 模式推断默认值
        { nameof(ScreenshotTool.Screenshot), "maxDimension", "1568" },     // 默认上限（铁律：改默认值须改 Description）
        { nameof(ScreenshotTool.Screenshot), "maxDimension", "0=不缩放" }, // 「不缩放」逃生门必须对 agent 可见
        { nameof(ScreenshotTool.Screenshot), "frameId", "旧画面" },        // 代际护栏语义
        { nameof(ScreenshotTool.Screenshot), "includeCursor", "光标" },
        { nameof(ScreenshotTool.Screenshot), "filePath", "临时目录" },          // 落盘基准=临时目录（防污染调用方项目）
        // 寻址参数的「取值来源」必须对 agent 可见（跨工具指路；漏改等于 agent 又回到靠猜）
        { nameof(ScreenshotTool.Screenshot), "display", "screenshot_displays" },
        { nameof(ScreenshotTool.Screenshot), "hwnd", "screenshot_windows" },
        { nameof(ScreenshotTool.Screenshot), "windowTitle", "screenshot_windows" },
        { nameof(ScreenshotTool.Screenshot), "processId", "screenshot_windows" },
        { nameof(ScreenshotTool.Screenshot), "region", "先截一张" },
        { nameof(ScreenshotTool.Screenshot), "element", "建议先用 ui_find" },
        { nameof(UiTools.UiAction), "frameId", "旧画面" },
        { nameof(UiTools.UiInput), "frameId", "旧画面" },
        { nameof(UiTools.UiGet), "frameId", "旧画面" },
    };

    [Theory]
    [MemberData(nameof(ContractData))]
    public void ToolDescription_KeepsCriticalCopy(string methodName, string fragment)
    {
        var method = FindToolMethod(methodName);
        var description = method.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.NotNull(description); // 工具方法必须带 [Description]
        Assert.Contains(fragment, description, StringComparison.Ordinal);
    }

    [Theory]
    [MemberData(nameof(ParamContractData))]
    public void ToolParameter_KeepsCriticalCopy(string methodName, string paramName, string fragment)
    {
        var parameter = FindToolMethod(methodName).GetParameters().Single(p => p.Name == paramName);
        var description = parameter.GetCustomAttribute<DescriptionAttribute>()?.Description;
        Assert.False(string.IsNullOrWhiteSpace(description)); // 参数必须带 [Description]
        Assert.Contains(fragment, description!, StringComparison.Ordinal);
    }

    /// <summary>
    /// 全工具「参数 Description 非空」底线护栏（对应铁律：agent 只能凭参数 [Description] 知道怎么传参，
    /// 漏写即该参数语义对 agent 不可见）。取消令牌按约定不写 [Description]、豁免。
    /// 只查「有没有」，不查内容新旧（内容由 ContractData/ParamContractData 锁）。
    /// </summary>
    [Fact]
    public void ToolParameter_都有Description()
    {
        var offenders = new List<string>();
        foreach (var type in typeof(DebugControlTool).Assembly.GetTypes())
        {
            if (type.GetCustomAttribute<McpServerToolTypeAttribute>() is null) continue;
            foreach (var method in type.GetMethods(BindingFlags.Public | BindingFlags.Static))
            {
                if (method.GetCustomAttribute<McpServerToolAttribute>() is null) continue;
                foreach (var parameter in method.GetParameters())
                {
                    if (parameter.ParameterType == typeof(CancellationToken)) continue; // SDK 注入、按约定不写
                    if (string.IsNullOrWhiteSpace(parameter.GetCustomAttribute<DescriptionAttribute>()?.Description))
                        offenders.Add($"{type.Name}.{method.Name}({parameter.Name})");
                }
            }
        }
        Assert.Empty(offenders);
    }

    private static MethodInfo FindToolMethod(string methodName)
        => typeof(DebugControlTool).Assembly.GetTypes()
            .SelectMany(t => t.GetMethods(BindingFlags.Public | BindingFlags.Static))
            .Single(m => m.Name == methodName && m.GetCustomAttribute<McpServerToolAttribute>() is not null);
}
