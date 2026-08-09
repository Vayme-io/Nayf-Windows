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

/// <summary>
/// Executes all agent tool types: PowerShell commands, file reads/writes,
/// and computer control (screenshot, mouse, keyboard).
/// Mirrors NayfAgentToolExecutor.swift.
/// Destructive shell commands require user confirmation before execution.
/// </summary>
public sealed class NayfAgentToolExecutor
{
    public event Func<AgentConfirmationRequest, Task>? ConfirmationRequested;

    // Maps the last screenshot's image space to real screen pixels so clicks land
    // correctly (screenshots are downscaled and may be on a non-primary monitor).
    private float _shotScaleX = 1f, _shotScaleY = 1f;
    private int _shotOffsetX, _shotOffsetY;

    /// <summary>Executes a tool call and returns the result for Claude's tool_result.</summary>
    public async Task<AgentToolResult> ExecuteToolAsync(
        AgentParsedToolCall toolCall,
        CancellationToken cancellationToken = default,
        string? authToken = null)
    {
        return toolCall.ToolName switch
        {
            "bash" => AgentToolResult.Message(await ExecuteBashToolAsync(toolCall.InputJson, cancellationToken)),
            "read_file" => AgentToolResult.Message(ExecuteReadFileTool(toolCall.InputJson)),
            "write_file" => AgentToolResult.Message(ExecuteWriteFileTool(toolCall.InputJson)),
            "computer" => await ExecuteComputerToolAsync(toolCall.InputJson, cancellationToken),
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

    private async Task<AgentToolResult> ExecuteComputerToolAsync(
        Dictionary<string, object> input,
        CancellationToken cancellationToken)
    {
        if (!input.TryGetValue("action", out var actionObj)) return AgentToolResult.Message("Missing 'action' parameter");
        var action = actionObj?.ToString() ?? "";

        return action switch
        {
            "screenshot" => await TakeScreenshotAsync(),
            "left_click" => AgentToolResult.Message(ExecuteMouseClick(input, false)),
            "right_click" => AgentToolResult.Message(ExecuteMouseClick(input, true)),
            "double_click" => AgentToolResult.Message(ExecuteMouseDoubleClick(input)),
            "type" => AgentToolResult.Message(ExecuteTypeText(input)),
            "key" => AgentToolResult.Message(ExecutePressKey(input)),
            _ => AgentToolResult.Message($"Unknown computer action: {action}")
        };
    }

    private async Task<AgentToolResult> TakeScreenshotAsync()
    {
        try
        {
            var screenshots = await ScreenCaptureUtility.CaptureAllScreensAsync();
            if (screenshots.Count == 0) return AgentToolResult.Message("No screenshot available");

            var shot = screenshots[0]; // primary display
            // Remember how to convert image coordinates → screen pixels for clicks.
            if (shot.ImageWidth > 0 && shot.ImageHeight > 0)
            {
                _shotScaleX = (float)shot.MonitorWidth / shot.ImageWidth;
                _shotScaleY = (float)shot.MonitorHeight / shot.ImageHeight;
                _shotOffsetX = shot.MonitorLeft;
                _shotOffsetY = shot.MonitorTop;
            }

            return new AgentToolResult
            {
                Text = $"Screenshot of the primary screen ({shot.ImageWidth}x{shot.ImageHeight}). " +
                       "Click using coordinates within this image.",
                ScreenshotJpeg = shot.ImageData
            };
        }
        catch (Exception ex)
        {
            return AgentToolResult.Message($"Screenshot failed: {ex.Message}");
        }
    }

    private bool TryGetScreenPoint(Dictionary<string, object> input, out int x, out int y)
    {
        x = 0; y = 0;
        if (!input.TryGetValue("coordinate", out var coordObj)) return false;
        int ix = 0, iy = 0;
        if (coordObj is JsonElement el && el.ValueKind == JsonValueKind.Array && el.GetArrayLength() >= 2)
        {
            ix = el[0].GetInt32();
            iy = el[1].GetInt32();
        }
        else return false;

        // Map from downscaled image space to actual screen pixels.
        x = _shotOffsetX + (int)Math.Round(ix * _shotScaleX);
        y = _shotOffsetY + (int)Math.Round(iy * _shotScaleY);
        return true;
    }

    private string ExecuteMouseClick(Dictionary<string, object> input, bool rightClick)
    {
        if (!TryGetScreenPoint(input, out int x, out int y)) return "Missing/invalid 'coordinate' parameter";
        try
        {
            SetCursorPos(x, y);
            var ev = rightClick ? MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP
                                : MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP;
            mouse_event(ev, 0, 0, 0, 0);
            return $"{(rightClick ? "Right" : "Left")} clicked at screen ({x}, {y})";
        }
        catch (Exception ex) { return $"Click failed: {ex.Message}"; }
    }

    private string ExecuteMouseDoubleClick(Dictionary<string, object> input)
    {
        if (!TryGetScreenPoint(input, out int x, out int y)) return "Missing/invalid 'coordinate' parameter";
        try
        {
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            return $"Double clicked at screen ({x}, {y})";
        }
        catch (Exception ex) { return $"Double click failed: {ex.Message}"; }
    }

    private string ExecuteTypeText(Dictionary<string, object> input)
    {
        if (!input.TryGetValue("text", out var textObj)) return "Missing 'text' parameter";
        var text = textObj?.ToString() ?? "";
        try
        {
            foreach (char c in text)
            {
                var inputs = new INPUT[]
                {
                    new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 4 } } },
                    new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 4 | 2 } } }
                };
                SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
            }
            return $"Typed {text.Length} characters";
        }
        catch (Exception ex) { return $"Type failed: {ex.Message}"; }
    }

    /// <summary>
    /// Presses a key or key combination, e.g. "enter", "ctrl+l", "ctrl+shift+p".
    /// "cmd"/"win"/"command" are treated as Ctrl (Windows uses Ctrl for the
    /// shortcuts Claude tends to reach for on the Mac).
    /// </summary>
    private string ExecutePressKey(Dictionary<string, object> input)
    {
        if (!input.TryGetValue("key", out var keyObj)) return "Missing 'key' parameter";
        var keyName = (keyObj?.ToString() ?? "").Trim();
        if (keyName.Length == 0) return "Empty 'key'";

        try
        {
            var parts = keyName.Split('+', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            var modifiers = new List<byte>();
            byte mainVk = 0;

            foreach (var p in parts)
            {
                switch (p.ToLowerInvariant())
                {
                    case "ctrl": case "control": case "cmd": case "command": case "win": case "super": case "meta":
                        modifiers.Add(0x11); break; // VK_CONTROL
                    case "shift": modifiers.Add(0x10); break; // VK_SHIFT
                    case "alt": case "option": modifiers.Add(0x12); break; // VK_MENU
                    default: mainVk = KeyNameToVk(p); break;
                }
            }
            if (mainVk == 0) return $"Unknown key: {keyName}";

            foreach (var m in modifiers) keybd_event(m, 0, 0, 0);
            keybd_event(mainVk, 0, 0, 0);
            keybd_event(mainVk, 0, 2, 0); // KEYEVENTF_KEYUP
            for (int i = modifiers.Count - 1; i >= 0; i--) keybd_event(modifiers[i], 0, 2, 0);
            return $"Pressed: {keyName}";
        }
        catch (Exception ex) { return $"Key press failed: {ex.Message}"; }
    }

    private static byte KeyNameToVk(string key)
    {
        switch (key.ToLowerInvariant())
        {
            case "return": case "enter": return 0x0D;
            case "escape": case "esc": return 0x1B;
            case "tab": return 0x09;
            case "backspace": return 0x08;
            case "delete": case "del": return 0x2E;
            case "space": case "spacebar": return 0x20;
            case "up": return 0x26; case "down": return 0x28;
            case "left": return 0x25; case "right": return 0x27;
            case "home": return 0x24; case "end": return 0x23;
            case "pageup": return 0x21; case "pagedown": return 0x22;
        }
        if (key.Length == 1 && key[0] >= 'a' && key[0] <= 'z') return (byte)(0x41 + (key[0] - 'a'));
        if (key.Length == 1 && key[0] >= 'A' && key[0] <= 'Z') return (byte)(0x41 + (key[0] - 'A'));
        if (key.Length == 1 && key[0] >= '0' && key[0] <= '9') return (byte)(0x30 + (key[0] - '0'));
        if (key.Length >= 2 && key[0] == 'f' && int.TryParse(key.Substring(1), out int fn) && fn is >= 1 and <= 12)
            return (byte)(0x70 + fn - 1);
        return 0;
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

    // P/Invoke for mouse/keyboard control
    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern bool SetCursorPos(int X, int Y);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void mouse_event(uint dwFlags, uint dx, uint dy, uint dwData, uint dwExtraInfo);

    private const uint MOUSEEVENTF_LEFTDOWN = 0x0002;
    private const uint MOUSEEVENTF_LEFTUP = 0x0004;
    private const uint MOUSEEVENTF_RIGHTDOWN = 0x0008;
    private const uint MOUSEEVENTF_RIGHTUP = 0x0010;

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern void keybd_event(byte bVk, byte bScan, uint dwFlags, uint dwExtraInfo);

    [System.Runtime.InteropServices.DllImport("user32.dll")]
    private static extern uint SendInput(uint nInputs, INPUT[] pInputs, int cbSize);

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct INPUT
    {
        public uint type;
        public InputUnion u;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Explicit)]
    private struct InputUnion
    {
        [System.Runtime.InteropServices.FieldOffset(0)]
        public KEYBDINPUT ki;
    }

    [System.Runtime.InteropServices.StructLayout(System.Runtime.InteropServices.LayoutKind.Sequential)]
    private struct KEYBDINPUT
    {
        public ushort wVk;
        public ushort wScan;
        public uint dwFlags;
        public uint time;
        public IntPtr dwExtraInfo;
    }
}
