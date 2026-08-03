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

    /// <summary>
    /// Supabase project used for authentication. The proxy verifies the user's
    /// JWT (issued by this project) and checks their credit balance before
    /// serving chat/TTS requests. Matches the Mac app's NayfConfig.
    /// </summary>
    public const string SupabaseURL = "https://vpigrsijmusymnjeuhsh.supabase.co";
    public const string SupabaseAnonKey = "eyJhbGciOiJIUzI1NiIsInR5cCI6IkpXVCJ9.eyJpc3MiOiJzdXBhYmFzZSIsInJlZiI6InZwaWdyc2lqbXVzeW1uamV1aHNoIiwicm9sZSI6ImFub24iLCJpYXQiOjE3Nzk2NjA2ODksImV4cCI6MjA5NTIzNjY4OX0.keRzkBuv4QqfA6VDwJcznpEQbUR4ZsRmwHKJTeTzl74";

    /// <summary>
    /// The two models Nayf routes between automatically — the user no longer picks.
    ///
    /// Routing answers ONE structural question: does this turn involve screen
    /// coordinates? It is decided from the interaction mode, NOT by guessing from
    /// the user's wording (cue-word matching decided the model *before* knowing what
    /// the turn needed, so screen-coordinate turns could silently land on the light
    /// model and point inaccurately — and it only ever worked in English).
    ///
    /// <see cref="ScreenModel"/> (Opus 5) is on the high-resolution vision tier: up to
    /// 2576px long edge with 1:1 coordinate mapping between the image and the
    /// coordinates it returns. Every turn that reads the screen or draws on it MUST
    /// use it, or the overlay inherits scaling drift.
    /// </summary>
    public const string ScreenModel = "claude-opus-5";

    /// <summary>
    /// The standard vision tier (1568px, non-1:1 coords) — used only where no screen
    /// coordinates are involved (background text work like memory extraction).
    /// Cheaper and faster than <see cref="ScreenModel"/>.
    /// </summary>
    public const string LightModel = "claude-sonnet-5";

    /// <summary>Model used when nothing more specific applies.</summary>
    public const string DefaultModel = LightModel;

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
