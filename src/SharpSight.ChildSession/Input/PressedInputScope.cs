namespace SharpSight.ChildSession.Input;

internal sealed class PressedInputScope(IChildInputPlatform platform, int sessionId, ulong marker)
{
    private readonly List<ChildInputEvent> _down = [];
    internal bool Submitted { get; private set; }
    internal string CleanupError { get; private set; } = "";

    internal bool OwnsKey(ushort key) => _down.Any(e => e.Kind == "key" && e.Key == key);
    internal bool OwnsButton(ushort key) => _down.Any(e => e.Kind == "button" && ButtonKey(e.Button) == key);

    internal void Submit(ChildInputEvent input)
    {
        var tracksDown = input.Kind is "key" or "unicode" or "button";
        if (tracksDown && !input.Up)
        {
            var physicalKey = input.Kind == "key" ? input.Key : input.Kind == "button" ? ButtonKey(input.Button) : (ushort)0;
            if (physicalKey != 0 && platform.IsKeyDown(physicalKey))
                throw new InvalidOperationException("该键或按钮已按下，拒绝取得并非本轮的按下所有权。");
        }
        if (!platform.Submit(input, marker)) throw new InvalidOperationException("系统未接受本轮输入事件。");
        Submitted = true;
        if (!tracksDown) return;
        if (!input.Up) _down.Add(input);
        else _down.RemoveAll(e => e.Kind == input.Kind && e.Key == input.Key && e.Button == input.Button);
    }

    internal bool Release()
    {
        try
        {
            for (var i = _down.Count - 1; i >= 0; i--)
            {
                if (platform.SessionId != sessionId || !platform.IsInteractive)
                {
                    CleanupError = "原子会话或input desktop已失效，未向新交互环境投放释放事件。";
                    return false;
                }
                // 目标失焦后只允许释放本轮确实提交的down，不能清理其他来源的物理键态。
                if (!platform.Submit(_down[i] with { Up = true }, marker))
                {
                    CleanupError = "系统未接受本轮按下状态的释放。";
                    return false;
                }
                _down.RemoveAt(i);
            }
            return true;
        }
        catch (Exception ex) { CleanupError = $"释放失败：{ex.Message}"; return false; }
    }

    private static ushort ButtonKey(string button) => button switch
    {
        "left" => 1,
        "right" => 2,
        "middle" => 4,
        _ => throw new ArgumentException("button只能为left/right/middle。")
    };
}
