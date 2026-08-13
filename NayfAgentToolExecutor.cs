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

/// <summary>The output of one tool call — text, plus an optional screenshot image.</summary>
public sealed class AgentToolResult
{
    public string Text { get; init; } = "";
    /// <summary>JPEG bytes when the tool produced a screenshot Claude should see.</summary>
    public byte[]? ScreenshotJpeg { get; init; }

    public static AgentToolResult Message(string text) => new() { Text = text };
}

/// <summary>What the current interaction is allowed to do on the user's machine.</summary>
public enum NayfToolMode
{
    /// <summary>
    /// Teaching. Nayf shows and speaks; the user performs every action themselves.
    /// Looking and pointing only — never actuation.
    /// </summary>
    GuidedWalkthrough,

    /// <summary>Background automation the user asked Nayf to carry out. Nayf acts.</summary>
    AgentTask
}

/// <summary>
/// Executes all agent tool types: PowerShell commands, file reads/writes, and screenshots.
/// Mirrors NayfAgentToolExecutor.swift.
/// Destructive shell commands require user confirmation before execution.
///
/// Nothing here drives the mouse or the keyboard. Nayf points at what the user should
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
    /// The last screenshot handed to the model, or null if it hasn't asked for one yet.
    ///
    /// Kept because a step's coordinates were read off this image, and pointing at the real
    /// spot means mapping them back to screen pixels — done by <see cref="NayfAgentManager"/>,
    /// which draws the step. Nothing here turns a coordinate into a click.
    /// </summary>
    public CapturedScreenshot? LastScreenshot { get; private set; }

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
            "github_list_issues" => AgentToolResult.Message(await CallWorkerIntegrationAsync("/integrations/github/list_issues", new(), authToken)),
            "github_list_pull_requests" => AgentToolResult.Message(await CallWorkerIntegrationAsync("/integrations/github/list_pull_requests", new(), authToken)),
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

        return await CallWorkerIntegrationAsync("/integrations/google/calendar/list_events", body, authToken);
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

        return await CallWorkerIntegrationAsync("/integrations/google/calendar/create_event", body, authToken);
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
        return await CallWorkerIntegrationAsync("/integrations/google/calendar/delete_event", body, authToken);
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

        return await CallWorkerIntegrationAsync("/integrations/github/create_issue", body, authToken);
    }

    // MARK: - Worker plumbing

    /// <summary>
    /// POSTs a JSON body to a Worker integration route with the user's auth token, returning
    /// the response text. Translates a 409 ("not connected") into a clear, actionable message
    /// so the model asks the user to connect rather than reporting a bare failure.
    /// </summary>
    private static async Task<string> CallWorkerIntegrationAsync(
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

            if (response.IsSuccessStatusCode) return responseText;
            if ((int)response.StatusCode == 409)
                return "That integration isn't connected yet. Tell the user to open Nayf, click \"Connect apps\", and connect it — then they can ask again.";

            return $"Integration request failed (HTTP {(int)response.StatusCode}): {responseText}";
        }
        catch (Exception ex)
        {
            return $"Integration request error: {ex.Message}";
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
    /// typed and pressed keys is gone rather than gated — Nayf shows the user where to
    /// click and they click it, in every mode, so there is nothing for that code to do.
    /// Still handled here because the model has been asking for this tool for a long time
    /// and may name it out of habit; being told to point is more use to it than "unknown
    /// tool", and asking to look is answered rather than refused.
    /// </summary>
    private async Task<AgentToolResult> ExecuteComputerToolAsync(Dictionary<string, object> input)
    {
        var action = input.TryGetValue("action", out var actionObj) ? actionObj?.ToString() ?? "" : "";
        if (action == "screenshot") return await TakeScreenshotAsync();

        Logger.Log("AgentTools", $"refused computer action '{action}' — Nayf never actuates");
        return AgentToolResult.Message(
            "Nayf never controls the mouse or keyboard. Don't click, type, drag, or press " +
            "keys on the user's behalf — there is no tool for it. Tell them what to click " +
            "and point at it with a [POINT] tag, and let them do it themselves.");
    }

    private async Task<AgentToolResult> TakeScreenshotAsync()
    {
        try
        {
            var screenshots = await ScreenCaptureUtility.CaptureAllScreensAsync();
            if (screenshots.Count == 0) return AgentToolResult.Message("No screenshot available");

            var shot = screenshots[0]; // primary display
            // Kept for pointing: a step's coordinates are read off this image, and the
            // monitor bounds on it are what map them back to the real screen.
            if (shot.ImageWidth > 0 && shot.ImageHeight > 0) LastScreenshot = shot;

            return new AgentToolResult
            {
                Text = $"Screenshot of the primary screen ({shot.ImageWidth}x{shot.ImageHeight}). " +
                       "Coordinates you read off this image are the pixel space you point in.",
                ScreenshotJpeg = shot.ImageData
            };
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

    /// <summary>
    /// Explains why a command needs the user's approval, or returns null when it can
    /// just run.
    ///
    /// Matching is on the command that *starts* each statement, never on the raw text.
    /// A plain substring search reads far too much as dangerous: "format-" matches
    /// Format-Table, the most common way to print anything in PowerShell; "del " sits
    /// inside "model "; "rd " inside "keyboard "; "kill " inside "skill ". Nayf ended
    /// up asking permission to list a folder, which teaches the user to approve
    /// without reading — the opposite of what a confirmation is for.
    /// </summary>
    private static string? DescribeCommandRisk(string command)
    {
        foreach (var statement in SplitIntoStatements(command))
        {
            var verb = LeadingCommandName(statement);
            if (verb.Length == 0) continue;

            if (DeletionCommands.Contains(verb))
                return "This permanently deletes files or erases their contents.";
            if (DiskCommands.Contains(verb))
                return "This modifies a disk volume or irreversibly erases data.";
            if (ProcessCommands.Contains(verb))
                return "This force-stops a running program or service.";
            if (PowerCommands.Contains(verb))
                return "This shuts down or restarts your PC.";
            if (PermissionCommands.Contains(verb))
                return "This changes who is allowed to access files on your PC.";

            // These two are only destructive with particular subcommands: `reg query`
            // and `net view` read, while `reg delete` and `net user` change the machine.
            if (verb.Equals("reg", StringComparison.OrdinalIgnoreCase) &&
                MentionsWord(statement, "delete", "import", "restore"))
                return "This changes the Windows registry.";
            if (verb.Equals("regedit", StringComparison.OrdinalIgnoreCase))
                return "This changes the Windows registry.";
            if (verb.Equals("net", StringComparison.OrdinalIgnoreCase) &&
                MentionsWord(statement, "user", "localgroup"))
                return "This changes the user accounts on your PC.";
        }

        if (MentionsWord(command, "runas"))
            return "This runs with administrator privileges.";
        if (HasOverwritingRedirect(command))
            return "This overwrites a file's contents.";

        return null;
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
    // SendInput in this file. Nayf shows the user where to click; the user clicks. The
    // synthetic-input code that used to live here was removed rather than left behind a
    // check, so no future edit can reach it by accident.
}
