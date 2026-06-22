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

        try
        {
            await _recognizer.ContinuousRecognitionSession.StopAsync();
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
        if (_recognizer != null)
        {
            try
            {
                _recognizer.ContinuousRecognitionSession.ResultGenerated -= OnResultGenerated;
                _recognizer.ContinuousRecognitionSession.Completed -= OnSessionCompleted;
                _recognizer.HypothesisGenerated -= OnHypothesisGenerated;
                _recognizer.RecognitionQualityDegrading -= OnQualityDegrading;
                _recognizer.Dispose();
            }
            catch { /* ignore disposal errors */ }
            _recognizer = null;
        }
    }
}
