using System;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;

namespace NayfWindows;

/// <summary>
/// Why Windows would not start listening, in the terms the user has to act in.
/// Each one is a different page of Settings and a different sentence to show.
/// </summary>
public enum SpeechProblem
{
    /// <summary>Online speech recognition is switched off in the privacy settings.</summary>
    OnlineSpeechOff,

    /// <summary>Windows is refusing the microphone to desktop apps, or to this user.</summary>
    MicrophoneBlocked,

    /// <summary>There is no dictation engine for the speech language this PC is set to.</summary>
    LanguageUnsupported,

    /// <summary>Anything else: a broken install, an N edition with no media pack, a dead device.</summary>
    Unknown
}

/// <summary>
/// Speech could not be started, carrying a reason the panel can put in front of the user.
///
/// Every one of these used to surface as a log line and nothing else. The chord was held,
/// the pill flashed for an instant and Vayme went back to idle without saying why, which
/// from the outside is indistinguishable from a hotkey that never arrived at all. That is
/// exactly how the first report of it was worded: nothing happens.
/// </summary>
public sealed class SpeechUnavailableException : Exception
{
    public SpeechUnavailableException(SpeechProblem problem, string message, Exception? innerException = null)
        : base(message, innerException) => Problem = problem;

    public SpeechProblem Problem { get; }
}

/// <summary>
/// Speech transcription provider using the Windows built-in speech engine
/// (Windows.Media.SpeechRecognition). No API keys or internet required —
/// uses the same engine as Windows Speech Recognition / Cortana.
///
/// Replaces AssemblyAIStreamingTranscriptionProvider as the default provider.
/// </summary>
public sealed class WindowsSpeechTranscriptionProvider : IDisposable
{
    public event Action<string>? TranscriptReceived;
    public event Action<string>? PartialTranscriptReceived;

    private SpeechRecognizer? _recognizer;
    private bool _isSessionActive = false;
    private TaskCompletionSource<bool>? _firstResultTcs;

    /// <summary>
    /// Whether the recognizer produced anything at all this session — a hypothesis counts,
    /// not just a finished result.
    ///
    /// This is the difference between "Windows heard you and could not make out the words"
    /// and "Windows was handed silence". Hypotheses fire readily on any audio, so none of
    /// them across a whole utterance means the recognizer's input was dead, whatever the
    /// microphone's own meter was reading at the time.
    /// </summary>
    public bool HeardSomething { get; private set; }

    /// <summary>
    /// How long <c>StopAsync</c> gets before it is left to finish on its own. It normally
    /// returns in milliseconds, but a recognizer that has been fed noise instead of speech
    /// can sit inside it for the better part of a minute — and the entire turn queues behind
    /// that call, so the user watches Vayme think hard about a question it has not been handed
    /// yet, and then answer nothing.
    /// </summary>
    private static readonly TimeSpan StopTimeout = TimeSpan.FromMilliseconds(1500);

    /// <summary>A stop that ran past its deadline and still has hold of the recognizer.</summary>
    private Task? _pendingStop;

    /// <summary>
    /// Initializes the speech recognizer and starts a continuous recognition session.
    /// Call when push-to-talk begins.
    /// </summary>
    public async Task StartSessionAsync(CancellationToken cancellationToken = default)
    {
        LogEnvironment();

        // Asked before anything is opened, because once Windows is refusing, every cause
        // reports the same couple of statuses. Order matters: dictation is a cloud grammar,
        // so with the privacy policy unaccepted the list of languages Windows says it can
        // dictate is not worth reading — and sending someone to change their language when
        // the real answer is a switch on another page is worse than telling them nothing.
        RequirePermissions();
        RequireDictationLanguage();

        try
        {
            _recognizer = new SpeechRecognizer();
        }
        catch (Exception ex)
        {
            throw Classify(ex, "Windows could not open its speech recognizer");
        }

        // Use a free-form dictation constraint — recognizes any spoken words
        var dictationConstraint = new SpeechRecognitionTopicConstraint(
            SpeechRecognitionScenario.Dictation, "dictation");
        _recognizer.Constraints.Add(dictationConstraint);

        SpeechRecognitionCompilationResult compilationResult;
        try
        {
            compilationResult = await _recognizer.CompileConstraintsAsync();
        }
        catch (Exception ex)
        {
            throw Classify(ex, "Windows could not prepare speech recognition");
        }

        if (compilationResult.Status != SpeechRecognitionResultStatus.Success)
        {
            Logger.Log("WindowsSpeech", $"Constraint compile failed: {compilationResult.Status}");

            // Dictation is a cloud grammar, so with the privacy policy unaccepted it will
            // not compile — and the status Windows returns for that says nothing about
            // privacy. Reading the switch itself is what turns this into an instruction.
            throw SpeechDiagnostics.IsOnlineSpeechAccepted()
                ? new SpeechUnavailableException(SpeechProblem.Unknown,
                    $"Windows would not prepare speech recognition ({compilationResult.Status})")
                : OnlineSpeechOff();
        }

        _firstResultTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
        _recognizer.ContinuousRecognitionSession.Completed += OnSessionCompleted;
        _recognizer.HypothesisGenerated += OnHypothesisGenerated;
        // Reports when the mic signal is poor (no signal, too quiet, too noisy)
        // — invaluable for diagnosing empty transcripts.
        _recognizer.RecognitionQualityDegrading += OnQualityDegrading;

        try
        {
            // Default (not PauseOnRecognition) so the recognizer keeps transcribing
            // a whole spoken sentence instead of pausing after the first phrase.
            await _recognizer.ContinuousRecognitionSession.StartAsync(
                SpeechContinuousRecognitionMode.Default);
        }
        catch (Exception ex)
        {
            throw Classify(ex, "Windows would not start listening");
        }

        _isSessionActive = true;
    }

