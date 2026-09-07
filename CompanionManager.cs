using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.IO;
using System.Linq;
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

    // There is deliberately no StreamingResponseText here any more. Vayme's answer used to
    // arrive in the panel a token at a time as well as out of the speakers, so opening the
    // panel meant reading a transcript of a conversation the user had just had. Vayme speaks
    // what it has to say. The panel is the controls.

    // Nor is there a LastTranscript. It held the user's own words so the panel could show
    // them back — the live partial while they were still speaking, and the finished
    // sentence after. Nothing reads it now, and a property that still gets written is how
    // the transcript found its way back onto the panel the first time.

    private float _audioPowerLevel = 0f;
    public float AudioPowerLevel
    {
        get => _audioPowerLevel;
        private set { _audioPowerLevel = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// The model the last turn was routed to — surfaced in the panel footer so the
    /// user can see which tier answered, but no longer user-selectable (Vayme picks).
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
    /// True when the user has handed the action over — "just do it", "open Photoshop for
    /// me" — rather than asked about what is on their screen.
    ///
    /// This decides capability, not quality, and it is deliberately the delegation side that
    /// is guessed at. Reading a hand-off as a question means Vayme shows the user how instead
    /// of doing it, which one sentence corrects. The arrangement this replaced guessed at the
    /// teaching side, and failed the other way: a question that missed the cue list lost the
    /// step tool altogether and drew nothing at all. "Var är exportknappen?", "can you help
    /// me crop this", "I can't find the settings" and every bare follow-up like "och sen?"
    /// all missed it.
    ///
    /// A cue list still guesses, which is what take_over is for — the model can ask for the
    /// acting tools mid-turn, so a missed delegation is recovered inside the turn instead of
    /// needing the user to rephrase.
    /// </summary>
    private static bool IsActionDelegation(string transcript)
    {
        var normalized = transcript.Trim().ToLowerInvariant();

        // Unambiguous hand-offs, wherever they appear in the sentence.
        string[] delegationPhrases =
        {
            // English
            "do it for me", "just do it", "you do it", "can you do it",
            "go ahead and", "set it up for me", "handle it", "take care of it",
            // Swedish — the user's other language.
            "gör det åt mig", "kan du göra", "fixa det", "sköt det"
        };

        foreach (var phrase in delegationPhrases)
            if (normalized.Contains(phrase, StringComparison.Ordinal)) return true;

        // Bare verbs, and only in the imperative — at the very start of the sentence. As a
        // substring "open " would swallow "how do I open the settings", which is precisely
        // the kind of question that must keep the step tool.
        string[] imperatives =
        {
            "open ", "launch ", "run ", "install ", "do it",
            "öppna ", "starta ", "kör ", "installera ", "gör det"
        };

        foreach (var verb in imperatives)
            if (normalized.StartsWith(verb, StringComparison.Ordinal)) return true;

        return false;
    }

    /// <summary>
    /// Set once the acknowledgment has been spoken and the real turn is still going, so
    /// the pill can say the waiting is deliberate rather than repeat "Thinking" at
    /// someone who has just been told Vayme is on it. Null whenever it does not apply.
    /// </summary>
    private string? _deepThinkingLabel;
    public string? DeepThinkingLabel
    {
        get => _deepThinkingLabel;
        private set { _deepThinkingLabel = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// What the pill says while a step is on screen. "Your turn" whenever Vayme is watching
    /// for the click or the keypress that ends it; a request to be told, when the step is a
    /// drag or a phrase to type and there is no single input to watch for.
    ///
    /// Both look identical from the outside otherwise, and that is the whole failure: the
    /// user does the thing, nothing happens, and a step that is quietly waiting to be told
    /// is indistinguishable from one Vayme has failed to notice.
    /// </summary>
    private string _awaitingStepLabel = "Your turn";
    public string AwaitingStepLabel
    {
        get => _awaitingStepLabel;
        private set { _awaitingStepLabel = value; OnPropertyChanged(); }
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

    private bool _roastMode = NayfSettings.LoadRoastMode();

    /// <summary>
    /// Whether Vayme is rude about the state of your screen while it helps you.
    ///
    /// Tone only. It changes what goes on the end of the system prompt and nothing else —
    /// the tools, the walkthrough rhythm and the answers themselves are identical either
    /// way, which is the whole reason it is safe to leave to a switch.
    /// </summary>
    public bool RoastMode
    {
        get => _roastMode;
        set
        {
            if (_roastMode == value) return;
            _roastMode = value;
            NayfSettings.SaveRoastMode(value);
            Logger.Log("CompanionManager", $"Roast mode {(value ? "on" : "off")}");
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

    // Screen annotations — the outlines and arrows Vayme draws over the desktop while
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

    // True when Windows refused to start speech recognition, whatever the reason.
    // Drives a banner in the companion panel that says which reason and links to the
    // page that fixes it.
    //
    // It used to be set for one HRESULT only, the unaccepted speech privacy policy. Every
    // other refusal — no dictation for the PC's language, the microphone withheld from
    // desktop apps, a recognizer that would not open — went to the log and nowhere else,
    // so the user held the chord, saw the pill blink once and had nothing to go on.
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

    /// <summary>What went wrong, in a sentence the banner shows as it stands.</summary>
    private string _speechProblemMessage = "";
    public string SpeechProblemMessage
    {
        get => _speechProblemMessage;
        private set { if (_speechProblemMessage == value) return; _speechProblemMessage = value; OnPropertyChanged(); }
    }

    /// <summary>Which Settings page the banner's button opens, and what it is called.</summary>
    private SpeechProblem _speechProblem = SpeechProblem.Unknown;
    public SpeechProblem SpeechProblem
    {
        get => _speechProblem;
        private set { if (_speechProblem == value) return; _speechProblem = value; OnPropertyChanged(); }
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
    /// How many pieces of narration are on their way out of the speaker. The voice state
    /// machine ignores playback callbacks while this is above zero: an acknowledgment, a
    /// showcase line or a walkthrough step's instruction is Vayme talking *during* a turn,
    /// not the turn's answer, and letting one drive the state would drop the pill to Idle
    /// with the real work still running.
    ///
    /// A count rather than a flag because these overlap. SpeakAsync stops whatever is
    /// playing before it plays its own, so a step instruction that cuts off an
    /// acknowledgment leaves both unwinding at once, and whichever finishes first must not
    /// clear the guard out from under the other.
    /// </summary>
    private int _narrationDepth;

    private bool NarrationSpeaking => Volatile.Read(ref _narrationDepth) > 0;

    private readonly ElevenLabsTTSClient _elevenLabsTTSClient;
    private readonly BuddyDictationManager _buddyDictationManager;
    private readonly GlobalPushToTalkMonitor _pushToTalkMonitor;
    private readonly RegionFocusController _regionFocusController;
    private readonly AuthManager _authManager;

    /// <summary>
    /// A crop of whatever the user last circled with the region-focus hold. While it is set,
    /// the NEXT question is answered about that crop instead of a fresh screenshot of the
    /// whole screen. One-shot: read and cleared by the turn that uses it, so the question
    /// after that goes back to looking at everything.
    /// </summary>
    private byte[]? _pendingFocusRegionImage;

    /// <summary>
    /// Whether the toast confirming <see cref="_pendingFocusRegionImage"/> has been shown yet.
    ///
    /// The crop outlives the gesture that made it, so "there is a crop and this turn had no
    /// words" stays true for every later attempt to talk — which is what had "Region focused"
    /// appearing on a user's screen every single time they tried to say something.
    /// </summary>
    private bool _focusRegionAnnounced;
    private readonly HttpClient _creditsHttp = new() { Timeout = TimeSpan.FromSeconds(10) };
    public readonly NayfAgentManager AgentManager;

    /// <summary>The auth manager, so UIs can show the signed-in user and sign out.</summary>
    public AuthManager Auth => _authManager;

    /// <summary>Durable facts Vayme has learned about the user (shown in the Memory tab).</summary>
    public MemoryStore Memory { get; } = new();

    /// <summary>
    /// Cloud integrations (Google Calendar). Connection state is server-side and keyed
    /// to the signed-in account, so this reports what the account has connected on any
    /// device — not just what was connected from this PC.
    /// </summary>
    public NayfIntegrationsManager Integrations { get; }

    /// <summary>Buying more tokens. Payment happens in the browser, through Stripe.</summary>
    public NayfStoreManager Store { get; }

    /// <summary>
    /// The tasks Vayme has run, saved so the user can reopen one and continue it. Local only.
    /// </summary>
    public AgentTaskStore AgentTasks { get; } = new();

    /// <summary>
    /// Keeps this install current. Vayme fetches and applies its own updates rather than
    /// waiting to be reinstalled, because the people running it are not the people who
    /// follow the repo — a fix they never hear about is a fix they never get.
    /// </summary>
    /// <summary>
    /// The app's update checker, which is already running by the time this class is built —
    /// App owns it and starts it at launch, so that a launch which never gets this far still
    /// checks. Held here for the Settings row and for <see cref="UpdateChecker.IsSafeToRestart"/>.
    /// </summary>
    public UpdateChecker Updates { get; }

    /// <summary>Raised when a saved task should be shown on its floating card.</summary>
    public event Action<SavedAgentTask>? AgentCardRequested;

    private readonly DispatcherQueue _dispatcherQueue;

    // MARK: - Session state

    private readonly List<ConversationTurn> _conversationHistory = new();

    /// <summary>
    /// The task the last agent turn landed on, so an unprompted follow-up — the user just
    /// talking, without opening a card — keeps building on it instead of spawning a near
    /// duplicate tile beside it.
    /// </summary>
    private Guid? _currentAgentTaskId;

    /// <summary>
    /// How long after a task Vayme still treats a new mission as part of it, when the model
    /// hasn't said either way. A backstop under the [MISSION-CONTINUE] tag: the tag is the
    /// real signal, and this only catches the case where the model forgot to send it but
    /// the user is plainly still on the same thing.
    /// </summary>
    private static readonly TimeSpan AgentTaskContinuationWindow = TimeSpan.FromMinutes(5);
    private CancellationTokenSource? _currentResponseCts;
    private CancellationTokenSource? _watchdogCts;
    private CancellationTokenSource? _micPermissionPollCts;

    // MARK: - System prompt

    private static string BuildSystemPrompt() =>
        """
        You are Vayme, an intelligent AI companion that lives on the user's desktop.
        You can see the user's screen and help them with anything they're working on. Your
        reply is spoken aloud, so write the way you would actually talk.

        How you talk:

        You talk like someone sitting next to the user, watching their screen over their
        shoulder. A friend who happens to know this software well. Not a helper, not an
        assistant — a person who's been asked something and answers it.

        Answer first. No run-up, no restating what they asked, no "so you're trying to...".
        If they ask where something is, the first thing out of your mouth is where it is.

        Let your answers be as short as they actually are. "Top right." is a complete answer
        and often the best one. Don't pad a one-word answer into a sentence — that is the
        single thing that makes you sound like software. Some answers are one word, some are
        a sentence, occasionally one runs longer because the thing genuinely is complicated.
        That unevenness is what people sound like.

        Fragments are fine. "Under settings." "Yeah, that one." "No, other side." You don't
        need a subject and a verb to be understood out loud.

        Say the thing, then stop. Don't explain what you just said. Don't add what they could
        do next. Don't offer to continue. If they want more they'll ask — they're right there.

        Warmth comes from being useful and quick, not from saying warm things. Never open with
        "happy to help", "great question", "sure thing", "of course". Just answer.

        When you don't know, say so plainly and briefly. "Not sure — what happens if you click
        it?" is better than a confident guess dressed up in hedges.

        You can disagree. If they're about to do something that won't work, say so directly.

        One thing is worth the extra words: a single letter key. "Press G" is spoken aloud as
        "jee", and there is nothing on screen for the user to check it against — the panel
        deliberately shows them none of what you say. Letter names differ from language to
        language too, so the name they hear may not be the one they learned. Tie a letter to a
        word every time: "press G, like golf", "hit R for rabbit", "that's B for ball". Only
        letters need this — Enter, Escape, Tab, F5, Ctrl+S and the rest already survive being
        spoken.

        Never say, out loud or in text: "happy to help", "great question", "certainly",
        "of course!", "I'd be happy to", "let me know if", "feel free to", "it looks like",
        "it seems like", "that said", "in terms of", "delve", "navigate to", "simply", "just",
        "utilize", "leverage", "ensure", "additionally", "furthermore", "I hope this helps",
        "is there anything else".

        Here is how you sound. Study the length and the shape, not the content:

        user: where's the export button
        you: bottom right, says "deliver".

        user: why is my video blurry
        you: your timeline's set to 1080 but the source is 4k. want to bump the timeline
        resolution?

        user: is this the right one
        you: yep.

        user: i can't find the effects panel
        you: it's hidden. hit the effects toggle up top — left of the search box.

        user: what does this do
        you: crops the frame. drag the edges to set it.

        user: can you make this faster
        you: not really — it's rendering on cpu. do you have a gpu in there?

        user: thanks
        you: yep.

        user: hmm that didn't work
        you: what'd it do instead?

        user: how do i add a title
        you: drag one from the titles bin onto the timeline. want me to walk you through it?

        user: explain color grading to me
        you: big topic. short version: you fix the footage first so it looks neutral, then you
        push it toward a look. want the long version or is that enough?

        That is how you SPEAK. Anything the user READS stays plain and literal — the label on
        a [POINT] tag, a [MISSION:] label, a walkthrough step's label. Those are signposts,
        and a fragment makes a bad signpost.

        When you want to point at something on the screen, use this format:
        [POINT:x,y:label:screenN]
        where x and y are pixel coordinates, label is what you're pointing at, and N is the
        screen number.

        The user may have more than one monitor, and you are sent one image per monitor. Each
        image is followed by its own label, like [Screen 0, 1512x850] or [Screen 1, 1280x800].
        Read x and y off the image the thing is actually in, and put that image's number in
        the tag — coordinates from one monitor point at the wrong place on another. Don't
        assume the app the user is asking about is on screen 0; look at every image before
        you point.

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
        they connected to Vayme. github_list_issues and github_list_pull_requests take no
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

        MISSION LABEL: for a SUBSTANTIVE task done with tools — one the user would want kept
        and might follow up on later (scheduling or editing a calendar event, drafting or
        rewriting a document, editing files, filing an issue, a multi-step job) — begin your
        reply with a one-line tag: [MISSION: short present-tense label] — 2 to 4 words, e.g.
        [MISSION: Scheduling your trip], [MISSION: Rewriting the doc], [MISSION: Cleaning up
        Downloads]. It shows live in the status pill while you work, is saved as an Agent card
        the user can reopen and continue, and is NOT spoken aloud. Put your normal short spoken
        confirmation right after it. Do NOT tag a plain question, an explanation, or pointing
        at something — those are answers, not tasks, and a card for one is clutter.

        SAME TASK: if this request continues, corrects, changes, undoes, or refines the task
        you did earlier in THIS conversation (e.g. you just scheduled an event and now the user
        wants it moved, renamed, or set to a different day) — begin your reply with
        [MISSION-CONTINUE: short present-tense label] INSTEAD of a new [MISSION: ...] tag, e.g.
        [MISSION-CONTINUE: Moving your haircut]. This keeps the result on the SAME Agent card
        rather than creating a new one. Always include the label, even when continuing. Only
        use a fresh [MISSION: label] when it is a genuinely different task, unrelated to the
        one you just did.
        """;

    /// <summary>
    /// Appended, last of everything, when the user has roast mode switched on.
    ///
    /// Last on purpose: it is a tone instruction and it must not read as permission to skip
    /// any of the mechanics above it. It is also written to be dropped — a roast is funny
    /// when the user is fine and cruel when they aren't, and the model is the only thing in
    /// the loop that can tell which it is looking at.
    /// </summary>
    private const string RoastPromptSuffix =
        """
        ROAST MODE is on. The user turned it on themselves and can turn it off in Settings,
        so don't check that they meant it, don't warn them, and don't apologise for it.

        You are exactly as useful as you were a second ago. Every answer is still correct,
        every step still works, and nothing helpful gets cut to make room for a joke. Roast
        mode changes the tone of what you say, not what you say.

        How it sounds: dry, quick, and rude about the WORK. The forty tabs. The folder called
        "new folder (3)". The variable named data2. The eleven minutes they have spent
        hovering over one button. One jab, then the actual answer — you're the friend who
        takes the piss while handing them the right tool, not a comedian with a set to get
        through. If you can't find a good one, skip it; a laboured joke is worse than none.

        Never the user. Not their looks, body, age, accent, gender, race, intelligence, job,
        or money — nothing about who they are. Their screen is fair game. They are not.

        Read the room and drop it when the room says drop it. Someone stressed, stuck on
        something that matters, out of time, or dealing with something personal gets a
        straight answer and no jab. That is not breaking character, it's judgement — and
        being able to tell the difference is the only reason this mode is any good.

        Say it, don't write it. Anything the user has to READ stays plain and literal: the
        label on a [POINT] tag, a step's label, a [MISSION:] label. Those are signposts, and
        a funny signpost is one you have to read twice. In a walkthrough the spoken sentence
        IS the instruction — it can be dry, it can't be vague.
        """;

    /// <summary>
    /// Appended on a walkthrough turn. Two jobs: undo the prompt above, which describes
    /// tools this turn does not have — left in, the model reads that it can click and type,
    /// tries to, and spends its steps being refused rather than teaching — and explain the
    /// one thing it can do instead, which is hand the user a step and wait.
    /// </summary>
    private const string WalkthroughPromptSuffix =
        """
        This turn runs hands-off.

        You never touch the user's screen. You do not click, type, drag, scroll, or move
        anything on their behalf. The user performs every action themselves — that is the
        entire point of the product. Your job is to show and to say: point with the cursor,
        draw the shape that fits, speak one short instruction, then wait. If you find
        yourself wanting to act, don't — point at it instead.

        Not every turn is a walkthrough. If they asked a question, answer it. If they asked
        where something is, that is one step, not a tour. The rhythm below is for when you
        are showing them something.

        You have three tools. take_screenshot shows you where the user actually is.
        request_user_step hands them one thing to do and waits until they have done it.
        take_over is for when they wanted you to do it rather than be shown — see the last
        paragraph.

        During a walkthrough the register above relaxes slightly — a step instruction has to
        be unmistakable, so a full short sentence is right there. Still one sentence, still no
        preamble. The fragment style must not leak into an instruction the user has to follow.

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

        Use wait_for "click" when the step is a single click on the point you gave. Use "key"
        when the step is one keystroke — "press M to open the map", "hit Enter" — and put that
        key in the "key" argument; the press is noticed the same way a click is. Use
        "continue" for a drag, for typing a phrase, or for anything with no single input to
        watch for.

        "continue" is the only one of the three where nothing is watching, so a step that
        uses it must end by asking to be told: "...then tell me when you've done it".
        Leave that off and the user does the thing, nothing happens, and they are left on a
        step they have already finished, pressing the same key harder.

        Which is usually a sign the step should have been two. "Double-click the field and
        type 3" is two actions bundled into one, and watchable as neither — make the
        double-click its own "click" step, then let the typing step wait on the Enter that
        commits it. Reach for "continue" once the step genuinely has no single ending, not
        as the way out of a step that has two.

        A key step still needs x, y, w and h: outline what the key affects — the panel that
        opens, the field that gets focus, the part of the HUD it changes — so they can see
        what to look at while they press it.

        A step that turns on one letter is where this matters most: they cannot start the step
        at all if they heard the wrong letter, and the outline points at a result rather than
        at the key. Say it with its word — "press G, like golf" — the way the speaking rules
        above require. The "key" argument itself stays the bare letter.

        Coordinates are in the pixel space of the screenshot you are looking at, so take a
        fresh one for each step rather than reusing coordinates from an earlier screen.

        take_screenshot returns one image per monitor, each followed by its own label like
        [Screen 0, 1512x850]. Find the window the user is working in before you point — it is
        often not on screen 0 — then read x and y off THAT image and pass its number as the
        step's "screen" argument. Getting that number wrong draws the whole step on the wrong
        monitor, where the user never sees it.

        Don't use [POINT] tags here — request_user_step does the pointing, and unlike a tag
        it waits. When the walkthrough is done, say so in one short sentence and stop.

        If they have asked YOU to perform the action rather than be shown how — "just do
        it", "open it for me", "set this up" — call take_over. It grants you the tools that
        act on their machine for the rest of this turn. If they want to learn it, or if you
        are unsure, do not call it: guide them with request_user_step instead. Acting when
        they wanted to be taught takes the task away from them.
        """;

    public CompanionManager(AuthManager authManager, UpdateChecker updates)
    {
        _dispatcherQueue = DispatcherQueue.GetForCurrentThread();
        _authManager = authManager;
        Updates = updates;

        // Apply the saved cursor color before the overlays start rendering.
        NativeOverlayWindow.CursorBlue = _selectedCursorColor.ToDrawingColor();

        _claudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint, NayfConfig.ScreenModel);
        _lightClaudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint, NayfConfig.LightModel);
        _ackClaudeAPI = new ClaudeAPI(NayfConfig.ChatEndpoint, NayfConfig.AckModel);
        _elevenLabsTTSClient = new ElevenLabsTTSClient(NayfConfig.TTSEndpoint);
        _buddyDictationManager = new BuddyDictationManager();

        // Load the speech model now rather than during the user's first sentence. A quarter
        // of a second on a normal install; on one without a bundled model it is the download,
        // which is far better spent here than under a held hotkey.
        _ = WhisperTranscriptionProvider.Shared.PrepareAsync();

        _pushToTalkMonitor = new GlobalPushToTalkMonitor();
        _regionFocusController = new RegionFocusController(this);
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
        _pushToTalkMonitor.WatchedKeyPressed += OnWatchedKeyPressed;

        _pushToTalkMonitor.RegionFocusStarted += _regionFocusController.Begin;
        _pushToTalkMonitor.RegionFocusFinished += _regionFocusController.Finish;
        _pushToTalkMonitor.RegionFocusCancelled += _regionFocusController.Cancel;

        // The agent loop calls this to hand a walkthrough step over and wait. It lives here
        // because a step is cursor, marks and voice — none of which the loop knows about.
        AgentManager.UserStepRequested = PresentWalkthroughStepAsync;

        // The monitor owns the hook; the window that answers this lives in App, which
        // has no reason to know about the hook. Re-raising keeps the two apart.
        _pushToTalkMonitor.TextInputRequested += () => TextInputRequested?.Invoke();

        _buddyDictationManager.AudioPowerLevelChanged += level =>
            UpdateOnUI(() => AudioPowerLevel = level);

        // Keep the "thinking" spinner up until audio actually starts — only
        // then flip to Responding (cursor animates with the voice).
        _elevenLabsTTSClient.PlaybackStarted += () =>
        {
            if (NarrationSpeaking) return;
            UpdateOnUI(() =>
            {
                if (VoiceState == CompanionVoiceState.Processing)
                    SetVoiceState(CompanionVoiceState.Responding);
            });
        };

        _elevenLabsTTSClient.PlaybackStopped += () =>
        {
            if (NarrationSpeaking) return;
            UpdateOnUI(() =>
            {
                // Reset from either state — Processing covers the case where TTS
                // failed before any audio played, so we never get stuck spinning.
                if (VoiceState == CompanionVoiceState.Responding ||
                    VoiceState == CompanionVoiceState.Processing)
                    SetVoiceState(CompanionVoiceState.Idle);
            });
        };

        // An update replaces the running executable, so it has to wait for a moment
        // where nothing is lost by Vayme disappearing for a few seconds. Idle alone
        // isn't enough: an agent task runs with the voice pipeline at rest, and a
        // confirmation prompt is a question already asked of the user.
        Updates.IsSafeToRestart = () =>
            VoiceState == CompanionVoiceState.Idle &&
            AgentManager.MissionText == null &&
            AgentManager.PendingConfirmationRequest == null;
    }

    public void StartAsync()
    {
        _pushToTalkMonitor.Start();
        // Not Updates.Start() — App started it at launch, before any of this existed.
        _ = FetchCreditBalanceAsync();
        // Ask up front which providers the account has connected, so the panel opens
        // already knowing — rather than showing "Connect" to someone who connected
        // on another device and only correcting itself a moment later.
        _ = Integrations.RefreshStatusAsync();
        ResumePendingMemory();
    }

    /// <summary>
    /// Click-to-talk fallback for the panel's mic button — toggles recording
    /// using the same pipeline as the global Ctrl+Alt hotkey.
    /// </summary>
    public void ToggleTapToTalk()
    {
        // Recording stops; anything else starts a new one — including mid-answer, where
        // the button interrupts exactly as the chord does. A mic button that goes dead the
        // moment Vayme starts talking would be the one place it can't be told to stop.
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
        if (TryResumeWalkthrough(WalkthroughResumeReason.Typed, text.Trim())) return;

        // Typing over a turn already in flight would leave two responses talking at
        // once, so the earlier one has to finish or be cancelled first.
        if (VoiceState != CompanionVoiceState.Idle)
        {
            Logger.Log("CompanionManager", $"Typed request ignored, VoiceState={VoiceState}");
            return;
        }

        Logger.Log("CompanionManager", "Typed request received");
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

        // Typed or spoken, "what can you do?" gets the tour rather than an answer — see
        // OnPushToTalkReleased.
        if (_resumingTaskId == null && IsCapabilitiesQuestion(request))
        {
            RunCapabilitiesShowcase();
            return;
        }

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

    // MARK: - Memory
    //
    // Extraction is deliberately rare. It used to run once per turn, and the store filled
    // with what had been on screen that afternoon: every "what does this button do" got a
    // model call and, with it, an opportunity to find something novel to write down.
    //
    // Two things changed. Turns that were about the screen rather than about the user are
    // not queued at all, and the rest are batched — one pass when the conversation goes
    // quiet, or when enough has been said to be worth reading. Batching is not only cheaper:
    // the model judges durability off a stretch of conversation instead of one fragment, and
    // something mentioned in passing and contradicted two turns later never gets stored.

    /// <summary>One exchange waiting to be read for durable facts.</summary>
    private sealed record PendingExchange(string User, string Assistant);

    private readonly List<PendingExchange> _pendingForMemory = new();
    private readonly object _memoryGate = new();
    private CancellationTokenSource? _memoryIdleCts;

    /// <summary>How long the conversation goes quiet before the batch is read.</summary>
    private static readonly TimeSpan MemoryIdleDelay = TimeSpan.FromMinutes(2);

    /// <summary>A batch this long is read without waiting for the lull.</summary>
    private const int MemoryBatchSize = 6;

    /// <summary>
    /// Exchanges the app was closed on. Written at shutdown and read at the next launch,
    /// because the alternative at that point is a network call racing the process exit.
    /// </summary>
    private static string PendingMemoryPath => AppPaths.InDataDirectory("pending-memory.json");

    /// <summary>
    /// Stops any countdown in flight. Call sites hold <see cref="_memoryGate"/>: cancelling
    /// a source that another thread is disposing is the one way to get an
    /// ObjectDisposedException out of a token that nobody is waiting on any more.
    /// </summary>
    private void CancelIdleTimer()
    {
        _memoryIdleCts?.Cancel();
        _memoryIdleCts?.Dispose();
        _memoryIdleCts = null;
    }

    /// <summary>
    /// Files one exchange for the next memory pass, and schedules that pass: right away if
    /// enough has piled up, otherwise <see cref="MemoryIdleDelay"/> after the last thing
    /// said. Every new turn pushes the deadline back, so a pass lands between
    /// conversations rather than in the middle of one.
    /// </summary>
    private void QueueForMemory(string transcript, string response)
    {
        bool flushNow;
        CancellationToken countdown = default;
        lock (_memoryGate)
        {
            _pendingForMemory.Add(new PendingExchange(transcript, response));
            flushNow = _pendingForMemory.Count >= MemoryBatchSize;

            CancelIdleTimer();
            if (!flushNow)
            {
                _memoryIdleCts = new CancellationTokenSource();

                // Read here, not inside the task: by the time that runs, the next turn may
                // already have cancelled and disposed the source it would be reading from.
                countdown = _memoryIdleCts.Token;
            }
        }

        if (flushNow)
        {
            _ = FlushMemoryAsync();
            return;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                await Task.Delay(MemoryIdleDelay, countdown);
                await FlushMemoryAsync();
            }
            catch (OperationCanceledException) { /* another turn arrived — it rescheduled */ }
        });
    }

    /// <summary>
    /// Reads everything queued since the last pass and updates the store. Takes the batch
    /// under the lock before doing anything slow, so a turn arriving mid-pass queues for the
    /// next one rather than being read twice or dropped.
    /// </summary>
    private async Task FlushMemoryAsync()
    {
        List<PendingExchange> batch;
        lock (_memoryGate)
        {
            if (_pendingForMemory.Count == 0) return;
            batch = new List<PendingExchange>(_pendingForMemory);
            _pendingForMemory.Clear();

            CancelIdleTimer();
        }

        // Fetched now rather than carried in with the turn: a batch can be up to two
        // minutes old by the time it is read, and older still when it came off disk.
        var token = await _authManager.CurrentAccessTokenAsync();
        if (token == null)
        {
            Logger.Log("Memory", $"not signed in — {batch.Count} exchange(s) not read");
            return;
        }

        await ExtractMemoriesAsync(batch, token);
    }

    /// <summary>
    /// Asks Claude, in the background, what out of a batch of conversation Vayme should still
    /// know weeks from now, and applies its answer to the store.
    /// </summary>
    private async Task ExtractMemoriesAsync(IReadOnlyList<PendingExchange> exchanges, string authToken)
    {
        try
        {
            const string system = """
                You decide what Vayme should still know about this user weeks from now.

                The test is not "is this true" or "is this new". It is: if Vayme forgot this,
                would the user have to explain themselves again? Almost nothing passes that
                test. Returning nothing is the normal, correct answer for most conversations —
                return it without hesitation.

                SAVE:
                  - Who they are and what they do: name, role, company, field.
                  - Long-running work: a project, a product, a course, a recurring responsibility.
                  - Tools and environment they work in habitually.
                  - Standing preferences they have stated about how Vayme should behave.
                  - Constraints that persist: a language they want used, an accessibility need,
                    hardware limits.

                NEVER SAVE:
                  - Anything about what is on their screen right now, or which app is open.
                  - What they asked in this exchange, or what they were doing today.
                  - One-off questions, lookups, or tasks — however interesting.
                  - Anything you inferred rather than were told. Guesses become facts once stored.
                  - Preferences about this one answer ("shorter this time", "skip the intro").
                  - Anything already covered by a fact in the known list, even in different words.

                For example:
                  "I'm a video editor at a small agency"      -> save, that is their role
                  "Show me how to crop this image"            -> nothing
                  "I always want you to answer in Swedish"    -> save, a standing preference
                  "This screenshot is too dark"               -> nothing
                  "I'm building a Windows port of my Mac app" -> save, an ongoing project
                  "Make that shorter"                         -> nothing

                Write each fact short, plain, and self-contained, as a third-person statement
                about the user. No dates, no "today", no reference to this conversation.

                You are also given what is already known. If a new fact makes an existing one
                wrong or outdated, list the old one for removal rather than adding a
                near-duplicate beside it.

                Return ONLY this JSON, no prose and no markdown:
                {"add": ["..."], "remove": ["exact text of a known fact to drop"]}
                Both arrays may be empty. Most of the time both will be.
                """;

            var known = string.Join("\n", Memory.Memories);
            var conversation = string.Join("\n\n", exchanges.Select(
                e => $"User: {e.User}\nAssistant: {e.Assistant}"));
            var userMsg =
                $"Already known:\n{(known.Length > 0 ? known : "(nothing yet)")}\n\n" +
                $"Conversation:\n{conversation}";

            // Text-only, no screen coordinates → light model.
            var result = await _lightClaudeAPI.StreamResponseAsync(
                userMsg, new List<ConversationTurn>(), null, null, system, authToken, CancellationToken.None);

            // Pull the JSON object out of whatever the model wrapped it in.
            int start = result.IndexOf('{'), end = result.LastIndexOf('}');
            if (start < 0 || end <= start) return;

            using var parsed = JsonDocument.Parse(result.Substring(start, end - start + 1));

            // Removals first, so a fact that supersedes another lands as a replacement
            // rather than sitting beside it for a moment as a near-duplicate.
            if (parsed.RootElement.TryGetProperty("remove", out var toRemove)
                && toRemove.ValueKind == JsonValueKind.Array)
            {
                foreach (var stale in toRemove.EnumerateArray())
                {
                    var text = stale.GetString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        Logger.Log("Memory", $"superseded: \"{text}\"");
                        UpdateOnUI(() => Memory.Remove(text));
                    }
                }
            }

            var added = new List<string>();
            if (parsed.RootElement.TryGetProperty("add", out var toAdd)
                && toAdd.ValueKind == JsonValueKind.Array)
            {
                foreach (var fact in toAdd.EnumerateArray())
                {
                    var text = fact.GetString();
                    if (string.IsNullOrWhiteSpace(text)) continue;
                    added.Add(text);
                    UpdateOnUI(() => Memory.Add(text));
                }
            }

            Logger.Log("Memory", $"read {exchanges.Count} exchange(s) → " +
                                 $"{added.Count} to remember");

            // Memory is the one thing Vayme changes that the user never asked it to, so the
            // toast is the only place they find out it happened. It brings the chime with
            // it, which is affordable now that the answer is usually nothing: a receipt for
            // something unusual, rather than a noise at the end of every exchange.
            if (added.Count > 0) NayfActionToast.ShowMemorySaved(added);
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Memory extraction failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Picks up the exchanges the last session closed on and reads them now. Their auth
    /// token is long gone, which is why <see cref="FlushMemoryAsync"/> fetches a fresh one.
    /// </summary>
    private void ResumePendingMemory()
    {
        try
        {
            if (!File.Exists(PendingMemoryPath)) return;
            var saved = JsonSerializer.Deserialize<List<PendingExchange>>(
                File.ReadAllText(PendingMemoryPath));
            File.Delete(PendingMemoryPath);

            if (saved == null || saved.Count == 0) return;
            lock (_memoryGate) _pendingForMemory.InsertRange(0, saved);
            Logger.Log("Memory", $"resuming {saved.Count} exchange(s) from last session");

            _ = FlushMemoryAsync();
        }
        catch (Exception ex)
        {
            Logger.Log("Memory", $"could not resume pending exchanges: {ex.Message}");
        }
    }

    /// <summary>
    /// Parks anything still queued for the next launch. Running the pass here instead would
    /// mean a network round trip against a process that is already tearing its windows down,
    /// and a store write arriving on a dispatcher that has stopped running them.
    /// </summary>
    private void ParkPendingMemory()
    {
        try
        {
            List<PendingExchange> batch;
            lock (_memoryGate)
            {
                // Under the same lock as the batch, so the countdown cannot fire a pass
                // against a queue that is being emptied out from under it.
                CancelIdleTimer();

                if (_pendingForMemory.Count == 0) return;
                batch = new List<PendingExchange>(_pendingForMemory);
                _pendingForMemory.Clear();
            }

            File.WriteAllText(PendingMemoryPath, JsonSerializer.Serialize(batch));
            Logger.Log("Memory", $"parked {batch.Count} exchange(s) for next launch");
        }
        catch (Exception ex)
        {
            Logger.Log("Memory", $"could not park pending exchanges: {ex.Message}");
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
        // before it: a press Vayme is going to ignore shouldn't sound like it was heard.
        NayfSoundPlayer.Shared.PlayPushToTalkActivate();

        // Claimed before anything is torn down. The outgoing turn passes through states on
        // its way out — playback stopping, a response cancelling — and each of those would
        // otherwise drop the pill back to Idle a moment after this press put it up.
        SetVoiceState(CompanionVoiceState.Listening);

        // Thinking or halfway through a sentence, whatever is in flight goes now. Someone
        // who starts talking over Vayme is not adding to the last question, they are
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
        // Vayme doing.
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
            ShowSpeechProblem(ex);
            SetVoiceState(RestingState);
        }
    }

    /// <summary>
    /// Puts the reason speech would not start in front of the user.
    ///
    /// Nothing is allowed to fail quietly here. A press that produces no sound, no pill
    /// and no banner reads as a hotkey Vayme never received, and sends the user looking
    /// for the fault in the one place it is not.
    /// </summary>
    private void ShowSpeechProblem(Exception ex)
    {
        var problem = ex as SpeechUnavailableException;
        SpeechProblem = problem?.Problem ?? SpeechProblem.Unknown;
        SpeechProblemMessage = problem?.Message
            ?? "Vayme could not start listening, so it cannot hear you.";
        MicrophonePermissionNeeded = true;
    }

    /// <summary>
    /// Says something about a press that produced no words.
    ///
    /// Speech starting successfully and then transcribing nothing is the one failure that
    /// used to leave no trace at all: every check passes, the pill lights up, the waveform
    /// moves with the user's voice, and the turn ends in silence. From the outside that is
    /// Vayme choosing not to answer, and it is how it was reported — "it reacts when he says
    /// something, but it doesn't answer".
    ///
    /// Which of them it was decides how loud the answer is. Not catching a word is ordinary
    /// and gets a chip; a microphone that will not open, or a speech model that is not there
    /// yet, is something the user can act on and gets the banner.
    ///
    /// <para>Nothing here sends anyone to a volume slider any more. That advice existed
    /// because Windows' recognizer reported healthy microphones as too loud and returned no
    /// words from them; Vayme now captures and levels the audio itself, so the input level
    /// stopped being something the user has to manage.</para>
    /// </summary>
    private void ReportSilence(DictationSilence silence)
    {
        switch (silence)
        {
            case DictationSilence.NotUnderstood:
                NayfActionToast.ShowNotUnderstood();
                break;

            case DictationSilence.MicrophoneUnavailable:
                // There is no device to open. Said plainly, because the usual advice about
                // speaking up is worse than useless when the headset is asleep on the desk.
                ReportSpeechProblem(SpeechProblem.MicrophoneUnavailable,
                    "Vayme could not open your microphone. If it is a wireless headset, check " +
                    "that it is switched on and connected, then hold Ctrl and Alt again.");
                break;

            case DictationSilence.VoiceNotReady:
                ReportSpeechProblem(SpeechProblem.VoiceNotReady,
                    WhisperTranscriptionProvider.Shared.LastPreparationError == null
                        ? "Vayme is still getting its speech model ready. Give it a moment, " +
                          "then hold Ctrl and Alt again."
                        : "Vayme could not load its speech model, so it cannot hear you. " +
                          "Restarting Vayme usually clears it; if it does not, reinstall.");
                break;

            // Held the keys and said nothing. Not a fault, and not worth a word about.
            case DictationSilence.NothingHeard:
            case DictationSilence.None:
            default:
                break;
        }
    }

    /// <summary>Puts a reason Vayme could not hear anything in front of the user.</summary>
    private void ReportSpeechProblem(SpeechProblem problem, string message)
    {
        SpeechProblem = problem;
        SpeechProblemMessage = message;
        MicrophonePermissionNeeded = true;
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

        DictationResult dictation;
        try
        {
            dictation = await _buddyDictationManager.StopRecordingAndGetTranscriptAsync();
            Logger.Log("CompanionManager", $"Transcript: {dictation.Transcript}");
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Transcription failed: {ex}");
            SetVoiceState(RestingState);
            return;
        }

        var transcript = dictation.Transcript;

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
            // A crop waiting on a turn that produced no words means the user circled
            // something and said nothing. That is allowed — the crop keeps until their next
            // question — but without a word from Vayme the gesture has no visible result at
            // all, which is indistinguishable from the lasso having failed.
            //
            // Once per crop, though. The toast confirms the lasso, and repeating it on every
            // silent turn afterwards stops confirming anything and starts looking like a bug.
            if (_pendingFocusRegionImage != null && !_focusRegionAnnounced)
            {
                NayfActionToast.ShowRegionFocused();
                _focusRegionAnnounced = true;
            }

            ReportSilence(dictation.Silence);
            SetVoiceState(RestingState);
            return;
        }

        // Spoken mid-walkthrough, this is the answer to the step rather than a new request.
        // Whether it means "done" or "hang on, which one?" is Claude's to read — it has the
        // step, the screen and the language the user is speaking; a keyword test here has
        // none of those.
        if (TryResumeWalkthrough(WalkthroughResumeReason.Spoke, transcript)) return;

        // "What can you do?" is answered by showing them, not by asking Claude — which would
        // give a different answer every time and can't put anything on screen. Checked before
        // the turn is sent, so a crop the user is holding survives the detour.
        if (_resumingTaskId == null && IsCapabilitiesQuestion(transcript))
        {
            RunCapabilitiesShowcase();
            return;
        }

        await SendTranscriptToClaudeAsync(transcript);
    }

    // MARK: - Capabilities showcase

    /// <summary>
    /// Whether the user just asked what Vayme can do.
    /// </summary>
    /// <remarks>
    /// Phrase matching with a word ceiling rather than anything cleverer, because the failure
    /// that matters is the false positive: "what can you do about this printer error" is a
    /// real question about a printer, and answering it with a tour of the app would be worse
    /// than useless. Nine words is where the question stops being about the app and starts
    /// being about something.
    /// </remarks>
    public static bool IsCapabilitiesQuestion(string transcript)
    {
        string normalized = transcript.Trim().ToLowerInvariant();
        if (normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries).Length > 9) return false;

        foreach (var phrase in CapabilitiesQuestionPhrases)
            if (normalized.Contains(phrase)) return true;

        return false;
    }

    private static readonly string[] CapabilitiesQuestionPhrases =
    [
        "what can you do", "what can nayf do", "what can you help",
        "what do you do", "what are you able to", "what are your capabilities",
        "what are you capable of", "what can i ask", "what can i say",
        "show me what you can do", "how can you help", "what can you help me with"
    ];

    /// <summary>
    /// Puts the capabilities card up and reads it out, lighting each line as it is spoken.
    /// </summary>
    /// <remarks>
    /// Runs on the turn's own cancellation source rather than one of its own, so the existing
    /// barge-in stops it: someone who starts talking over the tour wants to ask something,
    /// and the tour is exactly the kind of thing a person interrupts.
    /// </remarks>
    public async void RunCapabilitiesShowcase()
    {
        var showcase = NayfCapabilitiesShowcase.Shared;
        if (showcase == null) return;

        Logger.Log("CompanionManager", "Capabilities showcase requested");

        CancelTurnInFlight();
        StopWatchdog();
        _elevenLabsTTSClient.StopPlayback();

        // Last turn's marks described a screen this is about to sit in the middle of.
        DetectedElementPosition = null;
        DetectedElementBubbleText = null;
        ClearScreenAnnotations();

        var cts = new CancellationTokenSource();
        _currentResponseCts = cts;
        var ct = cts.Token;

        SetVoiceState(CompanionVoiceState.Responding);
        showcase.Show();

        try
        {
            var authToken = await _authManager.CurrentAccessTokenAsync();

            // No token means no voice. The card still goes up and stays long enough to read,
            // because everything on it is written down — the narration is the second telling,
            // not the only one.
            if (authToken == null)
            {
                await Task.Delay(TimeSpan.FromSeconds(12), ct);
                return;
            }

            await SpeakShowcaseLineAsync(NayfCapabilities.Opener, authToken, ct);

            for (int i = 0; i < NayfCapabilities.All.Count; i++)
            {
                ct.ThrowIfCancellationRequested();
                showcase.SetHighlight(i);
                await SpeakShowcaseLineAsync(NayfCapabilities.All[i].SpokenLine, authToken, ct);
            }

            showcase.SetHighlight(null);
            await SpeakShowcaseLineAsync(NayfCapabilities.Closer, authToken, ct);

            // A beat with the whole card lit again, so the last thing they see is all of it
            // rather than the sentence that happened to be last.
            await Task.Delay(TimeSpan.FromSeconds(2), ct);
        }
        catch (OperationCanceledException)
        {
            Logger.Log("CompanionManager", "Capabilities showcase interrupted");
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Capabilities showcase failed: {ex}");
        }
        finally
        {
            showcase.Hide();

            // Cancelled means someone else has taken the turn — they own the voice state now,
            // and setting it here would pull the pill out from under them.
            if (!ct.IsCancellationRequested)
            {
                if (ReferenceEquals(_currentResponseCts, cts)) _currentResponseCts = null;
                SetVoiceState(RestingState);
            }

            cts.Dispose();
        }
    }

    /// <summary>
    /// Speaks one line of the tour and comes back when the speaker is free again.
    /// </summary>
    /// <remarks>
    /// <see cref="SpeakAcknowledgmentAsync"/> already does precisely this, down to holding
    /// the narration guard across the line so the global playback callbacks don't drop the
    /// voice state to Idle at every full stop. This is that, under a name that fits where it
    /// is called from.
    /// </remarks>
    private Task SpeakShowcaseLineAsync(string line, string authToken, CancellationToken ct)
        => SpeakAcknowledgmentAsync(line, authToken, ct);

    // MARK: - Region focus

    /// <summary>
    /// The region-focus lasso just opened: start listening, so the user can ask about what
    /// they are circling <i>while</i> they circle it. Whatever they say is paired with the
    /// crop when they let go — drawing and asking are one gesture, not two.
    /// </summary>
    public void BeginRegionFocusVoiceCapture()
    {
        // Nothing to do if a recording is already open — this is the same guard the chord
        // itself uses, and reaching the microphone twice is what it exists to prevent.
        if (VoiceState == CompanionVoiceState.Listening) return;
        OnPushToTalkPressed();
    }

    /// <summary>
    /// Alt released and the crop is in hand. The crop is set <i>before</i> the recording is
    /// stopped so it is already waiting when the transcript arrives; if the user drew in
    /// silence there is no transcript and it simply stays pending for their next question.
    /// </summary>
    public void FinishRegionFocusVoiceCapture(byte[] regionImage)
    {
        _pendingFocusRegionImage = regionImage;
        _focusRegionAnnounced = false;
        Logger.Log("CompanionManager", $"Region focus: {regionImage.Length / 1024} KB crop pending");
        OnPushToTalkReleased();
    }

    /// <summary>
    /// The lasso closed without a region — nothing drawn, Escape, or a capture that failed.
    /// Drops the recording it opened without sending anything, and leaves any crop from an
    /// earlier gesture alone: this one produced nothing, so it takes nothing away either.
    /// </summary>
    public async void CancelRegionFocusVoiceCapture()
    {
        if (VoiceState != CompanionVoiceState.Listening) return;

        StopWatchdog();
        SetVoiceState(RestingState);

        try
        {
            // Stopped rather than abandoned: the recognizer holds the microphone until it is
            // told the utterance is over. The transcript it returns is thrown away — the user
            // never asked anything, they just moved their hand.
            await _buddyDictationManager.StopRecordingAndGetTranscriptAsync();
        }
        catch (Exception ex)
        {
            Logger.Log("CompanionManager", $"Region focus: dropping recording failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Saves the finished turn as an agent task, or folds it into the one it continues.
    /// Only missions are kept: a question Vayme answered is not a task, and a card for every
    /// answer would bury the handful the user actually wants to come back to.
    /// </summary>
    private void SaveOrUpdateAgentTask(Guid? resumingTaskId, ConversationTurn turn, string spokenText)
    {
        var summary = spokenText.Trim();
        if (summary.Length == 0) summary = "Task completed.";

        // Continuing a card the user opened. AppendTurn already refreshed it; this just
        // marks it current so a later spoken follow-up keeps landing here.
        if (resumingTaskId is { } resumed && AgentTasks.Task(resumed) != null)
        {
            AgentTasks.AppendTurn(resumed, turn, summary);
            _currentAgentTaskId = resumed;
            RefreshOpenAgentCard(resumed);
            return;
        }

        // The turn changed, corrected, or undid what Vayme just did — same task, new outcome.
        // Either the model tagged it, or it is a fresh mission close enough in time that
        // treating it as separate would split one job across two tiles.
        var current = _currentAgentTaskId is { } currentId ? AgentTasks.Task(currentId) : null;
        if (current != null
            && (AgentManager.TaskContinuesPrevious
                || (AgentManager.MissionText != null
                    && DateTimeOffset.Now - current.UpdatedAt < AgentTaskContinuationWindow)))
        {
            AgentTasks.AppendTurn(current.Id, turn, summary);
            RefreshOpenAgentCard(current.Id);
            return;
        }

        // A new task — either genuinely new, or a continuation whose predecessor Vayme has
        // no record of, because it was done before the tasks were being saved or has since
        // been deleted. That work is no less real for having lost the thread it belongs to,
        // so it is filed under the label the continuation carries rather than dropped.
        //
        // No label at all means the model judged this an answer rather than a job — a plain
        // question, an explanation — and nothing is saved.
        var mission = AgentManager.MissionText;
        if (string.IsNullOrWhiteSpace(mission)) return;

        var created = AgentTasks.CreateTask(mission, summary, _conversationHistory);
        _currentAgentTaskId = created.Id;
    }

    /// <summary>
    /// Updates the floating card for a task, but only if the user already has it open.
    /// A background continuation must not throw a card onto the screen unasked — the user
    /// is looking at whatever they were doing, not waiting to be interrupted by it.
    /// </summary>
    private void RefreshOpenAgentCard(Guid taskId)
    {
        var task = AgentTasks.Task(taskId);
        if (task == null) return;
        if (!IsAgentCardOpen(taskId)) return;
        UpdateOnUI(() => AgentCardRequested?.Invoke(task));
    }

    /// <summary>Set by the app so the manager can ask whether a card is currently on screen.</summary>
    public Func<Guid, bool> IsAgentCardOpen { get; set; } = _ => false;

    /// <summary>
    /// Reopens a saved task's floating card so the user can read it again and continue it.
    /// Called from the Agents grid.
    /// </summary>
    public void OpenSavedAgent(SavedAgentTask task)
    {
        _currentAgentTaskId = task.Id;
        UpdateOnUI(() => AgentCardRequested?.Invoke(task));
    }

    /// <summary>
    /// Starts a spoken follow-up that continues a saved task — the card's "Follow up" button.
    /// The turn it produces is appended to that task rather than to the global history.
    /// </summary>
    public void BeginAgentTaskFollowUp(Guid taskId)
    {
        if (AgentTasks.Task(taskId) == null) return;
        _resumingTaskId = taskId;
        Logger.Log("AgentTasks", $"follow-up on {taskId}");
        ToggleTapToTalk();
    }

    /// <summary>
    /// The task a follow-up started from the card belongs to. One-shot: read and cleared by
    /// the turn it applies to, so the question after it goes back to the normal history.
    /// </summary>
    private Guid? _resumingTaskId;

    /// <param name="resumingTaskId">
    /// Set when the user hit "Follow up" on a saved agent card. That turn runs against the
    /// task's own conversation thread instead of the global rolling history, and its result
    /// lands back on the same task — so picking a task up a week later continues it rather
    /// than starting something new that happens to mention it.
    /// </param>
    private async Task SendTranscriptToClaudeAsync(string transcript, Guid? resumingTaskId = null)
    {
        // A follow-up armed by the card's button arrives here as a perfectly ordinary voice
        // turn, so the task it belongs to is picked up rather than passed down. One-shot:
        // the next question is a new one unless the user asks for a follow-up again.
        resumingTaskId ??= _resumingTaskId;
        _resumingTaskId = null;

        // Cancel any previous in-flight response
        _currentResponseCts?.Cancel();
        _currentResponseCts = new CancellationTokenSource();
        var ct = _currentResponseCts.Token;

        // A circled region answers the question better than the whole screen does, and it is
        // the reason the user circled it. One-shot — read and cleared here, so the question
        // after this one goes back to looking at everything.
        var focusRegionImage = _pendingFocusRegionImage;
        _pendingFocusRegionImage = null;
        _focusRegionAnnounced = false;

        List<CapturedScreenshot>? screenshots = null;
        if (focusRegionImage != null)
        {
            Logger.Log("CompanionManager", "Using the circled region instead of a screenshot");
        }
        else
        {
            try
            {
                screenshots = await ScreenCaptureUtility.CaptureAllScreensAsync();
                Logger.Log("CompanionManager", $"Captured {screenshots?.Count ?? 0} screenshot(s)");
            }
            catch (Exception ex)
            {
                Logger.Log("CompanionManager", $"Screen capture failed: {ex.Message}");
            }
        }

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
            // prompt carries what Vayme remembers about the user.
            // Voice turns always carry screenshots and can point/draw → screen model.
            RouteModel(turnUsesScreenCoordinates: true);

            // Teaching is the default for a turn that can see the screen; acting is the
            // exception, entered only when the user hands the action over. Hands-off is not
            // a policy the model is asked to observe but a set of tools it does not have —
            // and it is the mode that can point at things, so a turn that turns out to be a
            // question still has what it needs to answer it on screen.
            var toolMode = IsActionDelegation(transcript)
                ? NayfToolMode.AgentTask
                : NayfToolMode.GuidedWalkthrough;
            Logger.Log("CompanionManager", $"toolMode={toolMode}");

            var systemPrompt = BuildSystemPrompt();
            if (toolMode == NayfToolMode.GuidedWalkthrough)
                systemPrompt += "\n\n" + WalkthroughPromptSuffix;
            if (RoastMode) systemPrompt += "\n\n" + RoastPromptSuffix;
            var memoryBlock = Memory.ContextBlock();
            if (memoryBlock.Length > 0) systemPrompt += "\n\n" + memoryBlock;

            // Resuming a saved task replays THAT task's thread. The global history is
            // whatever the user has said since, which for a task picked up days later is
            // about something else entirely — handing it over would bury the task it is
            // supposed to be continuing.
            var resumedTask = resumingTaskId is { } id ? AgentTasks.Task(id) : null;
            var historyForTurn = resumedTask?.History ?? _conversationHistory;

            // No delta handler. Its whole job was to put the answer on screen as it arrived;
            // the answer is spoken, and the finished text is what everything downstream —
            // TTS, the POINT tags, the saved task — reads.
            var responseText = await AgentManager.RunAgentLoopAsync(
                transcript,
                screenshots,
                systemPrompt,
                authToken,
                onTextDelta: null,
                ct,
                historyForTurn,
                toolMode,
                focusRegionImage);

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

            // Queue the exchange for a memory pass — see the Memory section. A turn spent
            // taking the user through their own screen is skipped outright: what was said is
            // a description of their application, and a fact drawn out of it would be about
            // what they had open, which is exactly what memory should not hold.
            //
            // Tested on what the turn did, not on which tools it was given. Walkthrough is
            // the default mode rather than a marker — almost every turn runs in it, including
            // the one where the user says what they do for a living.
            bool aboutTheScreen = AgentManager.LastTurnWalkedTheScreen
                                  || responseText.Contains("[POINT", StringComparison.Ordinal);
            if (!aboutTheScreen) QueueForMemory(transcript, responseText);

            if (ct.IsCancellationRequested) return;

            // Store in conversation history. A turn that belongs to a resumed task goes on
            // THAT task's thread instead — otherwise the global buffer fills with follow-ups
            // to a task the next unrelated question knows nothing about.
            var completedTurn = new ConversationTurn(transcript, responseText);
            if (resumingTaskId == null)
            {
                _conversationHistory.Add(completedTurn);
                if (_conversationHistory.Count > NayfConfig.MaxConversationHistoryTurns)
                    _conversationHistory.RemoveAt(0);
            }

            // Parse any POINT tags in the response — but not off a crop. A point read from a
            // cropped image is in the crop's own pixel space, and nothing here can map that
            // back to the screen. Sending the cursor to those coordinates would put it
            // somewhere arbitrary; saying the answer without pointing is the honest version.
            if (focusRegionImage == null) ParseAndApplyPointTags(responseText, screenshots);

            // Speak the response with its tags removed — read aloud they would have Vayme
            // announce its own bookkeeping.
            var ttsText = NayfResponseText.Clean(responseText);

            // Persist the outcome so the user can reopen this task and carry on with it.
            // Placed here because it needs both the cleaned spoken text as its summary and
            // the mission label, which is cleared once Vayme stops talking.
            SaveOrUpdateAgentTask(resumingTaskId, completedTurn, ttsText);

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
                // Deliberately not awaited — the turn is done, and what follows is driven by
                // the playback events rather than by this task. A task nobody observes also
                // swallows whatever went wrong inside it, so faults are logged here instead.
                _ = _elevenLabsTTSClient.SpeakAsync(ttsText, authToken, ct)
                    .ContinueWith(
                        t => Logger.Log("CompanionManager",
                            $"TTS task faulted: {t.Exception?.GetBaseException().Message}"),
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted,
                        TaskScheduler.Default);
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
    /// preamble, and speaking one would make Vayme slower to listen to than it actually is.
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

        // Read here, on the turn's own thread, while the history is still the one this
        // transcript is a reply to. The completed turn isn't appended until the very end of
        // ExecuteTurn, so the last entry is exactly what Vayme said last.
        var previousLine = PreviousAssistantLine();

        UpdateOnUI(() => DeepThinkingLabel = null);

        handover.Work = Task.Run(async () =>
        {
            try
            {
                var ackCall = _ackClaudeAPI.FetchAcknowledgmentAsync(transcript, previousLine, authToken, ct);
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
    /// The tail of what Vayme said last, for the acknowledgment to read the user's reply
    /// against. Null on the first turn of a conversation, where there is nothing to reply to.
    ///
    /// The tail rather than the whole thing: an offer is made at the end of an answer
    /// ("...want me to walk you through materials?"), never buried in the middle of it, and
    /// the acknowledgment is a race it loses by being slow. A few hundred characters carries
    /// the question and costs nothing.
    /// </summary>
    private string? PreviousAssistantLine()
    {
        if (_conversationHistory.Count == 0) return null;

        var line = _conversationHistory[^1].AssistantResponse;
        if (string.IsNullOrWhiteSpace(line)) return null;

        const int MaxChars = 400;
        if (line.Length <= MaxChars) return line;

        // Cut forward to a sentence boundary where there is one nearby, so the fragment
        // doesn't open mid-word and read as a different sentence than it was.
        var tail = line[^MaxChars..];
        int start = tail.IndexOfAny(new[] { '.', '!', '?', '\n' });
        if (start >= 0 && start < 120) tail = tail[(start + 1)..];

        return tail.TrimStart();
    }

    /// <summary>
    /// Speaks the acknowledgment and returns when the audio has actually finished, rather
    /// than when it starts — <see cref="ElevenLabsTTSClient.SpeakAsync"/> returns as soon
    /// as the first samples are queued, and the caller needs to know when the speaker is
    /// free again.
    ///
    /// Holds <c>_narrationDepth</c> for the whole of it, which is why the showcase and the
    /// walkthrough speak through here too: this is the path that keeps a line of narration
    /// from steering the voice state when it ends.
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

        Interlocked.Increment(ref _narrationDepth);
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
            // Last, and only once the stop callback above has already run: the global
            // handlers sit ahead of OnStopped in the invocation list, so the guard has to
            // still be up when they see this line's final stop.
            Interlocked.Decrement(ref _narrationDepth);
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

                // Falling back to the primary screen points at the right coordinates on the
                // wrong monitor, which looks like a working feature aimed at nothing — so say
                // so, rather than leaving it to be reported as "it only draws on screen 1".
                if (shot == null && screenshots.Count > 0)
                {
                    Logger.Log("CompanionManager",
                        $"POINT named screen{screenIndex}, which wasn't captured — using screen 0");
                    shot = screenshots[0];
                }
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
    /// The step currently on screen, or null when Vayme isn't waiting on the user.
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

            // Say which kind of waiting this is, because the two are not the same promise.
            AwaitingStepLabel = step.WaitFor == WalkthroughWaitFor.Continue
                ? "Tell me when done"
                : "Your turn";

            SetVoiceState(CompanionVoiceState.AwaitingUserStep);
        });

        // Only for a step that ends in a click. Everything else is finished by the user
        // saying so, and a hook watching for a click nobody is waiting for is pure cost.
        if (step.WaitFor == WalkthroughWaitFor.Click)
            _pushToTalkMonitor.StartWatchingClicks();
        else if (step.WaitFor == WalkthroughWaitFor.Key && step.WaitKeyCode != 0)
            _pushToTalkMonitor.WatchForKey(step.WaitKeyCode);

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
            _pushToTalkMonitor.StopWatchingKeys();
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
        //
        // With no target the ring is always an ellipse, and is asked for rather than
        // measured: it is wider than the 44px HighlightKindFor calls small, and a rounded
        // rectangle at that size would claim to have found an edge that was never read.
        var kind = step.TargetBounds is { Width: > 1, Height: > 1 } target
            ? ScreenAnnotation.HighlightKindFor(target)
            : AnnotationKind.Oval;

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
    /// the line sits just off the control rather than on top of its border, or a ring around
    /// the click point when there is no usable size.
    ///
    /// The ring is deliberately bigger than most of what it stands in for. At 34px it was
    /// indistinguishable from a confident outline of a small control, so an approximate
    /// answer looked exact; at 60 it reads as "somewhere around here", which is what it
    /// actually means.
    /// </summary>
    private static System.Drawing.RectangleF OutlineBoundsFor(WalkthroughStep step)
    {
        const float outlinePadding = 4f;
        const float fallbackDiameter = 60f;

        if (step.TargetBounds is { Width: > 1, Height: > 1 } target)
        {
            target.Inflate(outlinePadding, outlinePadding);
            return target;
        }

        Logger.Log("Walkthrough", "step had no usable bounds — drew fallback ring");

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
    /// and someone who already knows where to click should be able to click it while Vayme
    /// is still talking.
    ///
    /// Which is exactly why it goes through <see cref="SpeakAcknowledgmentAsync"/> rather
    /// than speaking directly. When the user does beat the sentence, the click hook has
    /// already moved the state to Processing and Vayme is off taking the next screenshot —
    /// and then this sentence reaches its end and the global stop handler, seeing
    /// Processing, resets to Idle. The pill drops off the screen mid-thought and reads as
    /// a crash. Narration must not end the state some other part of the turn is holding.
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

            await SpeakAcknowledgmentAsync(line, authToken, ct);
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
    /// Only a click on the thing Vayme pointed at counts. Advancing on any click anywhere
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
    /// The key a step was waiting for has been pressed.
    ///
    /// No target test, unlike a click: the monitor only reports the one key this step asked
    /// for, so there is nothing left to check by the time it gets here.
    /// </summary>
    private void OnWatchedKeyPressed()
    {
        var pending = _pendingStep;
        if (pending == null || pending.Step.WaitFor != WalkthroughWaitFor.Key) return;

        Logger.Log("Walkthrough", $"key '{pending.Step.WaitKey}' pressed — step done");
        TryResumeWalkthrough(WalkthroughResumeReason.Pressed);
    }

    /// <summary>
    /// True when a click is close enough to count as the step being done: inside the
    /// target's bounds, or near the point Vayme pointed at when it was given no bounds.
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
    /// While the banner is shown, watches for the user fixing the thing it named and
    /// takes it down as soon as they have — no need to retry Ctrl+Alt to find out.
    ///
    /// Only the two switches can be watched this way. A language Windows cannot dictate
    /// leaves the banner up until the next press, because changing the speech language
    /// does not settle at the moment the setting is written.
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

                bool resolved = SpeechProblem switch
                {
                    SpeechProblem.MicrophoneBlocked => SpeechDiagnostics.IsMicrophoneAllowed(),
                    SpeechProblem.VoiceNotReady => WhisperTranscriptionProvider.Shared.IsReady,
                    _ => false
                };

                if (resolved)
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

    private void SetVoiceState(CompanionVoiceState state)
    {
        VoiceState = state;
        if (state == CompanionVoiceState.Idle)
            AudioPowerLevel = 0f;

        // "Thinking deeper" only means anything while Vayme is actually thinking. Every
        // other state — speaking, listening, waiting on the user, idle — has to clear it,
        // or it outranks the real status in the pill and sticks there.
        if (state != CompanionVoiceState.Processing)
            DeepThinkingLabel = null;

        // The mission outlives the work by design — it stays up while Vayme speaks its
        // summary, so the pill still names the job the user is being told about. Idle is
        // where that ends; left standing it would title the next unrelated turn.
        if (state == CompanionVoiceState.Idle)
            AgentManager.ClearMission();
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
        ParkPendingMemory();
        _pushToTalkMonitor.Dispose();
        _buddyDictationManager.Dispose();
        _elevenLabsTTSClient.Dispose();
        _currentResponseCts?.Cancel();
        _latestAck?.Cts.Cancel();
        _watchdogCts?.Cancel();
        _micPermissionPollCts?.Cancel();
        _creditsHttp.Dispose();
        Updates.Dispose();
    }
}
