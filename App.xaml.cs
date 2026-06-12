using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;
using System.IO;

namespace NayfWindows;

public partial class App : Application
{
    private SystemTrayManager? _systemTrayManager;
    private CompanionManager? _companionManager;
    private OverlayWindowManager? _overlayWindowManager;
    private CompanionPanelWindow? _companionPanelWindow;
    private AnchorWindow? _anchorWindow;

    private static readonly string LogPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.Desktop), "nayf-crash.log");

    public static new App Current => (App)Application.Current;

    public CompanionManager CompanionManager => _companionManager!;
    public CompanionPanelWindow? CompanionPanelWindow => _companionPanelWindow;

    public App()
    {
        InitializeComponent();

        // Catch unhandled exceptions on any thread and log them to the desktop
        AppDomain.CurrentDomain.UnhandledException += (_, e) =>
            Log("UnhandledException", e.ExceptionObject?.ToString() ?? "null");

        UnhandledException += (_, e) =>
        {
            e.Handled = true;
            Log("WinUI UnhandledException", e.Exception?.ToString() ?? "null");
        };
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        try
        {
            Log("OnLaunched", "Starting...");

            _anchorWindow = new AnchorWindow();
            _anchorWindow.Activate();
            Log("Step", "AnchorWindow created");

            _companionManager = new CompanionManager();
            Log("Step", "CompanionManager created");

            _overlayWindowManager = new OverlayWindowManager(_companionManager);
            _overlayWindowManager.CreateOverlaysForAllMonitors();
            Log("Step", "Overlays created");

            _companionPanelWindow = new CompanionPanelWindow(_companionManager);
            Log("Step", "PanelWindow created");

            // Auto-show the panel when the user needs to enable speech
            // recognition, so the banner with the settings link is visible.
            _companionManager.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(CompanionManager.MicrophonePermissionNeeded) &&
                    _companionManager.MicrophonePermissionNeeded)
                {
                    _companionPanelWindow?.EnsureVisibleNearTray(_systemTrayManager?.GetTrayIconRect() ?? default);
                }
            };

            _systemTrayManager = new SystemTrayManager(
                onShowPanel: ShowCompanionPanel,
                onHidePanel: HideCompanionPanel,
                onQuit: QuitApp
            );
            _systemTrayManager.Initialize();
            Log("Step", "SystemTray initialized");

            _companionManager.StartAsync();
            Log("Step", "CompanionManager started — app running");
        }
        catch (Exception ex)
        {
            Log("CRASH in OnLaunched", ex.ToString());
            throw;
        }
    }

    private void ShowCompanionPanel()
    {
        _companionPanelWindow?.ShowNearTray(_systemTrayManager?.GetTrayIconRect() ?? default);
    }

    private void HideCompanionPanel()
    {
        _companionPanelWindow?.HidePanel();
    }

    private void QuitApp()
    {
        _systemTrayManager?.Dispose();
        _overlayWindowManager?.Dispose();
        _companionManager?.Dispose();
        Exit();
    }

    private static void Log(string step, string message)
    {
        try
        {
            var line = $"[{DateTime.Now:HH:mm:ss.fff}] {step}: {message}";
            File.AppendAllText(LogPath, line + Environment.NewLine);
            System.Diagnostics.Debug.WriteLine(line);
        }
        catch { /* never let logging crash the app */ }
    }
}
