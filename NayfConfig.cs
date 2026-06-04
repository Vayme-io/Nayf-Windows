namespace NayfWindows;

/// <summary>
/// Central configuration — all API endpoints and constants live here.
/// </summary>
public static class NayfConfig
{
    /// <summary>
    /// Base URL of the Cloudflare Worker proxy. All API requests route through
    /// this so keys never ship in the app binary.
    /// </summary>
    public const string WorkerBaseURL = "https://nayf-proxy.vayme.workers.dev";

    public const string ChatEndpoint = $"{WorkerBaseURL}/chat";
    public const string TTSEndpoint = $"{WorkerBaseURL}/tts";
    public const string TranscribeTokenEndpoint = $"{WorkerBaseURL}/transcribe-token";
    public const string CreditsEndpoint = $"{WorkerBaseURL}/credits";
    public const string WebSearchEndpoint = $"{WorkerBaseURL}/search";

    /// <summary>Default Claude model used for conversations.</summary>
    public const string DefaultModel = "claude-sonnet-4-6";

    /// <summary>Higher-quality/capability Claude model available as an option.</summary>
    public const string OpusModel = "claude-opus-4-6";

    /// <summary>
    /// Global push-to-talk keyboard shortcut — Ctrl+Alt matches the Mac's Ctrl+Option.
    /// </summary>
    public const int PushToTalkVirtualKey = 0x12; // VK_MENU (Alt key)
    public const int PushToTalkModifier = 0x02;   // MOD_CONTROL

    /// <summary>Max conversation turns to keep in memory before summarizing.</summary>
    public const int MaxConversationHistoryTurns = 20;

    /// <summary>Watchdog timeout in seconds — resets pipeline if stuck.</summary>
    public const int VoiceStateWatchdogTimeoutSeconds = 30;

    /// <summary>Audio sample rate used for AssemblyAI streaming (PCM16 mono).</summary>
    public const int AudioSampleRate = 16000;
    public const int AudioChannels = 1;
    public const int AudioBitsPerSample = 16;
}
