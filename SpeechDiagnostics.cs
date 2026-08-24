using System;
using Microsoft.Win32;

namespace NayfWindows;

/// <summary>
/// The three switches outside Vayme that decide whether it can hear anything, read
/// straight from the registry rather than inferred from a failure.
///
/// Windows reports all of them the same way when speech starts: an HRESULT, or a
/// constraint that will not compile. Asking the machine directly is what tells
/// "turn on online speech recognition" apart from "let desktop apps use the mic",
/// which are different pages of Settings and different things to say to the user.
/// </summary>
public static class SpeechDiagnostics
{
    /// <summary>
    /// Whether the user has accepted the online speech privacy policy.
    ///
    /// Dictation is a cloud grammar, so with this off there is no recognizer to start
    /// at all. It is off by default on a clean install unless the user opted in while
    /// first setting Windows up, which is the single most likely reason a fresh
    /// machine hears nothing.
    /// </summary>
    public static bool IsOnlineSpeechAccepted() =>
        ReadInt(@"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Speech_OneCore\Settings\OnlineSpeechPrivacy",
                "HasAccepted") == 1;

    /// <summary>
    /// Whether microphone access is granted both to the signed-in user and to desktop
    /// apps in particular.
    ///
    /// Two separate toggles on one Settings page, and an unpackaged app like Vayme needs
    /// both: the second one governs every program that did not come from the Store, so it
    /// can be off while the user is looking at a microphone that works everywhere else.
    /// </summary>
    public static bool IsMicrophoneAllowed() =>
        IsConsentAllowed(ConsentStorePath) && IsConsentAllowed(ConsentStorePath + @"\NonPackaged");

    /// <summary>Both microphone toggles as they stand, for the log.</summary>
    public static string DescribeMicrophoneAccess() =>
        $"user={ReadString(ConsentStorePath, "Value") ?? "unset"}, " +
        $"desktopApps={ReadString(ConsentStorePath + @"\NonPackaged", "Value") ?? "unset"}";

    private const string ConsentStorePath =
        @"HKEY_CURRENT_USER\SOFTWARE\Microsoft\Windows\CurrentVersion\CapabilityAccessManager\ConsentStore\microphone";

    /// <summary>
    /// An absent value is treated as granted. The key is only written once something has
    /// changed the setting, so on a machine nobody has touched it is simply not there, and
    /// reading that as a denial would put a banner in front of every new user.
    /// </summary>
    private static bool IsConsentAllowed(string path) =>
        !string.Equals(ReadString(path, "Value"), "Deny", StringComparison.OrdinalIgnoreCase);

    private static string? ReadString(string path, string name)
    {
        try { return Registry.GetValue(path, name, null) as string; }
        catch { return null; }
    }

    private static int ReadInt(string path, string name)
    {
        try { return Registry.GetValue(path, name, 0) is int value ? value : 0; }
        catch { return 0; }
    }
}
