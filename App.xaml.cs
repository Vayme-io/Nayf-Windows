using Microsoft.UI.Xaml;
using Microsoft.Windows.AppLifecycle;
using System;

namespace NayfWindows;

public partial class App : Application
{
    private SystemTrayManager? _systemTrayManager;
    private CompanionManager? _companionManager;
    private OverlayWindowManager? _overlayWindowManager;
    private CompanionPanelWindow? _companionPanelWindow;

    public static new App Current => (App)Application.Current;

    public CompanionManager CompanionManager => _companionManager!;
    public CompanionPanelWindow? CompanionPanelWindow => _companionPanelWindow;

    public App()
    {
        InitializeComponent();
    }

    protected override void OnLaunched(LaunchActivatedEventArgs args)
    {
        // Initialize the core state machine first
        _companionManager = new CompanionManager();

        // Create overlay windows for each monitor (click-through transparent overlays)
        _overlayWindowManager = new OverlayWindowManager(_companionManager);
        _overlayWindowManager.CreateOverlaysForAllMonitors();

        // Create the companion panel window (tray dropdown) but keep it hidden
        _companionPanelWindow = new CompanionPanelWindow(_companionManager);

        // Set up system tray icon — this is the app's main UI entry point
        _systemTrayManager = new SystemTrayManager(
            onShowPanel: ShowCompanionPanel,
            onHidePanel: HideCompanionPanel,
            onQuit: QuitApp
        );
        _systemTrayManager.Initialize();

        // Start the push-to-talk monitoring and companion services
        _companionManager.StartAsync();
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
}
