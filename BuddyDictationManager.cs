using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>Why one press of push-to-talk produced no words.</summary>
public enum DictationSilence
{
    /// <summary>It didn't — there is a transcript.</summary>
    None,

    /// <summary>
    /// Nothing reached the microphone. The user held the keys and didn't speak, or the
    /// device is muted or unplugged.
    /// </summary>
    NothingHeard,

    /// <summary>
    /// Audio arrived and no words came out of it — too far from the microphone, too much
    /// room noise, or not really speech.
    /// </summary>
    NotUnderstood,

    /// <summary>
    /// The microphone would not open. The device is switched off, asleep, or gone — a
    /// wireless headset between the user and Vayme, most often.
    /// </summary>
    MicrophoneUnavailable,

    /// <summary>The speech model is not on disk yet, or would not load.</summary>
    VoiceNotReady
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
/// Push-to-talk voice pipeline: Vayme's own microphone capture into a local speech model.
///
/// <para>Windows' recognizer used to carry this, and across three machines it transcribed
/// nothing at all while reporting the microphone as too loud — leaving the app nothing to
/// offer but a banner asking the user to go and turn a slider down in Settings. Owning the
/// capture means the level is corrected in <see cref="MicrophoneCapture"/> before anything
/// is transcribed, and owning the recognizer means nothing outside Vayme has to be switched
/// on for a press to work. Mirrors BuddyDictationManager.swift, which reaches Apple Speech
/// for the same reason.</para>
///
/// <para>There is no fallback path any more, and deliberately so: the only engine it could
/// fall back to is the one that never worked.</para>
/// </summary>
public sealed class BuddyDictationManager : IDisposable
{
    // No PartialTranscriptUpdated. It existed to caption the user's speech on the panel as
    // they spoke it, and the panel does not show what was said.
    public event Action<float>? AudioPowerLevelChanged;

    private readonly object _recordingLock = new();
    private CancellationTokenSource? _sessionCts;

    /// <summary>The recording in progress, or null between utterances.</summary>
    private DictationSession? _activeSession;

    /// <summary>
    /// One utterance: the open microphone and the audio it has produced so far.
    ///
    /// Per session rather than fields on the manager because a barge-in overlaps two of
    /// them — the new recording opens while the previous one is still being transcribed.
    /// Shared fields would have the outgoing session's teardown closing the incoming
    /// session's microphone, and its last words landing in the incoming session's audio.
    /// </summary>
    private sealed class DictationSession
    {
        public MicrophoneCapture? Capture { get; set; }

        /// <summary>
        /// The whole utterance, 16 kHz mono PCM16. Written from the capture thread and read
        /// by whoever stops the session, so both lock it.
        /// </summary>
        public readonly MemoryStream Audio = new();
    }

    /// <summary>Opens the microphone. Call when the user presses push-to-talk.</summary>
    public Task StartRecordingAsync()
    {
        DictationSession session;
        lock (_recordingLock)
        {
            if (_activeSession != null) return Task.CompletedTask;
            session = new DictationSession();
            _activeSession = session;
        }

        _sessionCts = new CancellationTokenSource();

        try
        {
            // Asked before the device is opened, because Windows answers a blocked
            // microphone with silence rather than with an error, and a press that records
            // nothing for that reason is indistinguishable from one that heard nothing.
            if (!SpeechDiagnostics.IsMicrophoneAllowed())
                throw new SpeechUnavailableException(SpeechProblem.MicrophoneBlocked,
                    "Windows is not letting Vayme use your microphone. Turn on microphone " +
                    "access for desktop apps, then hold Ctrl and Alt again.");

            var capture = new MicrophoneCapture();
            capture.ChunkReady += chunk =>
            {
                lock (session.Audio) session.Audio.Write(chunk, 0, chunk.Length);
            };
            capture.LevelChanged += level => AudioPowerLevelChanged?.Invoke(level);

            try
            {
                capture.Start();
            }
            catch (Exception ex)
            {
                capture.Dispose();
                throw new SpeechUnavailableException(SpeechProblem.MicrophoneUnavailable,
                    "Vayme could not open your microphone. If it is a wireless headset, check " +
                    "that it is switched on and connected, then hold Ctrl and Alt again.", ex);
            }

            session.Capture = capture;
        }
        catch
        {
            // Nothing is recording. Give the slot back rather than leaving every later press
            // queued behind a session that failed to start — which from the outside is a
            // hotkey that does nothing at all.
            lock (_recordingLock)
                if (ReferenceEquals(_activeSession, session)) _activeSession = null;
            DisposeSession(session);
            throw;
        }

        return Task.CompletedTask;
    }

    /// <summary>
    /// Closes the microphone and transcribes what it captured.
    /// Call when the user releases the push-to-talk key.
    /// </summary>
    public async Task<DictationResult> StopRecordingAndGetTranscriptAsync()
    {
        DictationSession session;
        lock (_recordingLock)
        {
            if (_activeSession == null) return DictationResult.Nothing;
            session = _activeSession;

            // Handed back before the transcription below, so a user who cuts in gets a new
            // recording immediately instead of queueing behind this one's last words.
            _activeSession = null;
        }

        var capture = session.Capture;

        // Stopping flushes the tail of the resampler, so the end of the question is in the
        // buffer before it is read.
        capture?.Stop();

        byte[] audio;
        lock (session.Audio) audio = session.Audio.ToArray();

        bool heardAudio = capture?.HeardAudio ?? false;
        float rawPeak = capture?.RawPeak ?? 0f;
        capture?.Dispose();
        session.Audio.Dispose();

        // Nothing came through the microphone at all. Transcribing that would only give
        // Whisper room to invent a sentence out of room tone.
        if (!heardAudio)
        {
            Logger.Log("Dictation", $"no audio captured (rawPeak={rawPeak:0.000})");
            return new DictationResult(null, DictationSilence.NothingHeard);
        }

        string transcript;
        try
        {
            transcript = (await WhisperTranscriptionProvider.Shared
                .TranscribeAsync(audio, _sessionCts?.Token ?? CancellationToken.None)).Trim();
        }
        catch (SpeechUnavailableException ex)
        {
            Logger.Log("Dictation", $"transcription unavailable: {ex.Message}");
            return new DictationResult(null, DictationSilence.VoiceNotReady);
        }
        catch (Exception ex)
        {
            Logger.Log("Dictation", $"transcription failed: {ex}");
            return new DictationResult(null, DictationSilence.VoiceNotReady);
        }

        if (transcript.Length > 0)
            return new DictationResult(transcript, DictationSilence.None);

        Logger.Log("Dictation",
            $"no transcript: rawPeak={rawPeak:0.000} " +
            $"seconds={audio.Length / 2.0 / NayfConfig.AudioSampleRate:0.00}");

        return new DictationResult(null, DictationSilence.NotUnderstood);
    }

    private static void DisposeSession(DictationSession session)
    {
        session.Capture?.Dispose();
        session.Audio.Dispose();
    }

    public void Dispose()
    {
        _sessionCts?.Cancel();
        if (_activeSession != null) DisposeSession(_activeSession);
    }
}
