using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace NayfWindows;

/// <summary>
/// A 1x1 invisible window kept alive off-screen for the sole purpose of
/// preventing WinUI 3 from auto-exiting when all other windows are hidden.
/// WinUI 3 shuts down the app when it sees zero open windows — this window
/// stays open but invisible so the app keeps running as a tray app.
/// </summary>
public sealed partial class AnchorWindow : Window
{
    /// <summary>
    /// Raised when the user activates this window via its taskbar button
    /// (the very first programmatic activation at startup is ignored).
    /// </summary>
    public event Action? TaskbarActivated;

    private bool _seenInitialActivation;

    public AnchorWindow()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        // Size 1x1 and park it far off-screen
        appWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        appWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));

        // Show a taskbar button (and Alt+Tab entry) using the Nayf icon
        appWindow.IsShownInSwitchers = true;
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "NayfIcon.ico"));

        // Remove title bar and border
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        appWindow.SetPresenter(presenter);

        // Block the close button so the user can't accidentally shut down the app
        Closed += (_, e) => e.Handled = true;

        // Clicking the taskbar button activates this window — surface that as a
        // request to open the panel. Skip the initial startup activation.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState == WindowActivationState.Deactivated) return;
            if (!_seenInitialActivation)
            {
                _seenInitialActivation = true;
                return;
            }
            TaskbarActivated?.Invoke();
        };
    }

    /// <summary>
    /// Minimizes this (invisible) window so it releases foreground focus. Without
    /// this, after a taskbar click closes the panel, the anchor window stays
    /// foreground and the next taskbar click is a no-op — minimizing means the
    /// next click restores it, firing a fresh activation that reopens the panel.
    /// </summary>
    public void ReleaseForeground()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_MINIMIZE);
    }
}
