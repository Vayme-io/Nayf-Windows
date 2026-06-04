using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
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

    private string _selectedModel = NayfConfig.DefaultModel;
    public string SelectedModel
    {
        get => _selectedModel;
        set
        {
            _selectedModel = value;
            _claudeAPI.Model = value;
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

    // MARK: - Dependencies

    private readonly ClaudeAPI _claudeAPI;
    private readonly ElevenLabsTTSClient _elevenLabsTTSClient;
    private readonly BuddyDictationManager _buddyDictationManager;
    private readonly GlobalPushToTalkMonitor _pushToTalkMonitor;
    public readonly NayfAgentManager AgentManager;

    private readonly DispatcherQueue _dispatcherQueue;

    // MARK: - Session state

    private readonly List<ConversationTurn> _conversationHistory = new();
    private CancellationTokenSource? _currentResponseCts;
    private CancellationTokenSource? _watchdogCts;

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
        """;

    public CompanionManager()
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();

        _claudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint);
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

        _elevenLabsTTSClient.PlaybackStopped += () =>
            UpdateOnUI(() =>
            {
                if (VoiceState == CompanionVoiceState.Responding)
                    SetVoiceState(CompanionVoiceState.Idle);
            });
    }

    public void StartAsync()
    {
        _pushToTalkMonitor.Start();
    }

    private async void OnPushToTalkPressed()
    {
        if (VoiceState != CompanionVoiceState.Idle) return;

        SetVoiceState(CompanionVoiceState.Listening);
        StreamingResponseText = "";
        DetectedElementPosition = null;
        DetectedElementBubbleText = null;

        try
        {
            await _buddyDictationManager.StartRecordingAsync();
            StartWatchdog();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompanionManager] Failed to start recording: {ex.Message}");
            SetVoiceState(CompanionVoiceState.Idle);
        }
    }

    private async void OnPushToTalkReleased()
    {
        if (VoiceState != CompanionVoiceState.Listening) return;

        SetVoiceState(CompanionVoiceState.Processing);
        StopWatchdog();

        string? transcript;
        try
        {
            transcript = await _buddyDictationManager.StopRecordingAndGetTranscriptAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompanionManager] Transcription failed: {ex.Message}");
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
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompanionManager] Screen capture failed: {ex.Message}");
        }

        SetVoiceState(CompanionVoiceState.Responding);
        StreamingResponseText = "";

        try
        {
            var responseText = await _claudeAPI.StreamResponseAsync(
                transcript,
                _conversationHistory,
                screenshots,
                delta => UpdateOnUI(() => StreamingResponseText += delta),
                BuildSystemPrompt(),
                null,
                ct);

            if (ct.IsCancellationRequested) return;

            // Store in conversation history
            _conversationHistory.Add(new ConversationTurn(transcript, responseText));
            if (_conversationHistory.Count > NayfConfig.MaxConversationHistoryTurns)
                _conversationHistory.RemoveAt(0);

            // Parse any POINT tags in the response
            ParseAndApplyPointTags(responseText, screenshots);

            // Speak the response (strip POINT tags from TTS)
            var ttsText = System.Text.RegularExpressions.Regex.Replace(
                responseText, @"\[POINT:[^\]]+\]", "");
            _ = _elevenLabsTTSClient.SpeakAsync(ttsText, ct);
        }
        catch (OperationCanceledException)
        {
            // User spoke again — normal cancellation
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[CompanionManager] Claude error: {ex.Message}");
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
            int.TryParse(match.Groups[2].Value, out int y))
        {
            UpdateOnUI(() =>
            {
                DetectedElementPosition = new System.Drawing.PointF(x, y);
                DetectedElementBubbleText = match.Groups[3].Value;
            });
        }
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
    }
}
