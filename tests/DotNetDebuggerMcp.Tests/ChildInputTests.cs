using System.Drawing;
using System.Text.Json;
using SharpSight.ChildSession.Contracts;
using SharpSight.ChildSession.Input;
using SharpSight.ChildSession.Requests;
using Xunit;

namespace DotNetDebuggerMcp.Tests;

public sealed class ChildInputTests
{
    [Fact]
    public async Task 前台变化后不继续键入其他窗口()
    {
        var platform = new Desktop { LoseForegroundAfterFirstCharacter = true };
        var controller = Controller(platform);
        var reply = await controller.KeyboardAsync(Request("child_keyboard", new
        {
            action = "text", processId = 3, text = "甲乙"
        }), TestContext.Current.CancellationToken);
        Assert.Equal("甲", platform.TargetText);
        Assert.Equal("", platform.OtherText);
        Assert.Equal("cancelled", reply.Status);
    }

    [Fact]
    public async Task 只释放本轮按下键而不释放后来出现的物理Ctrl()
    {
        var platform = new Desktop { HoldPhysicalCtrlAfterFirstKey = true };
        var reply = await Controller(platform).KeyboardAsync(Request("child_keyboard", new
        {
            action = "chord", processId = 3, keys = "Shift+K"
        }), TestContext.Current.CancellationToken);
        Assert.Equal("cancelled", reply.Status);
        Assert.True(platform.PhysicalCtrlDown);
        Assert.Empty(platform.OwnKeys);
    }

