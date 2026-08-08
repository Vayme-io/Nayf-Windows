using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
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
        new
        {
            name = "computer",
            description = "Control the computer — take screenshots, click, type text.",
            input_schema = new
            {
                type = "object",
                properties = new
                {
                    action = new
                    {
                        type = "string",
                        @enum = new[] { "screenshot", "left_click", "right_click", "double_click", "type", "key" }
                    },
                    coordinate = new { type = "array", items = new { type = "integer" } },
                    text = new { type = "string" },
                    key = new { type = "string" }
                },
                required = new[] { "action" }
            }
        },
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
    /// </summary>
    public async Task<string> RunAgentLoopAsync(
        string userRequest,
        List<CapturedScreenshot>? screenshots,
        string systemPrompt,
        string? authToken,
        Action<string>? onTextDelta,
        CancellationToken cancellationToken = default,
        List<ConversationTurn>? conversationHistory = null)
    {
        UpdateOnUI(() => AgentSteps.Clear());
        _runningToolLabel = null;

        var messages = BuildInitialMessages(userRequest, screenshots, conversationHistory);
        var fullFinalText = "";
        var lastAssistantText = "";

        const int maxIterations = 15;

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
                    AgentTools,
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
                var toolStep = AddStep($"Running: {toolCall.ToolName}", AgentStepStatus.Running);
                _runningToolLabel = DescribeTool(toolCall.ToolName);
                AgentToolResult toolResult;

                try
                {
                    toolResult = await _toolExecutor.ExecuteToolAsync(toolCall, cancellationToken, authToken);
                    UpdateStep(toolStep, AgentStepStatus.Completed,
                        toolResult.Text.Length > 100 ? toolResult.Text[..100] + "…" : toolResult.Text);
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

        return fullFinalText;
    }

    private List<object> BuildInitialMessages(
        string userRequest,
        List<CapturedScreenshot>? screenshots,
        List<ConversationTurn>? conversationHistory = null)
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
        "bash" => "Running a command",
        "read_file" => "Reading a file",
        "write_file" => "Writing a file",
        "computer" => "Working on your screen",
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
