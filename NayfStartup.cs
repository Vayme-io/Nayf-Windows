using System;
using Microsoft.Win32;

namespace NayfWindows;

/// <summary>
/// Reads and writes the "start Vayme when I sign in" registry entry — the same
/// HKCU Run value the installer's optional startup task creates, so the setting
/// in the panel and the checkbox in the installer control one thing rather than
/// two that can disagree. Because the value name matches, the uninstaller's
/// uninsdeletevalue still cleans up an entry the app wrote itself.
/// </summary>
public static class NayfStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Vayme";

    // What the value was called before the app was renamed. A machine upgraded
    // from a pre-rename install still has it, pointing at the same executable,
    // so every write clears it — otherwise sign-in launches the app twice.
    private const string LegacyRunValueName = "Nayf";

    /// <summary>
    /// True when Windows is set to launch Vayme at sign-in. Only the presence of
    /// the value is checked, not the path it points at — a stale path from an
    /// earlier install still counts as "on", and gets rewritten to the current
    /// executable the next time the user toggles it.
    /// </summary>
    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKeyPath);
                if (key == null) return false;
                return key.GetValue(RunValueName) != null
                    || key.GetValue(LegacyRunValueName) != null;
            }
            catch
            {
                // A locked-down or unreadable hive shouldn't take the panel down.
                return false;
            }
        }
    }

    /// <summary>
    /// Turns launch-at-sign-in on or off. Returns false if the registry refused
    /// the write, so the caller can put the toggle back where the user found it
    /// instead of showing a state Windows never accepted.
    /// </summary>
    public static bool SetEnabled(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKeyPath, writable: true);
            if (key == null) return false;

            // Drop the pre-rename entry either way. Leaving it while writing the
            // new one would start two copies at sign-in.
            key.DeleteValue(LegacyRunValueName, throwOnMissingValue: false);

            if (enabled)
            {
                string? exePath = Environment.ProcessPath;
                if (string.IsNullOrEmpty(exePath)) return false;

                // Quoted so a path containing spaces (the default install lives
                // under "Program Files"-style paths) is parsed as one argument.
                key.SetValue(RunValueName, $"\"{exePath}\"", RegistryValueKind.String);
            }
            else
            {
                key.DeleteValue(RunValueName, throwOnMissingValue: false);
            }

            return true;
        }
        catch
        {
            return false;
        }
    }
}
