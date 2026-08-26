using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Text;
using Microsoft.Win32;
using NAudio.CoreAudioApi;
// AudioSessionState alone lives here rather than with the rest of CoreAudioApi.
using NAudio.CoreAudioApi.Interfaces;

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

    /// <summary>
    /// The capture devices Windows is pointing at, and who else already has one open.
    ///
    /// <para>Written because the one failure we cannot currently explain — the meter moving
    /// while the recognizer is handed silence — has two completely different causes that look
    /// identical from inside Vayme. Either Windows speech opened a different device from the
    /// one being spoken into, or it opened the right one and something else was already
    /// holding it. The first is a settings problem, the second is Discord.</para>
    ///
    /// <para>It matters which, because the meter is not the witness it appears to be.
    /// <c>MasterPeakValue</c> reports the signal passing through the endpoint whoever opened
    /// it, so another app in a call keeps it reading healthy levels for a device Vayme never
    /// got a single sample from — and the banner then tells the user their microphone is
    /// working on the strength of somebody else's stream.</para>
    /// </summary>
    public static string DescribeCaptureDevices()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var description = new StringBuilder();

            // Two defaults, not one. Windows 10's Sound panel sets them separately, so
            // "changed the microphone" routinely moves Console and leaves Communications
            // — which is the role the meter follows — pointing at the old device.
            var console = DefaultCapture(enumerator, Role.Console);
            var communications = DefaultCapture(enumerator, Role.Communications);
            description.Append($"default='{Name(console)}' comms='{Name(communications)}'");
            if (Name(console) != Name(communications)) description.Append(" MISMATCH");

            var holders = CaptureHolders(enumerator).ToList();
            description.Append(holders.Count == 0
                ? " openBy=[nobody]"
                : $" openBy=[{string.Join(", ", holders)}]");

            console?.Dispose();
            communications?.Dispose();
            return description.ToString();
        }
        catch (Exception ex)
        {
            return $"could not read capture devices: {ex.Message}";
        }
    }

    /// <summary>
    /// Other programs capturing from a microphone right now, by display name, most likely
    /// culprit first — or an empty list if Vayme has the microphone to itself.
    ///
    /// <para>Used to name the cause in the banner instead of describing a category of cause.
    /// "Discord is using your microphone" is a sentence someone can act on; "check that
    /// Windows is set to the same microphone you are speaking into" sent the first person who
    /// saw it to change a setting that was never the problem.</para>
    /// </summary>
    public static IReadOnlyList<string> OtherAppsUsingMicrophone()
    {
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            int self = Environment.ProcessId;

            return CaptureSessionProcessIds(enumerator)
                .Where(id => id != 0 && id != self)
                .Distinct()
                .Select(FriendlyProcessName)
                .Where(name => name != null)
                .Select(name => name!)
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    private static IEnumerable<int> CaptureSessionProcessIds(MMDeviceEnumerator enumerator)
    {
        var ids = new List<int>();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.State == AudioSessionState.AudioSessionStateActive)
                        ids.Add((int)session.GetProcessID);
                }
            }
            catch { /* skip a device that will not enumerate */ }
            finally { device.Dispose(); }
        }

        return ids;
    }

    /// <summary>
    /// What to call a process in front of a user: its window title's product name where
    /// Windows knows one, and the bare executable name where it doesn't.
    /// </summary>
    private static string? FriendlyProcessName(int processId)
    {
        try
        {
            var process = Process.GetProcessById(processId);
            var product = process.MainModule?.FileVersionInfo.FileDescription;
            return string.IsNullOrWhiteSpace(product) ? process.ProcessName : product;
        }
        catch
        {
            // A process in another session, or one running elevated, refuses MainModule.
            // Its bare name is still worth having.
            try { return Process.GetProcessById(processId).ProcessName; }
            catch { return null; }
        }
    }

    private static MMDevice? DefaultCapture(MMDeviceEnumerator enumerator, Role role)
    {
        try { return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, role); }
        catch { return null; }
    }

    private static string Name(MMDevice? device)
    {
        try { return device?.FriendlyName ?? "none"; }
        catch { return "unreadable"; }
    }

    /// <summary>
    /// Every process currently capturing on an active microphone, by name.
    ///
    /// A session is only worth reporting while it is <see cref="AudioSessionState.AudioSessionStateActive"/>:
    /// Windows keeps expired and inactive ones around long after the app that owned them
    /// stopped listening, and naming those would accuse whatever the user last had open.
    /// </summary>
    private static IEnumerable<string> CaptureHolders(MMDeviceEnumerator enumerator)
    {
        var holders = new List<string>();

        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            try
            {
                var sessions = device.AudioSessionManager.Sessions;
                for (int i = 0; i < sessions.Count; i++)
                {
                    var session = sessions[i];
                    if (session.State != AudioSessionState.AudioSessionStateActive) continue;
                    holders.Add($"{ProcessName(session.GetProcessID)} on '{Name(device)}'");
                }
            }
            catch { /* a device that will not enumerate is not worth failing the log over */ }
            finally { device.Dispose(); }
        }

        return holders;
    }

    private static string ProcessName(uint processId)
    {
        // 0 is the system session rather than a process, and Windows reports it for the
        // shared loopback plumbing on most machines.
        if (processId == 0) return "system";

        try { return Process.GetProcessById((int)processId).ProcessName; }
        catch { return $"pid {processId}"; }
    }

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
