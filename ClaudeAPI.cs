using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
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
            system = CacheableSystem(systemPrompt),
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

                if (root.TryGetProperty("type", out var startEl) &&
                    startEl.GetString() == "message_start")
                    LogUsage(root);

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
    /// One short spoken acknowledgment, so something can be said while the main turn is
    /// still reasoning. The main call cannot supply this: thinking blocks stream before
    /// any text block, so its first words arrive far too late to open with.
    ///
    /// Deliberately given none of what makes the main call slow — no tools, no
    /// screenshot, no memory block. Non-streaming, because there is nothing to stream in
    /// one sentence.
    ///
    /// <paramref name="previousLine"/> is the exception, and it earns its tokens. Half of
    /// what people say is only meaningful against what was just said to them: someone who
    /// answers "yeah" to "want me to show you materials?" has asked for something specific,
    /// but the word on its own carries none of it. Told to echo back a request it cannot
    /// see, the model wrote lines like "let me work out what you're actually asking" —
    /// which lands as Vayme having forgotten the conversation it is in the middle of.
    /// </summary>
    public async Task<string> FetchAcknowledgmentAsync(
        string transcript, string? previousLine, string authToken,
        CancellationToken cancellationToken)
    {
        var messages = new List<object>();

        // As an assistant turn rather than pasted into the user message, so the model reads
        // it as something Vayme said and the reply as an answer to it.
        if (!string.IsNullOrWhiteSpace(previousLine))
            messages.Add(new { role = "assistant", content = previousLine });

        messages.Add(new { role = "user", content = transcript });

        var requestBody = new
        {
            model = Model,
            max_tokens = 64,
            system = AcknowledgmentSystemPrompt,
            messages
        };

        var json = JsonSerializer.Serialize(requestBody);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl) { Content = httpContent };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authToken);

        // Short and fixed: an acknowledgment that can't beat the main turn has no value,
        // so give up rather than hold the real answer behind a stalled connection.
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        var ct = timeout.Token;

        using var response = await _httpClient.SendAsync(request, ct);
        response.EnsureSuccessStatusCode();

        var body = await response.Content.ReadAsStringAsync(ct);
        using var doc = JsonDocument.Parse(body);
        if (!doc.RootElement.TryGetProperty("content", out var content)) return "";

        var text = new StringBuilder();
        foreach (var block in content.EnumerateArray())
        {
            if (block.TryGetProperty("type", out var type) && type.GetString() == "text" &&
                block.TryGetProperty("text", out var textEl))
                text.Append(textEl.GetString());
        }
        return text.ToString().Trim();
    }

    /// <summary>
    /// Three rules here are load-bearing rather than stylistic. The line must announce that
    /// Vayme is going away to work, because what follows it is silence — without that the
    /// user hears a complete-sounding reply and is then confused by the pause. It must
    /// promise nothing concrete: it is written without seeing the screen, so any specific
    /// commitment it makes may be wrong by the time the real turn looks. And when the user
    /// is only saying yes, it must stop trying to echo them: the words of a "yeah" carry no
    /// request, and a line that goes looking for one in them sounds like Vayme has lost the
    /// thread of its own question.
    /// </summary>
    private const string AcknowledgmentSystemPrompt =
        """
        You write a single short spoken line that acknowledges what the user just asked for,
        in Vayme's voice: warm, casual, lowercase, never corporate. One sentence, max 12 words.

        Echo back what they want so it sounds like you listened, AND make clear you're about
        to go away and work on it for a moment — the user will hear this line and then
        silence while you think, so it must set that expectation ("let me think this
        through", "gimme a moment to work this out"). Without that they assume you're
        finished and get confused by the pause.

        When there is a previous line from you above, the user is replying to it, and it is
        the offer — not their words — that says what was asked for. If they are simply
        agreeing ("yeah", "sure", "go on", "do it", "please"), do NOT try to work out what
        they meant and do NOT restate the offer back to them; they know, they just said yes
        to it. Accept and go: "on it — one sec.", "yep, gimme a moment." Reaching for their
        meaning here is the failure — it sounds like you have forgotten what you just
        offered.

        NEVER answer the question. NEVER explain how you'll do it. NEVER promise a specific
        outcome or step. NEVER ask a question. NEVER say you are working out what they meant
        or what they are actually asking. Output the sentence only — no quotes, no tags.

        Examples:
        "ok, let me take a proper look at your timeline."
        "got it — give me a sec to think this one through."
        "sure, let me read the screen and work it out."
        "on it — one sec."           (after they said yes to something you offered)
        "yep, gimme a moment."       (same)
        """;

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
            system = CacheableSystem(systemPrompt),
            stream = true,
            tools,
            messages
        };

        var json = SerializeWithConversationCacheBreakpoint(requestBody);
        using var httpContent = new StringContent(json, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _apiUrl) { Content = httpContent };

        if (authorizationToken != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorizationToken);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);

        // Not EnsureSuccessStatusCode: it throws away the body, and the body is the entire
        // content of the message. A 4xx here says something specific and actionable — the
        // request is over the context window, a message is malformed, the account is out of
        // credit — and all of that was being reduced to "400 (Bad Request)" in the log, which
        // is unactionable and looks identical to every other cause.
        if (!response.IsSuccessStatusCode)
        {
            var errorBody = await response.Content.ReadAsStringAsync(cancellationToken);
            Logger.Log("ClaudeAPI", $"HTTP {(int)response.StatusCode}: {Truncate(errorBody, 600)}");
            throw new HttpRequestException(
                $"Claude returned {(int)response.StatusCode}: {Truncate(errorBody, 300)}");
        }

        return await ReadAgentSseStreamAsync(response, onTextDelta, cancellationToken);
    }

    private static string Truncate(string text, int max)
        => string.IsNullOrEmpty(text) ? "(empty body)"
         : text.Length <= max ? text
         : text[..max] + "…";

    // MARK: - Prompt caching
    //
    // Nothing below changes a single token the model reads. Caching is a billing and
    // latency mechanism: the same prompt is sent either way, and Claude answers it the
    // same way. What changes is that the parts which are identical call after call stop
    // being re-uploaded and re-charged at full price on every one of them.
    //
    // It matters here more than it would in most apps because the agent loop re-sends
    // the entire request every time round — system prompt, tools, transcript, every
    // screenshot — and a walkthrough goes round it fifty times. Uncached, step fifty
    // pays for step one's screenshot for the fiftieth time.

    /// <summary>
    /// The system prompt as a content block that closes a cache breakpoint.
    ///
    /// <para>Caching is prefix-based and ordered tools → system → messages, so this one
    /// breakpoint covers the tool definitions too — the two largest fixed pieces of every
    /// request, together several thousand tokens, previously re-billed in full on every
    /// iteration. Cached, the first call pays 1.25× for them and every later one 0.1×.</para>
    ///
    /// <para>Below the smallest cacheable prefix a breakpoint simply does nothing, so the
    /// short-prompt path returns a plain block rather than planting a marker that could
    /// never pay for itself.</para>
    /// </summary>
    private static object[] CacheableSystem(string systemPrompt)
    {
        // The minimum cacheable prefix is ~1,024 tokens, and four characters to the token
        // is the usual rule of thumb for prose. Anything shorter — memory extraction, the
        // acknowledgment — goes through unmarked.
        const int MinCacheableChars = 4_500;

        if (systemPrompt.Length < MinCacheableChars)
            return new object[] { new { type = "text", text = systemPrompt } };

        return new object[]
        {
            new
            {
                type = "text",
                text = systemPrompt,
                cache_control = new { type = "ephemeral" }
            }
        };
    }

    /// <summary>
    /// Serialises the request with a second cache breakpoint at the end of the
    /// conversation, so each pass of the agent loop reads the previous pass's messages
    /// back out of cache instead of re-uploading them.
    ///
    /// <para>The breakpoint rolls forward by itself. What iteration N writes is exactly
    /// the prefix iteration N+1 sends, so N+1 reads all of it at 0.1× and writes only
    /// the messages it added. In a long walkthrough the transcript, the tool results and
    /// the screenshots are most of the payload and grow at every step; this is the
    /// difference between paying for that history once and paying for it once per step.</para>
    ///
    /// <para>Done on the serialised JSON rather than in the caller because the message
    /// list is built from anonymous types whose content is sometimes a string and
    /// sometimes an array of blocks. Both have to end up carrying the marker on their
    /// final block, and the JSON is where they finally have one shape in common.</para>
    /// </summary>
    private static string SerializeWithConversationCacheBreakpoint<T>(T requestBody)
    {
        // Generic rather than object-typed on purpose: serialising through a parameter
        // declared as object would write the declared type and produce "{}".
        var root = JsonSerializer.SerializeToNode(requestBody) as JsonObject;
        if (root is null) return JsonSerializer.Serialize(requestBody);

        if (root["messages"] is not JsonArray messages || messages.Count == 0)
            return root.ToJsonString();

        if (messages[^1] is not JsonObject last) return root.ToJsonString();

        switch (last["content"])
        {
            // The usual shape by the time the loop is running: tool results, text and
            // images sitting side by side as separate blocks.
            case JsonArray blocks when blocks.Count > 0 && blocks[^1] is JsonObject block:
                block["cache_control"] = EphemeralCacheControl();
                break;

            // A plain-string message — the opening user request — which has to become a
            // block before it can carry the marker.
            case JsonValue value when value.TryGetValue<string>(out var text):
                last["content"] = new JsonArray(
                    new JsonObject
                    {
                        ["type"] = "text",
                        ["text"] = text,
                        ["cache_control"] = EphemeralCacheControl()
                    });
                break;
        }

        return root.ToJsonString();
    }

    private static JsonObject EphemeralCacheControl() => new() { ["type"] = "ephemeral" };

    /// <summary>
    /// Records what one request actually cost, from the usage block Claude puts on its
    /// <c>message_start</c> event.
    ///
    /// <para>Spend is the one thing about this app that has never been observable from the
    /// inside: the proxy counts output tokens to bill the user and throws the input count
    /// away, so a request that quietly sends half a million tokens looks exactly like one
    /// that sends five thousand. Four numbers a line in the log is enough to see which.</para>
    /// </summary>
    private void LogUsage(JsonElement root)
    {
        if (!root.TryGetProperty("message", out var message) ||
            !message.TryGetProperty("usage", out var usage))
            return;

        static int Count(JsonElement usage, string name)
            => usage.TryGetProperty(name, out var el) && el.TryGetInt32(out var n) ? n : 0;

        int input = Count(usage, "input_tokens");
        int cacheRead = Count(usage, "cache_read_input_tokens");
        int cacheWrite = Count(usage, "cache_creation_input_tokens");
        int output = Count(usage, "output_tokens");

        Logger.Log("Usage",
            $"{Model}: in={input} cached={cacheRead} wrote={cacheWrite} out={output}");
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
                    // Not needed to assemble the reply — logged because the cost of running
                    // Vayme is otherwise invisible from in here. These four numbers are the
                    // only way to tell a working prompt cache from a silently broken one:
                    // "cached" should be nearly the whole prompt from the second iteration
                    // of a loop onward, and "in" should fall to just the newest messages.
                    case "message_start":
                        LogUsage(root);
                        break;

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
