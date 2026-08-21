using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NayfWindows;

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
    public async Task<string?> StopRecordingAndGetTranscriptAsync()
    {
        DictationSession session;
        lock (_recordingLock)
        {
            if (_activeSession == null) return null;
            session = _activeSession;

            // Handed back before the wait below, so a user who cuts in gets a new
            // recording immediately instead of queueing behind this one's last words.
            _activeSession = null;
        }

        StopMicrophonePowerMonitor();

        // The final result usually arrives during this call, so the handlers stay attached
        // until it returns — they write into this session's own transcript, never into
        // whatever recording has started in the meantime.
        await session.Provider.EndSessionAsync();
        session.Provider.Dispose();

        string fullTranscript;
        lock (session)
        {
            fullTranscript = string.Join(" ", session.FinalizedSegments).Trim();
            if (string.IsNullOrWhiteSpace(fullTranscript))
                fullTranscript = session.CurrentPartialTranscript.Trim();
        }

        return string.IsNullOrWhiteSpace(fullTranscript) ? null : fullTranscript;
    }

    private DateTime _lastPowerLog = DateTime.MinValue;

    private void StartMicrophonePowerMonitor()
    {
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
