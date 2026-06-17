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
    private AuthManager? _authManager;
    private AuthWindow? _authWindow;
    private bool _companionStarted;
    private bool _authSucceeded;

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
            _authManager = new AuthManager();
            _ = InitializeAsync();
        }
        catch (Exception ex)
        {
            Log("CRASH in OnLaunched", ex.ToString());
            throw;
        }
    }

    /// <summary>
    /// Restores a saved session if possible; otherwise shows the sign-in window.
    /// The companion only starts once the user is authenticated.
    /// </summary>
    private async System.Threading.Tasks.Task InitializeAsync()
    {
        try
        {
            bool restored = await _authManager!.RestoreSessionAsync();
            Log("Step", $"Session restored: {restored}");

            if (restored)
                StartCompanion();
            else
                ShowAuthWindow();
        }
        catch (Exception ex)
        {
            Log("CRASH in InitializeAsync", ex.ToString());
            ShowAuthWindow();
        }
    }

    private void ShowAuthWindow()
    {
        _authWindow = new AuthWindow(_authManager!);
        _authWindow.AuthenticationSucceeded += () =>
        {
            Log("Step", "Authentication succeeded");
            _authSucceeded = true;
            _authWindow?.Close();
            _authWindow = null;
            StartCompanion();
        };

        // If the user closes the sign-in window without authenticating, quit.
        // (Skip when we closed it ourselves after a successful sign-in.)
        _authWindow.Closed += (_, _) =>
        {
            if (!_authSucceeded)
            {
                Log("Step", "Auth window closed without sign-in — quitting");
                Exit();
            }
        };

        _authWindow.Activate();
        Log("Step", "Auth window shown");
    }

    private void StartCompanion()
    {
        if (_companionStarted) return;
        _companionStarted = true;

        try
        {
            _anchorWindow = new AnchorWindow();
            _anchorWindow.Activate();
            Log("Step", "AnchorWindow created");

            _companionManager = new CompanionManager(_authManager!);
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
            Log("CRASH in StartCompanion", ex.ToString());
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
