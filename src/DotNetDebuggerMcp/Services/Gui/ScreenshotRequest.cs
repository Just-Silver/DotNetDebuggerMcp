using SharpSight.Capture;

namespace DotNetDebuggerMcp.Services.Gui;

internal sealed record ScreenshotRequest(
    string Mode = "auto", string Display = "", int ProcessId = 0, string WindowTitle = "", string Hwnd = "",
    bool ClientArea = false, string Region = "", string Element = "", int FrameId = 0,
    bool IncludeCursor = false, int TimeoutSeconds = 5, string FilePath = "", string Contains = "");

internal sealed record ScreenshotExecution(CaptureResult? Image, string TargetLine, string Mode,
    IReadOnlyList<string> Notes, int ProcessId, string? Text = null, bool IsError = false,
    string TargetHwnd = "");
