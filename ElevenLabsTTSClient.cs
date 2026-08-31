using System;
using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using NAudio.Wave;

namespace NayfWindows;

/// <summary>
/// ElevenLabs TTS client — sends text to the Cloudflare Worker proxy and
/// plays back the returned MP3 audio via NAudio. Mirrors ElevenLabsTTSClient.swift.
/// </summary>
public sealed class ElevenLabsTTSClient : IDisposable
{
    public bool IsPlaying => _waveOut?.PlaybackState == PlaybackState.Playing;

    public event Action? PlaybackStarted;
    public event Action? PlaybackStopped;

    private readonly Uri _proxyUrl;
    private readonly HttpClient _httpClient;
    private IWavePlayer? _waveOut;
    private Mp3FileReader? _mp3Reader;
    private MemoryStream? _audioStream;

    /// <summary>
    /// Guards the three playback fields above and <see cref="_speechGeneration"/>. Held only
    /// across state changes — never across the fetch, which takes seconds.
    /// </summary>
    private readonly object _playbackLock = new();

    /// <summary>
    /// Stamped onto every utterance. A fetch that comes back to find this moved on has been
    /// superseded and throws its audio away rather than talking over whatever replaced it.
    /// </summary>
    private int _speechGeneration;

    public ElevenLabsTTSClient(string proxyUrl)
    {
        _proxyUrl = new Uri(proxyUrl);
        // 60s, matching the Mac client's timeoutIntervalForResource. This covers generating
        // the speech *and* downloading it, and multilingual_v2 trades latency for quality —
        // a long answer can take a while. Too short a limit here is silence, not an error
        // the user ever sees.
        _httpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(60) };
    }

    /// <summary>
    /// Converts <paramref name="text"/> to speech and plays it immediately.
    /// Cancels any currently playing audio first.
    /// </summary>
    public async Task SpeakAsync(string text, string? authorizationToken = null,
        CancellationToken cancellationToken = default)
    {
        // Each call supersedes the one before it. The fetch takes seconds, so without a
        // generation stamp two calls that overlap in that window both reach PlayAudio and
        // both play — two voices at once, and only the newer one reachable by StopPlayback.
        int generation;
        lock (_playbackLock)
        {
            generation = ++_speechGeneration;
            StopPlaybackLocked();
        }

        try
        {
            // Timed because this is where a slow model shows up. If these creep towards the
            // client timeout, that is the knob to turn — not something the user can hear.
            var fetchClock = System.Diagnostics.Stopwatch.StartNew();
            var audioBytes = await FetchAudioAsync(text, authorizationToken, cancellationToken);
            Logger.Log("ElevenLabs",
                $"audio fetched in {fetchClock.Elapsed.TotalSeconds:F1}s for {text.Length} chars");

            bool nothingCameBack = false;
            lock (_playbackLock)
            {
                // Something newer started while this was in flight. Its audio is the one the
                // user should hear; drop ours rather than talking over it.
                if (generation != _speechGeneration)
                {
                    Logger.Log("ElevenLabs", $"discarded superseded audio (gen {generation})");
                    return;
                }

                if (audioBytes.Length == 0) nothingCameBack = true;
                else PlayAudioLocked(audioBytes, generation);
            }

            // Raised outside the lock: a subscriber is free to call straight back in here,
            // and none of them should be doing that while this thread holds it.
            if (nothingCameBack) PlaybackStopped?.Invoke();  // let the state machine recover
        }
        catch (OperationCanceledException)
        {
            // Normal cancellation — a new turn will manage state.
        }
        catch (Exception ex)
        {
            Logger.Log("ElevenLabs", $"TTS error: {ex.Message}");
            // Fire stopped so the caller doesn't stay stuck on the spinner.
            PlaybackStopped?.Invoke();
        }
    }

    public void StopPlayback()
    {
        bool wasSpeaking;
        lock (_playbackLock)
        {
            // A barge-in must also cancel audio that hasn't arrived yet, or it lands a second
            // later and starts talking over the user's new request.
            _speechGeneration++;
            wasSpeaking = StopPlaybackLocked();
        }

        // That bump silences the stopped player's own PlaybackStopped — right when a newer
        // clip is on its way, wrong here, where this is genuinely the end of the speech.
        // Whoever is waiting on it (the acknowledgment, the state machine) has to be told.
        if (wasSpeaking) PlaybackStopped?.Invoke();
    }

    /// <summary>
    /// Tears down the current player and everything it reads from. Returns whether it
    /// interrupted audio that was still playing, which the caller needs in order to decide
    /// whether a <see cref="PlaybackStopped"/> is owed.
    /// </summary>
    private bool StopPlaybackLocked()
    {
        bool wasSpeaking = _waveOut is { PlaybackState: not PlaybackState.Stopped };

        if (_waveOut != null)
        {
            _waveOut.Stop();
            _waveOut.Dispose();
            _waveOut = null;
        }
        _mp3Reader?.Dispose();
        _mp3Reader = null;
        // Owned by the reader in every other respect, but disposing the reader doesn't close
        // it — held in a field purely so it can be released here rather than left to the GC.
        _audioStream?.Dispose();
        _audioStream = null;

        return wasSpeaking;
    }

    private async Task<byte[]> FetchAudioAsync(string text, string? authorizationToken,
        CancellationToken cancellationToken)
    {
        var requestBody = JsonSerializer.Serialize(new
        {
            // Clean up Markdown, emoji and pacing before the model ever sees the text —
            // otherwise numbers/acronyms get rushed and sentences run together.
            text = PrepareForSpeech(text),
            // multilingual_v2 is ElevenLabs' natural, high-quality model. Unlike the
            // flash/turbo v2.5 models (which disable text normalization for latency and
            // can't turn it back on outside Enterprise), it normalizes numbers, times and
            // abbreviations properly and has far better prosody — the trade is a little
            // more latency per response, which is worth it for a companion voice.
            model_id = "eleven_multilingual_v2",
            // mp3_22050_32 is a low-bitrate format — significantly smaller audio files
            // arrive faster over the network without any perceptible quality loss for
            // spoken voice responses.
            output_format = "mp3_22050_32",
            voice_settings = new
            {
                stability = 0.5,
                similarity_boost = 0.75,
                // Slightly under 1.0 so speech doesn't feel rushed; speaker_boost keeps
                // the voice full and consistent. Both are cheap to nudge if the feel is off.
                speed = 0.95,
                use_speaker_boost = true
            }
        });
        using var content = new StringContent(requestBody, Encoding.UTF8, "application/json");
        using var request = new HttpRequestMessage(HttpMethod.Post, _proxyUrl) { Content = content };

        if (authorizationToken != null)
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", authorizationToken);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseContentRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        return await response.Content.ReadAsByteArrayAsync(cancellationToken);
    }

    // ── Speech text preparation ──────────────────────────────────────────────

    // Short SSML pauses inserted at deliberate stops. multilingual_v2 (and every model
    // except v3) supports <break>; ElevenLabs warns that *excessive* breaks cause
    // instability, so these are short and only land at real boundaries. Tune here.
    //
    // No break is inserted at sentence boundaries. The model already pauses at a full stop,
    // and this was the one substitution that scaled with response length — a long answer
    // carried a dozen tags, which is the "excessive breaks" case ElevenLabs warns degrades
    // into slurred, run-together speech.
    private const string ClausePause = "0.4s";      // where the writer put an ellipsis
    private const string ParagraphPause = "0.4s";   // across line breaks

    /// <summary>
    /// Above this many break tags in one utterance, ElevenLabs' prosody becomes unstable and
    /// the result is slurred rather than well-paced. Past the cap the pauses are dropped
    /// rather than risking the whole answer being unintelligible — losing a beat between
    /// paragraphs is a far smaller loss than losing the words.
    /// </summary>
    private const int MaxBreakTags = 4;

    private static readonly Regex MarkdownLinkPattern = new(@"\[([^\]]+)\]\([^)]+\)");
    private static readonly Regex LineLeadingMarkPattern =
        new(@"(?m)^[ \t]*(?:#{1,6}|>|[-*•])[ \t]+");
    private static readonly Regex EllipsisPattern = new(@"\s*(?:…|\.\.\.+)\s*");
    private static readonly Regex LineBreakPattern = new(@"\s*\n+\s*");
    private static readonly Regex RepeatedSpacePattern = new(@"[ \t]{2,}");
    private static readonly Regex BreakTagPattern = new(@"\s*<break\s+time=""[^""]*""\s*/>\s*");

    /// <summary>
    /// Turns raw model text into something ElevenLabs speaks naturally: strips Markdown/emoji
    /// that would be mispronounced or read literally, collapses whitespace, and marks the
    /// pauses the writer intended with short <c>&lt;break&gt;</c> tags. Mirrors
    /// <c>prepareForSpeech(_:)</c> in <c>ElevenLabsTTSClient.swift</c>.
    ///
    /// <para>Without this the model is handed the answer exactly as it was written for the
    /// screen — asterisks, backticks, bullets and all — and it does try to say them. That is
    /// what turns a clear sentence into something mangled and hard to make out.</para>
    /// </summary>
    public static string PrepareForSpeech(string raw)
    {
        string text = raw;

        // 1. Strip Markdown the model sometimes leaves in spoken text, so "**tap**" or
        //    "`Export`" aren't mangled.
        text = text.Replace("**", "").Replace("__", "").Replace("`", "");
        // Markdown links [label](url) → label (never read the URL aloud).
        text = MarkdownLinkPattern.Replace(text, "$1");
        // Leading heading (#), blockquote (>), and bullet (-, *, •) marks at line starts.
        text = LineLeadingMarkPattern.Replace(text, "");

        // 2. Drop emoji / pictographs so they aren't spoken as their names.
        text = RemoveEmoji(text);

        // 3. Deliberate pauses the writer marked: ellipses and line breaks → real breaks.
        text = EllipsisPattern.Replace(text, $" <break time=\"{ClausePause}\"/> ");
        text = LineBreakPattern.Replace(text, $" <break time=\"{ParagraphPause}\"/> ");

        // 4. Collapse runs of spaces/tabs left over from the substitutions.
        text = RepeatedSpacePattern.Replace(text, " ");

        // 5. Even the two remaining kinds add up in a long structured answer, so the total
        //    is capped before it reaches the model.
        text = CapBreakTags(text);

        string result = text.Trim();
        return result.Length == 0 ? raw.Trim() : result;
    }

    /// <summary>
    /// Drops every pause once there are more of them than <see cref="MaxBreakTags"/> —
    /// all of them, not the surplus, because which ones to keep is not a judgement this can
    /// make and an answer paced by the model alone still sounds right.
    /// </summary>
    private static string CapBreakTags(string text)
    {
        var matches = BreakTagPattern.Matches(text);
        if (matches.Count <= MaxBreakTags) return text;

        Logger.Log("ElevenLabs",
            $"stripped {matches.Count} break tags (cap {MaxBreakTags}) to protect prosody");
        return BreakTagPattern.Replace(text, " ").Trim();
    }

    /// <summary>
    /// Removes emoji and pictographs by code-point range. Swift can ask Unicode directly
    /// whether a scalar defaults to emoji presentation; .NET can't, so the blocks that are
    /// wholly pictographic are listed instead. ASCII is untouched either way, which is the
    /// property that matters — digits, # and * only become emoji with a variation selector,
    /// and they have to survive to be spoken as themselves.
    /// </summary>
    private static string RemoveEmoji(string text)
    {
        var builder = new StringBuilder(text.Length);

        foreach (var rune in text.EnumerateRunes())
        {
            int value = rune.Value;

            bool isPictograph =
                (value >= 0x1F000 && value <= 0x1FAFF) || // pictographs, emoticons, flags, extended-A
                (value >= 0x2600 && value <= 0x27BF) ||   // misc symbols + dingbats
                (value >= 0x2B00 && value <= 0x2BFF) ||   // misc symbols and arrows (⭐, ⬛)
                (value >= 0xFE00 && value <= 0xFE0F) ||   // variation selectors, incl. VS16
                (value >= 0xE0020 && value <= 0xE007F) || // tag characters used in flag sequences
                value == 0x200D ||                        // zero-width joiner
                value == 0x20E3;                          // combining enclosing keycap

            if (!isPictograph) builder.Append(rune);
        }

        return builder.ToString();
    }

    /// <summary>
    /// Starts playing <paramref name="mp3Bytes"/>. Call with <see cref="_playbackLock"/> held:
    /// it replaces all three playback fields, and the reader and player it creates have to be
    /// the ones the next stop tears down.
    /// </summary>
    private void PlayAudioLocked(byte[] mp3Bytes, int generation)
    {
        // Whatever was here goes first. Assigning over these fields without disposing them
        // left the previous WaveOutEvent playing and unreachable — a second voice nothing
        // could stop, and a leaked device handle with it.
        StopPlaybackLocked();

        _audioStream = new MemoryStream(mp3Bytes);
        _mp3Reader = new Mp3FileReader(_audioStream);
        _waveOut = new WaveOutEvent();
        _waveOut.Init(_mp3Reader);

        // Speech cutting out mid-sentence otherwise leaves no trace at all: NAudio reports a
        // device failure through StoppedEventArgs instead of throwing, and to everything
        // downstream a stop is a stop however early it came. Record how much of the audio
        // actually played, so a truncated answer is visible as one rather than as silence.
        var expected = _mp3Reader.TotalTime;
        var clock = System.Diagnostics.Stopwatch.StartNew();

        _waveOut.PlaybackStopped += (_, e) =>
        {
            clock.Stop();

            if (e.Exception != null)
                Logger.Log("ElevenLabs", $"playback FAILED at {clock.Elapsed.TotalSeconds:F1}s " +
                                         $"of {expected.TotalSeconds:F1}s: {e.Exception}");
            else if (clock.Elapsed + TimeSpan.FromSeconds(1) < expected)
                // Also what a barge-in looks like — the GlobalPTT line just above says which.
                Logger.Log("ElevenLabs", $"playback stopped early: {clock.Elapsed.TotalSeconds:F1}s " +
                                         $"of {expected.TotalSeconds:F1}s");
            else
                Logger.Log("ElevenLabs", $"playback finished ({expected.TotalSeconds:F1}s)");

            // A player left behind by a superseded call must not tell the state machine that
            // speech has ended — the current one is still going, or is a moment from starting.
            // An explicit StopPlayback raises this itself, for the same reason in reverse.
            lock (_playbackLock)
            {
                if (generation != _speechGeneration) return;
            }
            PlaybackStopped?.Invoke();
        };

        Logger.Log("ElevenLabs",
            $"playback starting: {expected.TotalSeconds:F1}s of audio, {mp3Bytes.Length / 1024} KB");
        PlaybackStarted?.Invoke();
        _waveOut.Play();
    }

    public void Dispose()
    {
        // Not StopPlayback: the app is going away, and announcing that speech stopped at this
        // point only wakes a state machine that is being torn down alongside it.
        lock (_playbackLock)
        {
            _speechGeneration++;
            StopPlaybackLocked();
        }
        _httpClient.Dispose();
    }
}
