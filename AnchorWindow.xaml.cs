using System;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace NayfWindows;

/// <summary>
/// A 1x1 invisible window kept alive off-screen for two reasons: WinUI 3 shuts
/// the app down when it sees zero open windows, and this window owns Nayf's
/// taskbar button so Nayf sits among the user's other programs like any other
/// app.
///
/// It stays permanently minimized. Windows only ever asks a minimized window to
/// restore, so every taskbar click arrives as the same message, which this
/// window turns into <see cref="TaskbarActivated"/> and then swallows. Nothing
/// is ever restored or focused, so the panel opened in response is free to take
/// focus itself rather than losing it to this window.
/// </summary>
public sealed partial class AnchorWindow : Window
{
    /// <summary>Raised when the user clicks this window's taskbar button.</summary>
    public event Action? TaskbarActivated;

    // Held in a field so the GC can't collect the delegate Windows calls back into.
    private readonly SubclassProc _subclassProc;

    private const uint WM_SYSCOMMAND = 0x0112;
    private const int SC_RESTORE = 0xF120;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_MASK = 0xFFF0; // low 4 bits of wParam are reserved by the system

    public AnchorWindow()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        // Size 1x1 and park it far off-screen
        appWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        appWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));

        // Show a taskbar button so Nayf can be found and pinned like any other
        // program. This window is what the button belongs to.
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

        _subclassProc = TaskbarButtonSubclass;
        SetWindowSubclass(hwnd, _subclassProc, IntPtr.Zero, IntPtr.Zero);
    }

    /// <summary>
    /// Makes the taskbar button appear without taking focus, by showing the
    /// window minimized — which is also the state every taskbar click is decoded
    /// from. Window.Activate() would instead steal focus at launch.
    /// </summary>
    public void ShowAsTaskbarButton()
    {
        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        appWindow.Show(activateWindow: false);
        NativeMethods.ShowWindow(hwnd, NativeMethods.SW_SHOWMINNOACTIVE);
    }

    private IntPtr TaskbarButtonSubclass(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        IntPtr idSubclass, IntPtr refData)
    {
        if (msg == WM_SYSCOMMAND)
        {
            int command = (int)(wParam.ToInt64() & SC_MASK);

            // The shell's "user clicked the taskbar button" message. Report it and
            // swallow it: restoring would hand this window the focus the panel needs.
            if (command == SC_RESTORE)
            {
                TaskbarActivated?.Invoke();
                return IntPtr.Zero;
            }

            // Stay minimized — that state is what makes the next click a restore.
            if (command == SC_MINIMIZE) return IntPtr.Zero;
        }

        return DefSubclassProc(hwnd, msg, wParam, lParam);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        IntPtr idSubclass, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback,
        IntPtr idSubclass, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);
}
