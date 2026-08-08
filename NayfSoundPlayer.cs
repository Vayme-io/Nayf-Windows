using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;

namespace NayfWindows;

/// <summary>
/// Plays Nayf's small UI sound effects (the same MP3s the Mac bundles). Each effect is
/// decoded once at startup and reused, so playback is instant with no first-hit disk or
/// decoder latency. Fire-and-forget: a missing file or a decode error just plays nothing.
/// Port of NayfSoundPlayer.swift.
/// </summary>
public sealed class NayfSoundPlayer : IDisposable
{
    /// <remarks>
    /// Lazy, not <c>= new()</c>. Static field initializers run in declaration order, so a
    /// plain initializer here would construct the player — and decode the sounds — before
    /// MixFormat below had been assigned, and every load would die on a null dereference.
    /// Deferring construction to first access sidesteps the ordering question entirely,
    /// however the fields are later rearranged.
    /// </remarks>
    private static readonly Lazy<NayfSoundPlayer> Instance = new(() => new NayfSoundPlayer());

    public static NayfSoundPlayer Shared => Instance.Value;

    /// <summary>
    /// The lead-in before the activation blip, so it lands as the status pill finishes
    /// springing up rather than the instant the key goes down. The Mac's value.
    /// </summary>
    private static readonly TimeSpan ActivateLeadIn = TimeSpan.FromMilliseconds(200);
    private static readonly TimeSpan ReleaseLeadIn = TimeSpan.FromMilliseconds(100);

    /// <summary>
    /// Everything is mixed at one fixed format so the output device is opened once and
    /// stays open. Opening a WaveOut costs tens of milliseconds, which would swamp the
    /// lead-in timings above if it happened per blip.
    /// </summary>
    private static readonly WaveFormat MixFormat =
        WaveFormat.CreateIeeeFloatWaveFormat(44100, 2);

    private readonly object _gate = new();
    private IWavePlayer? _output;
    private MixingSampleProvider? _mixer;
    private bool _isDisposed;

    // Decoded PCM, ready to hand straight to the mixer.
    private readonly float[]? _pushToTalkActivateSamples;
    private readonly float[]? _pushToTalkReleaseSamples;
    private readonly float[]? _taskCompleteSamples;

    private NayfSoundPlayer()
    {
        // Start uses the "bubbly select" click; release uses the "pops" blip — the Mac
        // pairs the files this way round, which is why the names look swapped.
        _pushToTalkActivateSamples = LoadSamples("ptt-start.mp3");
        _pushToTalkReleaseSamples = LoadSamples("ptt-activate.mp3");
        _taskCompleteSamples = LoadSamples("task-complete.mp3");
    }

    /// <summary>Plays the push-to-talk activation blip after a short lead-in (~200 ms).</summary>
    public void PlayPushToTalkActivate() => PlayAfter(_pushToTalkActivateSamples, ActivateLeadIn);

    /// <summary>Plays the push-to-talk release blip after a short lead-in (~100 ms).</summary>
    public void PlayPushToTalkRelease() => PlayAfter(_pushToTalkReleaseSamples, ReleaseLeadIn);

    /// <summary>Plays the completion chime once Nayf has finished doing something.</summary>
    public void PlayTaskComplete() => PlayAfter(_taskCompleteSamples, TimeSpan.Zero);

    private void PlayAfter(float[]? samples, TimeSpan leadIn)
    {
        if (samples == null || _isDisposed) return;

        if (leadIn <= TimeSpan.Zero)
        {
            Play(samples);
            return;
        }

        // Detached on purpose: a sound effect must never make the caller — the
        // push-to-talk hook — wait, and it must never surface an error at them either.
        _ = Task.Run(async () =>
        {
            await Task.Delay(leadIn).ConfigureAwait(false);
            Play(samples);
        });
    }

    private void Play(float[] samples)
    {
        try
        {
            lock (_gate)
            {
                if (_isDisposed) return;
                EnsureOutputStarted();
                _mixer?.AddMixerInput(new SampleArrayProvider(samples));
            }
        }
        catch (Exception ex)
        {
            Logger.Log("NayfSoundPlayer", $"Playback failed: {ex.Message}");
        }
    }

    /// <summary>
    /// Opens the output device on first use rather than in the constructor, so a machine
    /// with no working audio device costs nothing until something actually tries to play.
    /// </summary>
    private void EnsureOutputStarted()
    {
        if (_output != null) return;

        _mixer = new MixingSampleProvider(MixFormat) { ReadFully = true };
        _output = new WaveOutEvent { DesiredLatency = 100 };
        _output.Init(_mixer);
        _output.Play();
    }

    /// <summary>
    /// Decodes a bundled MP3 to the mixer's format once, up front. Returns null — and
    /// logs — rather than throwing, so a missing asset silences one effect instead of
    /// taking down whatever was about to play it.
    /// </summary>
    private static float[]? LoadSamples(string fileName)
    {
        var path = Path.Combine(AppContext.BaseDirectory, "Assets", "Sounds", fileName);

        try
        {
            if (!File.Exists(path))
            {
                Logger.Log("NayfSoundPlayer", $"Missing sound resource {fileName}");
                return null;
            }

            using var reader = new Mp3FileReader(path);
            ISampleProvider source = reader.ToSampleProvider();

            if (source.WaveFormat.Channels == 1)
                source = new MonoToStereoSampleProvider(source);
            if (source.WaveFormat.SampleRate != MixFormat.SampleRate)
                source = new WdlResamplingSampleProvider(source, MixFormat.SampleRate);

            var buffer = new float[MixFormat.SampleRate * MixFormat.Channels * 4]; // 4s ceiling
            int total = 0;
            int read;
            while (total < buffer.Length &&
                   (read = source.Read(buffer, total, buffer.Length - total)) > 0)
            {
                total += read;
            }

            var samples = new float[total];
            Array.Copy(buffer, samples, total);
            return samples;
        }
        catch (Exception ex)
        {
            Logger.Log("NayfSoundPlayer", $"Failed to load {fileName}: {ex}");
            return null;
        }
    }

    /// <summary>
    /// Decodes the sounds now, off the caller's thread, so the first push-to-talk press
    /// isn't the one that pays for it — that press is on the keyboard-hook path, and a
    /// couple of hundred milliseconds there is a visible hitch before the pill appears.
    /// </summary>
    public static void Warmup() => Task.Run(() => _ = Shared);

    /// <summary>
    /// Tears down the shared player, if anything ever used it. Safe to call blind: it
    /// won't construct one just to dispose it.
    /// </summary>
    public static void DisposeShared()
    {
        if (Instance.IsValueCreated) Instance.Value.Dispose();
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_isDisposed) return;
            _isDisposed = true;
            _output?.Dispose();
            _output = null;
            _mixer = null;
        }
    }

    /// <summary>
    /// Plays a preloaded buffer through the mixer. A fresh one is created per hit, so
    /// rapid presses each get their own playback instead of fighting over one cursor.
    /// </summary>
    private sealed class SampleArrayProvider : ISampleProvider
    {
        private readonly float[] _samples;
        private int _position;

        public SampleArrayProvider(float[] samples) => _samples = samples;

        public WaveFormat WaveFormat => MixFormat;

        public int Read(float[] buffer, int offset, int count)
        {
            int available = Math.Min(count, _samples.Length - _position);
            if (available <= 0) return 0;

            Array.Copy(_samples, _position, buffer, offset, available);
            _position += available;
            return available;
        }
    }
}
