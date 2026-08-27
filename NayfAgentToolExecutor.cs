using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>The output of one tool call — text, plus any screenshots Claude should see.</summary>
public sealed class AgentToolResult
{
    public string Text { get; init; } = "";

    /// <summary>
    /// One entry per monitor when the tool produced screenshots, empty otherwise.
    ///
    /// A list rather than a single image because the app the user is working in is often
    /// not on the primary display, and a model that was only ever shown screen 0 has no
    /// way to point at anything on screen 1.
    /// </summary>
    public IReadOnlyList<CapturedScreenshot> Screenshots { get; init; } = Array.Empty<CapturedScreenshot>();

    public static AgentToolResult Message(string text) => new() { Text = text };
}

/// <summary>What the current interaction is allowed to do on the user's machine.</summary>
public enum NayfToolMode
{
    /// <summary>
    /// Teaching. Vayme shows and speaks; the user performs every action themselves.
    /// Looking and pointing only — never actuation.
    /// </summary>
    GuidedWalkthrough,

    /// <summary>Background automation the user asked Vayme to carry out. Vayme acts.</summary>
    AgentTask
}

/// <summary>
/// Executes all agent tool types: PowerShell commands, file reads/writes, and screenshots.
/// Mirrors NayfAgentToolExecutor.swift.
/// Destructive shell commands require user confirmation before execution.
///
/// Nothing here drives the mouse or the keyboard. Vayme points at what the user should
/// click and the user clicks it — in every mode, not just while teaching.
/// </summary>
public sealed class NayfAgentToolExecutor
{
    public event Func<AgentConfirmationRequest, Task>? ConfirmationRequested;

    /// <summary>
    /// Scopes what this run may do. Set by <see cref="NayfAgentManager"/> before each turn.
    /// Defaults to the hands-off mode, so a caller that forgets to set it cannot act.
    /// </summary>
    public NayfToolMode ToolMode { get; set; } = NayfToolMode.GuidedWalkthrough;

    /// <summary>
    /// Tools that change something on the user's machine.
    ///
    /// A walkthrough is never offered these, and is refused them here as well. The two
    /// checks are not redundant: the tool list is a request the model can ignore, and a
    /// model that names a tool it was never given must still not reach the user's screen.
    /// </summary>
    private static readonly HashSet<string> ActuationToolNames = new(StringComparer.Ordinal)
    {
        "bash", "write_file", "spotify_play",
        "google_calendar_create_event", "google_calendar_delete_event", "github_create_issue"
    };

    /// <summary>
    /// The screenshots last handed to the model — one per monitor — or empty if it hasn't
    /// asked for any yet.
    ///
    /// Kept because a step's coordinates were read off one of these images, and pointing at
    /// the real spot means mapping them back to screen pixels — done by
    /// <see cref="NayfAgentManager"/>, which draws the step. Nothing here turns a coordinate
    /// into a click.
    /// </summary>
    public IReadOnlyList<CapturedScreenshot> LastScreenshots { get; private set; } =
        Array.Empty<CapturedScreenshot>();

    /// <summary>
    /// The screenshot for one display out of <see cref="LastScreenshots"/>, or null when that
    /// screen wasn't among them — which is what happens if the model names a monitor that
    /// isn't there, so callers must fall back rather than assume a hit.
    /// </summary>
    public CapturedScreenshot? LastScreenshotForScreen(int screenIndex)
    {
        foreach (var shot in LastScreenshots)
        {
            if (shot.ScreenIndex == screenIndex) return shot;
        }
        return null;
    }

    /// <summary>Executes a tool call and returns the result for Claude's tool_result.</summary>
    public async Task<AgentToolResult> ExecuteToolAsync(
        AgentParsedToolCall toolCall,
        CancellationToken cancellationToken = default,
        string? authToken = null)
    {
        // A walkthrough teaches; it never touches the machine. The refusal is worded rather
        // than silent because it goes back to the model as a tool result — this is the last
        // chance to steer it into pointing at the step instead of performing it.
        if (ToolMode == NayfToolMode.GuidedWalkthrough && ActuationToolNames.Contains(toolCall.ToolName))
        {
            Logger.Log("AgentTools", $"walkthrough: refused actuation tool '{toolCall.ToolName}'");
            return AgentToolResult.Message(
                "Not available while teaching — the user performs every action themselves. " +
                "Don't click, type, drag, or run anything. Point at the spot, tell them what " +
                "to do in one short sentence, and wait for them to do it.");
        }

        return toolCall.ToolName switch
        {
            "take_screenshot" => await TakeScreenshotAsync(),
            "bash" => AgentToolResult.Message(await ExecuteBashToolAsync(toolCall.InputJson, cancellationToken)),
            "read_file" => AgentToolResult.Message(ExecuteReadFileTool(toolCall.InputJson)),
            "write_file" => AgentToolResult.Message(ExecuteWriteFileTool(toolCall.InputJson)),
            "computer" => await ExecuteComputerToolAsync(toolCall.InputJson),
            "spotify_play" => AgentToolResult.Message(await ExecuteSpotifyPlayAsync(toolCall.InputJson, authToken)),
            "google_calendar_list_events" => AgentToolResult.Message(await ExecuteGoogleCalendarListEventsAsync(toolCall.InputJson, authToken)),
            "google_calendar_create_event" => AgentToolResult.Message(await ExecuteGoogleCalendarCreateEventAsync(toolCall.InputJson, authToken)),
            "google_calendar_delete_event" => AgentToolResult.Message(await ExecuteGoogleCalendarDeleteEventAsync(toolCall.InputJson, authToken)),
            "github_list_issues" => AgentToolResult.Message((await CallWorkerIntegrationAsync("/integrations/github/list_issues", new(), authToken)).Text),
            "github_list_pull_requests" => AgentToolResult.Message((await CallWorkerIntegrationAsync("/integrations/github/list_pull_requests", new(), authToken)).Text),
            "github_create_issue" => AgentToolResult.Message(await ExecuteGitHubCreateIssueAsync(toolCall.InputJson, authToken)),
            _ => AgentToolResult.Message($"Unknown tool: {toolCall.ToolName}")
        };
    }

    /// <summary>
    /// Resolves a natural-language query to an exact Spotify track via the Worker's
    /// /spotify/search endpoint, then plays it in the desktop app by opening the
    /// track's spotify: URI. Far more reliable than driving Spotify's UI.
    /// </summary>
    private static async Task<string> ExecuteSpotifyPlayAsync(Dictionary<string, object> input, string? authToken)
    {
        if (!input.TryGetValue("query", out var q)) return "Missing 'query' parameter";
        var query = q?.ToString()?.Trim() ?? "";
        if (query.Length == 0) return "Empty 'query'";

        string uri, name, artist;
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(15) };
            using var req = new HttpRequestMessage(HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}/spotify/search");
            if (authToken != null) req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            req.Content = new StringContent(JsonSerializer.Serialize(new { query }), Encoding.UTF8, "application/json");

