namespace DotNetDebuggerMcp.Services.Gui;

internal sealed record UiOperationRequest(
    string Process = "", string Title = "", string Text = "", string Type = "", string AutomationId = "",
    int Limit = 50, int Scope = -1, int Depth = 0, int TimeoutSeconds = 5,
    string Verb = "", int Index = -1, string Name = "", int FrameId = 0, string Direction = "",
    int Lines = 0, string WindowState = "", bool ClickPoint = false,
    string Value = "", bool Commit = true, string What = "", string TextChangedFrom = "", string TextChangedTo = "");
