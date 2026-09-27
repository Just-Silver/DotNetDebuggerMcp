namespace SharpSight.UiAutomation;

/// <summary>
/// 元素采集代际号（spec §7.4 代际一致性）：每次元素采集（<c>ui_find</c> / <c>screenshot element</c>）产出
/// 一帧，单调递增；消费侧（<c>ui_action</c>/<c>ui_input</c>/<c>ui_get</c>/<c>screenshot element</c>）携带旧
/// <c>frameId</c> → <see cref="StaleFrameException"/> + 中文教学提示。
/// <para><b>0 是哨兵</b>：<see cref="Validate"/> 对 0 放行（「不校验」，向后兼容 spec §4.1）。</para>
/// <para>线程安全：<see cref="Next"/> 用 <c>Interlocked</c>，<see cref="Current"/> 用 <c>Volatile</c> 读——
/// 多个采集路径并发产帧也不重号。</para>
/// </summary>
public sealed class FrameRegistry
{
    private int _current;

    /// <summary>当前代际号（进程内单调递增；初始 0）。由采集方（<see cref="Next"/>）推进。</summary>
    public int Current => Volatile.Read(ref _current);

    /// <summary>产出下一帧代际号（从 1 起，单调递增）。</summary>
    public int Next() => Interlocked.Increment(ref _current);

    /// <summary>
    /// 校验消费侧携带的 <paramref name="frameId"/>：0=不校验（向后兼容）；非 0 且 ≠ <see cref="Current"/> →
    /// 抛 <see cref="StaleFrameException"/>（中文教学提示，spec §5）。
    /// </summary>
    public void Validate(int frameId)
    {
        if (frameId == 0) return;
        var current = Current;
        if (frameId != current)
            throw new StaleFrameException(
                $"编号来自旧画面（帧 {frameId}，当前 {current}），请重新 screenshot/ui_find 后再操作。");
    }
}

/// <summary>
/// 旧帧引用（spec §5/§7.4）：元素清单与 <c>frameId</c> 绑定，消费旧帧 → 拒绝 + 教学提示。
/// 独立于 <see cref="UiException"/>（后者为一般 UIA 失败提示）；本异常专表代际失配。
/// </summary>
public sealed class StaleFrameException : Exception
{
    public StaleFrameException(string message) : base(message) { }
}
