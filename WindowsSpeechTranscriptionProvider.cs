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

        _recognizer.ContinuousRecognitionSession.ResultGenerated += OnResultGenerated;
        _recognizer.ContinuousRecognitionSession.Completed += OnSessionCompleted;

        await _recognizer.ContinuousRecognitionSession.StartAsync(
            SpeechContinuousRecognitionMode.PauseOnRecognition);

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

        try
        {
            await _recognizer.ContinuousRecognitionSession.StopAsync();
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[WindowsSpeech] Stop error: {ex.Message}");
        }
    }

    private void OnResultGenerated(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionResultGeneratedEventArgs args)
    {
        var text = args.Result.Text;
        if (string.IsNullOrWhiteSpace(text)) return;

        var confidence = args.Result.Confidence;

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
    }

    private void OnSessionCompleted(
        SpeechContinuousRecognitionSession sender,
        SpeechContinuousRecognitionCompletedEventArgs args)
    {
        System.Diagnostics.Debug.WriteLine($"[WindowsSpeech] Session completed: {args.Status}");
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
                _recognizer.Dispose();
            }
            catch { /* ignore disposal errors */ }
            _recognizer = null;
        }
    }
}
