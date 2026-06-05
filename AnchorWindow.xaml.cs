using System;
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
    public AnchorWindow()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        // Size 1x1 and park it far off-screen
        appWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        appWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));

        // No taskbar button, no Alt+Tab entry
        appWindow.IsShownInSwitchers = false;

        // Remove title bar and border
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        appWindow.SetPresenter(presenter);

        // Hide from taskbar via Win32 extended styles
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW | NativeMethods.WS_EX_NOACTIVATE);

        // Block the close button so the user can't accidentally shut down the app
        Closed += (_, e) => e.Handled = true;
    }
}
