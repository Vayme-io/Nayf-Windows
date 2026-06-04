using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// Claude API client with SSE streaming support. Mirrors ClaudeAPI.swift.
/// All requests are proxied through the Cloudflare Worker so API keys
/// never live in this binary.
/// </summary>
public class ClaudeAPI
{
    public string Model { get; set; }

    private readonly Uri _apiUrl;
    private readonly HttpClient _httpClient;

    public ClaudeAPI(string proxyUrl, string model = NayfConfig.DefaultModel)
    {
        _apiUrl = new Uri(proxyUrl);
        Model = model;

        var handler = new SocketsHttpHandler
        {
            PooledConnectionLifetime = TimeSpan.FromMinutes(10),
            KeepAlivePingPolicy = HttpKeepAlivePingPolicy.WithActiveRequests
        };
        _httpClient = new HttpClient(handler)
        {
            Timeout = TimeSpan.FromSeconds(120)
        };

        // Fire a background TLS warmup request so the first real API call
        // (which carries a large image payload) doesn't incur a cold handshake.
        _ = WarmUpTlsConnectionAsync();
    }

    private async Task WarmUpTlsConnectionAsync()
    {
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Head, _apiUrl);
            await _httpClient.SendAsync(request, CancellationToken.None).ConfigureAwait(false);
        }
        catch { /* Expected — the endpoint doesn't handle HEAD */ }
    }

    /// <summary>
    /// Streams a Claude response via SSE. Calls <paramref name="onTextDelta"/> for
    /// each text chunk as it arrives, then returns the complete assembled response.
    /// Accepts optional image data (JPEG or PNG) from screen captures.
    /// </summary>
    public async Task<string> StreamResponseAsync(
        string userTranscript,
        List<ConversationTurn> conversationHistory,
        List<CapturedScreenshot>? screenshots,
        Action<string>? onTextDelta,
        string systemPrompt,
        string? authorizationToken = null,
        CancellationToken cancellationToken = default)
    {
        var messages = BuildMessageArray(userTranscript, conversationHistory, screenshots);
        var requestBody = new
        {
            model = Model,
            max_tokens = 2048,
            system = systemPrompt,
            stream = true,
            messages
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl) { Content = httpContent };

        if (authorizationToken != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorizationToken);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await ReadSseStreamAsync(response, onTextDelta, cancellationToken);
    }

    private async Task<string> ReadSseStreamAsync(
        HttpResponseMessage response,
        Action<string>? onTextDelta,
        CancellationToken cancellationToken)
    {
        var fullText = new StringBuilder();
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break; // EOF
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;

                // SSE event types: content_block_delta carries text deltas
                if (root.TryGetProperty("type", out var typeEl) &&
                    typeEl.GetString() == "content_block_delta")
                {
                    if (root.TryGetProperty("delta", out var delta) &&
                        delta.TryGetProperty("type", out var deltaType) &&
                        deltaType.GetString() == "text_delta" &&
                        delta.TryGetProperty("text", out var textEl))
                    {
                        var text = textEl.GetString() ?? "";
                        fullText.Append(text);
                        onTextDelta?.Invoke(text);
                    }
                }
            }
            catch (JsonException) { /* Ignore malformed SSE lines */ }
        }

        return fullText.ToString();
    }

    /// <summary>
    /// Executes one turn of an agentic tool-use loop, returning both any text
    /// content and any tool calls Claude wants to make.
    /// </summary>
    public async Task<AgentTurnResult> ExecuteAgentTurnAsync(
        List<object> messages,
        List<object> tools,
        string systemPrompt,
        string? authorizationToken = null,
        Action<string>? onTextDelta = null,
        CancellationToken cancellationToken = default)
    {
        var requestBody = new
        {
            model = Model,
            max_tokens = 4096,
            system = systemPrompt,
            stream = true,
            tools,
            messages
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl) { Content = httpContent };

        if (authorizationToken != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorizationToken);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        return await ReadAgentSseStreamAsync(response, onTextDelta, cancellationToken);
    }

    private async Task<AgentTurnResult> ReadAgentSseStreamAsync(
        HttpResponseMessage response,
        Action<string>? onTextDelta,
        CancellationToken cancellationToken)
    {
        var fullText = new StringBuilder();
        var toolCalls = new List<AgentParsedToolCall>();
        string stopReason = "end_turn";

        // Tool input accumulation
        string? currentToolUseId = null;
        string? currentToolName = null;
        var currentInputJson = new StringBuilder();

        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (line == null) break; // EOF
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line["data: ".Length..];
            if (data == "[DONE]") break;

            try
            {
                using var doc = JsonDocument.Parse(data);
                var root = doc.RootElement;
                if (!root.TryGetProperty("type", out var typeEl)) continue;
                var eventType = typeEl.GetString();

                switch (eventType)
                {
                    case "content_block_start":
                        if (root.TryGetProperty("content_block", out var block))
                        {
                            if (block.TryGetProperty("type", out var blockType) &&
                                blockType.GetString() == "tool_use")
                            {
                                currentToolUseId = block.TryGetProperty("id", out var idEl) ? idEl.GetString() : null;
                                currentToolName = block.TryGetProperty("name", out var nameEl) ? nameEl.GetString() : null;
                                currentInputJson.Clear();
                            }
                        }
                        break;

                    case "content_block_delta":
                        if (root.TryGetProperty("delta", out var delta))
                        {
                            if (delta.TryGetProperty("type", out var deltaType))
                            {
                                if (deltaType.GetString() == "text_delta" &&
                                    delta.TryGetProperty("text", out var textEl))
                                {
                                    var text = textEl.GetString() ?? "";
                                    fullText.Append(text);
                                    onTextDelta?.Invoke(text);
                                }
                                else if (deltaType.GetString() == "input_json_delta" &&
                                         delta.TryGetProperty("partial_json", out var partialJson))
                                {
                                    currentInputJson.Append(partialJson.GetString());
                                }
                            }
                        }
                        break;

                    case "content_block_stop":
                        if (currentToolUseId != null && currentToolName != null)
                        {
                            var inputDict = new Dictionary<string, object>();
                            try
                            {
                                using var inputDoc = JsonDocument.Parse(currentInputJson.ToString());
                                foreach (var prop in inputDoc.RootElement.EnumerateObject())
                                    inputDict[prop.Name] = prop.Value.Clone();
                            }
                            catch { /* Ignore parse errors */ }

                            toolCalls.Add(new AgentParsedToolCall
                            {
                                ToolUseId = currentToolUseId,
                                ToolName = currentToolName,
                                InputJson = inputDict
                            });
                            currentToolUseId = null;
                            currentToolName = null;
                        }
                        break;

                    case "message_delta":
                        if (root.TryGetProperty("delta", out var msgDelta) &&
                            msgDelta.TryGetProperty("stop_reason", out var stopEl))
                        {
                            stopReason = stopEl.GetString() ?? "end_turn";
                        }
                        break;
                }
            }
            catch (JsonException) { /* Ignore malformed events */ }
        }

        return new AgentTurnResult
        {
            TextContent = fullText.ToString(),
            ToolCalls = toolCalls,
            StopReason = stopReason
        };
    }

    private List<object> BuildMessageArray(
        string userTranscript,
        List<ConversationTurn> history,
        List<CapturedScreenshot>? screenshots)
    {
        var messages = new List<object>();

        // Add historical turns
        foreach (var turn in history)
        {
            var userContent = new List<object> { new { type = "text", text = turn.UserTranscript } };
            if (turn.AttachedImageData != null)
            {
                userContent.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = DetectImageMediaType(turn.AttachedImageData),
                        data = Convert.ToBase64String(turn.AttachedImageData)
                    }
                });
            }
            messages.Add(new { role = "user", content = userContent });
            messages.Add(new { role = "assistant", content = turn.AssistantResponse });
        }

        // Build the current user message with transcript + screenshots
        var currentContent = new List<object>();

        if (screenshots != null)
        {
            foreach (var screenshot in screenshots)
            {
                currentContent.Add(new
                {
                    type = "image",
                    source = new
                    {
                        type = "base64",
                        media_type = "image/jpeg",
                        data = Convert.ToBase64String(screenshot.ImageData)
                    }
                });
                currentContent.Add(new
                {
                    type = "text",
                    text = $"[{screenshot.ScreenLabel}]"
                });
            }
        }

        currentContent.Add(new { type = "text", text = userTranscript });
        messages.Add(new { role = "user", content = currentContent });
        return messages;
    }

    private static string DetectImageMediaType(byte[] imageData)
    {
        // PNG files start with the 8-byte signature: 89 50 4E 47 0D 0A 1A 0A
        if (imageData.Length >= 4)
        {
            byte[] pngSig = [0x89, 0x50, 0x4E, 0x47];
            bool isPng = true;
            for (int i = 0; i < 4; i++)
            {
                if (imageData[i] != pngSig[i]) { isPng = false; break; }
            }
            if (isPng) return "image/png";
        }
        return "image/jpeg";
    }
}
