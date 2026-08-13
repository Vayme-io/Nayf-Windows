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
public sealed class CompanionManager : INotifyPropertyChanged, IScreenAnnotationSource, IDisposable
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
        CompanionVoiceState.AwaitingUserStep => "Your turn — go ahead",
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

    /// <summary>
    /// True when the user is asking to be TAUGHT — "show me how", "walk me through" —
    /// rather than asking Nayf to DO something for them.
    ///
    /// This decides capability, not quality. A teaching turn runs hands-off, with no tool
    /// that can click, type, or run a command, because the user performing each action
    /// themselves is the whole point. Everything else keeps the full toolset.
    ///
    /// A keyword list is normally the wrong way to read intent, and it is used here because
    /// missing is cheap in one direction only: an unusually-phrased teaching request behaves
    /// the way it always has, while every phrasing that is recognised becomes hands-off. It
    /// can never hand actuation to a turn that would not already have had it.
    /// </summary>
    private static bool IsTeachingRequest(string transcript)
    {
        var normalized = transcript.ToLowerInvariant();
        string[] teachingCues =
        {
            // English
            "show me how", "show me the", "teach me", "walk me through", "guide me",
            "how do i", "how do you", "how can i", "how would i", "how to",
            "step by step", "one step at a time", "click by click", "next step",
            "what do i do", "what should i do", "where do i",
            // Swedish — the user's other language.
            "visa mig", "lär mig", "hur gör jag", "hur gör man", "steg för steg",
            "vad gör jag", "var hittar jag"
        };

        foreach (var cue in teachingCues)
            if (normalized.Contains(cue, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// Set once the acknowledgment has been spoken and the real turn is still going, so
    /// the pill can say the waiting is deliberate rather than repeat "Thinking" at
    /// someone who has just been told Nayf is on it. Null whenever it does not apply.
    /// </summary>
    private string? _deepThinkingLabel;
    public string? DeepThinkingLabel
    {
        get => _deepThinkingLabel;
        private set { _deepThinkingLabel = value; OnPropertyChanged(); }
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

    // Screen annotations — the outlines and arrows Nayf draws over the desktop while
    // explaining something. Observed by AnnotationOverlayWindow, one per monitor.
    private IReadOnlyList<ScreenAnnotation> _screenAnnotations = Array.Empty<ScreenAnnotation>();

    /// <summary>
    /// The marks currently on screen, in virtual screen coordinates.
    ///
    /// Replaced wholesale, never mutated in place: the overlays read this from their own render
    /// threads, and swapping a reference means they always see one complete list or the other
    /// rather than a half-updated one.
    /// </summary>
    public IReadOnlyList<ScreenAnnotation> ScreenAnnotations
    {
        get => _screenAnnotations;
        private set { _screenAnnotations = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// Raised after <see cref="ScreenAnnotations"/> is replaced.
    ///
    /// The overlays stop rendering once every mark has finished animating, so unlike the cursor
    /// — which they poll for at 60 fps — a new annotation has to wake them. This is that nudge.
    /// </summary>
    public event Action? ScreenAnnotationsChanged;

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

    /// <summary>
    /// Writes the spoken acknowledgment. Separate for the same reason as
    /// <see cref="_lightClaudeAPI"/>, and more urgently: it runs *concurrently with* the
    /// turn it belongs to, so a shared instance would have the two reassigning
    /// <see cref="ClaudeAPI.Model"/> underneath each other.
    /// </summary>
    private readonly ClaudeAPI _ackClaudeAPI;

    // Guards the handover between the acknowledgment and the real answer: whoever gets
    // there first wins, and the loser stays quiet.
    private readonly object _ackGate = new();

    /// <summary>
    /// The acknowledgment of the most recent turn, kept only so <see cref="Dispose"/> can
    /// cancel one still in flight. A turn works from its own local, never from this field.
    /// </summary>
    private AckHandover? _latestAck;

    /// <summary>
    /// One turn's acknowledgment. Per turn rather than a set of fields on the manager
    /// because two turns overlap whenever the user interrupts, and shared flags would have
    /// the incoming turn resetting the outgoing one's handover halfway through it.
    /// </summary>
    private sealed class AckHandover
    {
        public readonly CancellationTokenSource Cts;
        public Task Work = Task.CompletedTask;

        // Both guarded by _ackGate.
        public bool MainTurnHasFinalAnswer;
        public bool DidSpeak;

        public AckHandover(CancellationTokenSource cts) => Cts = cts;
    }

    /// <summary>
    /// True while the acknowledgment itself is coming out of the speaker. The voice
    /// state machine ignores playback callbacks during this: the ack is Nayf clearing its
    /// throat, not the answer, and letting it drive the state would drop the pill to Idle
    /// with the real turn still running.
    /// </summary>
    private volatile bool _ackSpeaking;

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

    /// <summary>
    /// Cloud integrations (Google Calendar). Connection state is server-side and keyed
    /// to the signed-in account, so this reports what the account has connected on any
    /// device — not just what was connected from this PC.
    /// </summary>
    public NayfIntegrationsManager Integrations { get; }

    /// <summary>Buying more tokens. Payment happens in the browser, through Paddle.</summary>
    public NayfStoreManager Store { get; }

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
        commands (the "bash" tool runs PowerShell), read or write files, and take
        screenshots. Use these when the user asks you to actually DO something, not just
        explain how. Examples: "clean up my downloads folder", "create a folder called
        projects on my desktop", "write that script and run it".

        You never control the mouse or the keyboard. You cannot click, type, drag, scroll,
        or press keys for the user, in any mode — there is no tool for it and asking for one
        is refused. The cursor on that screen belongs to the user. When something needs
        clicking, show them where and let them click it.

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

        The user's calendar:
        Use the google_calendar_* tools for anything about their schedule — reading it,
        adding to it, or clearing something off it. These reach their connected Google
        account, which is the same calendar they see on their other devices, NOT the
        Windows Calendar app. Never drive a calendar app's UI by clicking to do this.
        Always send ISO 8601 datetimes with the user's local timezone offset.

        The user's GitHub:
        Use the github_* tools for their issues and pull requests — never the `gh` CLI or
        git, which act as whatever account this PC is configured with rather than the one
        they connected to Nayf. github_list_issues and github_list_pull_requests take no
        arguments and already scope to them. github_create_issue needs owner and repo; ask
        which repository if it isn't clear rather than guessing one.

        Tasks that would mean using another app's UI:
        Do the part you can do without touching their screen, then hand the screen back.
        1. Anything achievable from PowerShell, do with bash — launching an app, moving
           files, settings, installed software. That is the whole job most of the time.
        2. If it can only be done in the app's interface, don't drive it. Open the app with
           bash `Start-Process <app>`, take a screenshot to see where things actually are,
           then tell them what to click and mark it with a [POINT] tag.
        3. For anything longer than a single click, offer to walk them through it step by
           step rather than listing the steps at them.
        Never describe a click you performed, and never claim to have clicked something.
        """;

    /// <summary>
    /// Appended on a walkthrough turn. Two jobs: undo the prompt above, which describes
    /// tools this turn does not have — left in, the model reads that it can click and type,
    /// tries to, and spends its steps being refused rather than teaching — and explain the
    /// one thing it can do instead, which is hand the user a step and wait.
    /// </summary>
    private const string WalkthroughPromptSuffix =
        """
        This turn is a guided walkthrough.

        You never touch the user's screen. You do not click, type, drag, scroll, or move
        anything on their behalf. The user performs every action themselves — that is the
        entire point of the product. Your job is to show and to say: point with the cursor,
        draw the shape that fits, speak one short instruction, then wait. If you find
        yourself wanting to act, don't — point at it instead.

        You have two tools. take_screenshot shows you where the user actually is.
        request_user_step hands them one thing to do and waits until they have done it.

        The rhythm is always the same: take a screenshot, look at what is really on screen,
        write ONE short sentence telling them what to do, and call request_user_step in the
        same message. That sentence is spoken aloud as your cursor flies to the point and
        the outline traces. The call comes back when they have done it, or when they have
        said something about it — which may well be a question, in which case answer it and
        show the SAME step again, worded differently. Do not advance until they are through.

        One action per step. One sentence per step. Never bundle two things into one step,
        and never describe a step you have not shown.

        What each mark means, so use the one that matches:
        - The cursor flying to a point means "click exactly here".
        - The outline means "this is the thing". Give w and h as the target's visual bounds
          including its padding — the whole button, not just its text — because the outline
          is traced in exactly those bounds and a wrong size looks wrong on screen.
        - An arrow means "drag from here to there", so send to_x and to_y only for a drag.

        Use wait_for "click" when the step is a single click on the point you gave. Use
        "continue" for a drag, for typing, or for anything with no one click to watch for.

        Coordinates are in the pixel space of the screenshot you are looking at, so take a
        fresh one for each step rather than reusing coordinates from an earlier screen.

        Don't use [POINT] tags here — request_user_step does the pointing, and unlike a tag
        it waits. When the walkthrough is done, say so in one short sentence and stop.
        """;

    public CompanionManager(AuthManager authManager)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _authManager = authManager;

        // Apply the saved cursor color before the overlays start rendering.
        NativeOverlayWindow.CursorBlue = _selectedCursorColor.ToDrawingColor();

        _claudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint, NayfConfig.ScreenModel);
        _lightClaudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint, NayfConfig.LightModel);
        _ackClaudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint, NayfConfig.AckModel);
        _elevenLabsTTSClient = new ElevenLabsTTSClient(NayfConfig.TTSEndpoint);
        _buddyDictationManager = new BuddyDictationManager();
        _pushToTalkMonitor = new GlobalPushToTalkMonitor();
        AgentManager = new NayfAgentManager(_claudeAPI);
        Integrations = new NayfIntegrationsManager(authManager);
        Store = new NayfStoreManager(authManager);

        WireUpEvents();
    }

    private void WireUpEvents()
    {
        _pushToTalkMonitor.PushToTalkPressed += OnPushToTalkPressed;
        _pushToTalkMonitor.PushToTalkReleased += OnPushToTalkReleased;
        _pushToTalkMonitor.MouseClicked += OnMouseClicked;

        // The agent loop calls this to hand a walkthrough step over and wait. It lives here
        // because a step is cursor, marks and voice — none of which the loop knows about.
        AgentManager.UserStepRequested = PresentWalkthroughStepAsync;

        // The monitor owns the hook; the window that answers this lives in App, which
        // has no reason to know about the hook. Re-raising keeps the two apart.
        _pushToTalkMonitor.TextInputRequested += () => TextInputRequested?.Invoke();

        _buddyDictationManager.AudioPowerLevelChanged += level =>
            UpdateOnUI(() => AudioPowerLevel = level);

        _buddyDictationManager.PartialTranscriptUpdated += text =>
            UpdateOnUI(() => LastTranscript = text);

        // Keep the "thinking" spinner up until audio actually starts — only
        // then flip to Responding (cursor animates with the voice).
        _elevenLabsTTSClient.PlaybackStarted += () =>
        {
            if (_ackSpeaking) return;
            UpdateOnUI(() =>
            {
                if (VoiceState == CompanionVoiceState.Processing)
                    SetVoiceState(CompanionVoiceState.Responding);
            });
        };

        _elevenLabsTTSClient.PlaybackStopped += () =>
        {
            if (_ackSpeaking) return;
            UpdateOnUI(() =>
            {
                // Reset from either state — Processing covers the case where TTS
                // failed before any audio played, so we never get stuck spinning.
                if (VoiceState == CompanionVoiceState.Responding ||
                    VoiceState == CompanionVoiceState.Processing)
                    SetVoiceState(CompanionVoiceState.Idle);
            });
        };
    }

    public void StartAsync()
    {
        _pushToTalkMonitor.Start();
        _ = FetchCreditBalanceAsync();
        // Ask up front which providers the account has connected, so the panel opens
        // already knowing — rather than showing "Connect" to someone who connected
        // on another device and only correcting itself a moment later.
        _ = Integrations.RefreshStatusAsync();
    }

    /// <summary>
    /// Click-to-talk fallback for the panel's mic button — toggles recording
    /// using the same pipeline as the global Ctrl+Alt hotkey.
    /// </summary>
    public void ToggleTapToTalk()
    {
        // Recording stops; anything else starts a new one — including mid-answer, where
        // the button interrupts exactly as the chord does. A mic button that goes dead the
        // moment Nayf starts talking would be the one place it can't be told to stop.
        if (VoiceState == CompanionVoiceState.Listening)
            OnPushToTalkReleased();
        else
            OnPushToTalkPressed();
    }

    /// <summary>The Alt+T chord fired — something should offer a place to type.</summary>
    public event Action? TextInputRequested;

    /// <summary>
    /// Sends a typed request. Past this point nothing distinguishes it from a spoken
    /// one: same screenshots, same tools, same spoken answer — only the transcription
    /// step is skipped, because the user already gave us the words.
    /// </summary>
    public async void SendTypedRequest(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;

        // Mid-walkthrough, typing is how the user answers the step they are on — "done",
        // "which window?" — not a new request. Same words and same handling as if they had
        // said them out loud; Claude reads the answer either way.
        if (TryResumeWalkthrough(WalkthroughResumeReason.Typed, text.Trim()))
        {
            UpdateOnUI(() => LastTranscript = text.Trim());
            return;
        }

        // Typing over a turn already in flight would leave two responses talking at
        // once, so the earlier one has to finish or be cancelled first.
        if (VoiceState != CompanionVoiceState.Idle)
        {
            Logger.Log("CompanionManager", $"Typed request ignored, VoiceState={VoiceState}");
            return;
        }

        Logger.Log("CompanionManager", "Typed request received");
        StreamingResponseText = "";
        DetectedElementPosition = null;
        DetectedElementBubbleText = null;
        // A new question wipes the last answer's marks — they described a screen the user has
        // since moved on from.
        ClearScreenAnnotations();

        // Nothing else sets this for a typed turn — push-to-talk normally does it on
        // release — and without it the TTS callbacks have no Processing state to
        // move out of, so the pill would spin for the rest of the session.
        SetVoiceState(CompanionVoiceState.Processing);

        var request = text.Trim();
        UpdateOnUI(() => LastTranscript = request);
        await SendTranscriptToClaudeAsync(request);
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

            // The Mac chimes with its "Saved to memory" toast. One chime for the batch,
            // not one per fact — a conversation can produce several at once.
            if (facts.Count > 0)
                NayfSoundPlayer.Shared.PlayTaskComplete();
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

        // Every state is let in but one. Listening means this press has nothing to start —
        // the chord is being held down, or the panel's mic button was pressed twice.
        // Everything else is the user cutting in, which is a thing they are allowed to do.
        if (VoiceState == CompanionVoiceState.Listening) return;

        // Instant audible feedback as the status pill springs up. After the guard, not
        // before it: a press Nayf is going to ignore shouldn't sound like it was heard.
        NayfSoundPlayer.Shared.PlayPushToTalkActivate();

        // Claimed before anything is torn down. The outgoing turn passes through states on
        // its way out — playback stopping, a response cancelling — and each of those would
        // otherwise drop the pill back to Idle a moment after this press put it up.
        SetVoiceState(CompanionVoiceState.Listening);
        StreamingResponseText = "";

        // Thinking or halfway through a sentence, whatever is in flight goes now. Someone
        // who starts talking over Nayf is not adding to the last question, they are
        // replacing it — and "stop" only means anything if the thing being asked to stop
        // is actually stopped rather than left to finish and then answer.
        //
        // EXCEPT while a walkthrough step is waiting on them. There, this press IS the
        // answer to the step, and the paused agent loop is suspended inside the very task
        // that would be cancelled — it would wake up, see the cancellation and throw,
        // leaving the walkthrough dead and its marks stranded on screen.
        if (_pendingStep == null) CancelTurnInFlight();

        // The voice stops either way. Mid-step or mid-answer, talking over someone who has
        // just started speaking is the one thing a push-to-talk press must never leave
        // Nayf doing.
        _elevenLabsTTSClient.StopPlayback();

        // Mid-step the marks are not last turn's leftovers — they are the step the user is
        // still on, and they may well be holding the chord to ask about the very thing that
        // is outlined. Wiping it as they open their mouth takes the question with it.
        if (_pendingStep == null)
        {
            DetectedElementPosition = null;
            DetectedElementBubbleText = null;
            ClearScreenAnnotations();
        }

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

            SetVoiceState(RestingState);
        }
    }

    /// <summary>
    /// Calls off the turn that is running right now — the agent loop, whatever tool it was
    /// in the middle of, and the acknowledgment that was going to speak for it.
    ///
    /// One cancellation reaches all three: the tool executor is handed the turn's token and
    /// the acknowledgment's is linked to it. So this is the single place a turn is stopped,
    /// whether it was still thinking or already talking.
    /// </summary>
    private void CancelTurnInFlight()
    {
        if (_currentResponseCts == null) return;

        Logger.Log("CompanionManager", "Barge-in — cancelling the turn in flight");
        _currentResponseCts.Cancel();
        _currentResponseCts = null;

        // A destructive command still waiting on a yes/no belongs to that turn too. Its
        // answer would go back to a loop that has stopped reading, and left up, the prompt
        // would sit there over the next conversation asking about a command nobody is
        // running any more.
        AgentManager.CancelPendingConfirmation();
    }

    private async void OnPushToTalkReleased()
    {
        Logger.Log("CompanionManager", $"OnPushToTalkReleased, VoiceState={VoiceState}");
        if (VoiceState != CompanionVoiceState.Listening) return;

        // Blip on release too, after a shorter lead-in.
        NayfSoundPlayer.Shared.PlayPushToTalkRelease();

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
            SetVoiceState(RestingState);
            return;
        }

        // The user can cut in while the recognizer is still finalizing — it waits up to a
        // couple of seconds for its last result. By then that press has taken the state and
        // opened a new recording, so these words belong to an utterance already replaced.
        // Sending them would put two turns in flight over one microphone.
        if (VoiceState != CompanionVoiceState.Processing)
        {
            Logger.Log("CompanionManager", $"Transcript dropped, VoiceState={VoiceState}");
            return;
        }

        if (string.IsNullOrWhiteSpace(transcript))
        {
            SetVoiceState(RestingState);
            return;
        }

        UpdateOnUI(() => LastTranscript = transcript);

        // Spoken mid-walkthrough, this is the answer to the step rather than a new request.
        // Whether it means "done" or "hang on, which one?" is Claude's to read — it has the
        // step, the screen and the language the user is speaking; a keyword test here has
        // none of those.
        if (TryResumeWalkthrough(WalkthroughResumeReason.Spoke, transcript)) return;

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

        // Alongside the turn, not before it — the acknowledgment exists to fill the wait,
        // so it must not add to it.
        var ack = StartAcknowledgment(transcript, authToken, ct);

        try
        {
            Logger.Log("CompanionManager", "Sending to Claude…");
            // Route through the agent loop: Claude may call tools (PowerShell,
            // files, computer control) before answering, or just respond/point
            // normally. Either way it returns the final text for TTS. The system
            // prompt carries what Nayf remembers about the user.
            // Voice turns always carry screenshots and can point/draw → screen model.
            RouteModel(turnUsesScreenCoordinates: true);

            // Someone asking to be shown how is asking to do it themselves. That turn gets
            // no tool that can act on their screen — not as a policy the model is asked to
            // observe, but as a set of tools it does not have.
            var toolMode = IsTeachingRequest(transcript)
                ? NayfToolMode.GuidedWalkthrough
                : NayfToolMode.AgentTask;
            Logger.Log("CompanionManager", $"toolMode={toolMode}");

            var systemPrompt = BuildSystemPrompt();
            if (toolMode == NayfToolMode.GuidedWalkthrough)
                systemPrompt += "\n\n" + WalkthroughPromptSuffix;
            var memoryBlock = Memory.ContextBlock();
            if (memoryBlock.Length > 0) systemPrompt += "\n\n" + memoryBlock;

            var responseText = await AgentManager.RunAgentLoopAsync(
                transcript,
                screenshots,
                systemPrompt,
                authToken,
                delta => UpdateOnUI(() => StreamingResponseText += delta),
                ct,
                _conversationHistory,
                toolMode);

            Logger.Log("CompanionManager", $"Claude responded ({responseText.Length} chars)");

            // The answer is in, so the acknowledgment has lost its job. If it hasn't
            // started talking yet it never will — cancelling also aborts an in-flight
            // request, so a slow ack can't hold the real answer up behind it.
            bool ackAlreadySpeaking;
            lock (_ackGate)
            {
                ack.MainTurnHasFinalAnswer = true;
                ackAlreadySpeaking = ack.DidSpeak;
            }
            if (!ackAlreadySpeaking) ack.Cts.Cancel();
            UpdateOnUI(() => DeepThinkingLabel = null);

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

            // Let the acknowledgment finish first. SpeakAsync stops whatever is playing,
            // so without this the answer would cut its own preamble off mid-word.
            try { await ack.Work; }
            catch (Exception ex) { Logger.Log("Ack", $"wait failed: {ex.Message}"); }
            if (ct.IsCancellationRequested) return;

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

    /// <summary>
    /// Starts the spoken acknowledgment for this turn, running beside it rather than
    /// ahead of it. Only agent-loop turns get one: dictation and text improvement produce
    /// no spoken answer, so there is no wait to fill.
    ///
    /// The grace delay is the whole design. A turn that answers in under a second needs no
    /// preamble, and speaking one would make Nayf slower to listen to than it actually is.
    /// So the request goes out immediately — that latency is unavoidable — but the decision
    /// to *say* it is deferred until the real turn has had its chance to win outright.
    ///
    /// Its cancellation is linked to the turn's, so that a turn the user interrupts takes
    /// its acknowledgment down with it instead of leaving one talking about a request
    /// nobody is answering any more.
    /// </summary>
    private AckHandover StartAcknowledgment(string transcript, string authToken, CancellationToken turnCt)
    {
        var handover = new AckHandover(CancellationTokenSource.CreateLinkedTokenSource(turnCt));
        _latestAck = handover;
        var ct = handover.Cts.Token;

        UpdateOnUI(() => DeepThinkingLabel = null);

        handover.Work = Task.Run(async () =>
        {
            try
            {
                var ackCall = _ackClaudeAPI.FetchAcknowledgmentAsync(transcript, authToken, ct);
                await Task.Delay(AcknowledgmentGraceMs, ct);
                var ack = await ackCall;

                lock (_ackGate)
                {
                    if (handover.MainTurnHasFinalAnswer || string.IsNullOrWhiteSpace(ack)) return;
                    handover.DidSpeak = true;
                }

                Logger.Log("Ack", $"speaking: {ack}");
                UpdateOnUI(() => SetVoiceState(CompanionVoiceState.Responding));
                await SpeakAcknowledgmentAsync(ack, authToken, ct);

                // Having just promised to go and work on it, say the silence that follows is
                // deliberate rather than dropping the pill back to a bare "Thinking".
                bool stillWorking;
                lock (_ackGate) stillWorking = !handover.MainTurnHasFinalAnswer;
                if (stillWorking)
                    UpdateOnUI(() =>
                    {
                        // Tested inside the hop, not before it. Stopping the audio is what
                        // releases this path, so a barge-in arrives here as a stop and a
                        // cancellation at almost the same moment — and reading the token on
                        // the UI thread puts the check after the press either way. Without
                        // it, "Thinking deeper" lands on top of someone who is talking.
                        if (ct.IsCancellationRequested) return;
                        SetVoiceState(CompanionVoiceState.Processing);
                        DeepThinkingLabel = "Thinking deeper";
                    });
            }
            catch (OperationCanceledException) { /* the real answer got there first */ }
            catch (Exception ex) { Logger.Log("Ack", $"skipped: {ex.Message}"); }
        }, ct);

        return handover;
    }

    /// <summary>
    /// Speaks the acknowledgment and returns when the audio has actually finished, rather
    /// than when it starts — <see cref="ElevenLabsTTSClient.SpeakAsync"/> returns as soon
    /// as the first samples are queued, and the caller needs to know when the speaker is
    /// free again.
    /// </summary>
    private async Task SpeakAcknowledgmentAsync(string ack, string authToken, CancellationToken ct)
    {
        var finished = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        bool started = false;

        void OnStarted() => started = true;
        void OnStopped()
        {
            // SpeakAsync stops any current audio before it plays its own, so ignore a stop
            // that arrives before this acknowledgment ever reached the speaker.
            if (!started) return;
            finished.TrySetResult();
        }

        _ackSpeaking = true;
        _elevenLabsTTSClient.PlaybackStarted += OnStarted;
        _elevenLabsTTSClient.PlaybackStopped += OnStopped;
        try
        {
            await _elevenLabsTTSClient.SpeakAsync(ack, authToken, ct);
            // No audio ever played — TTS failed or came back empty. Nothing to wait for.
            if (!started) return;
            using (ct.Register(() => finished.TrySetCanceled(ct)))
                await finished.Task;
        }
        finally
        {
            _elevenLabsTTSClient.PlaybackStarted -= OnStarted;
            _elevenLabsTTSClient.PlaybackStopped -= OnStopped;
            _ackSpeaking = false;
        }
    }

    /// <summary>
    /// How long the real turn gets to finish before the acknowledgment is worth speaking.
    /// Tuned against real turns: below this, the answer generally arrives first anyway.
    /// </summary>
    private const int AcknowledgmentGraceMs = 700;

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

    // MARK: - Guided walkthrough

    /// <summary>
    /// The three beats of a step, in seconds from the moment it appears.
    ///
    /// Kept together because they are one piece of timing rather than three independent
    /// numbers: the cursor arrives, the outline says which thing it arrived at, and the
    /// arrow — if there is one — says where that thing goes. Read as a sequence, it spells
    /// out the instruction in the order a person would point it out. Tune them as a set.
    /// </summary>
    private static class WalkthroughBeats
    {
        /// <summary>The cursor leaves for the target immediately.</summary>
        public const double CursorFlight = 0.0;

        /// <summary>
        /// The outline starts tracing — and the instruction starts being spoken. The words
        /// land over the drawing rather than after it; waiting for the full sequence to
        /// finish leaves a second and a half of silence with a shape on screen.
        /// </summary>
        public const double Outline = 0.35;

        /// <summary>A drag's arrow draws, once the outline has finished.</summary>
        public const double Arrow = 1.05;
    }

    /// <summary>
    /// The step currently on screen, or null when Nayf isn't waiting on the user.
    ///
    /// Doubles as the marker that a spoken or typed answer belongs to the walkthrough
    /// rather than starting a new turn — it outlives the voice state, which passes through
    /// Listening and Processing on the way to delivering that answer.
    /// </summary>
    private PendingWalkthroughStep? _pendingStep;

    private sealed class PendingWalkthroughStep
    {
        public required WalkthroughStep Step { get; init; }

        public TaskCompletionSource<WalkthroughReply> Completion { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>
    /// Where the voice pipeline comes to rest. Normally Idle — but with a step still on
    /// screen the user is mid-walkthrough, and dropping to Idle would take the pill down
    /// and leave them looking at an outline nothing is waiting for.
    /// </summary>
    private CompanionVoiceState RestingState =>
        _pendingStep != null ? CompanionVoiceState.AwaitingUserStep : CompanionVoiceState.Idle;

    /// <summary>
    /// Shows one walkthrough step and waits for the user to do it. Called by the agent loop,
    /// which stays suspended inside this for however long the user takes.
    /// </summary>
    private async Task<WalkthroughReply> PresentWalkthroughStepAsync(
        WalkthroughStep step, CancellationToken ct)
    {
        var pending = new PendingWalkthroughStep { Step = step };

        // Built before the hop to the UI thread: each annotation starts its own animation
        // clock when it is constructed, and the beats are measured from here.
        var annotations = BuildStepAnnotations(step);

        UpdateOnUI(() =>
        {
            _pendingStep = pending;

            // Beat 1. Setting the target is what launches the cursor — the overlay picks
            // the change up on its next frame and flies there.
            DetectedElementPosition = step.ClickPoint;
            DetectedElementBubbleText = step.Label;

            ShowScreenAnnotations(annotations);
            SetVoiceState(CompanionVoiceState.AwaitingUserStep);
        });

        // Only for a step that ends in a click. Everything else is finished by the user
        // saying so, and a hook watching for a click nobody is waiting for is pure cost.
        if (step.WaitFor == WalkthroughWaitFor.Click)
            _pushToTalkMonitor.StartWatchingClicks();

        _ = SpeakStepInstructionAsync(step, ct);

        try
        {
            using (ct.Register(() => pending.Completion.TrySetCanceled(ct)))
                return await pending.Completion.Task;
        }
        finally
        {
            // Also on the cancelled path: an interrupted walkthrough must not leave a hook
            // installed or an outline drawn around a step nobody is on any more.
            _pushToTalkMonitor.StopWatchingClicks();
            UpdateOnUI(() =>
            {
                if (ReferenceEquals(_pendingStep, pending)) _pendingStep = null;
                ClearScreenAnnotations();
            });
        }
    }

    /// <summary>
    /// The marks for one step: the outline that says "this is the thing", and — only for a
    /// drag — the arrow that says where it goes.
    ///
    /// A click step gets no arrow. The cursor has already flown to the spot, and a second
    /// mark pointing at the same place would be saying something the step doesn't mean.
    /// </summary>
    private static IReadOnlyList<ScreenAnnotation> BuildStepAnnotations(WalkthroughStep step)
    {
        var bounds = OutlineBoundsFor(step);

        // Classified on the target itself, not on the padded rectangle drawn around it.
        // A 16px icon on a 4K screen is ~39 real pixels — an ellipse — and the 4px of
        // clearance would otherwise push it over the threshold into a rounded box.
        var kind = ScreenAnnotation.HighlightKindFor(step.TargetBounds ?? bounds);

        var outline = ScreenAnnotation.Outline(bounds, step.Label, WalkthroughBeats.Outline, kind);
        var annotations = new List<ScreenAnnotation> { outline };

        if (step.DragTo is { } destination)
        {
            annotations.Add(ScreenAnnotation.ArrowTo(
                destination,
                ScreenAnnotation.ArrowOrigin(bounds, outline.Kind, destination),
                label: null,
                appearDelay: WalkthroughBeats.Arrow));
        }

        return annotations;
    }

    /// <summary>
    /// What to draw the outline around: the target's own bounds pushed out a few pixels so
    /// the line sits just off the control rather than on top of its border, or a small ring
    /// around the click point when Claude gave no size — a slightly generous circle still
    /// reads as "this one", where a zero-size rectangle draws nothing at all.
    /// </summary>
    private static System.Drawing.RectangleF OutlineBoundsFor(WalkthroughStep step)
    {
        const float outlinePadding = 4f;
        const float fallbackDiameter = 34f;

        if (step.TargetBounds is { Width: > 1, Height: > 1 } target)
        {
            target.Inflate(outlinePadding, outlinePadding);
            return target;
        }

        return new System.Drawing.RectangleF(
            step.ClickPoint.X - fallbackDiameter / 2f,
            step.ClickPoint.Y - fallbackDiameter / 2f,
            fallbackDiameter,
            fallbackDiameter);
    }

    /// <summary>
    /// Says the step's one sentence, starting at beat 2.
    ///
    /// Fired without being awaited: the step is waiting on the user, not on the speaker,
    /// and someone who already knows where to click should be able to click it while Nayf
    /// is still talking.
    /// </summary>
    private async Task SpeakStepInstructionAsync(WalkthroughStep step, CancellationToken ct)
    {
        var line = System.Text.RegularExpressions.Regex
            .Replace(step.Spoken ?? "", @"\[POINT:[^\]]+\]", "").Trim();
        if (line.Length == 0) return;

        try
        {
            await Task.Delay(TimeSpan.FromSeconds(WalkthroughBeats.Outline), ct);

            var authToken = await _authManager.CurrentAccessTokenAsync();
            if (authToken == null || ct.IsCancellationRequested) return;

            await _elevenLabsTTSClient.SpeakAsync(line, authToken, ct);
        }
        catch (OperationCanceledException) { /* the user moved on */ }
        catch (Exception ex)
        {
            Logger.Log("Walkthrough", $"step speech failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Finishes the step the user was on, letting the walkthrough carry on from where it
    /// paused. Returns false when nothing was waiting — in which case whatever the user
    /// said is a new request and belongs on the normal path.
    /// </summary>
    private bool TryResumeWalkthrough(WalkthroughResumeReason reason, string? transcript = null)
    {
        var pending = _pendingStep;
        if (pending == null) return false;
        if (!pending.Completion.TrySetResult(new WalkthroughReply(reason, transcript))) return false;

        _pendingStep = null;

        // Back to Processing: the loop is running again, and the pill should stop telling
        // the user it is their turn.
        SetVoiceState(CompanionVoiceState.Processing);
        return true;
    }

    /// <summary>
    /// A click landed somewhere while a step was waiting for one.
    ///
    /// Only a click on the thing Nayf pointed at counts. Advancing on any click anywhere
    /// means the user's own taskbar, a stray click in another window, or their attempt to
    /// focus the app all skip a step they never performed — and the walkthrough carries on
    /// describing a screen that never changed.
    /// </summary>
    private void OnMouseClicked(System.Drawing.Point point)
    {
        var pending = _pendingStep;
        if (pending == null || pending.Step.WaitFor != WalkthroughWaitFor.Click) return;
        if (!IsClickOnTarget(pending.Step, point)) return;

        TryResumeWalkthrough(WalkthroughResumeReason.Clicked);
    }

    /// <summary>
    /// True when a click is close enough to count as the step being done: inside the
    /// target's bounds, or near the point Nayf pointed at when it was given no bounds.
    /// </summary>
    private static bool IsClickOnTarget(WalkthroughStep step, System.Drawing.Point point)
    {
        const float clickProximityRadius = 40f;

        if (step.TargetBounds is { Width: > 1, Height: > 1 } bounds &&
            bounds.Contains(point.X, point.Y))
            return true;

        float dx = point.X - step.ClickPoint.X;
        float dy = point.Y - step.ClickPoint.Y;
        return MathF.Sqrt(dx * dx + dy * dy) <= clickProximityRadius;
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

    /// <summary>
    /// Puts a set of marks on screen, replacing whatever was there.
    ///
    /// The animation clock starts when each <see cref="ScreenAnnotation"/> is constructed, not
    /// here, so a caller staging a sequence gives each one an <c>AppearDelay</c> and hands them
    /// over together in a single call.
    /// </summary>
    public void ShowScreenAnnotations(IReadOnlyList<ScreenAnnotation> annotations)
    {
        UpdateOnUI(() =>
        {
            ScreenAnnotations = annotations;
            ScreenAnnotationsChanged?.Invoke();
        });
        Logger.Log("Annotations", $"showing {annotations.Count} annotation(s)");
    }

    /// <summary>Takes every mark off the screen.</summary>
    public void ClearScreenAnnotations()
    {
        if (ScreenAnnotations.Count == 0) return;

        UpdateOnUI(() =>
        {
            ScreenAnnotations = Array.Empty<ScreenAnnotation>();
            ScreenAnnotationsChanged?.Invoke();
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
                    UpdateOnUI(() => SetVoiceState(RestingState));
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

        // "Thinking deeper" only means anything while Nayf is actually thinking. Every
        // other state — speaking, listening, waiting on the user, idle — has to clear it,
        // or it outranks the real status in the pill and sticks there.
        if (state != CompanionVoiceState.Processing)
            DeepThinkingLabel = null;
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
        _latestAck?.Cts.Cancel();
        _watchdogCts?.Cancel();
        _micPermissionPollCts?.Cancel();
        _creditsHttp.Dispose();
    }
}