    /// <summary>
    /// Writes the state of everything speech depends on to the log, once per press.
    ///
    /// It is three registry reads and two property reads, and it is the difference
    /// between a support conversation that needs the machine in front of you and one
    /// answered from a log file the user already has on their desktop.
    /// </summary>
    private static void LogEnvironment()
    {
        try
        {
            var language = SpeechRecognizer.SystemSpeechLanguage;
            Logger.Log("WindowsSpeech",
                $"speechLanguage={language?.LanguageTag ?? "none"} " +
                $"dictation={(language != null && IsDictationLanguage(language.LanguageTag) ? "yes" : "no")} " +
                $"onlineSpeechAccepted={SpeechDiagnostics.IsOnlineSpeechAccepted()} " +
                $"mic[{SpeechDiagnostics.DescribeMicrophoneAccess()}]");
        }
        catch (Exception ex)
        {
            Logger.Log("WindowsSpeech", $"Could not read speech environment: {ex.Message}");
        }
    }

    /// <summary>
    /// The two switches that have to be on before Windows will listen for anybody.
    ///
    /// Checked up front rather than waited for, because both of them fail late and
    /// indistinguishably: the recognizer opens, the constraint refuses to compile, and the
    /// status says only that the topic language is unsupported.
    /// </summary>
    private static void RequirePermissions()
    {
        if (!SpeechDiagnostics.IsOnlineSpeechAccepted())
            throw OnlineSpeechOff();

        if (!SpeechDiagnostics.IsMicrophoneAllowed())
            throw new SpeechUnavailableException(SpeechProblem.MicrophoneBlocked,
                "Windows is not letting Vayme use the microphone. Allow microphone access " +
                "for desktop apps and hold Ctrl and Alt again.");
    }

    /// <summary>
    /// Windows dictates a shorter list of languages than it displays in, and it reports only
    /// the ones whose speech pack is actually on the machine. The recognizer takes the PC's
    /// speech language rather than one we choose, so a PC set to a language off that list can
    /// never dictate, however many permissions are granted.
    /// </summary>
    private static void RequireDictationLanguage()
    {
        var language = SpeechRecognizer.SystemSpeechLanguage;
        if (language == null)
            throw new SpeechUnavailableException(SpeechProblem.LanguageUnsupported,
                "Windows has no speech language set on this PC, so it cannot dictate.");

        if (IsDictationLanguage(language.LanguageTag)) return;

        throw new SpeechUnavailableException(SpeechProblem.LanguageUnsupported,
            $"Windows cannot dictate in {language.DisplayName}, the speech language this PC is " +
            "set to. Pick one it does dictate, English for instance, under Speech in Settings.");
    }

    private static bool IsDictationLanguage(string languageTag) =>
        SpeechRecognizer.SupportedTopicLanguages.Any(
            supported => string.Equals(supported.LanguageTag, languageTag, StringComparison.OrdinalIgnoreCase));

    /// <summary>
    /// Turns whatever Windows threw into something worth showing.
    ///
    /// The HRESULT alone rarely settles it, so where it is not conclusive the switches are
    /// read directly. The raw code is kept on the message either way: an unrecognized
    /// failure that at least names itself can be looked up, and a silent one cannot.
    /// </summary>
    private static SpeechUnavailableException Classify(Exception ex, string what)
    {
        uint hresult = unchecked((uint)ex.HResult);
        Logger.Log("WindowsSpeech", $"{what}: 0x{hresult:X8} {ex.Message}");

        // 0x80045509 is "the speech privacy policy was not accepted prior to attempting a
        // remote recognition" — Windows saying this outright rather than by omission.
        if (hresult == 0x80045509 || !SpeechDiagnostics.IsOnlineSpeechAccepted())
            return OnlineSpeechOff(ex);

        if (hresult == 0x80070005 || !SpeechDiagnostics.IsMicrophoneAllowed())
            return new SpeechUnavailableException(SpeechProblem.MicrophoneBlocked,
                "Windows is not letting Vayme use the microphone. Allow microphone access " +
                "for desktop apps and try again.", ex);

        return new SpeechUnavailableException(SpeechProblem.Unknown,
            $"{what} (0x{hresult:X8}).", ex);
    }

