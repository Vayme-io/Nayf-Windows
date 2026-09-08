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

// There is deliberately no AgentStep type here any more, and no step list in the panel.
// It showed a row per screenshot taken and per round trip with the model, each carrying the
// first hundred characters of what the tool returned — the instructions written for the
// model, and on a walkthrough step the user's own words quoted back at them. What Vayme is
// doing belongs in the status pill; what Vayme has to say belongs in the panel; how it got
// there belongs in the log.

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
    /// The step is a single click on a known spot, so Vayme watches for that click and
    /// carries on by itself.
    /// </summary>
    Click,

    /// <summary>
    /// The step is a right-click on a known spot — opening a context menu, nearly always.
    ///
    /// Watched the same way as <see cref="Click"/> and separately from it, so that the button
    /// the user was asked for is the button that finishes the step. Right-clicking used to be
    /// invisible: the hook only ever matched the left button, so the step sat on "your turn"
    /// with the context menu already open in front of it.
    /// </summary>
    RightClick,

    /// <summary>
    /// The step is to point at something without clicking it — a menu that opens on hover, a
    /// tooltip, a preview — so Vayme watches for the cursor coming to rest on the target.
    ///
    /// Separate from <see cref="Click"/> because a hover step used to fall into it, and then
    /// the only way past was to click the very thing the user had just been told not to click.
    /// </summary>
    Hover,

    /// <summary>
    /// The step is one keystroke — "press M to open the map" — so Vayme watches for that one
    /// key and carries on by itself.
    ///
    /// Separate from <see cref="Continue"/> because a keyboard step used to fall into it, and
    /// then nothing on screen ever registered that the user had pressed the key: the step sat
    /// on "your turn" while they carried on playing.
    /// </summary>
    Key,

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
    /// The target's visual bounds, when Claude gave a size that could be believed. Null when
    /// it gave none or gave one that could not be read off that screenshot, and the outline
    /// falls back to a ring around the point.
    /// </summary>
    public RectangleF? TargetBounds { get; init; }

    /// <summary>A couple of words naming the target, drawn in the chip beside the outline.</summary>
    public string Label { get; init; } = "";

    /// <summary>The one sentence to say out loud — Claude's narration for this step.</summary>
    public string? Spoken { get; init; }

    public WalkthroughWaitFor WaitFor { get; init; }

    /// <summary>
    /// The key that finishes the step, as the user would say it — "M", "Enter", "F5". Set
    /// only when <see cref="WaitFor"/> is <see cref="WalkthroughWaitFor.Key"/>, and shown in
    /// the step's chip so the user can see what they are being asked to press.
    /// </summary>
    public string? WaitKey { get; init; }

    /// <summary>
    /// <see cref="WaitKey"/> as a virtual-key code — the only form the keyboard hook can
    /// match against. Zero when the key wasn't one we recognise, which is what turns the step
    /// back into a <see cref="WalkthroughWaitFor.Continue"/> rather than one that can never
    /// finish.
    /// </summary>
    public uint WaitKeyCode { get; init; }

    /// <summary>Where a drag ends. Set only for a drag, and draws the arrow.</summary>
    public PointF? DragTo { get; init; }
}

/// <summary>How a walkthrough step ended.</summary>
public enum WalkthroughResumeReason
{
    /// <summary>The user clicked the thing that was pointed at.</summary>
    Clicked,

    /// <summary>The user pressed the key the step asked for.</summary>
    Pressed,

    /// <summary>The user right-clicked the thing that was pointed at.</summary>
    RightClicked,

    /// <summary>The user rested the cursor on the thing that was pointed at, without clicking.</summary>
    Hovered,

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
