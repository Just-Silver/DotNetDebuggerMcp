using ClrDebug;

namespace DotNetDebugger.Engine.Stepping;

/// <summary>单步控制封装。所有方法须在进程同步态（停点后、Continue 前）调用；调用后由调用方 Continue 让 step 发生。</summary>
public static class StepperManager
{
    /// <summary>step into（true）/ step over（false）。</summary>
    public static CorDebugStepper Step(CorDebugThread thread, bool stepIn)
    {
        var stepper = thread.CreateStepper();
        stepper.Step(stepIn);
        return stepper;
    }

    /// <summary>step out：步出当前方法到调用方（原生 StepOut，research/06 A.2 修正）。</summary>
    public static CorDebugStepper StepOut(CorDebugThread thread)
    {
        var stepper = thread.CreateStepper();
        stepper.StepOut();
        return stepper;
    }
}
