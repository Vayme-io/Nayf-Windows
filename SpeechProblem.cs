using System;

namespace NayfWindows;

/// <summary>
/// Why Vayme cannot hear the user — and therefore what the banner says and where its
/// button goes.
///
/// <para>Shorter than it was. Most of the old members described ways Windows' own
/// recognizer refused a press: the online-speech privacy toggle being off, a dictation
/// language it would not load, an input level it judged too hot. None of them can happen
/// now that Vayme captures the microphone itself and transcribes locally, and leaving them
/// in would only keep alive the advice that sent users to a slider that was never the
/// problem.</para>
/// </summary>
public enum SpeechProblem
{
    /// <summary>
    /// Windows privacy is refusing the microphone — either to the signed-in user or to
    /// desktop apps in particular, which is a separate toggle an unpackaged app also needs.
    /// </summary>
    MicrophoneBlocked,

    /// <summary>
    /// There is no device to open. Switched off, asleep, or unplugged — a wireless headset
    /// between the user and Vayme, most often.
    /// </summary>
    MicrophoneUnavailable,

    /// <summary>The speech model is not on disk yet, or would not load.</summary>
    VoiceNotReady,

    Unknown
}

/// <summary>Thrown when a press cannot even start recording, carrying the reason.</summary>
public sealed class SpeechUnavailableException : Exception
{
    public SpeechUnavailableException(SpeechProblem problem, string message, Exception? innerException = null)
        : base(message, innerException) => Problem = problem;

    public SpeechProblem Problem { get; }
}