    [Fact]
    public async Task 已按下的修饰键影响组合键时拒绝而不是当作自己按下()
    {
        var platform = new Desktop { PhysicalCtrlDown = true };
        var reply = await Controller(platform).KeyboardAsync(Request("child_keyboard", new
        {
            action = "chord", processId = 3, keys = "Ctrl+K"
        }), TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", reply.Status);
        Assert.True(platform.PhysicalCtrlDown);
        Assert.Empty(platform.OwnKeys);
        Assert.Equal("", platform.TargetText);
    }

    [Fact]
    public async Task 窗口负坐标仍按child物理像素拖动()
    {
        var platform = new Desktop();
        var reply = await Controller(platform).PointerAsync(Request("child_pointer", new
        {
            action = "drag", processId = 3, x = -180, y = 20, endX = -80, endY = 70, durationMs = 50
        }), TestContext.Current.CancellationToken);
        Assert.Equal("completed", reply.Status);
        Assert.Equal(new Point(-80, 70), platform.DragEnd);
        Assert.False(platform.OwnLeftDown);
    }

    [Fact]
    public async Task 取消拖拽不会遗留本轮鼠标按下()
    {
        using var cancelled = CancellationTokenSource.CreateLinkedTokenSource(TestContext.Current.CancellationToken);
        var platform = new Desktop { CancelWhenMouseDown = cancelled };
        var reply = await Controller(platform).PointerAsync(Request("child_pointer", new
        {
            action = "drag", processId = 3, x = -180, y = 20, endX = -80, endY = 70, durationMs = 300
        }), cancelled.Token);
        Assert.Equal("cancelled", reply.Status);
        Assert.False(platform.OwnLeftDown);
    }

    [Fact]
    public async Task 输入桌面改变后连清理事件也不能投放到新桌面()
    {
        var platform = new Desktop { ChangeDesktopWhenMouseDown = true };
        var reply = await Controller(platform).PointerAsync(Request("child_pointer", new
        {
            action = "drag", processId = 3, x = -180, y = 20, endX = -80, endY = 70, durationMs = 50
        }), TestContext.Current.CancellationToken);
        Assert.Equal("unknown", reply.Status);
        Assert.Equal("", platform.OtherText);
        Assert.False(platform.NewDesktopReceivedRelease);
    }

    [Theory]
    [InlineData("Ctrl+Ctrl+K")]
    [InlineData("Ctrl+A+B")]
    [InlineData("Ctrl+Unknown")]
    public async Task 非法组合键不会产生输入(string keys)
    {
        var platform = new Desktop();
        var reply = await Controller(platform).KeyboardAsync(Request("child_keyboard", new
        {
            action = "chord", processId = 3, keys
        }), TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", reply.Status);
        Assert.Empty(platform.OwnKeys);
        Assert.Equal("", platform.TargetText);
    }

    [Theory]
    [InlineData("hidden")]
    [InlineData("ambiguous")]
    public async Task 隐藏目标和多窗歧义不会退化为其他窗口输入(string state)
    {
        var platform = new Desktop { RejectedTarget = state };
        var reply = await Controller(platform).KeyboardAsync(Request("child_keyboard", new
        {
            action = "text", processId = 3, text = "不应写入"
        }), TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", reply.Status);
        Assert.Equal("", platform.OtherText);
        Assert.Equal("", platform.TargetText);
    }

    [Fact]
    public async Task 最小化目标还原并确认前台后才能提交输入()
    {
        var platform = new Desktop { Minimized = true };
        var reply = await Controller(platform).KeyboardAsync(Request("child_keyboard", new
        {
            action = "text", processId = 3, text = "正常"
        }), TestContext.Current.CancellationToken);
        Assert.Equal("completed", reply.Status);
        Assert.False(platform.Minimized);
        Assert.Equal("正常", platform.TargetText);
    }

    [Fact]
    public async Task 指针请求不接受键盘文本()
    {
        var platform = new Desktop();
        var reply = await Controller(platform).PointerAsync(Request("child_pointer", new
        {
            action = "move", processId = 3, x = -180, y = 20, text = "不能静默丢弃"
        }), TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", reply.Status);
        Assert.Equal(Point.Empty, platform.Cursor);
    }

    [Fact]
    public async Task 键盘请求不接受指针坐标()
    {
        var platform = new Desktop();
        var reply = await Controller(platform).KeyboardAsync(Request("child_keyboard", new
        {
            action = "text", processId = 3, x = -180, text = "不能写入"
        }), TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", reply.Status);
        Assert.Equal("", platform.TargetText);
    }

    [Theory]
    [InlineData("key", "Enter", "已提交")]
    [InlineData("chord", " Ctrl + k ", "快捷命令")]
    public async Task 单键和组合键可触发目标命令且不留下按下状态(string action, string keys, string expected)
    {
        var platform = new Desktop();
        var reply = await Controller(platform).KeyboardAsync(Request("child_keyboard", new
        {
            action, processId = 3, keys
        }), TestContext.Current.CancellationToken);
        Assert.Equal("completed", reply.Status);
        Assert.Equal(expected, platform.TargetText);
        Assert.Empty(platform.OwnKeys);
    }

    [Fact]
    public async Task 既有鼠标按下时不移动光标或接管拖拽()
    {
        var platform = new Desktop { PhysicalLeftDown = true };
        var reply = await Controller(platform).PointerAsync(Request("child_pointer", new
        {
            action = "drag", processId = 3, x = -180, y = 20, endX = -80, endY = 70, durationMs = 50
        }), TestContext.Current.CancellationToken);
        Assert.Equal("not-executed", reply.Status);
        Assert.Equal(Point.Empty, platform.Cursor);
        Assert.True(platform.PhysicalLeftDown);
        Assert.False(platform.OwnLeftDown);
    }

    private static ChildInputController Controller(Desktop platform)
        => new("input-tests", 7, new ChildGuiExecutionGate(), platform, (_, _) => { });

    private static ChildRequest Request(string operation, object args)
        => new("input-tests", Guid.NewGuid().ToString("N"), operation, JsonSerializer.SerializeToElement(args),
            DateTimeOffset.UtcNow.AddSeconds(5));

    private sealed class Desktop : IChildInputPlatform
    {
        private readonly ResolvedChildTarget _target = new(3, 100, 7, "40", new(-200, 0, 180, 140));
        private bool _foreground = true;
        private bool _interactive = true;
        private Point _cursor;
        internal string TargetText = "";
        internal string OtherText = "";
        internal bool PhysicalCtrlDown;
        internal bool PhysicalLeftDown;
        internal HashSet<ushort> OwnKeys { get; } = [];
        internal bool OwnLeftDown;
        internal Point DragEnd;
        internal Point Cursor => _cursor;
        internal bool LoseForegroundAfterFirstCharacter;
        internal bool HoldPhysicalCtrlAfterFirstKey;
        internal CancellationTokenSource? CancelWhenMouseDown;
        internal bool ChangeDesktopWhenMouseDown;
        internal bool NewDesktopReceivedRelease;
        internal string RejectedTarget = "";
        internal bool Minimized;

        public int SessionId => 7;
        public bool IsInteractive => _interactive;
        public ResolvedChildTarget Resolve(int processId, string hwnd)
            => RejectedTarget.Length != 0 ? throw new InvalidOperationException(RejectedTarget) : _target;
        public Task<ResolvedChildTarget> ActivateAsync(ResolvedChildTarget target, CancellationToken ct)
        {
            Minimized = false;
            _foreground = true;
            return Task.FromResult(target);
        }
        public bool IsForeground(ResolvedChildTarget target) => _foreground && !Minimized;
        public bool PointBelongsTo(ResolvedChildTarget target, Point point) => target.Bounds.Contains(point);
        public bool IsKeyDown(ushort key)
            => key == 0x11 && PhysicalCtrlDown || key == 1 && (PhysicalLeftDown || OwnLeftDown) || OwnKeys.Contains(key);
        public bool Submit(ChildInputEvent input, ulong marker)
        {
            if (!_interactive)
            {
                if (input.Up) NewDesktopReceivedRelease = true;
                return false;
            }
            if (input.Kind == "move") _cursor = new(input.X, input.Y);
            else if (input.Kind == "button")
            {
                OwnLeftDown = !input.Up;
                if (input.Up) DragEnd = _cursor;
                else
                {
                    CancelWhenMouseDown?.Cancel();
                    if (ChangeDesktopWhenMouseDown) _interactive = false;
                }
            }
            else if (input.Kind == "unicode" && !input.Up)
            {
                if (_foreground) TargetText += (char)input.Key;
                else OtherText += (char)input.Key;
                if (LoseForegroundAfterFirstCharacter) _foreground = false;
            }
            else if (input.Kind == "key")
            {
                if (input.Up) OwnKeys.Remove(input.Key);
                else
                {
                    OwnKeys.Add(input.Key);
                    if (input.Key == 0x0d) TargetText += "已提交";
                    if (input.Key == 0x4b && OwnKeys.Contains(0x11)) TargetText += "快捷命令";
                    if (HoldPhysicalCtrlAfterFirstKey)
                    {
                        PhysicalCtrlDown = true;
                        _foreground = false;
                    }
                }
            }
            return true;
        }
    }
}
