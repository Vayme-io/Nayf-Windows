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

    // The UI/dispatcher thread. The tray icon's message loop runs on its own
    // thread, so its callbacks must marshal here before touching any window.
    private Microsoft.UI.Dispatching.DispatcherQueue? _uiDispatcher;

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
        _authSucceeded = false;
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
            _uiDispatcher = Microsoft.UI.Dispatching.DispatcherQueue.GetForCurrentThread();

            _anchorWindow = new AnchorWindow();
            // Clicking the taskbar button opens the panel (it's an off-screen
            // anchor window, so there's nothing to restore otherwise). Center it
            // over where the user clicked — i.e. the taskbar button itself.
            _anchorWindow.TaskbarActivated += () =>
                _uiDispatcher?.TryEnqueue(() =>
                {
                    NativeMethods.RECT anchor = default;
                    if (NativeMethods.GetCursorPos(out var pt))
                        anchor = new NativeMethods.RECT
                        {
                            Left = pt.X - 8, Top = pt.Y - 8,
                            Right = pt.X + 8, Bottom = pt.Y + 8
                        };
                    bool opened = _companionPanelWindow?.EnsureVisibleNearTray(anchor) ?? false;
                    // If this click closed (or didn't open) the panel, drop the
                    // anchor window out of foreground so the next click reopens it.
                    if (!opened)
                        _anchorWindow?.ReleaseForeground();
                });
            _anchorWindow.Activate();
            Log("Step", "AnchorWindow created");

            _companionManager = new CompanionManager(_authManager!);
            Log("Step", "CompanionManager created");

            _overlayWindowManager = new OverlayWindowManager(_companionManager);
            _overlayWindowManager.CreateOverlaysForAllMonitors();
            Log("Step", "Overlays created");

            _companionPanelWindow = new CompanionPanelWindow(_companionManager);
            _companionPanelWindow.SignOutRequested += OnSignOutRequested;
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

            // Auto-show the panel when the agent needs a destructive-command
            // decision — it blocks until the user approves or denies.
            _companionManager.AgentManager.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(NayfAgentManager.PendingConfirmationRequest) &&
                    _companionManager.AgentManager.PendingConfirmationRequest != null)
                {
                    _companionPanelWindow?.EnsureVisibleNearTray(_systemTrayManager?.GetTrayIconRect() ?? default);
                }
            };

            _systemTrayManager = new SystemTrayManager(
                onTogglePanel: ShowCompanionPanel,
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

    // These are invoked from the tray icon's background message-loop thread,
    // so they hop to the UI thread before touching any window.
    private void ShowCompanionPanel()
    {
        _uiDispatcher?.TryEnqueue(() =>
            _companionPanelWindow?.ShowNearTray(_systemTrayManager?.GetTrayIconRect() ?? default));
    }

    /// <summary>
    /// Clears the saved session and shows the login window again. The companion
    /// stays alive but is inert until the user re-authenticates (it shares the
    /// same AuthManager, so it picks up the new token automatically).
    /// </summary>
    private void OnSignOutRequested()
    {
        Log("Step", "Sign out requested");
        _companionPanelWindow?.HidePanel();
        _authManager!.SignOut();
        ShowAuthWindow();
    }

    private void QuitApp()
    {
        // Invoked from the tray thread (context-menu "Quit") — marshal to UI.
        _uiDispatcher?.TryEnqueue(() =>
        {
            _systemTrayManager?.Dispose();
            _overlayWindowManager?.Dispose();
            _companionManager?.Dispose();
            Exit();
        });
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
