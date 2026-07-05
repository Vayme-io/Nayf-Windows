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
