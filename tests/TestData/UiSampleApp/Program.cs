// U1A UI 自动化测试目标（WinForms，net10.0-windows）：供 ui_find/ui_action/ui_input/ui_get/ui_wait 与 debug_* 编排的 e2e 锚点。
// 控件覆盖各 UIA pattern：Button/Menu(invoke)、CheckBox(toggle)、ComboBox(expandcollapse)、ListBox/ListItem(selectionitem)、
// TrackBar(rangevalue)、TextBox(value + 只读)、RichTextBox(尽量暴露 scroll)、MenuItem(Legacy)。
// 窗口标题 UiSample；控件名（AutomationId）/文本/处理器方法名即测试契约，改动前看 DebugUiToolsTests.cs。
// 处理器方法为 debug_breakpoint_set typeName+memberName 断点锚点——勿改方法名。
using System.Drawing;
using System.Runtime.InteropServices;
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
    private readonly TextBox _readOnlyBox = new();
    private readonly TextBox _passwordBox = new();
    private readonly ListBox _listBox = new();
    private readonly CheckBox _checkBox = new();
    private readonly ComboBox _comboBox = new();
    private readonly TrackBar _trackBar = new();
    private readonly VScrollBar _vScroll = new();
    private readonly ProgressBar _progress = new();
    private readonly RichTextBox _richBox = new();
    private readonly MenuStrip _menu = new();
    private readonly Label _rightClickLabel = new();
    private readonly Label _countLabel = new();
    private readonly Label _doubleClickLabel = new();
    private readonly Label _echoLabel = new();
    private readonly Label _checkLabel = new();
    private readonly Label _comboLabel = new();
    private readonly Label _rangeLabel = new();
    private readonly Label _scrollLabel = new();
    private readonly Label _menuLabel = new();
    private string _state = "手动";

    public MainForm()
    {
        Text = "UiSample";
        ClientSize = new Size(760, 520);
        MinimumSize = ClientSize;
        StartPosition = FormStartPosition.CenterScreen;
        FormBorderStyle = FormBorderStyle.FixedSingle;
        Font = new Font("Microsoft YaHei UI", 9F);

        // 状态切换按钮（invoke；右键切换另一 Label 状态——保留为历史 UI 契约）
        _toggleState.Name = "toggleState";
        _toggleState.Text = _state;
        _toggleState.SetBounds(14, 14, 100, 32);
        _toggleState.Click += OnToggleState;

        _rightClickLabel.Text = "右键:0";
        _rightClickLabel.SetBounds(126, 22, 140, 20);

        // 计数按钮（invoke + focus + 断点锚点）
        _countButton.Name = "countButton";
        _countButton.Text = "计数";
        _countButton.SetBounds(14, 56, 100, 32);
        _countButton.Click += OnCountClick;
        _countLabel.Text = "计数:0";
        _countLabel.SetBounds(126, 64, 140, 20);

        // 输入框（ValuePattern）+ 输入按钮
        _inputBox.Name = "inputBox";
        _inputBox.Text = "sample text";
        _inputBox.SetBounds(14, 98, 150, 26);
        _inputButton.Name = "inputButton";
        _inputButton.Text = "输入";
        _inputButton.SetBounds(170, 95, 70, 30);
        _inputButton.Click += OnInputClick;
        _echoLabel.Text = "（回显）";
        _echoLabel.SetBounds(250, 101, 230, 20);

        // 只读 TextBox（ui_input 只读拒绝锚点）
        _readOnlyBox.Name = "readOnlyBox";
        _readOnlyBox.Text = "只读文本";
        _readOnlyBox.ReadOnly = true;
        _readOnlyBox.SetBounds(500, 98, 150, 26);

        // 敏感名 TextBox（DB1：uiAssert 失败实际值脱敏锚点；AutomationId=password 命中敏感名规则）
        _passwordBox.Name = "password";
        _passwordBox.Text = "secret-value";
        _passwordBox.SetBounds(500, 14, 150, 26);

        // 复选框（TogglePattern）
        _checkBox.Name = "checkBox";
        _checkBox.Text = "启用";
        _checkBox.SetBounds(14, 132, 100, 26);
        _checkBox.CheckedChanged += OnToggleCheck;
        _checkLabel.Text = "勾选:False";
        _checkLabel.SetBounds(126, 136, 140, 20);

        // 下拉框（ExpandCollapsePattern；项 SelectionItem）
        _comboBox.Name = "comboBox";
        _comboBox.DropDownStyle = ComboBoxStyle.DropDownList;
        _comboBox.SetBounds(280, 130, 140, 26);
        _comboBox.Items.AddRange(new object[] { "选项A", "选项B", "选项C" });
        _comboBox.SelectedIndexChanged += OnComboChanged;
        _comboLabel.Text = "选择:（无）";
        _comboLabel.SetBounds(430, 136, 200, 20);

        // 滑块（RangeValuePattern）
        _trackBar.Name = "trackBar";
        _trackBar.Minimum = 0;
        _trackBar.Maximum = 100;
        _trackBar.Value = 30;
        _trackBar.SetBounds(14, 168, 300, 40);
        _trackBar.Scroll += OnTrackScroll;
        _rangeLabel.Text = "范围:30";
        _rangeLabel.SetBounds(330, 174, 200, 20);

        // 独立滚动条（RangeValuePattern 锚点：TrackBar 经 MSAA-UIA 桥暴露的是 ValuePattern）
        _vScroll.Name = "vscrollBar";
        _vScroll.Minimum = 0;
        _vScroll.Maximum = 100;
        _vScroll.Value = 20;
        _vScroll.SetBounds(540, 168, 20, 100);
        _vScroll.ValueChanged += OnVScrollChanged;
        _scrollLabel.Text = "滑块:20";
        _scrollLabel.SetBounds(570, 174, 120, 20);

        // 进度条（RangeValuePattern 只读锚点）
        _progress.Name = "progressBar";
        _progress.Minimum = 0;
        _progress.Maximum = 100;
        _progress.Value = 40;
        _progress.SetBounds(690, 168, 60, 24);

        // 列表（SelectionItem / ScrollItem 锚点）
        _listBox.Name = "listBox";
        _listBox.SetBounds(14, 214, 330, 250);
        _listBox.HorizontalScrollbar = false;
        for (var i = 0; i < 100; i++) _listBox.Items.Add($"Item {i}");
        _listBox.SelectedIndexChanged += OnListSelected;
        _listBox.DoubleClick += OnListDoubleClick;
        _doubleClickLabel.Text = "双击:0";
        _doubleClickLabel.SetBounds(356, 220, 130, 20);

        // 多行富文本（尽量暴露 ScrollPattern 的可滚动容器）
        _richBox.Name = "richBox";
        _richBox.SetBounds(360, 250, 380, 214);
        _richBox.ScrollBars = RichTextBoxScrollBars.Vertical;
        for (var i = 0; i < 200; i++) _richBox.AppendText($"行 {i}\n");

        // 菜单（MenuBar/MenuItem，invoke/Legacy 锚点）
        var actionMenu = new ToolStripMenuItem("动作");
        var commandItem = new ToolStripMenuItem("命令") { Name = "menuCommand" };
        commandItem.Click += OnMenuCommand;
        actionMenu.DropDownItems.Add(commandItem);
        _menu.Items.Add(actionMenu);
        _menu.SetBounds(0, 0, 760, 24);
        _menuLabel.Text = "菜单:0";
        _menuLabel.SetBounds(500, 220, 140, 20);

        Controls.AddRange(new Control[]
        {
            _menu, _toggleState, _countButton, _inputButton, _inputBox, _readOnlyBox, _passwordBox,
            _listBox, _checkBox, _checkLabel, _comboBox, _comboLabel, _trackBar, _rangeLabel,
            _vScroll, _scrollLabel, _progress,
            _richBox, _rightClickLabel, _countLabel, _doubleClickLabel, _echoLabel, _menuLabel,
        });
    }

    // ===== 仅测试目标：debug_verify 的 LaunchAndAttachAsync 以 WindowStyle.Hidden 起进程，UIA 读不到顶层窗口；
    // 首窗显示时强制 ShowWindow(SW_SHOW) 绕过隐藏启动（不改 Engine/Session）。
    private const int SW_SHOW = 5;

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    protected override void OnShown(EventArgs e)
    {
        base.OnShown(e);
        try { ShowWindow(Handle, SW_SHOW); } catch { /* 非 Windows/失败忽略 */ }
    }

    /// <summary>切换 手动↔自动 状态（invoke；uia/断点编排锚点）。</summary>
    private void OnToggleState(object? sender, EventArgs e)
    {
        _state = _state == "手动" ? "自动" : "手动";
        _toggleState.Text = _state;
    }

    private void OnCountClick(object? sender, EventArgs e)
    {
        _countLabel.Text = _countLabel.Text.EndsWith("0", StringComparison.Ordinal) ? "计数:1" : "计数:0";
    }

    private void OnInputClick(object? sender, EventArgs e)
    {
        _echoLabel.Text = string.IsNullOrWhiteSpace(_inputBox.Text) ? "（空输入）" : _inputBox.Text;
    }

    /// <summary>复选框切换 → 勾选 Label 变化（toggle e2e 锚点）。</summary>
    private void OnToggleCheck(object? sender, EventArgs e)
    {
        _checkLabel.Text = "勾选:" + _checkBox.Checked;
    }

    /// <summary>下拉选择 → 选择 Label（select/expand e2e 锚点）。</summary>
    private void OnComboChanged(object? sender, EventArgs e)
    {
        _comboLabel.Text = "选择:" + (_comboBox.SelectedItem?.ToString() ?? "（无）");
    }

    /// <summary>滑块拖动 → 范围 Label（rangevalue e2e 锚点）。</summary>
    private void OnTrackScroll(object? sender, EventArgs e)
    {
        _rangeLabel.Text = "范围:" + _trackBar.Value;
    }

    /// <summary>独立滚动条 → 滑块 Label（RangeValuePattern e2e 锚点）。</summary>
    private void OnVScrollChanged(object? sender, EventArgs e)
    {
        _scrollLabel.Text = "滑块:" + _vScroll.Value;
    }

    private void OnListSelected(object? sender, EventArgs e)
    {
        _doubleClickLabel.Text = "选中:" + (_listBox.SelectedItem?.ToString() ?? "（无）");
    }

    /// <summary>双击列表项（保留处理器名；U1A 不再有物理双击入口）。</summary>
    private void OnListDoubleClick(object? sender, EventArgs e)
    {
        _doubleClickLabel.Text = "双击:1";
    }

    /// <summary>菜单命令 → 菜单 Label（invoke/Legacy e2e 锚点）。</summary>
    private void OnMenuCommand(object? sender, EventArgs e)
    {
        _menuLabel.Text = "菜单:1";
    }
}
