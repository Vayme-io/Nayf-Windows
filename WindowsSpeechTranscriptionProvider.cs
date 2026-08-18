using System;
using System.Threading;
using System.Threading.Tasks;
using Windows.Media.SpeechRecognition;

namespace NayfWindows;

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
    /// How long <c>StopAsync</c> gets before it is left to finish on its own. It normally
    /// returns in milliseconds, but a recognizer that has been fed noise instead of speech
    /// can sit inside it for the better part of a minute — and the entire turn queues behind
    /// that call, so the user watches Nayf think hard about a question it has not been handed
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
        _recognizer = new SpeechRecognizer();

        // Use a free-form dictation constraint — recognizes any spoken words
        var dictationConstraint = new SpeechRecognitionTopicConstraint(
            SpeechRecognitionScenario.Dictation, "dictation");
        _recognizer.Constraints.Add(dictationConstraint);

        var compilationResult = await _recognizer.CompileConstraintsAsync();
        if (compilationResult.Status != SpeechRecognitionResultStatus.Success)
        {
            throw new InvalidOperationException(
                $"Failed to compile speech constraints: {compilationResult.Status}. " +
                "Make sure Windows Speech Recognition is enabled in Settings → Time & Language → Speech.");
        }

        _firstResultTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
        _recognizer.ContinuousRecognitionSession.Completed += OnSessionCompleted;
        _recognizer.HypothesisGenerated += OnHypothesisGenerated;
        // Reports when the mic signal is poor (no signal, too quiet, too noisy)
        // — invaluable for diagnosing empty transcripts.
        _recognizer.RecognitionQualityDegrading += OnQualityDegrading;

        // Default (not PauseOnRecognition) so the recognizer keeps transcribing
        // a whole spoken sentence instead of pausing after the first phrase.
        await _recognizer.ContinuousRecognitionSession.StartAsync(
            SpeechContinuousRecognitionMode.Default);

        _isSessionActive = true;
    }

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
