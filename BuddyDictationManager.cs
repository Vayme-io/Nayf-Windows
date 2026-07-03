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
    public event Action<string>? PartialTranscriptUpdated;
    public event Action<float>? AudioPowerLevelChanged;

    private WindowsSpeechTranscriptionProvider? _speechProvider;
    private MMDevice? _meterDevice;
    private System.Threading.Timer? _meterTimer;
    private CancellationTokenSource? _sessionCts;
    private bool _isRecording = false;
    private readonly object _recordingLock = new();

    private string _currentPartialTranscript = "";
    private readonly List<string> _finalizedSegments = new();

    public BuddyDictationManager() { }

    /// <summary>
    /// Starts the Windows speech recognizer and microphone power monitoring.
    /// Call when the user presses the push-to-talk key.
    /// </summary>
    public async Task StartRecordingAsync()
    {
        lock (_recordingLock)
        {
            if (_isRecording) return;
            _isRecording = true;
        }

        _finalizedSegments.Clear();
        _currentPartialTranscript = "";
        _sessionCts = new CancellationTokenSource();

        // Start Windows speech recognition
        _speechProvider = new WindowsSpeechTranscriptionProvider();
        _speechProvider.TranscriptReceived += OnTranscriptFinalized;
        _speechProvider.PartialTranscriptReceived += OnPartialTranscript;
        await _speechProvider.StartSessionAsync(_sessionCts.Token);

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
        lock (_recordingLock)
        {
            if (!_isRecording) return null;
            _isRecording = false;
        }

        StopMicrophonePowerMonitor();

        if (_speechProvider != null)
        {
            await _speechProvider.EndSessionAsync();
            _speechProvider.TranscriptReceived -= OnTranscriptFinalized;
            _speechProvider.PartialTranscriptReceived -= OnPartialTranscript;
            _speechProvider.Dispose();
            _speechProvider = null;
        }

        var fullTranscript = string.Join(" ", _finalizedSegments).Trim();
        if (string.IsNullOrWhiteSpace(fullTranscript))
            fullTranscript = _currentPartialTranscript.Trim();

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

    private void OnTranscriptFinalized(string text)
    {
        _finalizedSegments.Add(text);
        _currentPartialTranscript = "";
    }

    private void OnPartialTranscript(string text)
    {
        _currentPartialTranscript = text;
        PartialTranscriptUpdated?.Invoke(text);
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
        _speechProvider?.Dispose();
    }
}
