using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// Executes all agent tool types: PowerShell commands, file reads/writes,
/// and computer control (screenshot, mouse, keyboard).
/// Mirrors NayfAgentToolExecutor.swift.
/// Destructive shell commands require user confirmation before execution.
/// </summary>
public sealed class NayfAgentToolExecutor
{
    public event Func<AgentConfirmationRequest, Task>? ConfirmationRequested;

    /// <summary>
    /// Executes a tool call and returns the result string for Claude's tool_result.
    /// </summary>
    public async Task<string> ExecuteToolAsync(
        AgentParsedToolCall toolCall,
        CancellationToken cancellationToken = default)
    {
        return toolCall.ToolName switch
        {
            "bash" => await ExecuteBashToolAsync(toolCall.InputJson, cancellationToken),
            "read_file" => ExecuteReadFileTool(toolCall.InputJson),
            "write_file" => ExecuteWriteFileTool(toolCall.InputJson),
            "computer" => await ExecuteComputerToolAsync(toolCall.InputJson, cancellationToken),
            _ => $"Unknown tool: {toolCall.ToolName}"
        };
    }

    private async Task<string> ExecuteBashToolAsync(
        Dictionary<string, object> input,
        CancellationToken cancellationToken)
    {
        if (!input.TryGetValue("command", out var commandObj)) return "Missing 'command' parameter";
        var command = commandObj?.ToString() ?? "";

        // Check if this is a destructive command that needs confirmation
        if (IsDestructiveCommand(command))
        {
            var confirmation = new AgentConfirmationRequest
            {
                CommandDescription = $"Run destructive command: {command}",
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
            // Truncate very large files to avoid overwhelming Claude's context
            if (content.Length > 50000)
                content = content[..50000] + $"\n\n[Truncated — file is {content.Length} chars]";
            return content;
        }
        catch (Exception ex)
        {
            return $"Error reading file: {ex.Message}";
        }
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
            if (!string.IsNullOrEmpty(directory))
                Directory.CreateDirectory(directory);

            File.WriteAllText(path, content);
            return $"Successfully wrote {content.Length} chars to {path}";
        }
        catch (Exception ex)
        {
            return $"Error writing file: {ex.Message}";
        }
    }

    private async Task<string> ExecuteComputerToolAsync(
        Dictionary<string, object> input,
        CancellationToken cancellationToken)
    {
        if (!input.TryGetValue("action", out var actionObj)) return "Missing 'action' parameter";
        var action = actionObj?.ToString() ?? "";

        return action switch
        {
            "screenshot" => await TakeScreenshotAsync(),
            "left_click" => ExecuteMouseClick(input, false),
            "right_click" => ExecuteMouseClick(input, true),
            "double_click" => ExecuteMouseDoubleClick(input),
            "type" => ExecuteTypeText(input),
            "key" => ExecutePressKey(input),
            _ => $"Unknown computer action: {action}"
        };
    }

    private async Task<string> TakeScreenshotAsync()
    {
        try
        {
            var screenshots = await ScreenCaptureUtility.CaptureAllScreensAsync();
            if (screenshots.Count == 0) return "No screenshot available";
            var primaryScreenshot = screenshots[0];
            return $"data:image/jpeg;base64,{Convert.ToBase64String(primaryScreenshot.ImageData)}";
        }
        catch (Exception ex)
        {
            return $"Screenshot failed: {ex.Message}";
        }
    }

    private string ExecuteMouseClick(Dictionary<string, object> input, bool rightClick)
    {
        if (!input.TryGetValue("coordinate", out var coordObj)) return "Missing 'coordinate' parameter";

        try
        {
            int x = 0, y = 0;
            if (coordObj is JsonElement coordEl && coordEl.ValueKind == JsonValueKind.Array)
            {
                x = coordEl[0].GetInt32();
                y = coordEl[1].GetInt32();
            }

            SetCursorPos(x, y);
            var mouseEvent = rightClick ? MOUSEEVENTF_RIGHTDOWN | MOUSEEVENTF_RIGHTUP
                                        : MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP;
            mouse_event(mouseEvent, 0, 0, 0, 0);
            return $"{(rightClick ? "Right" : "Left")} clicked at ({x}, {y})";
        }
        catch (Exception ex)
        {
            return $"Click failed: {ex.Message}";
        }
    }

