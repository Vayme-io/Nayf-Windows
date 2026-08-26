using System;
using NAudio.CoreAudioApi;
using NAudio.Wave;

namespace NayfWindows;

/// <summary>
/// Vayme's own microphone: captured through WASAPI, mixed to mono, resampled to 16 kHz and
/// levelled in software before anyone else sees it.
///
/// <para>It exists because the level was the problem and the level was not ours to fix.
/// Windows speech recognition takes whatever the endpoint hands it, and from a signal it
/// judges too hot it returns no words at all — not a bad transcript, nothing. The only
/// remedy we could offer for that was a sentence asking the user to open Settings and move
/// a slider. A gaming headset ships with its gain turned up; that is not a mistake the user
/// made, and correcting it by hand every time is not something an app should ask for.</para>
///
/// <para>Capturing the audio ourselves moves the gain to where it can be measured and
/// corrected per utterance. A microphone driven into clipping is scaled down, a faint one is
/// brought up, and neither reaches the transcriber at a level it cannot read.</para>
/// </summary>
public sealed class MicrophoneCapture : IDisposable
{
    /// <summary>16 kHz mono PCM16, roughly 100 ms per chunk, gain already applied.</summary>
    public event Action<byte[]>? ChunkReady;

    /// <summary>The level of the audio as captured, 0–1, for the waveform.</summary>
    public event Action<float>? LevelChanged;

    /// <summary>What the transcriber is fed, and the rate the speech model is trained at.</summary>
    private const int OutputSampleRate = NayfConfig.AudioSampleRate;

    /// <summary>
    /// Samples per chunk. 100 ms is short enough that the waveform follows the voice and
    /// long enough that the gain is set from a stretch of speech rather than a syllable.
    /// </summary>
    private const int ChunkSampleCount = OutputSampleRate / 10;

    /// <summary>
    /// Where the loudest part of an utterance is aimed, well clear of full scale.
    ///
    /// Speech recognizers want headroom, not volume. Aiming at the top is what produces the
    /// clipped, wordless audio this class was written to stop sending.
    /// </summary>
    private const float TargetPeak = 0.55f;

    /// <summary>
    /// How far the gain may travel in each direction. The ceiling is what rescues a faint
    /// headset; the floor is what rescues a hot one, and it is the larger of the two problems
    /// — a mic can arrive 20 dB too loud, and every dB of that is words the recognizer loses.
    /// </summary>
    private const float MaximumGain = 12f;
    private const float MinimumGain = 0.05f;

    /// <summary>
    /// Below this the envelope is treated as a quiet room rather than a quiet speaker, so
    /// pauses between words are not amplified into hiss.
    /// </summary>
    private const float NoiseFloor = 0.02f;

    /// <summary>
    /// How much of the envelope survives a chunk with nothing in it — about a two-second
    /// half-life at 100 ms chunks. Slow on purpose: gain that moves within a word is audible
    /// as pumping and reads to a recognizer as a changing voice.
    /// </summary>
    private const float EnvelopeDecayPerChunk = 0.965f;

    /// <summary>Never let a sample reach the rail, whatever the gain worked out to.</summary>
    private const float LimiterCeiling = 0.98f;

    private WasapiCapture? _capture;
    private MMDevice? _device;

    private readonly float[] _pendingSamples = new float[ChunkSampleCount];
    private int _pendingCount;

    // Resampler state. One output sample is the mean of the input samples that fall inside
    // its window, which both converts the rate and low-passes on the way down — decimating
    // 48 kHz to 16 kHz without it folds every sibilant back into the speech band.
    private double _inputSamplesPerOutputSample = 1;
    private double _windowFill;
    private double _windowSum;
    private int _windowCount;

    private float _envelope;
    private float _gain = 1f;

    /// <summary>The loudest raw sample of the recording, before any gain of ours.</summary>
    public float RawPeak { get; private set; }

    /// <summary>
    /// How many raw samples arrived at full scale — the microphone genuinely clipping, in the
    /// hardware, where no gain of ours can undo it.
    ///
    /// Logged rather than acted on. It is the number that settles arguments: Windows reported
    /// this input as too loud on nearly every attempt across two machines, and whether that
    /// was true of the samples themselves is not something a meter reading can answer.
    /// </summary>
    public long ClippedSampleCount { get; private set; }

    /// <summary>Total raw samples seen, so the clipped count means something.</summary>
    public long SampleCount { get; private set; }

    /// <summary>Whether anything above a whisper reached us at all.</summary>
    public bool HeardAudio => RawPeak >= 0.01f;

    /// <summary>
    /// Opens the capture device and starts delivering chunks. Throws if there is no capture
    /// device or WASAPI refuses it, which the caller turns into the fallback path.
    /// </summary>
    public void Start()
    {
        using var enumerator = new MMDeviceEnumerator();
        _device = enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Communications);

        _capture = new WasapiCapture(_device);
        var format = _capture.WaveFormat;
        _inputSamplesPerOutputSample = (double)format.SampleRate / OutputSampleRate;

        Logger.Log("MicCapture",
            $"'{_device.FriendlyName}' {format.Encoding} {format.BitsPerSample}-bit " +
            $"{format.SampleRate}Hz {format.Channels}ch -> {OutputSampleRate}Hz mono PCM16");

