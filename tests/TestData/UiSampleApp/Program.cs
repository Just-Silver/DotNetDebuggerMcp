// U1 UI 自动化测试目标（WinForms，net10.0-windows）：供 ui_find/ui_invoke/ui_wait/ui_scroll 与 debug_* 编排的 e2e 锚点。
// 窗口标题 UiSample；控件名（AutomationId）/文本/处理器方法名即测试契约，改动前看 DebugUiToolsTests.cs。
// 处理器方法（OnToggleState / OnToggleStateRightClick / OnListDoubleClick / OnCountClick / OnInputClick）为
// debug_breakpoint_set typeName+memberName 断点锚点——勿改方法名（token 随源码变化）。
using System.Drawing;
using System.Windows.Forms;

namespace UiSampleApp;

internal static class Program
{
    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        Application.Run(new MainForm());
    }
}

internal sealed class MainForm : Form
{
    private readonly Button _toggleState = new();
    private readonly Button _countButton = new();
    private readonly Button _inputButton = new();
    private readonly TextBox _inputBox = new();
    private readonly ListBox _listBox = new();
    private readonly Label _rightClickLabel = new();
    private readonly Label _countLabel = new();
    private readonly Label _doubleClickLabel = new();
    private readonly Label _echoLabel = new();
    private string _state = "手动";

    public MainForm()
    {
        Text = "UiSample";
        ClientSize = new Size(500, 440);
        MinimumSize = ClientSize;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        Font = new Font("Microsoft YaHei UI", 9F);

        // 状态切换按钮（左键切换 手动↔自动；右键切换另一 Label 状态）
        _toggleState.Text = _state;
        _toggleState.SetBounds(14, 14, 100, 32);
        _toggleState.Click += OnToggleState;
        _toggleState.MouseUp += OnToggleStateRightClick;

        _rightClickLabel.Text = "右键:0";
        _rightClickLabel.SetBounds(126, 22, 140, 20);

        // 计数按钮
        _countButton.Text = "计数";
        _countButton.SetBounds(14, 56, 100, 32);
        _countButton.Click += OnCountClick;
        _countLabel.Text = "计数:0";
        _countLabel.SetBounds(126, 64, 140, 20);

        // 输入框 + 输入按钮（ui_input v1.5 之前的占位控件；同时验证 TextBox/Edit 类型可被定位）
        _inputBox.Text = "sample text";
        _inputBox.SetBounds(14, 98, 150, 26);
        _inputButton.Text = "输入";
        _inputButton.SetBounds(170, 95, 70, 30);
        _inputButton.Click += OnInputClick;
        _echoLabel.Text = "（回显）";
        _echoLabel.SetBounds(250, 101, 230, 20);

        // 100 项列表：滚动（ui_scroll）与双击计数（ui_invoke doubleClick）锚点
        _listBox.SetBounds(14, 134, 330, 280);
        _listBox.HorizontalScrollbar = false;
        for (var i = 0; i < 100; i++) _listBox.Items.Add($"Item {i}");
        _listBox.DoubleClick += OnListDoubleClick;

        _doubleClickLabel.Text = "双击:0";
        _doubleClickLabel.SetBounds(356, 140, 130, 20);

        Controls.AddRange(new Control[] { _toggleState, _countButton, _inputButton, _inputBox, _listBox, _rightClickLabel, _countLabel, _doubleClickLabel, _echoLabel });
    }

    /// <summary>切换 手动↔自动 状态（左键；uia 断点/编排锚点）。</summary>
    private void OnToggleState(object? sender, EventArgs e)
    {
        _state = _state == "手动" ? "自动" : "手动";
        _toggleState.Text = _state;
    }

    /// <summary>右键点击切换按钮 → 右侧状态 Label 变化（右键 e2e 锚点）。</summary>
    private void OnToggleStateRightClick(object? sender, MouseEventArgs e)
    {
        if (e.Button == MouseButtons.Right) _rightClickLabel.Text = "右键:1";
    }

    private void OnCountClick(object? sender, EventArgs e)
    {
        _countLabel.Text = _countLabel.Text.EndsWith("0", StringComparison.Ordinal) ? "计数:1" : "计数:0";
    }

    private void OnInputClick(object? sender, EventArgs e)
    {
        _echoLabel.Text = string.IsNullOrWhiteSpace(_inputBox.Text) ? "（空输入）" : _inputBox.Text;
    }

    /// <summary>双击列表项 → 双击计数 Label 变化（double-click e2e 锚点）。</summary>
    private void OnListDoubleClick(object? sender, EventArgs e)
    {
        _doubleClickLabel.Text = "双击:1";
    }
}