    private string ExecuteMouseDoubleClick(Dictionary<string, object> input)
    {
        if (!input.TryGetValue("coordinate", out var coordObj)) return "Missing 'coordinate' parameter";

        try
        {
            int x = 0, y = 0;
            if (coordObj is JsonElement coordEl && coordEl.ValueKind == JsonValueKind.Array)
            {
                x = coordEl[0].GetInt32();
                y = coordEl[1].GetInt32();
            }
            SetCursorPos(x, y);
            mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            mouse_event(MOUSEEVENTF_LEFTDOWN | MOUSEEVENTF_LEFTUP, 0, 0, 0, 0);
            return $"Double clicked at ({x}, {y})";
        }
        catch (Exception ex)
        {
            return $"Double click failed: {ex.Message}";
        }
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
                    new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 4 /* KEYEVENTF_UNICODE */ } } },
                    new() { type = 1, u = new InputUnion { ki = new KEYBDINPUT { wVk = 0, wScan = c, dwFlags = 4 | 2 /* KEYEVENTF_UNICODE | KEYEVENTF_KEYUP */ } } }
                };
                SendInput(2, inputs, System.Runtime.InteropServices.Marshal.SizeOf<INPUT>());
            }
            return $"Typed {text.Length} characters";
        }
        catch (Exception ex)
        {
            return $"Type failed: {ex.Message}";
        }
    }

    private string ExecutePressKey(Dictionary<string, object> input)
    {
        if (!input.TryGetValue("key", out var keyObj)) return "Missing 'key' parameter";
        var keyName = keyObj?.ToString() ?? "";

        try
        {
            ushort vk = keyName.ToLower() switch
            {
                "return" or "enter" => 0x0D,
                "escape" or "esc" => 0x1B,
                "tab" => 0x09,
                "backspace" => 0x08,
                "delete" => 0x2E,
                "space" => 0x20,
                "up" => 0x26,
                "down" => 0x28,
                "left" => 0x25,
                "right" => 0x27,
                "ctrl+c" or "ctrl+c" => 0, // handled specially below
                _ => 0
            };

            if (keyName.ToLower() == "ctrl+c")
            {
                keybd_event(0x11 /* VK_CONTROL */, 0, 0, 0);
                keybd_event(0x43 /* VK_C */, 0, 0, 0);
                keybd_event(0x43, 0, 2, 0); // KEYEVENTF_KEYUP
                keybd_event(0x11, 0, 2, 0);
            }
            else if (vk != 0)
            {
                keybd_event((byte)vk, 0, 0, 0);
                keybd_event((byte)vk, 0, 2, 0);
            }

            return $"Pressed key: {keyName}";
        }
        catch (Exception ex)
        {
            return $"Key press failed: {ex.Message}";
        }
    }

    private static async Task<string> RunPowerShellCommandAsync(
        string command, CancellationToken cancellationToken)
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
        catch (OperationCanceledException)
        {
            return "Command timed out after 60 seconds";
        }
        catch (Exception ex)
        {
            return $"Command failed: {ex.Message}";
        }
    }

    /// <summary>
    /// Returns true for commands that could delete files, modify system state,
    /// or execute shells within shells — these require user confirmation.
    /// </summary>
    private static bool IsDestructiveCommand(string command)
    {
        var lower = command.ToLowerInvariant();
        string[] destructivePatterns =
        [
            "remove-item", "del ", "rd ", "rmdir", "rm -",
            "format-", "stop-process", "kill ",
            "reg delete", "regedit",
            "net user", "net localgroup",
            "shutdown", "restart-computer",
            "clear-content", "> "
        ];

        foreach (var pattern in destructivePatterns)
        {
            if (lower.Contains(pattern)) return true;
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
