using DotNetDebuggerMcp.Tools.Debugger;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

/// <summary>
/// async 状态机类型识别助手（StateMachineFrameHelper.TryParseStateMachine）纯函数单元测试。
/// 输入为 Task 2（C）后栈帧/停点类型全名（嵌套类型以「+」连接）：判定「&lt;方法名&gt;d__N」形态并
/// 提取原方法名。非状态机类型（普通类型 / lambda 闭包 / 缺「&lt;」或「&gt;d__」的相似形态）返回 null。
/// </summary>
public class StateMachineFrameHelperTests
{
    [Fact]
    public void 命名空间嵌套类型全名_解析出原方法名与前缀()
    {
        var r = StateMachineFrameHelper.TryParseStateMachine("Ns.Outer+<Foo>d__44");
        Assert.NotNull(r);
        Assert.Equal("Ns.Outer+", r!.Value.Prefix);
        Assert.Equal("Foo", r.Value.MethodName);
    }

    [Fact]
    public void 裸类型名_前缀为空()
    {
        var r = StateMachineFrameHelper.TryParseStateMachine("<Foo>d__44");
        Assert.NotNull(r);
        Assert.Equal("", r!.Value.Prefix);
        Assert.Equal("Foo", r.Value.MethodName);
    }

    [Fact]
    public void 深层嵌套_解析正确()
    {
        var r = StateMachineFrameHelper.TryParseStateMachine("Ns.A.B+C+D+<SwitchAutoState>d__1");
        Assert.NotNull(r);
        Assert.Equal("Ns.A.B+C+D+", r!.Value.Prefix);
        Assert.Equal("SwitchAutoState", r.Value.MethodName);
    }

    [Fact]
    public void 状态机内嵌套lambda闭包_不误判为状态机()
    {
        // async 方法内 lambda 的闭包类嵌套在状态机类型内：Ns.Outer+<Foo>d__44+<>c__DisplayClass0_0。
        // LastIndexOf("<") 定位到内层 <>c 闭包的 "<"——其名后无 ">d__" 序列 → 判非状态机
        // （闭包帧不是状态机 MoveNext，不应标「状态机 X」；本助手只认当前类型自身为 <X>d__N 形态）。
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("Ns.Outer+<Foo>d__44+<>c__DisplayClass0_0"));
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("Ns+<Go>d__3+<>c"));
    }

    [Fact]
    public void 普通类型_返回null()
    {
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("Ns.Program"));
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("DebugTarget.Program"));
    }

    [Fact]
    public void 缺尖括号形态_返回null()
    {
        // 缺 "<"（>d__ 前无左尖括号）——LastIndexOf 找不到，判非状态机
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("Foo>d__44"));
    }

    [Fact]
    public void 缺gt_d__形态_返回null()
    {
        // 含 "<" 但无 ">d__" 序列（lambda 闭包等其它编译器生成形态；编译器生成的普通方法体内嵌名也类似）
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("Ns+<>c__DisplayClass1_0"));
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("Ns.Outer+<Foo>"));
        // 尾部缺 "d__"（含 ">" 后接数字但无 d__）判非状态机——TryParseStateMachine 只认 >d__ 序列
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("Ns.Outer+<Foo>44"));
    }

    [Fact]
    public void 空串或null_返回null()
    {
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine(""));
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("<"));
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine(">d__"));
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("d__"));
        Assert.Null(StateMachineFrameHelper.TryParseStateMachine("MoveNext"));
    }
}
