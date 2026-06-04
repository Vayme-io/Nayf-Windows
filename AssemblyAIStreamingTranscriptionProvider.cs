using System;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// AssemblyAI real-time streaming transcription provider.
/// Mirrors AssemblyAIStreamingTranscriptionProvider.swift:
/// - Fetches a short-lived websocket token from the Cloudflare Worker
/// - Opens an AssemblyAI v3 websocket
/// - Streams PCM16 audio chunks
/// - Delivers finalized transcript text on key-up
/// </summary>
public sealed class AssemblyAIStreamingTranscriptionProvider : IDisposable
{
    public event Action<string>? TranscriptReceived;
    public event Action<string>? PartialTranscriptReceived;

    private readonly string _tokenEndpoint;
    private readonly HttpClient _httpClient;
    private ClientWebSocket? _webSocket;
    private CancellationTokenSource? _connectionCts;

    // A shared HttpClient is used for token fetches — matches the Mac's
    // "shared URLSession" approach to avoid connection pool corruption.
    private static readonly HttpClient SharedHttpClient = new();

    public AssemblyAIStreamingTranscriptionProvider(string tokenEndpoint)
    {
        _tokenEndpoint = tokenEndpoint;
        _httpClient = SharedHttpClient;
    }

    /// <summary>
    /// Opens the AssemblyAI websocket. Call this when push-to-talk begins.
    /// </summary>
    public async Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        _connectionCts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

        var token = await FetchTranscribeTokenAsync(_connectionCts.Token);
        var wsUrl = $"wss://streaming.assemblyai.com/v3/ws?token={token}&sample_rate={NayfConfig.AudioSampleRate}&encoding=pcm_s16le&format_turns=true";

        _webSocket = new ClientWebSocket();
        await _webSocket.ConnectAsync(new Uri(wsUrl), _connectionCts.Token);

        // Start a background receive loop to consume incoming messages
        _ = ReceiveLoopAsync(_connectionCts.Token);
    }

    /// <summary>
    /// Sends a PCM16 audio chunk to the websocket.
    /// </summary>
    public async Task SendAudioChunkAsync(byte[] pcm16Chunk, CancellationToken cancellationToken = default)
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        try
        {
            await _webSocket.SendAsync(
                new ArraySegment<byte>(pcm16Chunk),
                WebSocketMessageType.Binary,
                endOfMessage: true,
                cancellationToken);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssemblyAI] Send error: {ex.Message}");
        }
    }

    /// <summary>
    /// Sends a "terminate session" message and closes the websocket.
    /// The AssemblyAI server flushes any pending transcript on close.
    /// </summary>
    public async Task EndSessionAsync()
    {
        if (_webSocket?.State != WebSocketState.Open) return;
        try
        {
            // Send terminate_session per AssemblyAI v3 protocol
            var terminateMsg = JsonSerializer.Serialize(new { type = "terminate_session" });
            await _webSocket.SendAsync(
                new ArraySegment<byte>(Encoding.UTF8.GetBytes(terminateMsg)),
                WebSocketMessageType.Text,
                endOfMessage: true,
                CancellationToken.None);

            await _webSocket.CloseAsync(
                WebSocketCloseStatus.NormalClosure,
                "Session ended",
                CancellationToken.None);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssemblyAI] Close error: {ex.Message}");
        }
        finally
        {
            _connectionCts?.Cancel();
        }
    }

    private async Task ReceiveLoopAsync(CancellationToken cancellationToken)
    {
        var buffer = new byte[4096];
        var messageBuilder = new StringBuilder();

        try
        {
            while (_webSocket?.State == WebSocketState.Open && !cancellationToken.IsCancellationRequested)
            {
                var result = await _webSocket.ReceiveAsync(
                    new ArraySegment<byte>(buffer), cancellationToken);

                if (result.MessageType == WebSocketMessageType.Close) break;

                messageBuilder.Append(Encoding.UTF8.GetString(buffer, 0, result.Count));

                if (result.EndOfMessage)
                {
                    var message = messageBuilder.ToString();
                    messageBuilder.Clear();
                    ProcessMessage(message);
                }
            }
        }
        catch (OperationCanceledException) { /* Normal shutdown */ }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssemblyAI] Receive error: {ex.Message}");
        }
    }

    private void ProcessMessage(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;

            if (!root.TryGetProperty("type", out var typeEl)) return;
            var messageType = typeEl.GetString();

            if (messageType == "turn")
            {
                // A turn message contains a finalized transcript segment
                if (root.TryGetProperty("transcript", out var transcriptEl))
                {
                    var text = transcriptEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        TranscriptReceived?.Invoke(text);
                }
            }
            else if (messageType == "partial_transcript")
            {
                if (root.TryGetProperty("text", out var textEl))
                {
                    var text = textEl.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                        PartialTranscriptReceived?.Invoke(text);
                }
            }
        }
        catch (JsonException ex)
        {
            System.Diagnostics.Debug.WriteLine($"[AssemblyAI] JSON parse error: {ex.Message}");
        }
    }

    private async Task<string> FetchTranscribeTokenAsync(CancellationToken cancellationToken)
    {
        var response = await _httpClient.PostAsync(_tokenEndpoint, null, cancellationToken);
        response.EnsureSuccessStatusCode();

        var json = await response.Content.ReadAsStringAsync(cancellationToken);
        using var doc = JsonDocument.Parse(json);

        if (doc.RootElement.TryGetProperty("token", out var tokenEl))
            return tokenEl.GetString() ?? throw new InvalidOperationException("Empty token from server");

        throw new InvalidOperationException($"Unexpected token response: {json}");
    }

    public void Dispose()
    {
        _connectionCts?.Cancel();
        _webSocket?.Dispose();
    }
}