    private static SpeechUnavailableException OnlineSpeechOff(Exception? innerException = null) =>
        new(SpeechProblem.OnlineSpeechOff,
            "Windows has online speech recognition turned off, so Vayme cannot hear you. " +
            "Turn it on and hold Ctrl and Alt again.", innerException);

    /// <summary>
    /// Stops the recognition session and fires TranscriptReceived with the final text.
    /// Call when push-to-talk is released.
    /// </summary>
    public async Task EndSessionAsync()
    {
        if (!_isSessionActive || _recognizer == null) return;
        _isSessionActive = false;

        // Wait up to 2.5 s for the recognizer to deliver at least one result
        // before stopping — online recognition can take 1-2 s to respond, and
        // calling StopAsync immediately discards any in-flight result.
        bool gotResult = false;
        if (_firstResultTcs != null)
            gotResult = await Task.WhenAny(_firstResultTcs.Task, Task.Delay(2500)) == _firstResultTcs.Task;
        Logger.Log("WindowsSpeech", $"EndSession: gotResult={gotResult}");

        var stop = StopRecognizerAsync(_recognizer);
        if (await Task.WhenAny(stop, Task.Delay(StopTimeout)) == stop) return;

        // Everything the recognizer heard is already in hand — results arrive on their own
        // event rather than out of this call — so there is nothing left to wait for. It is
        // left running and its teardown deferred until it returns.
        Logger.Log("WindowsSpeech", "Stop is overrunning — leaving the recognizer to finish");
        _pendingStop = stop;
    }

    private static async Task StopRecognizerAsync(SpeechRecognizer recognizer)
    {
        try
        {
            await recognizer.ContinuousRecognitionSession.StopAsync();
        }
        catch (Exception ex)
        {
            Logger.Log("WindowsSpeech", $"Stop error: {ex.Message}");
        }
    }

    private void OnHypothesisGenerated(
        SpeechRecognizer sender, SpeechRecognitionHypothesisGeneratedEventArgs args)
    {
        // A hypothesis means the recognizer IS hearing audio, even if it never
        // finalizes — distinguishes "mic dead" from "low confidence".
        HeardSomething = true;
        Logger.Log("WindowsSpeech", $"Hypothesis: '{args.Hypothesis.Text}'");
    }

    private void OnQualityDegrading(
        SpeechRecognizer sender, SpeechRecognitionQualityDegradingEventArgs args)
    {
        Logger.Log("WindowsSpeech", $"Audio problem: {args.Problem}");
    }

    private void OnResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        var text = args.Result.Text;
        var confidence = args.Result.Confidence;
        HeardSomething = true;
        Logger.Log("WindowsSpeech", $"Result: '{text}' ({confidence})");

        if (string.IsNullOrWhiteSpace(text)) return;

        // High/medium confidence → treat as finalized
        if (confidence == SpeechRecognitionConfidence.High ||
            confidence == SpeechRecognitionConfidence.Medium)
        {
            TranscriptReceived?.Invoke(text);
        }
        else
        {
            // Low confidence → show as partial/hypothesis
            PartialTranscriptReceived?.Invoke(text);
        }

        _firstResultTcs?.TrySetResult(true);
    }

    private void OnSessionCompleted(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        Logger.Log("WindowsSpeech", $"Session completed: {args.Status}");
        _isSessionActive = false;
    }

    public void Dispose()
    {
        var recognizer = _recognizer;
        if (recognizer == null) return;
        _recognizer = null;

        // A stop that overran its deadline is still inside this recognizer, and disposing it
        // out from under an operation that has not returned turns a slow stop into a crash.
        // So the teardown waits on the stop rather than racing it. Anything the recognizer
        // says in the meantime is harmless: it lands in the transcript of an utterance that
        // is already over, which is exactly where those words belong.
        var pendingStop = _pendingStop;
        _pendingStop = null;
        if (pendingStop is { IsCompleted: false })
        {
            _ = pendingStop.ContinueWith(_ => DisposeRecognizer(recognizer), TaskScheduler.Default);
            return;
        }

        DisposeRecognizer(recognizer);
    }

    private void DisposeRecognizer(SpeechRecognizer recognizer)
    {
        try
        {
            recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
            recognizer.ContinuousRecognitionSession.Completed -= OnSessionCompleted;
            recognizer.HypothesisGenerated -= OnHypothesisGenerated;
            recognizer.RecognitionQualityDegrading -= OnQualityDegrading;
            recognizer.Dispose();
        }
        catch { /* ignore disposal errors */ }
    }
}
