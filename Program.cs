using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

namespace NayfWindows;

/// <summary>
/// Application entry point. Starts the WinUI 3 app directly: the Windows App SDK is
/// built into this app (see WindowsAppSDKSelfContained in the csproj) rather than
/// looked up on the machine, so there is no runtime to bootstrap first.
/// </summary>
static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "nayf-crash.log");

    [STAThread]
    static void Main(string[] args)
    {
        // No Bootstrap.Initialize here. The build is WindowsAppSDKSelfContained, so the
        // Windows App SDK sits next to the exe and loads from there; the bootstrapper's
        // only job is to locate a copy installed on the machine and point the process at
        // it, which is the one thing this app must not do. On a machine that has the
        // runtime installed it does exactly that, and the process then holds two copies —
        // the package's and its own — and dies in CoreMessagingXP.dll a moment after
        // startup. On a machine without it there is nothing to find and it fails outright.
        // Self-contained apps do not use the bootstrapper.

        try
        {
            // WinUI 3 startup — must call Application.Start with a DispatcherQueue
            // synchronization context so async/await works on the UI thread.
            Application.Start((p) =>
            {
                var syncContext = new DispatcherQueueSynchronizationContext(
                    DispatcherQueue.GetForCurrentThread());
                SynchronizationContext.SetSynchronizationContext(syncContext);
                _ = new App();
            });
        }
        catch (Exception ex)
        {
            Log("App startup FAILED", ex.ToString());
            ShowError($"Nayf failed to start:\n{ex.Message}");
        }
    }

    private static void Log(string step, string message)
    {
        try { File.AppendAllText(LogPath, $"[{DateTime.Now:HH:mm:ss}] {step}: {message}\n"); }
        catch { }
    }

    private static void ShowError(string message)
    {
        MessageBox(IntPtr.Zero, message, "Nayf Error", 0x10 /* MB_ICONERROR */);
    }

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int MessageBox(IntPtr hWnd, string text, string caption, uint type);
}