        _capture.DataAvailable += OnDataAvailable;
        _capture.StartRecording();
    }

    /// <summary>
    /// Stops the device and flushes whatever is left in the buffer, so the last fraction of a
    /// second of speech is sent rather than dropped — that fraction is usually the end of the
    /// question.
    /// </summary>
    public void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture == null) return;

        try
        {
            capture.DataAvailable -= OnDataAvailable;
            capture.StopRecording();
            capture.Dispose();
        }
        catch (Exception ex)
        {
            Logger.Log("MicCapture", $"Stop error: {ex.Message}");
        }

        FlushPendingSamples();

        _device?.Dispose();
        _device = null;

        Logger.Log("MicCapture",
            $"captured {SampleCount} samples, rawPeak={RawPeak:0.000} " +
            $"clipped={ClippedSampleCount} ({ClippedPercentage:0.000}%) finalGain={_gain:0.00}");
    }

    private double ClippedPercentage => SampleCount == 0 ? 0 : 100.0 * ClippedSampleCount / SampleCount;

    private void OnDataAvailable(object? sender, WaveInEventArgs args)
    {
        var format = _capture?.WaveFormat;
        if (format == null) return;

        int bytesPerSample = format.BitsPerSample / 8;
        int bytesPerFrame = bytesPerSample * format.Channels;
        if (bytesPerFrame == 0) return;

        for (int offset = 0; offset + bytesPerFrame <= args.BytesRecorded; offset += bytesPerFrame)
        {
            // Channels are averaged rather than picked from, because a headset that presents
            // its microphone as stereo puts the signal on one of the two and silence on the
            // other, and which one is not consistent between drivers.
            float frame = 0f;
            for (int channel = 0; channel < format.Channels; channel++)
                frame += ReadSample(args.Buffer, offset + channel * bytesPerSample, format);
            frame /= format.Channels;

            SampleCount++;
            float magnitude = Math.Abs(frame);
            if (magnitude > RawPeak) RawPeak = magnitude;
            if (magnitude >= 0.999f) ClippedSampleCount++;

            _windowSum += frame;
            _windowCount++;
            _windowFill += 1.0;

            if (_windowFill < _inputSamplesPerOutputSample) continue;

            _windowFill -= _inputSamplesPerOutputSample;
            AppendResampled((float)(_windowSum / _windowCount));
            _windowSum = 0;
            _windowCount = 0;
        }
    }

    private static float ReadSample(byte[] buffer, int offset, WaveFormat format)
    {
        if (format.Encoding == WaveFormatEncoding.IeeeFloat)
            return BitConverter.ToSingle(buffer, offset);

        return format.BitsPerSample switch
        {
            16 => BitConverter.ToInt16(buffer, offset) / 32768f,
            // 24-bit is packed little-endian; read the top two bytes and keep the scale.
            24 => (short)(buffer[offset + 1] | (buffer[offset + 2] << 8)) / 32768f,
            32 => BitConverter.ToInt32(buffer, offset) / 2147483648f,
            8 => (buffer[offset] - 128) / 128f,
            _ => 0f
        };
    }

    private void AppendResampled(float sample)
    {
        _pendingSamples[_pendingCount++] = sample;
        if (_pendingCount < ChunkSampleCount) return;
        FlushPendingSamples();
    }

    private void FlushPendingSamples()
    {
        int count = _pendingCount;
        _pendingCount = 0;
        if (count == 0) return;

        float chunkPeak = 0f;
        double sumSquares = 0;
        for (int i = 0; i < count; i++)
        {
            float magnitude = Math.Abs(_pendingSamples[i]);
            if (magnitude > chunkPeak) chunkPeak = magnitude;
            sumSquares += (double)_pendingSamples[i] * _pendingSamples[i];
        }

        LevelChanged?.Invoke((float)Math.Min(Math.Sqrt(sumSquares / count) * 3.0, 1.0));

        UpdateGain(chunkPeak);

        var pcm16 = new byte[count * 2];
        for (int i = 0; i < count; i++)
        {
            float amplified = _pendingSamples[i] * _gain;
            if (amplified > LimiterCeiling) amplified = LimiterCeiling;
            else if (amplified < -LimiterCeiling) amplified = -LimiterCeiling;

            short value = (short)(amplified * 32767f);
            pcm16[i * 2] = (byte)(value & 0xFF);
            pcm16[i * 2 + 1] = (byte)((value >> 8) & 0xFF);
        }

        ChunkReady?.Invoke(pcm16);
    }

    /// <summary>
    /// Moves the gain toward whatever would put the loudest recent audio at <see
    /// cref="TargetPeak"/>.
    ///
    /// <para>Downward instantly and upward slowly, which is the asymmetry every limiter has
    /// and for the same reason: arriving late to a signal that is too loud costs the words
    /// that were spoken in the meantime, while arriving late to one that is too quiet costs
    /// nothing — the recognizer still gets those words, just faintly.</para>
    /// </summary>
    private void UpdateGain(float chunkPeak)
    {
        _envelope = Math.Max(chunkPeak, _envelope * EnvelopeDecayPerChunk);

        float desiredGain = TargetPeak / Math.Max(_envelope, NoiseFloor);
        desiredGain = Math.Clamp(desiredGain, MinimumGain, MaximumGain);

        _gain = desiredGain < _gain
            ? desiredGain
            : _gain + (desiredGain - _gain) * 0.2f;
    }

    public void Dispose() => Stop();
}
