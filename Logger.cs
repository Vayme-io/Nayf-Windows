using System;
using System.IO;

namespace NayfWindows;

/// <summary>
/// Always-on file logger (unlike Debug.WriteLine, which is stripped from
/// Release builds). Writes to the same desktop crash log as App.xaml.cs.
/// </summary>
public static class Logger
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "nayf-crash.log");

    public static void Log(string step, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {step}: {message}";
            File.AppendAllText(LogPath, line + Environment.NewLine);
        }
        catch { /* never let logging crash the app */ }
    }
}
