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
    private NativeStatusPillWindow? _statusPillWindow;
    private NayfActionToast? _actionToast;
    private NayfCapabilitiesShowcase? _capabilitiesShowcase;
    private CompanionPanelWindow? _companionPanelWindow;
    private TextInputWindow? _textInputWindow;
    private NayfAgentCardHost? _agentCardHost;
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

            // Tear the window down on a later turn of the dispatcher rather than
            // here. This handler runs on the sign-in window's own submit stack, and
            // closing the window from inside it destroys the XAML island that stack
            // is still unwinding through — an access violation inside
            // Microsoft.UI.Xaml.dll, which no catch block in this app can see.
            var window = _authWindow;
            _authWindow = null;
            if (window is null) return;

            var dispatcher = window.DispatcherQueue;
            dispatcher.TryEnqueue(() =>
            {
                // Bring the companion up *before* dismissing the sign-in window.
                // WinUI starts shutting the app down the moment its last window
                // closes, so something has to outlive this one: closing first tears
                // XAML down underneath every window StartCompanion goes on to
                // create, and that fault lands in Microsoft.UI.Xaml.dll as an
                // access violation rather than as an exception anything can catch.
                StartCompanion();
                dispatcher.TryEnqueue(window.Close);
            });
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
            // Clicking the taskbar button toggles the panel, opening it over the
            // button the user clicked. ShowNearTray also covers the closing half:
            // clicking the button while the panel is open blur-dismisses it, and
            // the just-hidden guard inside keeps that same click from reopening it.
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
                    _companionPanelWindow?.ShowNearTray(anchor);
                });
            _anchorWindow.ShowAsTaskbarButton();
            Log("Step", "AnchorWindow created");

            NayfSoundPlayer.Warmup();

            _companionManager = new CompanionManager(_authManager!);
            Log("Step", "CompanionManager created");

            _overlayWindowManager = new OverlayWindowManager(_companionManager);
            _overlayWindowManager.CreateOverlaysForAllMonitors();
            Log("Step", "Overlays created");

            _statusPillWindow = new NativeStatusPillWindow(_companionManager);
            _statusPillWindow.Start();
            Log("Step", "StatusPill created");

            _actionToast = new NayfActionToast(_companionManager);
            _actionToast.Start();
            Log("Step", "ActionToast created");

            _capabilitiesShowcase = new NayfCapabilitiesShowcase(_companionManager);
            _capabilitiesShowcase.Start();
            Log("Step", "CapabilitiesShowcase created");

            _companionPanelWindow = new CompanionPanelWindow(_companionManager);
            _companionPanelWindow.SignOutRequested += OnSignOutRequested;
            _companionPanelWindow.QuitRequested += QuitApp;
            Log("Step", "PanelWindow created");

            // Kept alive for the life of the app and only ever hidden — closing a
            // WinUI window destroys it, and the last one closing takes the app down.
            _textInputWindow = new TextInputWindow();
            _textInputWindow.RequestSubmitted += text => _companionManager?.SendTypedRequest(text);
            _companionManager.TextInputRequested += () =>
                _uiDispatcher?.TryEnqueue(() =>
                    // Read the foreground window here rather than inside the window:
                    // by the time it activates, the answer is Nayf itself.
                    _textInputWindow?.ShowForRequest(NativeMethods.GetForegroundWindow()));
            Log("Step", "TextInputWindow created");

            // Agent result cards. The host is what decides whether a task already has a
            // card up, which is also the manager's test for whether a background
            // continuation may refresh one — a task finishing quietly must never throw a
            // card onto the screen the user didn't ask for.
            _agentCardHost = new NayfAgentCardHost();
            _agentCardHost.FollowUpRequested += id =>
                _uiDispatcher?.TryEnqueue(() => _companionManager?.BeginAgentTaskFollowUp(id));
            _companionManager.IsAgentCardOpen = id => _agentCardHost.IsOpen(id);
            _companionManager.AgentCardRequested += task =>
                _uiDispatcher?.TryEnqueue(() => _agentCardHost?.Show(task));
            _companionManager.PropertyChanged += (_, e) =>
            {
                // The follow-up button says "Listening…" until Nayf stops. Idle is the
                // only state that means it has, whether the turn finished or was cut off.
                if (e.PropertyName == nameof(CompanionManager.VoiceState) &&
                    _companionManager.VoiceState == CompanionVoiceState.Idle)
                    _uiDispatcher?.TryEnqueue(() => _agentCardHost?.EndFollowUp());
            };
            Log("Step", "AgentCardHost created");

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

    /// <summary>
    /// Shuts Nayf down. The one way out — the tray's Quit and the panel's power button
    /// both come here, so neither can skip the teardown and leave a dead tray icon or a
    /// keyboard hook behind.
    /// </summary>
    private void QuitApp()
    {
        // Invoked from the tray thread (context-menu "Quit") — marshal to UI.
        _uiDispatcher?.TryEnqueue(() =>
        {
            Log("Quit", "Shutting down");

            _systemTrayManager?.Dispose();
            _agentCardHost?.CloseAll();
            _overlayWindowManager?.Dispose();
            _statusPillWindow?.Dispose();
            _actionToast?.Dispose();
            _capabilitiesShowcase?.Dispose();
            _companionManager?.Dispose();
            NayfSoundPlayer.DisposeShared();

            // Before Exit(), which closes the windows: the anchor window turns every
            // close down until told otherwise, this one included.
            if (_anchorWindow != null) _anchorWindow.AllowClose = true;

            Exit();

            // Exit() is a request, and any window left standing can refuse it. Nothing
            // should now, but a user who has just told Nayf to quit and watched the
            // teardown happen must not be left with it still running. Everything above
            // has already been disposed, so there is nothing here left to lose.
            _ = System.Threading.Tasks.Task.Delay(TimeSpan.FromSeconds(2)).ContinueWith(_ =>
            {
                Log("Quit", "Graceful exit didn't finish — terminating");
                Environment.Exit(0);
            });
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
