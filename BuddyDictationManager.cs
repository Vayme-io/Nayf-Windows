using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
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
    private WaveInEvent? _waveIn;
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

    private void StartMicrophonePowerMonitor()
    {
        _waveIn = new WaveInEvent
        {
            WaveFormat = new WaveFormat(NayfConfig.AudioSampleRate, NayfConfig.AudioBitsPerSample, NayfConfig.AudioChannels),
            BufferMilliseconds = 50
        };
        _waveIn.DataAvailable += OnAudioDataAvailable;
        try { _waveIn.StartRecording(); }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"[Dictation] Mic monitor failed: {ex.Message}");
        }
    }

    private void StopMicrophonePowerMonitor()
    {
        _waveIn?.StopRecording();
        _waveIn?.Dispose();
        _waveIn = null;
    }

    private void OnAudioDataAvailable(object? sender, WaveInEventArgs e)
    {
        var power = CalculateAudioPower(e.Buffer, e.BytesRecorded);
        AudioPowerLevelChanged?.Invoke(power);
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

    private static float CalculateAudioPower(byte[] buffer, int bytesRecorded)
    {
        if (bytesRecorded < 2) return 0f;
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
