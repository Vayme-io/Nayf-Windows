using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using Whisper.net;

namespace NayfWindows;

/// <summary>
/// Speech to text, on this machine, with nothing switched on for it and nothing charged
/// for it.
///
/// <para>This replaces <c>Windows.Media.SpeechRecognition</c>, which never once returned a
/// word on any of the three machines it was reported from. Its dictation grammar is a cloud
/// grammar behind a privacy toggle, and even with every prerequisite green — microphone
/// allowed for the user and for desktop apps, online speech accepted, the right device
/// selected — it produced no hypothesis and no result at all while reporting the input as
/// too loud. The audio was fine: <see cref="MicrophoneCapture"/> measured the same signal at
/// a 0.70 peak with not one clipped sample.</para>
///
/// <para>Whisper takes the 16 kHz mono PCM16 that <see cref="MicrophoneCapture"/> already
/// produces, runs on the CPU, and on that same recording transcribed the sentence correctly.
/// It is the Windows counterpart to the Mac's Apple Speech: the platform's own free, local
/// recognizer there, a local model here. No key, no account, no network once the model is on
/// disk, and no per-utterance cost that would grow with the number of users.</para>
/// </summary>
public sealed class WhisperTranscriptionProvider : IDisposable
{
    /// <summary>
    /// One instance for the process. The model is ~150 MB of weights and a quarter of a
    /// second to load; it is loaded once at startup and every press reuses it.
    /// </summary>
    public static WhisperTranscriptionProvider Shared { get; } = new();

    public const string ModelFileName = "ggml-base.en.bin";

    /// <summary>
    /// Where the model comes from when it is not already installed alongside the app.
    ///
    /// <para>The installer ships it, so this is the safety net rather than the normal path:
    /// a developer build, or an install whose model file was removed. whisper.cpp's own
    /// published weights, which is where every whisper.net sample points.</para>
    /// </summary>
    private const string ModelDownloadUrl =
        "https://huggingface.co/ggerganov/whisper.cpp/resolve/main/" + ModelFileName;

    /// <summary>
    /// Below this there is not enough audio to transcribe, only enough to hallucinate from.
    /// A tap of the chord rather than a sentence.
    /// </summary>
    private const int MinimumSampleCount = NayfConfig.AudioSampleRate / 3;

    private readonly SemaphoreSlim _preparation = new(1, 1);
    private WhisperFactory? _factory;

    /// <summary>Whether a press can be transcribed right now.</summary>
    public bool IsReady => _factory != null;

    /// <summary>What went wrong the last time preparation was attempted, if it did.</summary>
    public string? LastPreparationError { get; private set; }

    private WhisperTranscriptionProvider() { }

    /// <summary>
    /// Gets the model onto disk and into memory. Safe to call repeatedly and from anywhere;
    /// the first call does the work and the rest wait for it.
    ///
    /// <para>Called at startup so the quarter-second load — and, on the rare install without
    /// a bundled model, the download — happens while the user is still finding the hotkey,
    /// rather than in the middle of their first sentence.</para>
    /// </summary>
    public async Task PrepareAsync(CancellationToken cancellationToken = default)
    {
        if (_factory != null) return;

        await _preparation.WaitAsync(cancellationToken);
        try
        {
            if (_factory != null) return;

            var modelPath = await EnsureModelOnDiskAsync(cancellationToken);

            var loadTimer = Stopwatch.StartNew();
            _factory = await Task.Run(() => WhisperFactory.FromPath(modelPath), cancellationToken);
            loadTimer.Stop();

            LastPreparationError = null;
            Logger.Log("Whisper", $"Model loaded from {modelPath} in {loadTimer.ElapsedMilliseconds} ms");
        }
        catch (Exception ex)
        {
            LastPreparationError = ex.Message;
            Logger.Log("Whisper", $"Could not prepare the model: {ex}");
        }
        finally
        {
            _preparation.Release();
        }
    }

    /// <summary>
    /// Transcribes one utterance: 16 kHz mono PCM16, exactly what
    /// <see cref="MicrophoneCapture.ChunkReady"/> delivers.
    /// </summary>
    /// <returns>The words, or an empty string when there were none worth keeping.</returns>
    public async Task<string> TranscribeAsync(byte[] pcm16Mono16k, CancellationToken cancellationToken = default)
    {
        if (pcm16Mono16k.Length / 2 < MinimumSampleCount) return "";

        await PrepareAsync(cancellationToken);
        var factory = _factory;
        if (factory == null) throw new SpeechUnavailableException(SpeechProblem.VoiceNotReady,
            "Vayme's speech model is not ready yet, so it could not make out what you said.");

        var timer = Stopwatch.StartNew();

        // A processor holds the decode state for one pass and is not shareable, so each
        // utterance gets its own. Only the builder is cheap here — the weights stay in the
        // factory and are not reloaded.
        var text = await Task.Run(async () =>
        {
            using var processor = factory.CreateBuilder().WithLanguage("en").Build();
            await using var audio = new MemoryStream(BuildWaveFile(pcm16Mono16k), writable: false);

            var spoken = new StringBuilder();
            await foreach (var segment in processor.ProcessAsync(audio, cancellationToken))
            {
                var cleaned = CleanSegment(segment.Text);
                if (cleaned.Length == 0) continue;
                if (spoken.Length > 0) spoken.Append(' ');
                spoken.Append(cleaned);
            }
            return spoken.ToString();
        }, cancellationToken);

        timer.Stop();
        Logger.Log("Whisper",
            $"{pcm16Mono16k.Length / 2 / (double)NayfConfig.AudioSampleRate:0.0}s of audio " +
            $"in {timer.ElapsedMilliseconds} ms -> {(text.Length == 0 ? "(nothing)" : $"'{text}'")}");

        return text;
    }

