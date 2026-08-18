using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Drawing;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace NayfWindows;

/// <summary>
/// Orchestrates the agentic tool-use loop — calls Claude, executes tool calls,
/// feeds results back, and loops until Claude returns "end_turn".
/// Mirrors NayfAgentManager.swift.
/// </summary>
public sealed class NayfAgentManager : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<AgentStep> AgentSteps { get; } = new();

    private volatile string? _runningToolLabel;

    /// <summary>
    /// What Nayf is doing right now, in words the user would use — "Reading a file" —
    /// or null when no tool is running. Kept as a plain volatile string rather than
    /// read off <see cref="AgentSteps"/> because the status pill polls it from its own
    /// render thread, and the step list belongs to the UI thread alone.
    /// </summary>
    public string? RunningToolLabel => _runningToolLabel;

    private AgentConfirmationRequest? _pendingConfirmationRequest;
    public AgentConfirmationRequest? PendingConfirmationRequest
    {
        get => _pendingConfirmationRequest;
        private set { _pendingConfirmationRequest = value; OnPropertyChanged(); }
    }

    private readonly ClaudeAPI _claudeAPI;
    private readonly NayfAgentToolExecutor _toolExecutor;
    private readonly DispatcherQueue _dispatcherQueue;

    /// <summary>
    /// A read-only screenshot — the only way any turn sees the screen.
    ///
    /// Declared above the lists that use it: these are static field initializers, which run
    /// in the order they are written, so a list built before this one is assigned holds a
    /// null where the tool should be.
    /// </summary>
    private static readonly object TakeScreenshotTool = new
    {
        name = "take_screenshot",
        description = "Capture the user's current screen so you can see where they are before giving the next step. Read-only — it changes nothing. Coordinates you read off this image are the same pixel space you use when you point at something.",
        input_schema = new
        {
            type = "object",
            properties = new { },
            required = Array.Empty<string>()
        }
    };

    private static readonly List<object> AgentTools = new()
    {
        new
        {
            name = "bash",
            description = "Execute a PowerShell command on the Windows system. Use for file operations, running programs, querying system state, etc.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    command = new { type = "string", description = "The PowerShell command to execute" }
                },
                required = new[] { "command" }
            }
        },
        new
        {
            name = "read_file",
            description = "Read the contents of a file on the filesystem.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Absolute or relative path to the file" }
                },
                required = new[] { "path" }
            }
        },
        new
        {
            name = "write_file",
            description = "Write content to a file on the filesystem. Creates parent directories if needed.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    path = new { type = "string", description = "Path to write to" },
                    content = new { type = "string", description = "File content to write" }
                },
                required = new[] { "path", "content" }
            }
        },
        // Looking, in place of the old "computer" tool. That one bundled a screenshot
        // together with click, type and key, and a model handed it reaches for the whole
        // thing — which is how Nayf ended up moving the user's cursor. It is not offered
        // to any turn now: Nayf sees the screen, and the user does the clicking.
        TakeScreenshotTool,
        new
        {
            name = "spotify_play",
            description = "Play a specific song, album, or artist on Spotify by name. This is the RELIABLE way to play music on Spotify — it searches the Spotify catalog for the best match and plays that exact track in the Spotify desktop app, launching Spotify if needed. ALWAYS use this for \"play <song/artist> on spotify\" requests instead of opening Spotify and navigating its UI by clicking.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    query = new { type = "string", description = "The song, album, or artist to play, e.g. 'Go by The Chemical Brothers'" }
                },
                required = new[] { "query" }
            }
        },
        new
        {
            name = "google_calendar_list_events",
            description = "List upcoming events from the user's Google Calendar (their primary calendar). Use this for questions like \"what's on my calendar\", \"what does my day look like\", \"am I free tomorrow afternoon\", \"when's my next meeting\". Defaults to the next 7 days. To narrow to a specific day or range, pass timeMin/timeMax as ISO 8601 with the user's timezone offset — e.g. for \"tomorrow\" use the start and end of tomorrow in their local time. This reads the user's CONNECTED Google account (different from the Windows Calendar app). If they haven't connected Google Calendar, the call returns an error telling you to ask them to connect it in Nayf.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    timeMin = new { type = "string", description = "ISO 8601 start of the range with timezone offset (e.g. 2026-06-21T00:00:00+02:00). Optional; defaults to now." },
                    timeMax = new { type = "string", description = "ISO 8601 end of the range. Optional; defaults to 7 days from now." },
                    maxResults = new { type = "number", description = "Max events to return, 1–50. Optional; defaults to 10." },
                    query = new { type = "string", description = "Optional free-text filter (a person's name, a keyword)." }
                },
                required = Array.Empty<string>()
            }
        },
        new
        {
            name = "google_calendar_create_event",
            description = "Create an event on the user's Google Calendar (their primary calendar). Use for \"schedule…\", \"add to my calendar\", \"book…\", \"put a meeting on…\". Provide startDateTime and endDateTime as ISO 8601 with the user's local timezone offset. If the user gives no end time, default endDateTime to one hour after the start. This writes to the user's CONNECTED Google account. If they haven't connected Google Calendar, the call returns an error telling you to ask them to connect it.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    summary = new { type = "string", description = "Event title, e.g. \"Lunch with Sara\"" },
                    startDateTime = new { type = "string", description = "ISO 8601 start with timezone offset, e.g. 2026-06-21T12:00:00+02:00" },
                    endDateTime = new { type = "string", description = "ISO 8601 end with timezone offset. Default to one hour after start if the user didn't specify." },
                    description = new { type = "string", description = "Optional notes/details for the event." },
                    location = new { type = "string", description = "Optional location." }
                },
                required = new[] { "summary", "startDateTime", "endDateTime" }
            }
        },
        new
        {
            name = "google_calendar_delete_event",
            description = "Delete an event from the user's Google Calendar by its event id. First call google_calendar_list_events to find the event the user means and read its \"id\" field, then pass that id here. Deleting can't be undone, so unless the user already named the exact event to delete, CONFIRM first: tell them which event (title + time) you're about to delete and get a quick yes before calling this.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    eventId = new { type = "string", description = "The Google Calendar event id — the \"id\" field returned by google_calendar_list_events." }
                },
                required = new[] { "eventId" }
            }
        },
        new
        {
            name = "github_list_issues",
            description = "List the open GitHub issues assigned to the user, across all their repositories. Use for \"what issues are assigned to me\", \"what's on my plate on GitHub\", \"any open issues for me\". Reads the user's CONNECTED GitHub account; if not connected, the call returns an error telling you to ask them to connect it in Nayf.",
            input_schema = new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }
        },
        new
        {
            name = "github_list_pull_requests",
            description = "List the open GitHub pull requests authored by the user, across all repositories. Use for \"what PRs do I have open\", \"my open pull requests\". Reads the user's CONNECTED GitHub account.",
            input_schema = new
            {
                type = "object",
                properties = new { },
                required = Array.Empty<string>()
            }
        },
        new
        {
            name = "github_create_issue",
            description = "Open a new GitHub issue in a repository. Use for \"file an issue\", \"open a bug on <repo>\", \"create an issue in <owner>/<repo>\". You must know the owner and repo; if the user only gives a repo name, ask which owner/org, or infer it if obvious from context. Writes to the user's CONNECTED GitHub account.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    owner = new { type = "string", description = "Repository owner — the user or organization, e.g. \"Vayme-io\"" },
                    repo = new { type = "string", description = "Repository name, e.g. \"Nayf\"" },
                    title = new { type = "string", description = "Issue title" },
                    body = new { type = "string", description = "Optional issue body / description (Markdown allowed)." }
                },
                required = new[] { "owner", "repo", "title" }
            }
        }
    };

    /// <summary>
    /// Hands one step to the user and waits for them to do it — the whole of a walkthrough,
    /// one call at a time.
    ///
    /// The description spends its length on the two things a model gets wrong here: that the
    /// bounds are drawn literally, so a guessed size is a visibly wrong box on someone's
    /// screen; and that the call blocks, so there is no reason to queue up three steps.
    /// </summary>
    private static readonly object RequestUserStepTool = new
    {
        name = "request_user_step",
        description =
            "Show the user ONE thing to do, then stop and wait for them to do it. Your cursor " +
            "flies to (x, y), an outline traces around the bounds you give, and the sentence you " +
            "write alongside this call is spoken aloud — so write that sentence as one short " +
            "spoken instruction. This call does not return until the user has done the step or " +
            "said something back, so never call it twice in one message and never put two " +
            "actions in one step.",
        input_schema = new
        {
            type = "object",
            properties = new
            {
                x = new
                {
                    type = "integer",
                    description = "Click point X, in the pixel space of the screenshot you are looking at — the centre of the thing they should click."
                },
                y = new
                {
                    type = "integer",
                    description = "Click point Y, in that same screenshot pixel space."
                },
                w = new
                {
                    type = "integer",
                    description = "The target's visual width in screenshot pixels, including its padding — the whole button, not just its text. The outline is traced in exactly these bounds, so a wrong size looks wrong on screen. Omit only if you genuinely cannot tell."
                },
                h = new
                {
                    type = "integer",
                    description = "The target's visual height in screenshot pixels, including its padding."
                },
                label = new
                {
                    type = "string",
                    description = "Two or three words naming the target, e.g. \"the export button\". Drawn in a small chip beside the outline — this is not the spoken instruction."
                },
                wait_for = new
                {
                    type = "string",
                    @enum = new[] { "click", "continue" },
                    description = "\"click\" when the step is a single click on (x, y) — that click is noticed and the walkthrough carries on by itself. \"continue\" for a drag, for typing, or for anything with no one click to watch for; the user says when they're done."
                },
                to_x = new
                {
                    type = "integer",
                    description = "Drag destination X, screenshot pixels. Send to_x and to_y together, and only for a real drag — they draw an arrow from the target to there."
                },
                to_y = new
                {
                    type = "integer",
                    description = "Drag destination Y, screenshot pixels."
                }
            },
            required = new[] { "x", "y", "label", "wait_for" }
        }
    };

    /// <summary>
    /// What a teaching turn may call. Deliberately tiny — Nayf looks, points, and waits.
    /// </summary>
    private static readonly List<object> WalkthroughTools = new()
    {
        TakeScreenshotTool, RequestUserStepTool
    };

    /// <summary>
    /// The tools sent with a turn, scoped to what it is allowed to do.
    ///
    /// A walkthrough is a teaching interaction: the user performs every action themselves,
    /// so Nayf gets looking and pointing only. An agent task is automation the user asked
    /// for, and keeps the full set — background tasks legitimately act on the machine.
    ///
    /// Neither set can drive the mouse or the keyboard. An agent task acts through
    /// PowerShell, files and the integration tools; anything on screen that needs a click
    /// is shown to the user, because the user's cursor is theirs.
    /// </summary>
    private static List<object> ToolsFor(NayfToolMode mode) => mode switch
    {
        NayfToolMode.GuidedWalkthrough => WalkthroughTools,
        _ => AgentTools
    };

    /// <summary>
    /// Shows one walkthrough step and returns what the user did about it.
    ///
    /// Set by <see cref="CompanionManager"/>, which owns the cursor, the annotations and the
    /// voice — none of which this class knows anything about. The loop is suspended for as
    /// long as this takes, which is however long the user needs.
    /// </summary>
    public Func<WalkthroughStep, CancellationToken, Task<WalkthroughReply>>? UserStepRequested;

    public NayfAgentManager(ClaudeAPI claudeAPI)
    {
        _claudeAPI = claudeAPI;
        _toolExecutor = new NayfAgentToolExecutor();
        _toolExecutor.ConfirmationRequested += OnConfirmationRequested;
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
    }

    /// <summary>
    /// Runs the agentic loop for a user request. Calls Claude, executes any
    /// tool calls, feeds results back, and loops until Claude returns "end_turn".
    /// Returns Claude's final text response.
    ///
    /// <paramref name="toolMode"/> defaults to the hands-off mode on purpose: forgetting to
    /// pass it can only make Nayf more cautious than the caller meant, never less.
    /// </summary>
    public async Task<string> RunAgentLoopAsync(
        string userRequest,
        List<CapturedScreenshot>? screenshots,
        string systemPrompt,
        string? authToken,
        Action<string>? onTextDelta,
        CancellationToken cancellationToken = default,
        List<ConversationTurn>? conversationHistory = null,
        NayfToolMode toolMode = NayfToolMode.GuidedWalkthrough,
        byte[]? focusRegionImage = null)
    {
        UpdateOnUI(() => AgentSteps.Clear());
        _runningToolLabel = null;

        // Scope what this run may do. The executor refuses every actuation tool in
        // walkthrough mode, so Nayf cannot touch the screen even if the model asks.
        _toolExecutor.ToolMode = toolMode;
        var tools = ToolsFor(toolMode);

        var messages = BuildInitialMessages(userRequest, screenshots, conversationHistory, focusRegionImage);
        var fullFinalText = "";
        var lastAssistantText = "";
        var executedAnyTool = false;

        // A walkthrough spends two iterations on every step — look, then hand the step over —
        // so the agent-task limit would cut one off after seven steps. That limit is a
        // runaway guard for a loop nobody is watching; a walkthrough is paced by the user,
        // who is right there and can stop it by saying so.
        int maxIterations = toolMode == NayfToolMode.GuidedWalkthrough ? 40 : 15;

        for (int iteration = 0; iteration < maxIterations; iteration++)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var stepLabel = iteration == 0 ? "Thinking…" : $"Step {iteration + 1}…";
            var currentStep = AddStep(stepLabel, AgentStepStatus.Running);

            AgentTurnResult turnResult;
            try
            {
                turnResult = await _claudeAPI.ExecuteAgentTurnAsync(
                    messages,
                    tools,
                    systemPrompt,
                    authToken,
                    onTextDelta,
                    cancellationToken);
            }
            catch (Exception ex)
            {
                UpdateStep(currentStep, AgentStepStatus.Failed, ex.Message);
                throw;
            }

            // Add Claude's response to the message history
            var assistantContent = new List<object>();
            if (!string.IsNullOrEmpty(turnResult.TextContent))
            {
                assistantContent.Add(new { type = "text", text = turnResult.TextContent });
                lastAssistantText = turnResult.TextContent; // keep the latest narration
            }

            foreach (var toolCall in turnResult.ToolCalls)
            {
                assistantContent.Add(new
                {
                    type = "tool_use",
                    id = toolCall.ToolUseId,
                    name = toolCall.ToolName,
                    input = toolCall.InputJson
                });
            }

            messages.Add(new { role = "assistant", content = assistantContent });

            if (turnResult.StopReason == "end_turn" || turnResult.ToolCalls.Count == 0)
            {
                fullFinalText = turnResult.TextContent;
                UpdateStep(currentStep, AgentStepStatus.Completed);
                break;
            }

            // Execute all tool calls and collect results
            UpdateStep(currentStep, AgentStepStatus.Completed);
            var toolResults = new List<object>();

            foreach (var toolCall in turnResult.ToolCalls)
            {
                // A step is the user's turn, not Nayf's. It runs through the loop like a tool
                // because that is exactly what it is to the model — a call that returns an
                // answer — but nothing about it is Nayf doing something, so it gets no
                // "Running…" label and the pill says whose turn it is instead.
                bool isUserStep = toolMode == NayfToolMode.GuidedWalkthrough
                                  && toolCall.ToolName == "request_user_step";

                var toolStep = AddStep(
                    isUserStep ? "Waiting for you" : $"Running: {toolCall.ToolName}",
                    AgentStepStatus.Running);
                if (!isUserStep) _runningToolLabel = DescribeTool(toolCall.ToolName);
                executedAnyTool = true;
                AgentToolResult toolResult;

                try
                {
                    toolResult = isUserStep
                        ? await AwaitUserStepAsync(toolCall, screenshots, turnResult.TextContent, cancellationToken)
                        : await _toolExecutor.ExecuteToolAsync(toolCall, cancellationToken, authToken);
                    UpdateStep(toolStep, AgentStepStatus.Completed,
                        toolResult.Text.Length > 100 ? toolResult.Text[..100] + "…" : toolResult.Text);
                }
                catch (OperationCanceledException)
                {
                    // The user interrupted. Let it unwind — feeding "Tool error: the operation
                    // was canceled" back to the model would have it carry on regardless, which
                    // during a walkthrough means the next step arriving over the interruption.
                    UpdateStep(toolStep, AgentStepStatus.Failed, "Stopped");
                    throw;
                }
                catch (Exception ex)
                {
                    toolResult = AgentToolResult.Message($"Tool error: {ex.Message}");
                    UpdateStep(toolStep, AgentStepStatus.Failed, ex.Message);
                }
                finally
                {
                    _runningToolLabel = null;
                }

                // A screenshot must go back as an image block so Claude can see it;
                // everything else is plain text.
                if (toolResult.ScreenshotJpeg != null)
                {
                    toolResults.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = toolCall.ToolUseId,
                        content = new object[]
                        {
                            new
                            {
                                type = "image",
                                source = new
                                {
                                    type = "base64",
                                    media_type = "image/jpeg",
                                    data = Convert.ToBase64String(toolResult.ScreenshotJpeg)
                                }
                            },
                            new { type = "text", text = toolResult.Text }
                        }
                    });
                }
                else
                {
                    toolResults.Add(new
                    {
                        type = "tool_result",
                        tool_use_id = toolCall.ToolUseId,
                        content = toolResult.Text
                    });
                }
            }

            messages.Add(new { role = "user", content = toolResults });
        }

        // If we hit the step limit without a clean finish, still say something so
        // the user isn't left with silence.
        if (string.IsNullOrWhiteSpace(fullFinalText))
        {
            fullFinalText = string.IsNullOrWhiteSpace(lastAssistantText)
                ? "I couldn't quite finish that — it took more steps than I could complete. Want me to keep going?"
                : lastAssistantText;
        }

        // The Mac chimes as its "task done" pill drops in. Windows has no such pill, so
        // the chime marks the same moment directly: Nayf went and did something and has
        // now finished. Gated on a tool having run, or every spoken answer would chime.
        //
        // Never while teaching. The only tool there is a screenshot, and nothing is done
        // at the end of a step — the user is about to do it.
        if (executedAnyTool && toolMode != NayfToolMode.GuidedWalkthrough)
            NayfSoundPlayer.Shared.PlayTaskComplete();

        return fullFinalText;
    }

    /// <summary>
    /// Hands one step to the user and waits. This is the pause in the middle of a
    /// walkthrough: the loop stops here with its message history intact, for as long as the
    /// user takes, and then the same conversation carries on with their answer as this
    /// call's result. Nothing is restarted and nothing is re-explained from the top.
    /// </summary>
    private async Task<AgentToolResult> AwaitUserStepAsync(
        AgentParsedToolCall toolCall,
        List<CapturedScreenshot>? screenshots,
        string narration,
        CancellationToken cancellationToken)
    {
        var handler = UserStepRequested;
        if (handler == null)
        {
            Logger.Log("Walkthrough", "request_user_step with no presenter attached");
            return AgentToolResult.Message(
                "I can't show a step right now. Explain the remaining steps out loud instead.");
        }

        var shot = ReferenceScreenshot(screenshots);
        if (shot == null)
        {
            return AgentToolResult.Message(
                "I don't have a screenshot to place that on. Call take_screenshot first, then " +
                "point using the coordinates you read off it.");
        }

        var step = BuildWalkthroughStep(toolCall.InputJson, narration, shot);
        if (step == null)
        {
            return AgentToolResult.Message(
                "That step was missing x, y or label. Send the click point in the screenshot's " +
                "pixel space plus a short label, and I'll show it.");
        }

        Logger.Log("Walkthrough",
            $"step '{step.Label}' at {step.ClickPoint.X:0},{step.ClickPoint.Y:0} " +
            $"bounds={step.TargetBounds?.ToString() ?? "none"} waitFor={step.WaitFor}");

        var reply = await handler(step, cancellationToken);
        Logger.Log("Walkthrough", $"step '{step.Label}' resumed by {reply.Reason}");

        // What the user said goes back raw, with instructions for reading it, rather than
        // being classified here. A local keyword list would have to work in every language
        // the user might answer in — and would turn "wait, which window?" into an advance.
        return AgentToolResult.Message(reply.Reason switch
        {
            WalkthroughResumeReason.Clicked =>
                "The user clicked the spot you pointed at. Take a fresh screenshot to see what " +
                "changed, then give the next step.",

            _ =>
                $"The user said: \"{reply.Transcript}\". " +
                "If they indicate they are done, take a fresh screenshot and give the next step. " +
                "If they are confused, stuck, or asking a question, answer it and re-explain " +
                "THIS step differently — do not advance."
        });
    }

    /// <summary>
    /// Turns the model's step arguments into something drawable, in screen coordinates.
    /// Returns null when the call is missing what it needs to point at anything.
    /// </summary>
    private static WalkthroughStep? BuildWalkthroughStep(
        Dictionary<string, object> input, string narration, CapturedScreenshot shot)
    {
        double? imageX = TryGetNumber(input, "x");
        double? imageY = TryGetNumber(input, "y");
        var label = input.TryGetValue("label", out var rawLabel)
            ? rawLabel?.ToString()?.Trim() ?? "" : "";

        if (imageX == null || imageY == null || label.Length == 0) return null;

        // w/h describe the target's visual box centred on the click point.
        RectangleF? bounds = null;
        double? imageW = TryGetNumber(input, "w");
        double? imageH = TryGetNumber(input, "h");
        if (imageW is > 0 && imageH is > 0)
        {
            var topLeft = ToScreenPoint(shot, imageX.Value - imageW.Value / 2, imageY.Value - imageH.Value / 2);
            var bottomRight = ToScreenPoint(shot, imageX.Value + imageW.Value / 2, imageY.Value + imageH.Value / 2);
            bounds = RectangleF.FromLTRB(topLeft.X, topLeft.Y, bottomRight.X, bottomRight.Y);
        }

        double? toX = TryGetNumber(input, "to_x");
        double? toY = TryGetNumber(input, "to_y");
        PointF? dragTo = toX != null && toY != null
            ? ToScreenPoint(shot, toX.Value, toY.Value)
            : null;

        var waitFor = string.Equals(
            input.TryGetValue("wait_for", out var rawWait) ? rawWait?.ToString() : null,
            "continue", StringComparison.OrdinalIgnoreCase)
            ? WalkthroughWaitFor.Continue
            : WalkthroughWaitFor.Click;

        // A drag always waits to be told. Watching for a click would watch the wrong place —
        // the button comes back up at the destination, not at the target — and the user
        // would be left standing on a step they had already finished.
        if (dragTo != null) waitFor = WalkthroughWaitFor.Continue;

        return new WalkthroughStep
        {
            ClickPoint = ToScreenPoint(shot, imageX.Value, imageY.Value),
            TargetBounds = bounds,
            Label = label,
            Spoken = narration,
            WaitFor = waitFor,
            DragTo = dragTo
        };
    }

    /// <summary>
    /// The screenshot a step's coordinates were read off: the last one the model asked for,
    /// or the primary screen captured when the turn started if it hasn't asked yet.
    /// </summary>
    private CapturedScreenshot? ReferenceScreenshot(List<CapturedScreenshot>? turnScreenshots)
    {
        if (_toolExecutor.LastScreenshot is { ImageWidth: > 0, ImageHeight: > 0 } latest)
            return latest;

        if (turnScreenshots == null) return null;
        foreach (var shot in turnScreenshots)
            if (shot is { ScreenIndex: 0, ImageWidth: > 0, ImageHeight: > 0 }) return shot;

        return turnScreenshots.Count > 0 ? turnScreenshots[0] : null;
    }

    /// <summary>
    /// Maps a point the model read off a screenshot into virtual-screen pixels — the space
    /// the cursor and the annotations are drawn in.
    ///
    /// Two corrections, both carried on <see cref="CapturedScreenshot"/>: the image was
    /// downscaled before being sent, and the monitor it came from need not start at the
    /// origin of the virtual desktop.
    /// </summary>
    private static PointF ToScreenPoint(CapturedScreenshot shot, double imageX, double imageY)
    {
        float scaleX = (float)shot.MonitorWidth / shot.ImageWidth;
        float scaleY = (float)shot.MonitorHeight / shot.ImageHeight;
        return new PointF(
            shot.MonitorLeft + (float)imageX * scaleX,
            shot.MonitorTop + (float)imageY * scaleY);
    }

    /// <summary>
    /// Reads a numeric tool argument. Values arrive as <see cref="JsonElement"/>, whose
    /// ToString() is the raw JSON text — so a number parses straight out of it, and anything
    /// else fails cleanly rather than throwing.
    /// </summary>
    private static double? TryGetNumber(Dictionary<string, object> input, string key)
    {
        if (!input.TryGetValue(key, out var raw)) return null;
        var text = raw?.ToString();
        if (string.IsNullOrWhiteSpace(text)) return null;

        return double.TryParse(text, System.Globalization.NumberStyles.Float,
            System.Globalization.CultureInfo.InvariantCulture, out double value)
            ? value
            : null;
    }

    private List<object> BuildInitialMessages(
        string userRequest,
        List<CapturedScreenshot>? screenshots,
        List<ConversationTurn>? conversationHistory = null,
        byte[]? focusRegionImage = null)
    {
        var messages = new List<object>();

        // Prepend prior turns so the agent has conversation context.
        if (conversationHistory != null)
        {
            foreach (var turn in conversationHistory)
            {
                messages.Add(new { role = "user", content = new[] { new { type = "text", text = turn.UserTranscript } } });
                messages.Add(new { role = "assistant", content = new[] { new { type = "text", text = turn.AssistantResponse } } });
            }
        }

        var content = new List<object>();

        // A crop the user circled stands in for the screenshots entirely — the caller sends
        // one or the other, never both. It carries no pixel dimensions because there is
        // nothing to map them onto: coordinates read off a crop mean nothing on the screen,
        // and the label says to answer about the area rather than locate things in it.
        if (focusRegionImage != null)
        {
            content.Add(new
            {
                type = "image",
                source = new
                {
                    type = "base64",
                    media_type = "image/jpeg",
                    data = Convert.ToBase64String(focusRegionImage)
                }
            });
            content.Add(new
            {
                type = "text",
                text = "[A region the user circled on their screen to focus on — answer " +
                       "specifically about THIS cropped area, not the whole screen.]"
            });
        }

        if (screenshots != null)
        {
            foreach (var screenshot in screenshots)
            {
                content.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = "image/jpeg",
                        data = Convert.ToBase64String(screenshot.ImageData)
                    }
                });
                content.Add(new { type = "text", text = $"[{screenshot.ScreenLabel}]" });
            }
        }

        content.Add(new { type = "text", text = userRequest });
        messages.Add(new { role = "user", content });
        return messages;
    }

    /// <summary>
    /// Turns a tool name into something worth reading on screen. The panel's step list
    /// shows the raw name for debugging; the status pill is the only thing the user
    /// sees mid-task, so it says what's happening instead.
    /// </summary>
    private static string DescribeTool(string toolName) => toolName switch
    {
        "take_screenshot" => "Looking at your screen",
        "bash" => "Running a command",
        "read_file" => "Reading a file",
        "write_file" => "Writing a file",
        "spotify_play" => "Playing on Spotify",
        "google_calendar_list_events" => "Checking your calendar",
        "google_calendar_create_event" => "Adding to your calendar",
        "google_calendar_delete_event" => "Clearing your calendar",
        "github_list_issues" => "Checking GitHub issues",
        "github_list_pull_requests" => "Checking pull requests",
        "github_create_issue" => "Opening a GitHub issue",
        _ => "Working"
    };

    private AgentStep AddStep(string label, AgentStepStatus status)
    {
        var step = new AgentStep { StepLabel = label, Status = status };
        UpdateOnUI(() => AgentSteps.Add(step));
        return step;
    }

    private void UpdateStep(AgentStep step, AgentStepStatus status, string? outputPreview = null)
    {
        UpdateOnUI(() =>
        {
            step.Status = status;
            if (outputPreview != null) step.OutputPreview = outputPreview;
        });
    }

    private Task OnConfirmationRequested(AgentConfirmationRequest request)
    {
        UpdateOnUI(() => PendingConfirmationRequest = request);
        return Task.CompletedTask;
    }

    public void ApproveConfirmation(bool approved)
    {
        if (PendingConfirmationRequest == null) return;
        PendingConfirmationRequest.CompletionSource.TrySetResult(approved);
        UpdateOnUI(() => PendingConfirmationRequest = null);
    }

    /// <summary>
    /// Drops a destructive-command confirmation nobody is going to answer, because the turn
    /// that asked for it has been cancelled.
    ///
    /// Cancelled rather than denied, and the difference matters: a denial goes back to the
    /// model as a tool result and the loop reads it and carries on, which is the one thing
    /// an interrupted turn must not do.
    /// </summary>
    public void CancelPendingConfirmation()
    {
        var request = PendingConfirmationRequest;
        if (request == null) return;

        request.CompletionSource.TrySetCanceled();
        UpdateOnUI(() => PendingConfirmationRequest = null);
    }

    private void UpdateOnUI(Action action)
    {
        if (_dispatcherQueue.HasThreadAccess)
            action();
        else
            _dispatcherQueue.TryEnqueue(() => action());
    }

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public void Dispose()
    {
        // Nothing to dispose; tool executor has no IDisposable resources
    }
}
