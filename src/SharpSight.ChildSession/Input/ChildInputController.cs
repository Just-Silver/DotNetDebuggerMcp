using System.Drawing;
using System.Security.Cryptography;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Requests;

namespace SharpSight.ChildSession.Input;

public sealed class ChildInputController(string instance, int sessionId, ChildGuiExecutionGate gate,
    IChildInputPlatform platform, Action<ChildRequest, ResolvedChildTarget> validateObservation,
    TimeProvider? timeProvider = null)
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    private static readonly ushort[] Modifiers = [0x10, 0x11, 0x12, 0x5b, 0x5c];
    private static readonly ushort[] MouseButtons = [1, 2, 4, 5, 6];
    private readonly TimeProvider _time = timeProvider ?? TimeProvider.System;

    public Task<ChildReply> PointerAsync(ChildRequest request, CancellationToken ct)
        => ExecuteAsync(request, pointer: true, ct);

    public Task<ChildReply> KeyboardAsync(ChildRequest request, CancellationToken ct)
        => ExecuteAsync(request, pointer: false, ct);

    private async Task<ChildReply> ExecuteAsync(ChildRequest request, bool pointer, CancellationToken ct)
    {
        try
        {
            if (request.Instance != instance || string.IsNullOrWhiteSpace(instance))
                throw new ArgumentException("必须指定本执行器的instance；不会回退主会话。");
            var args = request.Arguments.Deserialize<InputRequest>(Json) ?? throw new ArgumentException("输入参数无效。");
            var keys = Validate(args, pointer);
            return await gate.RunAsync(token => RunSequenceAsync(request, args, keys, pointer, token), request.Deadline, ct);
        }
        catch (OperationCanceledException) { return ChildReply.Create(request, "not-executed", "输入排队已取消或截止，未执行。"); }
        catch (Exception ex) { return ChildReply.Create(request, "not-executed", ex.Message); }
    }

    private async Task<ChildReply> RunSequenceAsync(ChildRequest request, InputRequest args,
        IReadOnlyList<ushort> keys, bool pointer, CancellationToken ct)
    {
        var scope = new PressedInputScope(platform, sessionId, BitConverter.ToUInt64(RandomNumberGenerator.GetBytes(8)) | 1UL);
        var status = "completed";
        var message = "输入事件已提交；不代表应用已消费，请用UIA或截图确认。";
        var activated = false;
        ResolvedChildTarget? bound = null;
        try
        {
            CheckEnvironment(ct);
            var target = platform.Resolve(args.ProcessId, args.Hwnd);
            ValidateTarget(target, args);
            validateObservation(request, target);
            RejectForeignModifiers(scope);
            if (pointer) RejectForeignButtons(scope);
            activated = true;
            var active = await platform.ActivateAsync(target, ct);
            bound = active;
            if (active.ProcessId != target.ProcessId || active.StartedTicks != target.StartedTicks || active.Hwnd != target.Hwnd)
                throw new InvalidOperationException("激活期间目标身份变化，拒绝输入。");
            validateObservation(request, active);
            if (pointer) await PointerSequenceAsync(args, active, scope, ct);
            else KeyboardSequence(args, keys, active, scope, ct);
        }
        catch (Exception ex)
        {
            status = activated || scope.Submitted ? "cancelled" : "not-executed";
            message = $"已停止后续输入：{ex.Message}；已有副作用不回滚。";
        }
        if (!scope.Release())
        {
            status = "unknown";
            message += $" {scope.CleanupError}";
        }
        return ChildReply.Create(request, status, message, new
        {
            sessionId, submitted = scope.Submitted, processId = bound?.ProcessId ?? args.ProcessId,
            startedTicks = bound?.StartedTicks ?? 0, hwnd = bound?.Hwnd ?? args.Hwnd,
            note = "未带observationId时仅绑定本次受理的目标，不证明坐标来自先前画面。"
        });
    }

    private async Task PointerSequenceAsync(InputRequest args, ResolvedChildTarget target, PressedInputScope scope, CancellationToken ct)
    {
        void Send(ChildInputEvent input)
        {
            Guard(target, scope, ct, new(input.X, input.Y));
            scope.Submit(input);
        }
        Send(new("move", args.X, args.Y));
        if (args.Action == "move") return;
        if (args.Action == "scroll") { Send(new("wheel", args.X, args.Y, Delta: args.Delta)); return; }
        if (args.Action == "drag")
        {
            Send(new("button", args.X, args.Y, Button: args.Button));
            var steps = Math.Clamp(args.DurationMs / 20, 2, 250);
            for (var i = 1; i <= steps; i++)
            {
                await Task.Delay(TimeSpan.FromMilliseconds((double)args.DurationMs / steps), _time, ct);
                var x = (int)Math.Round(args.X + ((double)args.EndX - args.X) * i / steps);
                var y = (int)Math.Round(args.Y + ((double)args.EndY - args.Y) * i / steps);
                Send(new("move", x, y));
            }
            Send(new("button", args.EndX, args.EndY, Up: true, Button: args.Button));
            return;
        }
        for (var i = 0; i < (args.Action == "doubleclick" ? 2 : 1); i++)
        {
            Send(new("button", args.X, args.Y, Button: args.Button));
            Send(new("button", args.X, args.Y, Up: true, Button: args.Button));
        }
    }

    private void KeyboardSequence(InputRequest args, IReadOnlyList<ushort> keys,
        ResolvedChildTarget target, PressedInputScope scope, CancellationToken ct)
    {
        void Send(ChildInputEvent input)
        {
            Guard(target, scope, ct);
            scope.Submit(input);
        }
        if (args.Action == "text")
        {
            foreach (var character in args.Text)
            {
                Send(new("unicode", Key: character));
                Send(new("unicode", Key: character, Up: true));
            }
            return;
        }
        foreach (var key in keys) Send(new("key", Key: key));
        foreach (var key in keys.Reverse()) Send(new("key", Key: key, Up: true));
    }

    private void Guard(ResolvedChildTarget target, PressedInputScope scope, CancellationToken ct, Point? point = null)
    {
        CheckEnvironment(ct);
        if (platform.Resolve(target.ProcessId, target.Hwnd) != target || !platform.IsForeground(target))
            throw new InvalidOperationException("目标身份、几何或前台已变化。");
        RejectForeignModifiers(scope);
        if (point is not null) RejectForeignButtons(scope);
        if (point is { } p && (!target.Bounds.Contains(p) || !platform.PointBelongsTo(target, p)))
            throw new InvalidOperationException("指定child物理像素点不属于目标有效区域或已被遮挡。");
        CheckEnvironment(ct);
    }

    private void CheckEnvironment(CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (sessionId == 0 || platform.SessionId != sessionId || !platform.IsInteractive)
            throw new InvalidOperationException("child连接、实际会话或input desktop不可用。");
    }

    private void RejectForeignModifiers(PressedInputScope scope)
    {
        if (Modifiers.Any(key => platform.IsKeyDown(key) && !scope.OwnsKey(key)))
            throw new InvalidOperationException("存在非本轮按下的修饰键，拒绝改变组合键语义。");
    }

    private void RejectForeignButtons(PressedInputScope scope)
    {
        if (MouseButtons.Any(key => platform.IsKeyDown(key) && !scope.OwnsButton(key)))
            throw new InvalidOperationException("存在非本轮按下的鼠标按钮，拒绝移动光标或接管拖拽。");
    }

    private void ValidateTarget(ResolvedChildTarget target, InputRequest args)
    {
        if (target.ProcessId != args.ProcessId || target.SessionId != sessionId ||
            (args.Hwnd.Length != 0 && target.Hwnd != args.Hwnd))
            throw new InvalidOperationException("目标进程或窗口不属于指定child。");
    }

    private static IReadOnlyList<ushort> Validate(InputRequest args, bool pointer)
    {
        if (args.ProcessId <= 0) throw new ArgumentException("processId必须显式给出目标pid。");
        if (!pointer)
        {
            if (args.X != 0 || args.Y != 0 || args.EndX != 0 || args.EndY != 0 || args.Delta != 0 ||
                args.Button != "left" || args.DurationMs != 300)
                throw new ArgumentException("keyboard不接受指针坐标、button、delta或durationMs。");
            if (args.Action is not ("text" or "key" or "chord")) throw new ArgumentException("keyboard action：text/key/chord。");
            if (args.Action == "text")
            {
                if (string.IsNullOrEmpty(args.Text) || args.Keys.Length != 0) throw new ArgumentException("text仅接受非空text，不接受keys。");
                return [];
            }
            if (args.Text.Length != 0) throw new ArgumentException("key/chord不接受text。");
            return args.Action == "key" ? [KeyChordParser.ParseKey(args.Keys)] : KeyChordParser.Parse(args.Keys);
        }
        if (args.Text.Length != 0 || args.Keys.Length != 0) throw new ArgumentException("pointer不接受text或keys。");
        if (args.Action is not ("move" or "click" or "doubleclick" or "drag" or "scroll"))
            throw new ArgumentException("pointer action：move/click/doubleclick/drag/scroll。");
        if (args.Button is not ("left" or "right" or "middle")) throw new ArgumentException("button：left/right/middle。");
        if (args.Action is "move" or "scroll" && args.Button != "left") throw new ArgumentException("move/scroll不接受button。");
        if (args.Action != "drag" && (args.EndX != 0 || args.EndY != 0 || args.DurationMs != 300))
            throw new ArgumentException("endX/endY/durationMs只适用于drag。");
        if (args.Action == "drag" && args.DurationMs is < 50 or > 5000) throw new ArgumentException("durationMs范围50-5000。");
        if (args.Action == "scroll" ? args.Delta == 0 : args.Delta != 0) throw new ArgumentException("delta仅scroll使用且必须非零。");
        return [];
    }

    private sealed record InputRequest(int ProcessId = 0, string Hwnd = "", string Action = "",
        int X = 0, int Y = 0, int EndX = 0, int EndY = 0, string Button = "left", int Delta = 0,
        int DurationMs = 300, string Text = "", string Keys = "");
}