    /// <summary>
    /// Strips the things Whisper writes when it has nothing to transcribe.
    ///
    /// <para>Fed near-silence it does not return an empty segment; it returns an annotation
    /// — <c>[BLANK_AUDIO]</c>, <c>(wind blowing)</c>, a musical note — or a lone piece of
    /// punctuation. Passing any of those on as the user's question would have Vayme answer
    /// something nobody asked, which is worse than saying it did not catch that.</para>
    /// </summary>
    private static string CleanSegment(string segmentText)
    {
        var text = segmentText.Trim();

        // Annotations are whole segments, so this removes them rather than editing them out
        // of the middle of a sentence — where a bracket is far more likely to be dictated.
        if (text.StartsWith('[') && text.EndsWith(']')) return "";
        if (text.StartsWith('(') && text.EndsWith(')')) return "";
        if (text.StartsWith('*') && text.EndsWith('*')) return "";

        foreach (var character in text)
            if (char.IsLetterOrDigit(character))
                return text;

        // Nothing but punctuation, whitespace or symbols.
        return "";
    }

    /// <summary>
    /// Resolves the model, fetching it only if this install does not have one.
    ///
    /// <para>Beside the executable first, which is where the installer puts it. The copy in
    /// the user's own data folder is what a download lands in, and is looked at second so a
    /// reinstall's fresh model wins over one downloaded by an earlier build.</para>
    /// </summary>
    private static async Task<string> EnsureModelOnDiskAsync(CancellationToken cancellationToken)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "Models", ModelFileName);
        if (File.Exists(bundled)) return bundled;

        var downloaded = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "Vayme", "Models", ModelFileName);
        if (File.Exists(downloaded)) return downloaded;

        Directory.CreateDirectory(Path.GetDirectoryName(downloaded)!);
        Logger.Log("Whisper", $"No model installed — downloading {ModelFileName}");

        // Written under a temporary name and moved into place, so an interrupted download
        // does not leave a half-file that every later start treats as the model.
        var partial = downloaded + ".partial";
        using (var http = new HttpClient { Timeout = TimeSpan.FromMinutes(20) })
        await using (var response = await http.GetStreamAsync(ModelDownloadUrl, cancellationToken))
        await using (var file = File.Create(partial))
        {
            await response.CopyToAsync(file, cancellationToken);
        }

        File.Move(partial, downloaded, overwrite: true);
        Logger.Log("Whisper", $"Downloaded {new FileInfo(downloaded).Length / (1024 * 1024)} MB to {downloaded}");
        return downloaded;
    }

    /// <summary>
    /// Wraps raw PCM in the WAV header the processor reads the format from. Cheaper than it
    /// looks — 44 bytes and one copy of audio that is already in memory.
    /// </summary>
    private static byte[] BuildWaveFile(byte[] pcm)
    {
        const int channels = 1;
        const int bitsPerSample = 16;
        const int sampleRate = NayfConfig.AudioSampleRate;

        var wave = new byte[44 + pcm.Length];
        using (var writer = new BinaryWriter(new MemoryStream(wave, writable: true), Encoding.ASCII))
        {
            writer.Write(new[] { 'R', 'I', 'F', 'F' });
            writer.Write(36 + pcm.Length);
            writer.Write(new[] { 'W', 'A', 'V', 'E' });
            writer.Write(new[] { 'f', 'm', 't', ' ' });
            writer.Write(16);                                        // PCM header length
            writer.Write((short)1);                                  // PCM, uncompressed
            writer.Write((short)channels);
            writer.Write(sampleRate);
            writer.Write(sampleRate * channels * bitsPerSample / 8); // byte rate
            writer.Write((short)(channels * bitsPerSample / 8));     // block align
            writer.Write((short)bitsPerSample);
            writer.Write(new[] { 'd', 'a', 't', 'a' });
            writer.Write(pcm.Length);
            writer.Write(pcm);
        }
        return wave;
    }

    public void Dispose()
    {
        _factory?.Dispose();
        _factory = null;
        _preparation.Dispose();
    }
}
