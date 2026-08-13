using System;
using System.Collections.Generic;
using System.Drawing;

namespace NayfWindows;

/// <summary>The voice pipeline state shown in the overlay and panel UI.</summary>
public enum CompanionVoiceState
{
    Idle,
    Listening,
    Processing,
    Responding,

    /// <summary>
    /// A walkthrough has shown a step and is waiting for the user to perform it.
    ///
    /// Its own state rather than a flavour of <see cref="Responding"/>, because the
    /// push-to-talk entry guard has to let a spoken answer *in* here — and route it to the
    /// paused walkthrough — where every other busy state turns one away.
    /// </summary>
    AwaitingUserStep
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
    // Asynchronous continuations: whoever settles this is on the UI thread — a button
    // click, or a barge-in unwinding the turn from inside the keyboard hook — and the
    // waiter is the agent loop, which would otherwise resume the whole of its remaining
    // work right there on top of them.
    public TaskCompletionSource<bool> CompletionSource { get; } =
        new(TaskCreationOptions.RunContinuationsAsynchronously);
}

/// <summary>What finishes a walkthrough step.</summary>
public enum WalkthroughWaitFor
{
    /// <summary>
    /// The step is a single click on a known spot, so Nayf watches for that click and
    /// carries on by itself.
    /// </summary>
    Click,

    /// <summary>
    /// A drag, some typing, anything with no one click to watch for — the user says when
    /// they are done.
    /// </summary>
    Continue
}

/// <summary>
/// One step of a guided walkthrough: the single thing the user is being asked to do, and
/// everything needed to show it to them.
///
/// Coordinates are virtual-screen pixels. Claude works in the pixel space of the screenshot
/// it was given; <see cref="NayfAgentManager"/> maps them across on the way here, because it
/// is the only place that knows which screenshot the numbers were read off.
/// </summary>
public class WalkthroughStep
{
    /// <summary>Where the user has to click, and the centre the click test measures from.</summary>
    public PointF ClickPoint { get; init; }

    /// <summary>
    /// The target's visual bounds, when Claude gave a size. Null when it only gave a point,
    /// and the outline falls back to a small ring around it.
    /// </summary>
    public RectangleF? TargetBounds { get; init; }

    /// <summary>A couple of words naming the target, drawn in the chip beside the outline.</summary>
    public string Label { get; init; } = "";

    /// <summary>The one sentence to say out loud — Claude's narration for this step.</summary>
    public string? Spoken { get; init; }

    public WalkthroughWaitFor WaitFor { get; init; }

    /// <summary>Where a drag ends. Set only for a drag, and draws the arrow.</summary>
    public PointF? DragTo { get; init; }
}

/// <summary>How a walkthrough step ended.</summary>
public enum WalkthroughResumeReason
{
    /// <summary>The user clicked the thing that was pointed at.</summary>
    Clicked,
    Spoke,
    Typed
}

/// <summary>
/// What the user did about a step. The transcript is passed to Claude as-is rather than
/// being classified here — see <see cref="NayfAgentManager"/>.
/// </summary>
public record WalkthroughReply(WalkthroughResumeReason Reason, string? Transcript = null);

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