            using var resp = await http.SendAsync(req);
            var body = await resp.Content.ReadAsStringAsync();
            if (!resp.IsSuccessStatusCode)
                return $"Spotify search failed (HTTP {(int)resp.StatusCode}). Fallback: drive Spotify via keyboard.";

            using var doc = JsonDocument.Parse(body);
            uri = doc.RootElement.TryGetProperty("uri", out var u) ? u.GetString() ?? "" : "";
            if (uri.Length == 0) return "No matching track found on Spotify.";
            name = doc.RootElement.TryGetProperty("name", out var n) ? n.GetString() ?? "track" : "track";
            artist = doc.RootElement.TryGetProperty("artist", out var a) ? a.GetString() ?? "" : "";
        }
        catch (Exception ex) { return $"Spotify search failed: {ex.Message}"; }

        try
        {
            // The spotify: URI launches Spotify (if needed) and starts playback.
            Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
        }
        catch (Exception ex) { return $"Found \"{name}\" but couldn't open Spotify: {ex.Message}"; }

        NayfActionToast.ShowNowPlaying(name, artist);

        var suffix = artist.Length > 0 ? $" by {artist}" : "";
        return $"Now playing \"{name}\"{suffix} on Spotify.";
    }

    // MARK: - Google Calendar
    //
    // These read and write the user's CONNECTED Google account through the Worker, which
    // holds the OAuth tokens server-side against their Supabase user. Nothing calls Google
    // directly and no token ever reaches this process.

    /// <summary>
    /// Lists upcoming Google Calendar events via the Worker. Returns the JSON event list as
    /// text for the model to read and summarise aloud.
    /// </summary>
    private static async Task<string> ExecuteGoogleCalendarListEventsAsync(
        Dictionary<string, object> input, string? authToken)
    {
        // Every field is optional — the Worker defaults to the next 7 days, 10 events.
        var body = new Dictionary<string, object>();
        if (TryGetTrimmedString(input, "timeMin") is { } timeMin) body["timeMin"] = timeMin;
        if (TryGetTrimmedString(input, "timeMax") is { } timeMax) body["timeMax"] = timeMax;
        if (TryGetTrimmedString(input, "query") is { } query) body["query"] = query;
        if (TryGetTrimmedString(input, "maxResults") is { } maxResultsText &&
            int.TryParse(maxResultsText, out int maxResults))
            body["maxResults"] = maxResults;

        return (await CallWorkerIntegrationAsync("/integrations/google/calendar/list_events", body, authToken)).Text;
    }

    /// <summary>Creates a Google Calendar event via the Worker. Requires summary + start + end.</summary>
    private static async Task<string> ExecuteGoogleCalendarCreateEventAsync(
        Dictionary<string, object> input, string? authToken)
    {
        var summary = TryGetTrimmedString(input, "summary");
        var startDateTime = TryGetTrimmedString(input, "startDateTime");
        var endDateTime = TryGetTrimmedString(input, "endDateTime");

        if (summary == null || startDateTime == null || endDateTime == null)
            return "Missing required fields: summary, startDateTime, endDateTime (ISO 8601 with timezone offset).";

        var body = new Dictionary<string, object>
        {
            ["summary"] = summary,
            ["startDateTime"] = startDateTime,
            ["endDateTime"] = endDateTime
        };
        if (TryGetTrimmedString(input, "description") is { } description) body["description"] = description;
        if (TryGetTrimmedString(input, "location") is { } location) body["location"] = location;
        if (TryGetTrimmedString(input, "timeZone") is { } timeZone) body["timeZone"] = timeZone;

        var result = await CallWorkerIntegrationAsync("/integrations/google/calendar/create_event", body, authToken);
        if (!result.IsError) NayfActionToast.ShowCalendarEventAdded(summary, startDateTime);
        return result.Text;
    }

    /// <summary>
    /// Deletes a Google Calendar event by id via the Worker. The id comes from a prior
    /// google_calendar_list_events call.
    /// </summary>
    private static async Task<string> ExecuteGoogleCalendarDeleteEventAsync(
        Dictionary<string, object> input, string? authToken)
    {
        var eventId = TryGetTrimmedString(input, "eventId");
        if (eventId == null)
            return "Missing required field: eventId (get it from google_calendar_list_events).";

        var body = new Dictionary<string, object> { ["eventId"] = eventId };
        var result = await CallWorkerIntegrationAsync("/integrations/google/calendar/delete_event", body, authToken);
        if (!result.IsError) NayfActionToast.ShowCalendarEventRemoved();
        return result.Text;
    }

    // MARK: - GitHub

    /// <summary>Creates a GitHub issue in owner/repo via the Worker. Requires owner, repo, title.</summary>
    private static async Task<string> ExecuteGitHubCreateIssueAsync(
        Dictionary<string, object> input, string? authToken)
    {
        var owner = TryGetTrimmedString(input, "owner");
        var repo = TryGetTrimmedString(input, "repo");
        var title = TryGetTrimmedString(input, "title");

        if (owner == null || repo == null || title == null)
            return "Missing required fields: owner, repo, title.";

        var body = new Dictionary<string, object>
        {
            ["owner"] = owner,
            ["repo"] = repo,
            ["title"] = title
        };
        if (TryGetTrimmedString(input, "body") is { } issueBody) body["body"] = issueBody;

        var result = await CallWorkerIntegrationAsync("/integrations/github/create_issue", body, authToken);
        if (!result.IsError) NayfActionToast.ShowIssueCreated(title, owner, repo);
        return result.Text;
    }

    // MARK: - Worker plumbing

    /// <summary>
    /// What a Worker integration call came back with: the text handed to the model either
    /// way, and whether it describes a result or a failure.
    /// </summary>
    /// <remarks>
    /// The flag exists because the failures are prose — "that integration isn't connected
    /// yet" is a perfectly good sentence to give the model, and utterly indistinguishable
    /// from a success by inspection. Anything that acts on a call having <i>worked</i>,
    /// like the action toast, needs to be told rather than left to guess.
    /// </remarks>
    private readonly record struct IntegrationResult(string Text, bool IsError);

    /// <summary>
    /// POSTs a JSON body to a Worker integration route with the user's auth token, returning
    /// the response text. Translates a 409 ("not connected") into a clear, actionable message
    /// so the model asks the user to connect rather than reporting a bare failure.
    /// </summary>
    private static async Task<IntegrationResult> CallWorkerIntegrationAsync(
        string path, Dictionary<string, object> body, string? authToken)
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{NayfConfig.WorkerBaseURL}{path}");
            if (authToken != null)
                request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);
            request.Content = new StringContent(JsonSerializer.Serialize(body), Encoding.UTF8, "application/json");

            using var response = await http.SendAsync(request);
            var responseText = await response.Content.ReadAsStringAsync();

            if (response.IsSuccessStatusCode) return new IntegrationResult(responseText, IsError: false);
            if ((int)response.StatusCode == 409)
                return new IntegrationResult(
                    "That integration isn't connected yet. Tell the user to open Vayme, click \"Connect apps\", and connect it — then they can ask again.",
                    IsError: true);

            return new IntegrationResult(
                $"Integration request failed (HTTP {(int)response.StatusCode}): {responseText}", IsError: true);
        }
        catch (Exception ex)
        {
            return new IntegrationResult($"Integration request error: {ex.Message}", IsError: true);
        }
    }

    /// <summary>
    /// Reads a tool argument as a non-empty trimmed string, or null when it's absent or
    /// blank. Values arrive as JsonElement, whose ToString() yields the underlying scalar.
    /// </summary>
    private static string? TryGetTrimmedString(Dictionary<string, object> input, string key)
    {
        if (!input.TryGetValue(key, out var raw)) return null;
        var text = raw?.ToString()?.Trim();
        return string.IsNullOrEmpty(text) ? null : text;
    }

    private async Task<string> ExecuteBashToolAsync(
        Dictionary<string, object> input,
        CancellationToken cancellationToken)
    {
        if (!input.TryGetValue("command", out var commandObj)) return "Missing 'command' parameter";
        var command = commandObj?.ToString() ?? "";

        var riskReason = DescribeCommandRisk(command);
        if (riskReason != null)
        {
            var confirmation = new AgentConfirmationRequest
            {
                CommandDescription = riskReason,
                CommandText = command
            };
            if (ConfirmationRequested != null)
                await ConfirmationRequested(confirmation);
            var approved = await confirmation.CompletionSource.Task;
            if (!approved) return "Command cancelled by user.";
        }

        return await RunPowerShellCommandAsync(command, cancellationToken);
    }

    private string ExecuteReadFileTool(Dictionary<string, object> input)
    {
        if (!input.TryGetValue("path", out var pathObj)) return "Missing 'path' parameter";
        var path = pathObj?.ToString() ?? "";
        try
        {
            if (!File.Exists(path)) return $"File not found: {path}";
            var content = File.ReadAllText(path);
            if (content.Length > 50000)
                content = content[..50000] + $"\n\n[Truncated — file is {content.Length} chars]";
            return content;
        }
        catch (Exception ex) { return $"Error reading file: {ex.Message}"; }
    }

    private string ExecuteWriteFileTool(Dictionary<string, object> input)
    {
        if (!input.TryGetValue("path", out var pathObj)) return "Missing 'path' parameter";
        if (!input.TryGetValue("content", out var contentObj)) return "Missing 'content' parameter";
        var path = pathObj?.ToString() ?? "";
        var content = contentObj?.ToString() ?? "";
        try
        {
            var directory = Path.GetDirectoryName(path);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            File.WriteAllText(path, content);
            return $"Successfully wrote {content.Length} chars to {path}";
        }
        catch (Exception ex) { return $"Error writing file: {ex.Message}"; }
    }

    /// <summary>
    /// The old control-the-computer tool, now reduced to its one harmless action.
    ///
    /// It is no longer offered in any mode, and the code that moved the cursor, clicked,
    /// typed and pressed keys is gone rather than gated — Vayme shows the user where to
    /// click and they click it, in every mode, so there is nothing for that code to do.
    /// Still handled here because the model has been asking for this tool for a long time
    /// and may name it out of habit; being told to point is more use to it than "unknown
    /// tool", and asking to look is answered rather than refused.
    /// </summary>
    private async Task<AgentToolResult> ExecuteComputerToolAsync(Dictionary<string, object> input)
    {
        var action = input.TryGetValue("action", out var actionObj) ? actionObj?.ToString() ?? "" : "";
        if (action == "screenshot") return await TakeScreenshotAsync();

        Logger.Log("AgentTools", $"refused computer action '{action}' — Vayme never actuates");
        return AgentToolResult.Message(
            "Vayme never controls the mouse or keyboard. Don't click, type, drag, or press " +
            "keys on the user's behalf — there is no tool for it. Tell them what to click " +
            "and point at it with a [POINT] tag, and let them do it themselves.");
    }

    private async Task<AgentToolResult> TakeScreenshotAsync()
    {
        try
        {
            var screenshots = await ScreenCaptureUtility.CaptureAllScreensAsync();
            if (screenshots.Count == 0) return AgentToolResult.Message("No screenshot available");

            // Every monitor, not just the primary one. The window the user is asking about
            // is regularly on their second display, and a screenshot of screen 0 shows the
            // model an empty desktop and no reason to think it is missing anything.
            var usable = new List<CapturedScreenshot>();
            foreach (var shot in screenshots)
            {
                if (shot.ImageWidth > 0 && shot.ImageHeight > 0) usable.Add(shot);
            }
            if (usable.Count == 0) return AgentToolResult.Message("No screenshot available");

            // Kept for pointing: a step's coordinates are read off one of these images, and
            // the monitor bounds on it are what map them back to the real screen.
            LastScreenshots = usable;

            var text = new StringBuilder();
            text.Append(usable.Count == 1 ? "Screenshot of " : $"Screenshots of all {usable.Count} displays: ");
            for (int i = 0; i < usable.Count; i++)
            {
                if (i > 0) text.Append("; ");
                text.Append($"{usable[i].ScreenLabel}");
            }
            text.Append(". Each image is labelled with its screen number — use that number in a ")
                .Append("[POINT] tag or a step's \"screen\" argument, and read coordinates off ")
                .Append("that screen's own image.");

            return new AgentToolResult { Text = text.ToString(), Screenshots = usable };
        }
        catch (Exception ex)
        {
            return AgentToolResult.Message($"Screenshot failed: {ex.Message}");
        }
    }

    private static async Task<string> RunPowerShellCommandAsync(string command, CancellationToken cancellationToken)
    {
        try
        {
            using var process = new Process
            {
                StartInfo = new ProcessStartInfo
                {
                    FileName = "powershell.exe",
                    Arguments = $"-NoProfile -NonInteractive -ExecutionPolicy Bypass -Command \"{command.Replace("\"", "\\\"")}\"",
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true
                }
            };
            process.Start();

            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            cts.CancelAfter(TimeSpan.FromSeconds(60));

            var stdout = await process.StandardOutput.ReadToEndAsync(cts.Token);
            var stderr = await process.StandardError.ReadToEndAsync(cts.Token);
            await process.WaitForExitAsync(cts.Token);

            var result = new StringBuilder();
            if (!string.IsNullOrWhiteSpace(stdout)) result.AppendLine(stdout.TrimEnd());
            if (!string.IsNullOrWhiteSpace(stderr)) result.AppendLine($"STDERR: {stderr.TrimEnd()}");
            if (process.ExitCode != 0) result.AppendLine($"Exit code: {process.ExitCode}");
            return result.Length > 0 ? result.ToString().TrimEnd() : "(no output)";
        }
        catch (OperationCanceledException) { return "Command timed out after 60 seconds"; }
        catch (Exception ex) { return $"Command failed: {ex.Message}"; }
    }

    /// <summary>Commands that delete files or erase their contents.</summary>
    private static readonly HashSet<string> DeletionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "remove-item", "ri", "rm", "rmdir", "rd", "del", "erase",
        "clear-content", "clc", "clear-item", "remove-itemproperty", "remove-psdrive"
    };

    /// <summary>
    /// Commands that operate on disks. "format" alone is the disk formatter —
    /// unrelated to Format-Table, Format-List and the other output formatters,
    /// which are how PowerShell prints almost anything.
    /// </summary>
    private static readonly HashSet<string> DiskCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "format", "format-volume", "clear-disk", "initialize-disk",
        "remove-partition", "set-partition", "diskpart"
    };

    private static readonly HashSet<string> ProcessCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "stop-process", "spps", "kill", "taskkill", "stop-service"
    };

    private static readonly HashSet<string> PowerCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "shutdown", "restart-computer", "stop-computer"
    };

    private static readonly HashSet<string> PermissionCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "icacls", "takeown", "set-acl", "set-executionpolicy"
    };

    /// <summary>Commands that read a folder's contents into a pipeline.</summary>
    private static readonly HashSet<string> EnumerationCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "get-childitem", "gci", "ls", "dir", "get-item", "gi"
    };

    /// <summary>Commands that relocate files rather than destroy them.</summary>
    private static readonly HashSet<string> MoveCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "move-item", "mi", "move", "mv"
    };

    /// <summary>Parameters whose value names the file or folder a command acts on.</summary>
    private static readonly HashSet<string> PathParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "-path", "-literalpath"
    };

    private static readonly HashSet<string> ProcessNameParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "-name", "-processname", "-id", "/im", "/pid"
    };

    private static readonly HashSet<string> DiskTargetParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "-driveletter", "-disknumber", "-number", "-friendlyname"
    };

    /// <summary>takeown names its file with /f; icacls and Set-Acl take it positionally.</summary>
    private static readonly HashSet<string> PermissionTargetParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "/f"
    };

    /// <summary>
    /// Parameters whose next token belongs to the parameter, so it is not what the
    /// command acts on. Without this, "-Destination C:\Backup" reads as a second target
    /// and a move gets described as touching the place it is moving things to.
    /// </summary>
    private static readonly HashSet<string> ValueParameters = new(StringComparer.OrdinalIgnoreCase)
    {
        "-destination", "-filter", "-include", "-exclude", "-erroraction", "-warningaction",
        "-value", "-newname", "-encoding", "-scope", "-credential", "-stream", "-name", "-id"
    };

    /// <summary>
    /// The environment variables worth expanding when naming a place on the PC. Kept to a
    /// list on purpose — these each stand for a folder, and nothing else from the
    /// environment is put on screen.
    /// </summary>
    private static readonly HashSet<string> PathEnvironmentVariables = new(StringComparer.OrdinalIgnoreCase)
    {
        "userprofile", "home", "homedrive", "homepath", "appdata", "localappdata",
        "programdata", "programfiles", "programfiles(x86)", "programw6432", "public",
        "systemdrive", "systemroot", "windir", "temp", "tmp", "onedrive", "username"
    };

    /// <summary>
    /// Says what a command is about to do, in the user's own terms, or returns null when
    /// it can just run.
    ///
    /// Matching is on the command that *starts* each statement, never on the raw text.
    /// A plain substring search reads far too much as dangerous: "format-" matches
    /// Format-Table, the most common way to print anything in PowerShell; "del " sits
    /// inside "model "; "rd " inside "keyboard "; "kill " inside "skill ". Vayme ended
    /// up asking permission to list a folder, which teaches the user to approve
    /// without reading — the opposite of what a confirmation is for.
    ///
    /// *Which* commands are asked about is unchanged here; what changed is what the user
    /// is told. Every category used to answer with one fixed sentence, so tidying a
    /// folder away — move everything out of it, then remove the empty folder — was
    /// announced as "This permanently deletes files or erases their contents". That is
    /// frightening and it is false: not one file was lost. Each effect is now described
    /// from the statement's own arguments, with paths resolved through the variables the
    /// command sets, and falls back to the old blanket sentence whenever the target
    /// cannot be worked out — vague is fine, wrong is not.
    /// </summary>
    private static string? DescribeCommandRisk(string command)
    {
        var pipelines = SplitIntoPipelines(command);
        var variables = ResolveAssignedVariables(pipelines);
        var effects = new List<string>();
        bool destroysData = false;

        foreach (var statement in SplitIntoStatements(command))
        {
            var verb = LeadingCommandName(statement);
            if (verb.Length == 0) continue;

            if (DeletionCommands.Contains(verb))
            {
                var deletion = DescribeDeletion(verb, statement, pipelines, variables);
                destroysData |= deletion.DestroysData;
                AddEffect(effects, deletion.Text);
            }
            else if (DiskCommands.Contains(verb))
                AddEffect(effects, DescribeDiskChange(statement, variables));
            else if (ProcessCommands.Contains(verb))
                AddEffect(effects, DescribeStoppedProgram(verb, statement, pipelines, variables));
            else if (PowerCommands.Contains(verb))
                AddEffect(effects, DescribePowerChange(verb, statement));
            else if (PermissionCommands.Contains(verb))
                AddEffect(effects, DescribePermissionChange(verb, statement, variables));

            // These two are only destructive with particular subcommands: `reg query`
            // and `net view` read, while `reg delete` and `net user` change the machine.
            else if (verb.Equals("reg", StringComparison.OrdinalIgnoreCase) &&
                     MentionsWord(statement, "delete", "import", "restore"))
                AddEffect(effects, DescribeRegistryChange(statement, variables));
            else if (verb.Equals("regedit", StringComparison.OrdinalIgnoreCase))
                AddEffect(effects, "Changes the Windows registry, where Windows keeps its settings.");
            else if (verb.Equals("net", StringComparison.OrdinalIgnoreCase) &&
                     MentionsWord(statement, "user", "localgroup"))
                AddEffect(effects, "Changes the user accounts on your PC.");
        }

        // Both of these read the whole command rather than a statement, and both are
        // worth saying even alongside something louder — running as administrator is
        // exactly the detail a user wants to know before approving anything.
        if (MentionsWord(command, "runas"))
            AddEffect(effects, "Runs as administrator, so it is not limited to your own files.");
        if (HasOverwritingRedirect(command))
        {
            var overwritten = RedirectTargetName(command, variables);
            AddEffect(effects, overwritten == null
                ? "Overwrites a file, replacing whatever was in it."
                : $"Overwrites {overwritten}, replacing whatever was in it.");
        }

        if (effects.Count == 0) return null;

        var summary = JoinEffects(effects);
        if (destroysData) summary += " Nothing removed this way goes to the Recycle Bin.";
        return summary;
    }

    /// <summary>Adds an effect unless it repeats one already listed.</summary>
    private static void AddEffect(List<string> effects, string effect)
    {
        if (effect.Length == 0) return;
        foreach (var existing in effects)
            if (string.Equals(existing, effect, StringComparison.Ordinal)) return;
        effects.Add(effect);
    }

    /// <summary>
    /// The effects as one paragraph, in the order they happen. A long script can trip
    /// half a dozen of these, and a wall of text is read no more carefully than a blank
    /// one, so the tail is counted rather than spelled out.
    /// </summary>
    private static string JoinEffects(List<string> effects)
    {
        const int mostToSpellOut = 3;
        if (effects.Count <= mostToSpellOut) return string.Join(" ", effects);

        var shown = effects.GetRange(0, mostToSpellOut);
        int rest = effects.Count - mostToSpellOut;
        shown.Add(rest == 1
            ? "It makes one more change of the same kind."
            : $"It makes {rest} more changes of the same kind.");
        return string.Join(" ", shown);
    }

    /// <summary>
    /// What a deletion actually removes, and whether anything is really lost by it.
    /// </summary>
    private static (string Text, bool DestroysData) DescribeDeletion(
        string verb,
        string statement,
        List<string> pipelines,
        IReadOnlyDictionary<string, string> variables)
    {
        bool emptiesRatherThanRemoves =
            verb.Equals("clear-content", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("clc", StringComparison.OrdinalIgnoreCase) ||
            verb.Equals("clear-item", StringComparison.OrdinalIgnoreCase);

        var raw = FirstTarget(statement, PathParameters);
        var target = raw == null ? null : ExpandTarget(raw, variables);

        if (target == null)
            return (emptiesRatherThanRemoves
                ? "Erases what is inside a file, leaving the empty file behind."
                : "Permanently deletes files, or erases what is inside them.", true);

        bool contentsOnly = EndsWithContentsWildcard(target);
        if (contentsOnly) target = target[..^2];
        target = target.TrimEnd('\\', '/');
        var shown = ShortenPath(target);

        if (emptiesRatherThanRemoves)
            return ($"Erases what is inside {shown}, leaving the empty file behind.", true);

        if (target.IndexOf('*') >= 0 || target.IndexOf('?') >= 0)
            return ($"Permanently deletes the files matching {shown}.", true);

        var movedInto = WhereContentsWereMovedFirst(target, statement, pipelines, variables);
        if (movedInto != null)
            return ($"Moves everything out of {shown} into {movedInto}, then removes the " +
                    $"emptied {LastSegment(shown)} folder. Your files are kept.", false);

        if (contentsOnly)
            return ($"Permanently deletes everything inside {shown}.", true);

        return (Path.GetExtension(LastSegment(target)).Length > 0
            ? $"Permanently deletes the file {shown}."
            : $"Permanently deletes {shown} and everything inside it.", true);
    }

    /// <summary>
    /// Where a folder's contents go earlier in the same command, or null if they don't.
    ///
    /// This is the whole difference between a script that destroys a folder and one that
    /// tidies it away: "Get-ChildItem X | Move-Item -Destination Y; Remove-Item X" loses
    /// nothing at all, and telling the user it deletes their files is both frightening
    /// and untrue. It only counts when the move takes everything — an -Include or a
    /// -Filter leaves some of it behind, and what stayed behind really would be deleted —
    /// and when the destination is somewhere other than inside the folder being removed.
    /// </summary>
    private static string? WhereContentsWereMovedFirst(
        string folder,
        string deleteStatement,
        List<string> pipelines,
        IReadOnlyDictionary<string, string> variables)
    {
        int deleteAt = PipelineIndexOf(deleteStatement, pipelines);

        for (int i = 0; i < deleteAt && i < pipelines.Count; i++)
        {
            var segments = pipelines[i].Split('|', StringSplitOptions.RemoveEmptyEntries);

            for (int k = 0; k < segments.Length; k++)
            {
                var move = segments[k].Trim();
                if (!MoveCommands.Contains(LeadingCommandName(move))) continue;

                string? source;
                if (k == 0)
                {
                    // "Move-Item X\* -Destination Y" empties X; "Move-Item X ..." moves X
                    // itself, which leaves nothing for a later delete to be about.
                    var raw = FirstTarget(move, PathParameters);
                    var expanded = raw == null ? null : ExpandTarget(raw, variables);
                    if (expanded == null || !EndsWithContentsWildcard(expanded)) continue;
                    source = expanded[..^2].TrimEnd('\\', '/');
                }
                else
                {
                    // Only straight off the listing: anything in between can narrow what
                    // is moved, and then the delete is not harmless after all.
                    if (k != 1) continue;
                    var head = segments[0].Trim();
                    if (!EnumerationCommands.Contains(LeadingCommandName(head))) continue;
                    if (MentionsWord(head, "-filter", "-include", "-exclude")) continue;

                    var raw = FirstTarget(head, PathParameters);
                    var expanded = raw == null ? null : ExpandTarget(raw, variables);
                    if (expanded == null) continue;
                    if (EndsWithContentsWildcard(expanded)) expanded = expanded[..^2];
                    source = expanded.TrimEnd('\\', '/');
                }

                if (!string.Equals(source, folder, StringComparison.OrdinalIgnoreCase)) continue;

                var destinationRaw = NamedParameterValue(move, "-destination")
                                     ?? (k == 0 ? SecondTarget(move) : null);
                var destination = destinationRaw == null ? null : ExpandTarget(destinationRaw, variables);
                if (destination == null || destination.IndexOf('*') >= 0) continue;

                destination = destination.TrimEnd('\\', '/');
                if (IsInside(destination, source)) continue;

                return ShortenPath(destination);
            }
        }

        return null;
    }

    private static string DescribeDiskChange(string statement, IReadOnlyDictionary<string, string> variables)
    {
        var raw = FirstTarget(statement, DiskTargetParameters);
        var target = raw == null ? null : ExpandTarget(raw, variables);
        if (target == null)
            return "Erases or repartitions a drive. Everything stored on it is lost.";

        var shown = target.Length <= 2 && char.IsLetter(target[0])
            ? $"drive {char.ToUpperInvariant(target[0])}:"
            : ShortenPath(target);
        return $"Erases or repartitions {shown}. Everything stored on it is lost.";
    }

    private static string DescribeStoppedProgram(
        string verb,
        string statement,
        List<string> pipelines,
        IReadOnlyDictionary<string, string> variables)
    {
        var names = ProgramNames(statement, variables);

        if (names.Count == 0)
        {
            // "Get-Process discord | Stop-Process" is the usual shape, and the half that
            // names the program is the half that isn't the risky one.
            int at = PipelineIndexOf(statement, pipelines);
            if (at < pipelines.Count)
            {
                var segments = pipelines[at].Split('|', StringSplitOptions.RemoveEmptyEntries);
                if (segments.Length > 1)
                {
                    var head = segments[0].Trim();
                    if (LeadingCommandName(head).StartsWith("get-", StringComparison.OrdinalIgnoreCase))
                        names = ProgramNames(head, variables);
                }
            }
        }

        if (verb.Equals("stop-service", StringComparison.OrdinalIgnoreCase))
            return names.Count == 0
                ? "Stops a Windows background service."
                : $"Stops the {JoinNames(names)} Windows service.";

        if (names.Count == 0)
            return "Force-stops a running program. Anything not saved in it is lost.";

        return $"Force-stops {JoinNames(names)}. " +
               $"Anything not saved in {(names.Count == 1 ? "it" : "them")} is lost.";
    }

    private static string DescribePowerChange(string verb, string statement)
    {
        if (verb.Equals("restart-computer", StringComparison.OrdinalIgnoreCase))
            return "Restarts your PC. Anything you have not saved is lost.";
        if (verb.Equals("stop-computer", StringComparison.OrdinalIgnoreCase))
            return "Shuts your PC down. Anything you have not saved is lost.";

        // shutdown.exe: /a calls off a shutdown that was already scheduled, /r and /g
        // restart, /l signs out, and everything else powers the machine off.
        if (HasSwitch(statement, "a")) return "Calls off a restart that was already scheduled.";
        if (HasSwitch(statement, "r", "g")) return "Restarts your PC. Anything you have not saved is lost.";
        if (HasSwitch(statement, "l")) return "Signs you out of Windows. Anything you have not saved is lost.";
        return "Shuts your PC down. Anything you have not saved is lost.";
    }

    private static string DescribePermissionChange(
        string verb, string statement, IReadOnlyDictionary<string, string> variables)
    {
        if (verb.Equals("set-executionpolicy", StringComparison.OrdinalIgnoreCase))
            return "Changes which PowerShell scripts Windows will let run on this PC.";

        var raw = FirstTarget(statement, PermissionTargetParameters);
        var target = raw == null ? null : ExpandTarget(raw, variables);
        var shown = target == null ? null : ShortenPath(target.TrimEnd('\\', '/'));

        if (verb.Equals("takeown", StringComparison.OrdinalIgnoreCase))
            return shown == null
                ? "Takes ownership of files, so a different account controls them."
                : $"Takes ownership of {shown}, so a different account controls it.";

        return shown == null
            ? "Changes who is allowed to open or change files on your PC."
            : $"Changes who is allowed to open or change {shown}.";
    }

    private static string DescribeRegistryChange(string statement, IReadOnlyDictionary<string, string> variables)
    {
        string? key = null;
        foreach (var token in TokenizeStatement(statement))
        {
            if (!LooksLikeRegistryKey(token)) continue;
            key = ShortenPath(ExpandTarget(token, variables) ?? token);
            break;
        }

        if (MentionsWord(statement, "delete"))
            return key == null
                ? "Deletes a setting from the Windows registry, where Windows keeps its settings."
                : $"Deletes {key} from the Windows registry, where Windows keeps its settings.";

        return key == null
            ? "Writes into the Windows registry, where Windows keeps its settings."
            : $"Writes into the Windows registry under {key}.";
    }

    /// <summary>
    /// Splits a command line into the individual statements a shell would run.
    ///
    /// Separators inside a quoted string are split on too, which can only ever add a
    /// confirmation, never skip one — the safe direction to be wrong in.
    /// </summary>
    private static List<string> SplitIntoStatements(string command)
    {
        var statements = command.Split(
            ["|", ";", "&&", "||", "\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries);

        var trimmed = new List<string>(statements.Length);
        foreach (var statement in statements) trimmed.Add(statement.Trim());
        return trimmed;
    }

    /// <summary>
    /// The command name a statement starts with, stripped of the grouping and
    /// call-operator punctuation PowerShell allows in front of it.
    /// </summary>
    private static string LeadingCommandName(string statement)
    {
        var text = statement.TrimStart('(', '{', '&', '.', '$', ' ', '\t', '"', '\'');

        int end = 0;
        while (end < text.Length && !char.IsWhiteSpace(text[end])) end++;
        var name = text[..end];

        // `C:\Windows\System32\shutdown.exe` should still read as "shutdown".
        int slash = name.LastIndexOfAny(['\\', '/']);
        if (slash >= 0) name = name[(slash + 1)..];
        if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];

        return name.Trim('"', '\'');
    }

    /// <summary>
    /// Splits a command line the way a shell sequences it, keeping each pipeline whole.
    ///
    /// Deciding whether to ask deliberately breaks pipelines apart as well, so that a
    /// `Remove-Item` buried at the end of one is still seen (see SplitIntoStatements).
    /// This second view exists only to describe what was found, because
    /// "Get-ChildItem X | Move-Item -Destination Y" means nothing read in halves.
    /// </summary>
    private static List<string> SplitIntoPipelines(string command)
    {
        var parts = command.Split(
            [";", "&&", "||", "\r\n", "\n"],
            StringSplitOptions.RemoveEmptyEntries);

        var pipelines = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            var trimmed = part.Trim();
            if (trimmed.Length > 0) pipelines.Add(trimmed);
        }
        return pipelines;
    }

    /// <summary>The pipeline a statement came from, or the count when it cannot be placed.</summary>
    private static int PipelineIndexOf(string statement, List<string> pipelines)
    {
        for (int i = 0; i < pipelines.Count; i++)
            if (pipelines[i].Contains(statement, StringComparison.Ordinal)) return i;
        return pipelines.Count;
    }

    /// <summary>
    /// The value of every plain "$name = ..." the command assigns to itself.
    ///
    /// Without this the confirmation names variables instead of places: a script that
    /// sets $g and then removes $g would be announced as deleting "$g", which tells the
    /// user nothing they could not already read in the command. Only literal assignments
    /// are followed — anything with a space, a quote or a subexpression in it is computed
    /// rather than typed out, and a guess about where it points is worse than no guess.
    /// </summary>
    private static Dictionary<string, string> ResolveAssignedVariables(List<string> pipelines)
    {
        var values = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

        foreach (var pipeline in pipelines)
        {
            if (!pipeline.StartsWith('$')) continue;
            int equals = pipeline.IndexOf('=');
            if (equals <= 1) continue;

            var name = pipeline[1..equals].Trim();
            if (!IsPlainVariableName(name)) continue;

            var assigned = pipeline[(equals + 1)..].Trim();
            bool quoted = assigned.Length >= 2 &&
                          (assigned[0] == '"' || assigned[0] == '\'') &&
                          assigned[^1] == assigned[0];
            var value = quoted ? assigned[1..^1] : assigned;

            if (value.Length == 0) continue;
            if (!quoted && value.IndexOf(' ') >= 0) continue;
            if (value.IndexOfAny(['(', ')', '|', ';', '`', '"', '\'']) >= 0) continue;

            var expanded = ExpandVariables(value, values);
            if (expanded.IndexOf('$') >= 0) continue;   // still leans on something unknown
            values[name] = expanded;
        }

        return values;
    }

    private static bool IsPlainVariableName(string name)
    {
        if (name.Length == 0) return false;
        foreach (var c in name)
            if (!char.IsLetterOrDigit(c) && c != '_') return false;
        return true;
    }

    /// <summary>
    /// Fills "$env:NAME" and the command's own "$name" into a path. Anything that cannot
    /// be resolved is left exactly as written, which callers read as "unknown".
    /// </summary>
    private static string ExpandVariables(string text, IReadOnlyDictionary<string, string> values)
    {
        if (text.IndexOf('$') < 0) return text;

        var result = new StringBuilder(text.Length);

        for (int i = 0; i < text.Length; i++)
        {
            if (text[i] != '$') { result.Append(text[i]); continue; }

            int start = i + 1;
            bool braced = start < text.Length && text[start] == '{';
            if (braced) start++;

            bool fromEnvironment = false;
            int end = start;
            while (end < text.Length &&
                   (char.IsLetterOrDigit(text[end]) || text[end] == '_' ||
                    text[end] == '(' || text[end] == ')' || text[end] == ':'))
            {
                if (text[end] == ':')
                {
                    // "$env:" is the one prefix worth following. "$script:x" and the other
                    // scopes are never seen being assigned, so they stay unresolved.
                    if (!fromEnvironment &&
                        string.Equals(text[start..end], "env", StringComparison.OrdinalIgnoreCase))
                    {
                        fromEnvironment = true;
                        start = end + 1;
                        end = start;
                        continue;
                    }
                    break;
                }
                // Parentheses only belong to a name as part of "PROGRAMFILES(X86)".
                if ((text[end] == '(' || text[end] == ')') && !fromEnvironment) break;
                end++;
            }

            var name = text[start..end];
            if (braced && end < text.Length && text[end] == '}') end++;

            string? value = null;
            if (name.Length > 0)
            {
                if (fromEnvironment && PathEnvironmentVariables.Contains(name))
                {
                    try { value = Environment.GetEnvironmentVariable(name); }
                    catch { value = null; }
                }
                else if (!fromEnvironment && values.TryGetValue(name, out var assigned))
                {
                    value = assigned;
                }
            }

            result.Append(value ?? text[i..end]);
            i = end - 1;
        }

        return result.ToString();
    }

    /// <summary>Splits a statement into its words, keeping quoted stretches whole.</summary>
    private static List<string> TokenizeStatement(string statement)
    {
        var tokens = new List<string>();
        var current = new StringBuilder();
        char quote = '\0';

        foreach (var c in statement)
        {
            if (quote != '\0')
            {
                if (c == quote) quote = '\0';
                else current.Append(c);
                continue;
            }

            if (c == '"' || c == '\'') { quote = c; continue; }

            if (char.IsWhiteSpace(c))
            {
                if (current.Length > 0) { tokens.Add(current.ToString()); current.Clear(); }
                continue;
            }

            current.Append(c);
        }

        if (current.Length > 0) tokens.Add(current.ToString());
        return tokens;
    }

    /// <summary>
    /// The things a statement acts on, with switches and their values left out.
    /// <paramref name="namedTargets"/> names the parameters that carry a target for this
    /// particular command, since what counts as one differs: Remove-Item has -Path,
    /// Stop-Process has -Name, taskkill has /IM.
    /// </summary>
    private static List<string> TargetArguments(string statement, HashSet<string> namedTargets)
    {
        var tokens = TokenizeStatement(statement);
        var targets = new List<string>();

        for (int i = 1; i < tokens.Count; i++)   // token 0 is the command itself
        {
            var token = tokens[i];
            if (token.Length == 0) continue;

            if (token[0] == '-' || token[0] == '/')
            {
                bool carriesTarget = PathParameters.Contains(token) || namedTargets.Contains(token);
                if ((carriesTarget || ValueParameters.Contains(token)) && i + 1 < tokens.Count)
                {
                    if (carriesTarget) targets.Add(tokens[i + 1]);
                    i++;
                }
                continue;
            }

            targets.Add(token);
        }

        return targets;
    }

    private static string? FirstTarget(string statement, HashSet<string> namedTargets)
    {
        var targets = TargetArguments(statement, namedTargets);
        return targets.Count > 0 ? targets[0] : null;
    }

    private static string? SecondTarget(string statement)
    {
        var targets = TargetArguments(statement, PathParameters);
        return targets.Count > 1 ? targets[1] : null;
    }

    private static string? NamedParameterValue(string statement, string parameter)
    {
        var tokens = TokenizeStatement(statement);
        for (int i = 0; i < tokens.Count - 1; i++)
            if (string.Equals(tokens[i], parameter, StringComparison.OrdinalIgnoreCase))
                return tokens[i + 1];
        return null;
    }

    /// <summary>True when a bare switch such as /r or -a is present.</summary>
    private static bool HasSwitch(string statement, params string[] letters)
    {
        foreach (var token in TokenizeStatement(statement))
        {
            if (token.Length != 2 || (token[0] != '/' && token[0] != '-')) continue;
            foreach (var letter in letters)
                if (string.Equals(token[1..], letter, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>
    /// A target argument with every variable filled in, or null when part of it is still
    /// unknown — in which case the caller says something general rather than reading a
    /// half-resolved path out at the user.
    /// </summary>
    private static string? ExpandTarget(string raw, IReadOnlyDictionary<string, string> variables)
    {
        var text = ExpandVariables(raw.Trim().Trim('"', '\''), variables).Trim();
        return text.Length == 0 || text.IndexOf('$') >= 0 ? null : text;
    }

    /// <summary>True when a target names a folder's contents rather than the folder itself.</summary>
    private static bool EndsWithContentsWildcard(string path)
        => path.EndsWith("\\*", StringComparison.Ordinal) || path.EndsWith("/*", StringComparison.Ordinal);

    private static string LastSegment(string path)
    {
        int slash = path.LastIndexOfAny(['\\', '/']);
        return slash >= 0 && slash + 1 < path.Length ? path[(slash + 1)..] : path;
    }

    private static bool IsInside(string child, string parent)
    {
        if (parent.Length == 0) return false;
        if (!child.StartsWith(parent, StringComparison.OrdinalIgnoreCase)) return false;
        return child.Length == parent.Length ||
               child[parent.Length] == '\\' || child[parent.Length] == '/';
    }

    private static bool LooksLikeRegistryKey(string token)
        => token.StartsWith("HKEY_", StringComparison.OrdinalIgnoreCase) ||
           token.StartsWith("HKLM", StringComparison.OrdinalIgnoreCase) ||
           token.StartsWith("HKCU", StringComparison.OrdinalIgnoreCase) ||
           token.StartsWith("HKCR", StringComparison.OrdinalIgnoreCase) ||
           token.StartsWith("HKCC", StringComparison.OrdinalIgnoreCase) ||
           token.StartsWith("HKU", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A path written the way the user would say it: "Desktop\Games" rather than
    /// "C:\Users\Melvin\Desktop\Games", and shortened in the middle when it is long
    /// enough to swallow the sentence around it. The home folder itself is left in full,
    /// since a command aimed at all of it should not look small.
    /// </summary>
    private static string ShortenPath(string path)
    {
        var home = Environment.GetEnvironmentVariable("USERPROFILE");
        if (!string.IsNullOrEmpty(home) &&
            path.StartsWith(home, StringComparison.OrdinalIgnoreCase) &&
            path.Length > home.Length + 1)
            path = path[(home.Length + 1)..];

        if (path.Length <= 64) return path;

        int firstSlash = path.IndexOfAny(['\\', '/']);
        var head = firstSlash > 0 ? path[..firstSlash] : path[..8];
        return $"{head}\\…\\{LastSegment(path)}";
    }

    /// <summary>The names a process command was given, as a user would recognise them.</summary>
    private static List<string> ProgramNames(string statement, IReadOnlyDictionary<string, string> variables)
    {
        var names = new List<string>();

        foreach (var token in TargetArguments(statement, ProcessNameParameters))
        {
            var name = ExpandTarget(token, variables);
            if (name == null || name.IndexOf('*') >= 0) continue;

            // "-Name chrome, firefox" is one list, not one name with a comma stuck to it.
            name = name.Trim(',');
            if (name.EndsWith(".exe", StringComparison.OrdinalIgnoreCase)) name = name[..^4];
            if (name.Length == 0) continue;

            bool alreadyListed = false;
            foreach (var listed in names)
                if (string.Equals(listed, name, StringComparison.OrdinalIgnoreCase)) alreadyListed = true;
            if (alreadyListed) continue;

            names.Add(name);
            if (names.Count == 3) break;
        }

        return names;
    }

    private static string JoinNames(List<string> names)
    {
        if (names.Count == 1) return names[0];
        if (names.Count == 2) return $"{names[0]} and {names[1]}";
        return $"{string.Join(", ", names.GetRange(0, names.Count - 1))} and {names[^1]}";
    }

    /// <summary>
    /// The file an overwriting redirect writes over, or null when it cannot be named.
    /// Deliberately separate from HasOverwritingRedirect, which decides whether to ask at
    /// all and is left exactly as it was.
    /// </summary>
    private static string? RedirectTargetName(string command, IReadOnlyDictionary<string, string> variables)
    {
        for (int i = 0; i < command.Length; i++)
        {
            if (command[i] != '>') continue;
            if (i > 0 && command[i - 1] == '>') continue;
            if (i + 1 < command.Length && command[i + 1] == '>') continue;

            var rest = command[(i + 1)..].TrimStart();
            if (rest.StartsWith('&') || rest.StartsWith("$null", StringComparison.OrdinalIgnoreCase)) continue;

            var tokens = TokenizeStatement(rest);
            if (tokens.Count == 0) continue;

            var target = ExpandTarget(tokens[0], variables);
            return target == null ? null : ShortenPath(target.TrimEnd('\\', '/'));
        }

        return null;
    }

    /// <summary>True when any of the words appears as a whole word, not inside another.</summary>
    private static bool MentionsWord(string text, params string[] words)
    {
        foreach (var word in words)
        {
            int index = 0;
            while ((index = text.IndexOf(word, index, StringComparison.OrdinalIgnoreCase)) >= 0)
            {
                bool startsWord = index == 0 || !char.IsLetterOrDigit(text[index - 1]);
                int after = index + word.Length;
                bool endsWord = after >= text.Length || !char.IsLetterOrDigit(text[after]);
                if (startsWord && endsWord) return true;
                index = after;
            }
        }
        return false;
    }

    /// <summary>
    /// True when the command redirects into a file, replacing what was there. Appending
    /// with &gt;&gt; keeps the file, and redirecting a stream to $null or to another
    /// stream (2&gt;&amp;1) writes no file at all.
    /// </summary>
    private static bool HasOverwritingRedirect(string command)
    {
        for (int i = 0; i < command.Length; i++)
        {
            if (command[i] != '>') continue;
            if (i > 0 && command[i - 1] == '>') continue;
            if (i + 1 < command.Length && command[i + 1] == '>') continue;

            var target = command[(i + 1)..].TrimStart();
            if (target.StartsWith('&')) continue;
            if (target.StartsWith("$null", StringComparison.OrdinalIgnoreCase)) continue;
            return true;
        }
        return false;
    }

    // There is deliberately no P/Invoke to SetCursorPos, mouse_event, keybd_event or
    // SendInput in this file. Vayme shows the user where to click; the user clicks. The
    // synthetic-input code that used to live here was removed rather than left behind a
    // check, so no future edit can reach it by accident.
}
