using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Windows.Media.SpeechRecognition;

namespace NayfWindows;

/// <summary>Why one press of push-to-talk produced no words.</summary>
public enum DictationSilence
{
    /// <summary>It didn't — there is a transcript.</summary>
    None,

    /// <summary>
    /// Neither the microphone's meter nor the recognizer registered anything. The user held
    /// the keys and didn't speak, or the microphone is muted or unplugged.
    /// </summary>
    NothingHeard,

    /// <summary>
    /// The microphone was picking up sound the whole time and the recognizer still received
    /// nothing — the two are not looking at the same device, or Windows speech is not
    /// working on this machine however configured it appears to be.
    /// </summary>
    RecognizerGotSilence,

    /// <summary>
    /// The recognizer heard speech but could not make out any words worth keeping — too
    /// quiet, too noisy, or not the language it dictates in.
    /// </summary>
    NotUnderstood,

    /// <summary>
    /// Windows reported the input as clipping. The recognizer returns nothing at all from a
    /// signal this hot, so it looks identical to a dead microphone from every other angle —
    /// and it is the one cause here that the user fixes with a slider rather than a device.
    /// </summary>
    MicrophoneTooLoud,

    /// <summary>The opposite, and the same kind of fix: the input level is too low to decode.</summary>
    MicrophoneTooQuiet,

    /// <summary>
    /// Windows could not open the microphone. The device is switched off, asleep, or gone —
    /// a wireless headset between the user and Vayme, most often.
    /// </summary>
    MicrophoneUnavailable
}

/// <summary>
/// What one press of push-to-talk produced: the words, and — when there were none — enough
/// to tell the user something more useful than nothing at all.
/// </summary>
public readonly record struct DictationResult(string? Transcript, DictationSilence Silence)
{
    public static readonly DictationResult Nothing = new(null, DictationSilence.NothingHeard);
}

/// <summary>
/// Push-to-talk voice pipeline. Starts Windows built-in speech recognition
/// when push-to-talk begins and delivers the finalized transcript on key-up.
/// Also captures microphone audio levels for waveform visualization.
/// Mirrors BuddyDictationManager.swift.
/// </summary>
public sealed class BuddyDictationManager : IDisposable
{
    // No PartialTranscriptUpdated. It existed to caption the user's speech on the panel as
    // they spoke it, and the panel does not show what was said. The partial text is still
    // kept per session — it is what the finished transcript is assembled from.
    public event Action<float>? AudioPowerLevelChanged;

    private MMDevice? _meterDevice;
    private System.Threading.Timer? _meterTimer;
    private CancellationTokenSource? _sessionCts;
    private readonly object _recordingLock = new();

    /// <summary>The recording in progress, or null between utterances.</summary>
    private DictationSession? _activeSession;

    /// <summary>
    /// One utterance: its recognizer, and the words that recognizer has produced.
    ///
    /// Per session rather than fields on the manager because a barge-in overlaps two of
    /// them — the new recording opens while the previous one is still finalizing, which
    /// Windows speech takes up to a couple of seconds to do. Shared fields would have the
    /// outgoing session's teardown disposing the incoming session's recognizer, and its
    /// last words landing in the incoming session's transcript.
    /// </summary>
    private sealed class DictationSession
    {
        public required WindowsSpeechTranscriptionProvider Provider { get; init; }

        // Written from the recognizer's threads, read by whoever stops the session, so
        // both are guarded by locking the session itself.
        public readonly List<string> FinalizedSegments = new();
        public string CurrentPartialTranscript = "";
    }

    public BuddyDictationManager() { }

    /// <summary>
    /// Starts the Windows speech recognizer and microphone power monitoring.
    /// Call when the user presses the push-to-talk key.
    /// </summary>
    public async Task StartRecordingAsync()
    {
        DictationSession session;
        lock (_recordingLock)
        {
            if (_activeSession != null) return;
            session = new DictationSession { Provider = new WindowsSpeechTranscriptionProvider() };
            _activeSession = session;
        }

        _sessionCts = new CancellationTokenSource();

        session.Provider.TranscriptReceived += text =>
        {
            lock (session)
            {
                session.FinalizedSegments.Add(text);
                session.CurrentPartialTranscript = "";
            }
        };

        session.Provider.PartialTranscriptReceived += text =>
        {
            lock (session) session.CurrentPartialTranscript = text;
        };

        try
        {
            await session.Provider.StartSessionAsync(_sessionCts.Token);
        }
        catch
        {
            // The recognizer never opened, so nothing is recording. Give the slot back
            // rather than leaving every later press queued behind a session that failed to
            // start — which is what a press that appears to do nothing at all looks like.
            lock (_recordingLock)
                if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            session.Provider.Dispose();
            throw;
        }

        // Start mic capture just for audio power level visualization —
        // the actual transcription is handled by Windows.Media.SpeechRecognition
        StartMicrophonePowerMonitor();
    }

