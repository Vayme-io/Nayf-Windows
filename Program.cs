using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.Windows.ApplicationModel.DynamicDependency;

namespace NayfWindows;

/// <summary>
/// Application entry point. Manually bootstraps the Windows App SDK runtime
/// before starting the WinUI 3 app — required for unpackaged apps.
/// Without Bootstrap.Initialize the process crashes silently on startup.
/// </summary>
static class Program
{
    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "nayf-crash.log");

    [STAThread]
    static void Main(string[] args)
    {
        // Bootstrap the Windows App SDK 2.0 runtime.
        // 0x00020000 = major version 2, minor version 0.
        try
        {
            Bootstrap.Initialize(0x00020000);
        }
        catch (Exception ex)
        {
            Log("Bootstrap FAILED", ex.ToString());
            ShowError(
                "Windows App SDK 2.0 runtime is not installed.\n\n" +
                "Download and run the installer:\n" +
                "https://aka.ms/windowsappsdk/2.0/latest/windowsappruntimeinstall-x64.exe\n\n" +
                $"Error: {ex.Message}");
            return;
        }

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
        finally
        {
            Bootstrap.Shutdown();
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
