using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.UI.Dispatching;

namespace NayfWindows;

/// <summary>
/// Central state machine for the companion voice mode.
/// Owns the push-to-talk pipeline, Claude API, TTS, screen capture, and overlay state.
/// Exposes observable properties for the panel and overlay UIs.
/// Mirrors CompanionManager.swift.
/// </summary>
public sealed class CompanionManager : INotifyPropertyChanged, IDisposable
{
    public event PropertyChangedEventHandler? PropertyChanged;

    // MARK: - Observable state

    private CompanionVoiceState _voiceState = CompanionVoiceState.Idle;
    public CompanionVoiceState VoiceState
    {
        get => _voiceState;
        private set { _voiceState = value; OnPropertyChanged(); OnPropertyChanged(nameof(VoiceStateLabel)); }
    }

    public string VoiceStateLabel => VoiceState switch
    {
        CompanionVoiceState.Listening => "Listening…",
        CompanionVoiceState.Processing => "Processing…",
        CompanionVoiceState.Responding => "Responding…",
        _ => "Press Ctrl+Alt to speak"
    };

    private string _streamingResponseText = "";
    public string StreamingResponseText
    {
        get => _streamingResponseText;
        private set { _streamingResponseText = value; OnPropertyChanged(); }
    }

    private string? _lastTranscript;
    public string? LastTranscript
    {
        get => _lastTranscript;
        private set { _lastTranscript = value; OnPropertyChanged(); }
    }

