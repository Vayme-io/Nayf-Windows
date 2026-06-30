using System;
using System.Collections.Generic;

namespace NayfWindows;

/// <summary>The voice pipeline state shown in the overlay and panel UI.</summary>
public enum CompanionVoiceState
{
    Idle,
    Listening,
    Processing,
    Responding
}

/// <summary>
/// One conversation exchange stored in session history so Claude can
/// reference prior turns within the same session.
/// </summary>
public record ConversationTurn(
    string UserTranscript,
    string AssistantResponse,
    byte[]? AttachedImageData = null
);

/// <summary>
/// A screenshot captured from one connected display. Carries the encoded image
/// size and the monitor's native bounds (in virtual-desktop coordinates) so a
/// point Claude reports in image space can be mapped back to the real screen.
/// </summary>
public record CapturedScreenshot(
    byte[] ImageData,
    int ScreenIndex,
    string ScreenLabel,
    int ImageWidth,
    int ImageHeight,
    int MonitorLeft,
    int MonitorTop,
    int MonitorWidth,
    int MonitorHeight
);

/// <summary>
/// A tool call received from Claude during a streaming agentic turn.
/// </summary>
public class AgentParsedToolCall
{
    public string ToolUseId { get; init; } = "";
    public string ToolName { get; init; } = "";
    public Dictionary<string, object> InputJson { get; init; } = new();
}

/// <summary>Complete result of one Claude agent turn.</summary>
public class AgentTurnResult
{
    public string TextContent { get; init; } = "";
    public List<AgentParsedToolCall> ToolCalls { get; init; } = new();
    /// <summary>"end_turn" or "tool_use"</summary>
    public string StopReason { get; init; } = "end_turn";
}

/// <summary>One step in an agentic task, displayed live in the panel UI.</summary>
public class AgentStep : System.ComponentModel.INotifyPropertyChanged
{
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;

    private string _stepLabel = "";
    public string StepLabel
    {
        get => _stepLabel;
        set { _stepLabel = value; OnChanged(nameof(StepLabel)); }
    }

    private AgentStepStatus _status = AgentStepStatus.Pending;
    public AgentStepStatus Status
    {
        get => _status;
        set { _status = value; OnChanged(nameof(Status)); OnChanged(nameof(StatusGlyph)); }
    }

    private string? _outputPreview;
    public string? OutputPreview
    {
        get => _outputPreview;
        set { _outputPreview = value; OnChanged(nameof(OutputPreview)); OnChanged(nameof(HasOutput)); }
    }

    public bool HasOutput => !string.IsNullOrEmpty(OutputPreview);

    /// <summary>A small status indicator glyph for the panel list.</summary>
    public string StatusGlyph => Status switch
    {
        AgentStepStatus.Running => "•",
        AgentStepStatus.Completed => "✓",
        AgentStepStatus.Failed => "✕",
        AgentStepStatus.AwaitingConfirmation => "?",
        _ => "•"
    };

    private void OnChanged(string name)
        => PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(name));
}

public enum AgentStepStatus
{
    Pending,
    Running,
    Completed,
    Failed,
    AwaitingConfirmation
}

/// <summary>
/// A destructive agent command that requires the user to approve before executing.
/// </summary>
public class AgentConfirmationRequest
{
    public required string CommandDescription { get; init; }
    public required string CommandText { get; init; }
    public TaskCompletionSource<bool> CompletionSource { get; } = new();
}

/// <summary>
/// Claude response segment types — used to parse POINT tags and WRITTEN blocks.
/// </summary>
public enum ResponseSegmentKind
{
    Text,
    PointTag,
    WrittenBlock
}

public class ResponseSegment
{
    public ResponseSegmentKind Kind { get; init; }
    public string Content { get; init; } = "";
    /// <summary>Parsed x coordinate (0–1 normalized) from a POINT tag.</summary>
    public double PointX { get; init; }
    /// <summary>Parsed y coordinate (0–1 normalized) from a POINT tag.</summary>
    public double PointY { get; init; }
    public string PointLabel { get; init; } = "";
    public int ScreenIndex { get; init; }
}
