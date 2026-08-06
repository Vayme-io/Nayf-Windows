using System;
using Microsoft.Win32;

namespace NayfWindows;

/// <summary>
/// Reads and writes the "start Nayf when I sign in" registry entry — the same
/// HKCU Run value the installer's optional startup task creates, so the setting
/// in the panel and the checkbox in the installer control one thing rather than
/// two that can disagree. Because the value name matches, the uninstaller's
/// uninsdeletevalue still cleans up an entry the app wrote itself.
/// </summary>
public static class NayfStartup
{
    private const string RunKeyPath = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string RunValueName = "Nayf";

    /// <summary>
    /// True when Windows is set to launch Nayf at sign-in. Only the presence of
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
                return key?.GetValue(RunValueName) != null;
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