    private float _audioPowerLevel = 0f;
    public float AudioPowerLevel
    {
        get => _audioPowerLevel;
        private set { _audioPowerLevel = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The model the last turn was routed to — surfaced in the panel footer so the
    /// user can see which tier answered, but no longer user-selectable (Nayf picks).
    /// </summary>
    private string _activeModel = NayfConfig.ScreenModel;
    public string ActiveModel
    {
        get => _activeModel;
        private set { _activeModel = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Routes the model on ONE structural question: does this turn involve screen
    /// coordinates? Voice turns run the agent loop with screenshots attached and can
    /// point, draw and run walkthroughs, so they need the high-res vision tier.
    /// Background text work (memory extraction) never touches coordinates.
    /// </summary>
    private void RouteModel(bool turnUsesScreenCoordinates)
    {
        var model = turnUsesScreenCoordinates ? NayfConfig.ScreenModel : NayfConfig.LightModel;
        _claudeAPI.Model = model;
        UpdateOnUI(() => ActiveModel = model);
        Logger.Log("CompanionManager", $"model={model} (screenCoords={turnUsesScreenCoordinates})");
    }

    private NayfCursorColor _selectedCursorColor = NayfSettings.LoadCursorColor();
    public NayfCursorColor SelectedCursorColor
    {
        get => _selectedCursorColor;
        set
        {
            _selectedCursorColor = value;
            NativeOverlayWindow.CursorBlue = value.ToDrawingColor();
            NayfSettings.SaveCursorColor(value);
            OnPropertyChanged();
        }
    }

    // Cursor pointing state — observed by OverlayWindowManager to animate the cursor
    private System.Drawing.PointF? _detectedElementPosition;
    public System.Drawing.PointF? DetectedElementPosition
    {
        get => _detectedElementPosition;
        private set { _detectedElementPosition = value; OnPropertyChanged(); }
    }

    private string? _detectedElementBubbleText;
    public string? DetectedElementBubbleText
    {
        get => _detectedElementBubbleText;
        private set { _detectedElementBubbleText = value; OnPropertyChanged(); }
    }

    // True when Windows refused to start speech recognition because the user
    // hasn't enabled "Online speech recognition" in the privacy settings yet.
    // Drives a banner in the companion panel that links straight to that page.
    private bool _microphonePermissionNeeded;
    public bool MicrophonePermissionNeeded
    {
        get => _microphonePermissionNeeded;
        private set
        {
            if (_microphonePermissionNeeded == value) return;
            _microphonePermissionNeeded = value;
            OnPropertyChanged();

            if (value)
                StartMicPermissionPolling();
            else
                StopMicPermissionPolling();
        }
    }

    // Remaining token balance for the signed-in user (null until first fetched).
    private int? _tokenBalance;
    public int? TokenBalance
    {
        get => _tokenBalance;
        private set { _tokenBalance = value; OnPropertyChanged(); OnPropertyChanged(nameof(TokenBalanceText)); }
    }

    public string TokenBalanceText => TokenBalance is int b ? FormatTokenBalance(b) : "Loading tokens…";

    private bool _isOutOfCredits;
    public bool IsOutOfCredits
    {
        get => _isOutOfCredits;
        private set { _isOutOfCredits = value; OnPropertyChanged(); }
    }

    // MARK: - Dependencies

    private readonly ClaudeAPI _claudeAPI;

    /// <summary>
    /// Dedicated client for background text-only work (memory extraction), pinned to
    /// the light model. Kept separate from <see cref="_claudeAPI"/> because it runs
    /// fire-and-forget alongside the next turn — sharing one instance would let the
    /// two races overwrite each other's model.
    /// </summary>
    private readonly ClaudeAPI _lightClaudeAPI;
    private readonly ElevenLabsTTSClient _elevenLabsTTSClient;
    private readonly BuddyDictationManager _buddyDictationManager;
    private readonly GlobalPushToTalkMonitor _pushToTalkMonitor;
    private readonly AuthManager _authManager;
    private readonly HttpClient _creditsHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    public readonly NayfAgentManager AgentManager;

    /// <summary>The auth manager, so UIs can show the signed-in user and sign out.</summary>
    public AuthManager Auth => _authManager;

    /// <summary>Durable facts Nayf has learned about the user (shown in the Memory tab).</summary>
    public MemoryStore Memory { get; } = new();

    private readonly DispatcherQueue _dispatcherQueue;

    // MARK: - Session state

    private readonly List<ConversationTurn> _conversationHistory = new();
    private CancellationTokenSource? _currentResponseCts;
    private CancellationTokenSource? _watchdogCts;
    private CancellationTokenSource? _micPermissionPollCts;

    // MARK: - System prompt

    private static string BuildSystemPrompt() =>
        """
        You are Nayf, an intelligent AI companion that lives on the user's desktop.
        You can see the user's screen and help them with anything they're working on.

        You are friendly, concise, and genuinely helpful. You speak naturally, as if you're
        right there next to the user.

        When you want to point at something on the screen, use this format:
        [POINT:x,y:label:screen0]
        where x and y are pixel coordinates, label is what you're pointing at, and screen0
        is the screen index (screen0 for the primary display).

        Keep your responses brief and conversational unless the user asks for detail.
        If you're not sure what the user wants, ask a clarifying question.

        You're running on Windows. Use Windows-specific knowledge for paths, apps, etc.

        Agentic capabilities:
        You also have tools to perform real actions on the user's PC — run PowerShell
        commands (the "bash" tool runs PowerShell), read or write files, take screenshots,
        and control the mouse and keyboard. Use these when the user asks you to actually DO
        something, not just explain how. Examples: "clean up my downloads folder", "create a
        folder called projects on my desktop", "write that script and run it".

        When the user asks you to do something, just do it — don't describe what you're about
        to do and ask for confirmation first; the user already asked, so that's the go-ahead.
        (Destructive commands are gated by a separate confirmation prompt, so you don't need
        to ask.) Explore first with read-only commands if needed, then act, then give a brief,
        casual spoken summary of what you did.

        Do NOT use tools for questions, explanations, or pointing — those need no action on the
        PC. Tools are for tasks, not answers. For "where is X / how do I Y" use [POINT] tags.

        Playing music on Spotify:
        Use the dedicated spotify_play tool with a natural query (e.g. "Go by The Chemical
        Brothers"). It searches the Spotify catalog and plays the exact track in the Spotify
        app reliably — far better than navigating Spotify's UI. ALWAYS use it for any
        "play <song/artist> on spotify" request. Never guess or construct track URIs.

        Controlling other apps by mouse/keyboard:
        Drive the app's UI, verifying with screenshots each step:
        1. Open the app: bash `Start-Process <app>`.
        2. Take a screenshot to see the layout.
        3. Use the key tool for shortcuts (it accepts combos like "ctrl+l", "enter",
           "ctrl+shift+p") and the type tool to enter text.
        4. left_click using the coordinates you see in the LATEST screenshot, then take a
           fresh screenshot to confirm the result. Never reuse old coordinates or guess.
        """;

    public CompanionManager(AuthManager authManager)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _authManager = authManager;

        // Apply the saved cursor color before the overlays start rendering.
        NativeOverlayWindow.CursorBlue = _selectedCursorColor.ToDrawingColor();

        _claudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint, NayfConfig.ScreenModel);
        _lightClaudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint, NayfConfig.LightModel);
        _elevenLabsTTSClient = new ElevenLabsTTSClient(NayfConfig.TTSEndpoint);
        _buddyDictationManager = new BuddyDictationManager();
        _pushToTalkMonitor = new GlobalPushToTalkMonitor();
        AgentManager = new NayfAgentManager(_claudeAPI);

        WireUpEvents();
    }

    private void WireUpEvents()
    {
        _pushToTalkMonitor.PushToTalkPressed += OnPushToTalkPressed;
        _pushToTalkMonitor.PushToTalkReleased += OnPushToTalkReleased;

        _buddyDictationManager.AudioPowerLevelChanged += level =>
            UpdateOnUI(() => AudioPowerLevel = level);

        _buddyDictationManager.PartialTranscriptUpdated += text =>
            UpdateOnUI(() => LastTranscript = text);

        // Keep the "thinking" spinner up until audio actually starts — only
        // then flip to Responding (cursor animates with the voice).
        _elevenLabsTTSClient.PlaybackStarted += () =>
            UpdateOnUI(() =>
            {
                if (VoiceState == CompanionVoiceState.Processing)
                    SetVoiceState(CompanionVoiceState.Responding);
            });

        _elevenLabsTTSClient.PlaybackStopped += () =>
            UpdateOnUI(() =>
            {
                // Reset from either state — Processing covers the case where TTS
                // failed before any audio played, so we never get stuck spinning.
                if (VoiceState == CompanionVoiceState.Responding ||
                    VoiceState == CompanionVoiceState.Processing)
                    SetVoiceState(CompanionVoiceState.Idle);
            });
    }

    public void StartAsync()
    {
        _pushToTalkMonitor.Start();
        _ = FetchCreditBalanceAsync();
    }

    /// <summary>
    /// Click-to-talk fallback for the panel's mic button — toggles recording
    /// using the same pipeline as the global Ctrl+Alt hotkey.
    /// </summary>
    public void ToggleTapToTalk()
    {
        if (VoiceState == CompanionVoiceState.Idle)
            OnPushToTalkPressed();
        else if (VoiceState == CompanionVoiceState.Listening)
            OnPushToTalkReleased();
    }

    /// <summary>
    /// Fetches the user's remaining token balance from the proxy and updates
    /// <see cref="TokenBalance"/>. Called on launch and after each response.
    /// </summary>
    public async Task FetchCreditBalanceAsync()
    {
        var token = await _authManager.CurrentAccessTokenAsync();
        if (token == null) return;

        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, NayfConfig.CreditsEndpoint);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            using var response = await _creditsHttp.SendAsync(request);
            if (!response.IsSuccessStatusCode) return;

            var body = await response.Content.ReadAsStringAsync();
            using var doc = JsonDocument.Parse(body);
            if (doc.RootElement.TryGetProperty("token_balance", out var b) && b.TryGetInt32(out int balance))
            {
                UpdateOnUI(() =>
                {
                    TokenBalance = balance;
                    IsOutOfCredits = balance <= 0;
                });
            }
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"FetchCreditBalance error: {ex.Message}");
        }
    }

    /// <summary>
    /// Asks Claude, in the background, to pull any new durable facts about the
    /// user out of the latest exchange and adds them to the memory store.
    /// </summary>
    private async Task ExtractMemoriesAsync(string transcript, string response, string authToken)
    {
        try
        {
            const string system =
                "You extract durable facts worth remembering about the USER across sessions — " +
                "their name, role, preferences, ongoing projects, tools they use, etc. " +
                "Only include NEW facts not already listed. Return ONLY a JSON array of short " +
                "strings (e.g. [\"Prefers concise answers\",\"Building a Windows port of Nayf\"]). " +
                "Return [] if nothing new or durable. No prose, no markdown.";

            var known = string.Join("\n", Memory.Memories);
            var userMsg =
                $"Already known:\n{(known.Length > 0 ? known : "(nothing yet)")}\n\n" +
                $"Exchange:\nUser: {transcript}\nAssistant: {response}";

            // Text-only, no screen coordinates → light model.
            var result = await _lightClaudeAPI.StreamResponseAsync(
                userMsg, new List<ConversationTurn>(), null, null, system, authToken, CancellationToken.None);

            // Pull the JSON array out of the response and add each fact.
            int start = result.IndexOf('['), end = result.LastIndexOf(']');
            if (start < 0 || end <= start) return;
            var json = result.Substring(start, end - start + 1);
            var facts = JsonSerializer.Deserialize<List<string>>(json);
            if (facts == null) return;

            foreach (var fact in facts)
                UpdateOnUI(() => Memory.Add(fact));
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Memory extraction failed: {ex.Message}");
        }
    }

    private static string FormatTokenBalance(int tokens)
    {
        if (tokens <= 0) return "No tokens remaining";
        if (tokens >= 1_000_000) return $"{tokens / 1_000_000.0:0.0}M tokens";
        if (tokens >= 1_000) return $"{tokens / 1_000.0:0.0}k tokens";
        return $"{tokens} tokens";
    }

    private async void OnPushToTalkPressed()
    {
        Logger.Log("CompanionManager", $"OnPushToTalkPressed, VoiceState={VoiceState}");
        if (VoiceState != CompanionVoiceState.Idle) return;

        SetVoiceState(CompanionVoiceState.Listening);
        StreamingResponseText = "";
        DetectedElementPosition = null;
        DetectedElementBubbleText = null;

        try
        {
            await _buddyDictationManager.StartRecordingAsync();
            StartWatchdog();
            MicrophonePermissionNeeded = false;
            Logger.Log("CompanionManager", "Recording started");
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Failed to start recording: {ex}");

            // HRESULT 0x80045509 — "speech privacy policy was not accepted" —
            // means the user hasn't turned on Online speech recognition yet.
            if (ex is System.Runtime.InteropServices.COMException comEx &&
                (uint)comEx.HResult == 0x80045509)
            {
                MicrophonePermissionNeeded = true;
            }

            SetVoiceState(CompanionVoiceState.Idle);
        }
    }

    private async void OnPushToTalkReleased()
    {
        Logger.Log("CompanionManager", $"OnPushToTalkReleased, VoiceState={VoiceState}");
        if (VoiceState != CompanionVoiceState.Listening) return;

        SetVoiceState(CompanionVoiceState.Processing);
        StopWatchdog();

        string? transcript;
        try
        {
            transcript = await _buddyDictationManager.StopRecordingAndGetTranscriptAsync();
            Logger.Log("CompanionManager", $"Transcript: {transcript}");
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Transcription failed: {ex}");
            SetVoiceState(CompanionVoiceState.Idle);
            return;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            SetVoiceState(CompanionVoiceState.Idle);
            return;
        }

        UpdateOnUI(() => LastTranscript = transcript);
        await SendTranscriptToClaudeAsync(transcript);
    }

    private async Task SendTranscriptToClaudeAsync(string transcript)
    {
        // Cancel any previous in-flight response
        _currentResponseCts?.Cancel();
        _currentResponseCts = new CancellationTokenSource();
        var ct = _currentResponseCts.Token;

        List<CapturedScreenshot>? screenshots = null;
        try
        {
            screenshots = await ScreenCaptureUtility.CaptureAllScreensAsync();
            Logger.Log("CompanionManager", $"Captured {screenshots?.Count ?? 0} screenshot(s)");
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Screen capture failed: {ex.Message}");
        }

        // Stay in Processing (the spinner) through the screenshot upload and
        // Claude's response — we only switch to Responding once TTS audio
        // actually begins, so the user always sees that something is happening.
        StreamingResponseText = "";

        // Fetch a fresh Supabase JWT so the proxy can verify identity + credits.
        var authToken = await _authManager.CurrentAccessTokenAsync();
        if (authToken == null)
        {
            Logger.Log("CompanionManager", "No auth token — user not signed in");
            SetVoiceState(CompanionVoiceState.Idle);
            return;
        }

        try
        {
            Logger.Log("CompanionManager", "Sending to Claude…");
            // Route through the agent loop: Claude may call tools (PowerShell,
            // files, computer control) before answering, or just respond/point
            // normally. Either way it returns the final text for TTS. The system
            // prompt carries what Nayf remembers about the user.
            // Voice turns always carry screenshots and can point/draw → screen model.
            RouteModel(turnUsesScreenCoordinates: true);

            var systemPrompt = BuildSystemPrompt();
            var memoryBlock = Memory.ContextBlock();
            if (memoryBlock.Length > 0) systemPrompt += "\n\n" + memoryBlock;

            var responseText = await AgentManager.RunAgentLoopAsync(
                transcript,
                screenshots,
                systemPrompt,
                authToken,
                delta => UpdateOnUI(() => StreamingResponseText += delta),
                ct,
                _conversationHistory);

            Logger.Log("CompanionManager", $"Claude responded ({responseText.Length} chars)");

            // A response consumed tokens — refresh the displayed balance.
            _ = FetchCreditBalanceAsync();

            // Learn durable facts about the user in the background (don't block TTS).
            _ = ExtractMemoriesAsync(transcript, responseText, authToken);

            if (ct.IsCancellationRequested) return;

            // Store in conversation history
            _conversationHistory.Add(new ConversationTurn(transcript, responseText));
            if (_conversationHistory.Count > NayfConfig.MaxConversationHistoryTurns)
                _conversationHistory.RemoveAt(0);

            // Parse any POINT tags in the response
            ParseAndApplyPointTags(responseText, screenshots);

            // Speak the response (strip POINT tags from TTS)
            var ttsText = System.Text.RegularExpressions.Regex.Replace(
                responseText, @"\[POINT:[^\]]+\]", "").Trim();
            if (string.IsNullOrEmpty(ttsText))
            {
                // Nothing to speak (e.g. response was only a POINT tag) — done.
                SetVoiceState(CompanionVoiceState.Idle);
            }
            else
            {
                Logger.Log("CompanionManager", "Starting TTS playback");
                _ = _elevenLabsTTSClient.SpeakAsync(ttsText, authToken, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // User spoke again — normal cancellation
            Logger.Log("CompanionManager", "Response cancelled");
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Claude/TTS error: {ex}");
            SetVoiceState(CompanionVoiceState.Idle);
        }
    }

    private void ParseAndApplyPointTags(string responseText, List<CapturedScreenshot>? screenshots)
    {
        // Pattern: [POINT:x,y:label:screenN]
        var matches = System.Text.RegularExpressions.Regex.Matches(
            responseText, @"\[POINT:(\d+),(\d+):([^:]+):screen(\d+)\]");

        if (matches.Count == 0) return;

        var match = matches[0]; // Use first point tag
        if (int.TryParse(match.Groups[1].Value, out int x) &&
            int.TryParse(match.Groups[2].Value, out int y) &&
            int.TryParse(match.Groups[4].Value, out int screenIndex))
        {
            // Claude's coordinates are in the (downscaled) image it was sent, and
            // are local to that screen. Map them back to absolute virtual-desktop
            // pixels: scale up by native/image size, then offset by the monitor.
            float screenX = x;
            float screenY = y;

            CapturedScreenshot? shot = null;
            if (screenshots != null)
            {
                foreach (var s in screenshots)
                {
                    if (s.ScreenIndex == screenIndex) { shot = s; break; }
                }
                shot ??= screenshots.Count > 0 ? screenshots[0] : null;
            }

            if (shot != null && shot.ImageWidth > 0 && shot.ImageHeight > 0)
            {
                float scaleX = (float)shot.MonitorWidth / shot.ImageWidth;
                float scaleY = (float)shot.MonitorHeight / shot.ImageHeight;
                screenX = shot.MonitorLeft + x * scaleX;
                screenY = shot.MonitorTop + y * scaleY;
            }

            Logger.Log("CompanionManager",
                $"POINT image({x},{y}) screen{screenIndex} -> abs({screenX:0},{screenY:0})");

            UpdateOnUI(() =>
            {
                DetectedElementPosition = new System.Drawing.PointF(screenX, screenY);
                DetectedElementBubbleText = match.Groups[3].Value;
            });
        }
    }

    /// <summary>
    /// Clears the detected-element pointing target once the overlay buddy has
    /// finished navigating to it, flown back, and resumed following the cursor.
    /// </summary>
    public void ClearDetectedElementLocation()
    {
        UpdateOnUI(() =>
        {
            DetectedElementPosition = null;
            DetectedElementBubbleText = null;
        });
    }

    /// <summary>Clears the conversation history for a fresh session.</summary>
    public void ClearConversationHistory()
    {
        _conversationHistory.Clear();
        UpdateOnUI(() =>
        {
            StreamingResponseText = "";
            LastTranscript = null;
        });
    }

    private void StartWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts = new CancellationTokenSource();
        var ct = _watchdogCts.Token;

        Task.Delay(TimeSpan.FromSeconds(NayfConfig.VoiceStateWatchdogTimeoutSeconds), ct)
            .ContinueWith(_ =>
            {
                if (!ct.IsCancellationRequested)
                    UpdateOnUI(() => SetVoiceState(CompanionVoiceState.Idle));
            }, ct, TaskContinuationOptions.OnlyOnRanToCompletion, TaskScheduler.Default);
    }

    private void StopWatchdog()
    {
        _watchdogCts?.Cancel();
        _watchdogCts = null;
    }

    /// <summary>
    /// While the mic-permission banner is shown, periodically checks whether the
    /// user has accepted the "Online speech recognition" privacy policy and
    /// clears the banner automatically once they have — no need to retry Ctrl+Alt.
    /// </summary>
    private void StartMicPermissionPolling()
    {
        _micPermissionPollCts?.Cancel();
        var cts = new CancellationTokenSource();
        _micPermissionPollCts = cts;
        var ct = cts.Token;

        _ = Task.Run(async () =>
        {
            while (!ct.IsCancellationRequested)
            {
                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(1), ct);
                }
                catch (TaskCanceledException)
                {
                    break;
                }

                if (IsOnlineSpeechRecognitionAccepted())
                {
                    UpdateOnUI(() => MicrophonePermissionNeeded = false);
                    break;
                }
            }
        }, ct);
    }

    private void StopMicPermissionPolling()
    {
        _micPermissionPollCts?.Cancel();
        _micPermissionPollCts = null;
    }

    private static bool IsOnlineSpeechRecognitionAccepted()
    {
        try
        {
            var value = Microsoft.Win32.Registry.GetValue(
                @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy",
                "HasAccepted", 0);
            return value is int accepted && accepted == 1;
        }
        catch
        {
            return false;
        }
    }

    private void SetVoiceState(CompanionVoiceState state)
    {
        VoiceState = state;
        if (state == CompanionVoiceState.Idle)
            AudioPowerLevel = 0f;
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
        _pushToTalkMonitor.Dispose();
        _buddyDictationManager.Dispose();
        _elevenLabsTTSClient.Dispose();
        _currentResponseCts?.Cancel();
        _watchdogCts?.Cancel();
        _micPermissionPollCts?.Cancel();
        _creditsHttp.Dispose();
    }
}