    /// <summary>
    /// Stops speech recognition and returns the full transcript.
    /// Call when the user releases the push-to-talk key.
    /// </summary>
    public async Task<DictationResult> StopRecordingAndGetTranscriptAsync()
    {
        DictationSession session;
        lock (_recordingLock)
        {
            if (_activeSession == null) return DictationResult.Nothing;
            session = _activeSession;

            // Handed back before the wait below, so a user who cuts in gets a new
            // recording immediately instead of queueing behind this one's last words.
            _activeSession = null;
        }

        // Read before the meter is torn down, and while it still belongs to this session.
        float peak = _peakLevelSeen;
        StopMicrophonePowerMonitor();

        // The final result usually arrives during this call, so the handlers stay attached
        // until it returns — they write into this session's own transcript, never into
        // whatever recording has started in the meantime.
        await session.Provider.EndSessionAsync();
        bool recognizerHeardSomething = session.Provider.HeardSomething;

        // Both read off the provider before it is disposed, and used below only if this
        // session produced no words. What Windows said about the audio outranks anything
        // inferable from the levels we sampled ourselves.
        var audioProblem = session.Provider.LastAudioProblem;
        bool microphoneWasUnavailable = session.Provider.MicrophoneWasUnavailable;
        session.Provider.Dispose();

        string fullTranscript;
        lock (session)
        {
            fullTranscript = string.Join(" ", session.FinalizedSegments).Trim();
            if (string.IsNullOrWhiteSpace(fullTranscript))
                fullTranscript = session.CurrentPartialTranscript.Trim();
        }

        if (!string.IsNullOrWhiteSpace(fullTranscript))
            return new DictationResult(fullTranscript, DictationSilence.None);

        // No words. Which reason it was decides what the user is told.
        //
        // Windows' own complaints are read before anything is inferred from the meter,
        // because when Windows has said what is wrong there is nothing left to deduce and
        // deducing anyway gets it wrong: a clipping microphone yields no words, no
        // hypotheses and a meter pinned near the top, which the guesswork below reads as
        // "the recognizer was handed silence" — sending the user off to change devices over
        // an input slider that is turned up too far.
        //
        // Only past those does the meter-versus-recognizer disagreement mean anything: the
        // meter watches the default communications capture device, the recognizer picks its
        // own, and a machine where those are not the same one produces a mic level that
        // dances while Windows speech is handed silence.
        var silence =
            microphoneWasUnavailable ? DictationSilence.MicrophoneUnavailable
            : audioProblem == SpeechRecognitionAudioProblem.TooLoud ? DictationSilence.MicrophoneTooLoud
            : audioProblem == SpeechRecognitionAudioProblem.TooQuiet ? DictationSilence.MicrophoneTooQuiet
            : recognizerHeardSomething ? DictationSilence.NotUnderstood
            : peak >= SpeechDetectedPeak ? DictationSilence.RecognizerGotSilence
            : DictationSilence.NothingHeard;

        Logger.Log("Dictation",
            $"no transcript: peak={peak:0.000} recognizerHeard={recognizerHeardSomething} " +
            $"audioProblem={audioProblem?.ToString() ?? "none"} -> {silence}");

        return new DictationResult(null, silence);
    }

    private DateTime _lastPowerLog = DateTime.MinValue;

    /// <summary>
    /// The loudest the microphone got during this recording, 0–1.
    ///
    /// Kept so that a press producing no words can say whether the microphone was working.
    /// The peak meter is what the waveform already runs on, so this costs nothing extra.
    /// </summary>
    private float _peakLevelSeen;

    /// <summary>
    /// A peak this high means someone spoke rather than the room being quiet.
    ///
    /// Well above a silent room's floor and well below normal speech, because the only
    /// question asked of it is which of two very different stories to tell the user.
    /// </summary>
    private const float SpeechDetectedPeak = 0.05f;

    private void StartMicrophonePowerMonitor()
    {
        _peakLevelSeen = 0f;

        // Poll the device's peak meter (no capture stream → doesn't disturb the
        // recognizer). All COM work happens inside the timer callback so the
        // MMDevice is created and read on the same (threadpool) apartment.
        _meterTimer = new System.Threading.Timer(MeterTick, null, 0, 33);
    }

    private void MeterTick(object? state)
    {
        try
        {
            if (_meterDevice == null)
            {
                var enumerator = new MMDeviceEnumerator();
                _meterDevice = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);
                Logger.Log("Dictation", $"Metering '{_meterDevice.FriendlyName}'");
            }

            float peak = _meterDevice.AudioMeterInformation.MasterPeakValue;
            if (peak > _peakLevelSeen) _peakLevelSeen = peak;
            AudioPowerLevelChanged?.Invoke(peak);
        }
        catch (Exception ex)
        {
            if ((DateTime.UtcNow - _lastPowerLog).TotalMilliseconds > 500)
            {
                _lastPowerLog = DateTime.UtcNow;
                Logger.Log("Dictation", $"Meter error: {ex.Message}");
            }
        }
    }

    private void StopMicrophonePowerMonitor()
    {
        _meterTimer?.Dispose();
        _meterTimer = null;
        _meterDevice?.Dispose();
        _meterDevice = null;
    }

    private static float CalculateAudioPower(byte[] buffer, int bytesRecorded, WaveFormat? format)
    {
        if (bytesRecorded < 4) return 0f;

        // WASAPI shared-mode capture is normally 32-bit IEEE float.
        if (format?.Encoding == NAudio.Wave.WaveFormatEncoding.IeeeFloat)
        {
            double sum = 0;
            int count = bytesRecorded / 4;
            for (int i = 0; i + 3 < bytesRecorded; i += 4)
                { float s = BitConverter.ToSingle(buffer, i); sum += (double)s * s; }
            return (float)Math.Min(Math.Sqrt(sum / count), 1.0);
        }

        // Otherwise treat as 16-bit PCM.
        long sumSquares = 0;
        int sampleCount = bytesRecorded / 2;
        for (int i = 0; i < bytesRecorded - 1; i += 2)
        {
            short sample = (short)(buffer[i] | (buffer[i + 1] << 8));
            sumSquares += (long)sample * sample;
        }
        var rms = Math.Sqrt((double)sumSquares / sampleCount);
        return (float)Math.Min(rms / 32768.0, 1.0);
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        StopMicrophonePowerMonitor();
        _activeSession?.Provider.Dispose();
    }
}
