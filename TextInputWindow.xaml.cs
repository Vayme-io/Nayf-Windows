using System;
using Microsoft.UI;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Composition;
using Microsoft.UI.Composition.SystemBackdrops;
using Windows.System;
using WinRT;
using WinRT.Interop;

namespace NayfWindows;

/// <summary>
/// The typed-request field, opened by the Alt+T chord. Everything typed here goes
/// down exactly the same path as a spoken request — Vayme does not distinguish.
///
/// A WinUI window rather than one of the Win32 layered overlays because this one
/// needs real text entry: IME composition, clipboard, selection and caret.
/// </summary>
public sealed partial class TextInputWindow : Window
{
    /// <summary>Raised with the finished request when the user presses Enter.</summary>
    public event Action<string>? RequestSubmitted;

    private bool _isVisible;

    /// <summary>
    /// The window the user was working in when the chord fired, restored on dismiss.
    ///
    /// Windows delivers keystrokes to the focused control of the *foreground* window,
    /// so unlike the Mac's non-activating panel this one has to take the foreground to
    /// be typeable at all. Handing it straight back afterwards is what keeps the chord
    /// from disturbing whatever the user was doing.
    /// </summary>
    private IntPtr _windowToRestore;

    /// <summary>
    /// The monitor the field opened on, fixed for the lifetime of one appearance.
    /// Re-reading the cursor on every resize would make the box jump to another
    /// screen mid-sentence if the mouse happened to be there.
    /// </summary>
    private NativeMethods.RECT _workArea;

    /// <summary>When the field was last shown, to tell a settling deactivation
    /// during the show itself from the user genuinely clicking away.</summary>
    private DateTimeOffset _shownAt;

    private DesktopAcrylicController? _acrylicController;
    private SystemBackdropConfiguration? _backdropConfig;

    public TextInputWindow()
    {
        InitializeComponent();
        SetupWindow();

        // Preview, not KeyDown: by the time the bubbling event arrives the TextBox has
        // already inserted the newline that Enter would produce, and marking it handled
        // then is too late to take it back.
        InputBox.PreviewKeyDown += OnInputKeyDown;
        ContentStack.SizeChanged += (_, _) => FitWindowToContent();
    }

    private void SetupWindow()
    {
        var hwnd = WindowNative.GetWindowHandle(this);

        // Tool window: no taskbar button, no Alt+Tab entry. WS_EX_NOACTIVATE is
        // deliberately absent — see _windowToRestore.
        var exStyle = NativeMethods.GetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE);
        NativeMethods.SetWindowLong(hwnd, NativeMethods.GWL_EXSTYLE,
            exStyle | NativeMethods.WS_EX_TOOLWINDOW);

        // Keeps the overlays off the box the user is typing into.
        OverlayZOrder.RegisterInteractive(hwnd);

        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        var presenter = OverlappedPresenter.Create();
        presenter.IsAlwaysOnTop = true;
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: true, hasTitleBar: false);
        appWindow.SetPresenter(presenter);
        appWindow.IsShownInSwitchers = false;

        int darkModeValue = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, 20 /* DWMWA_USE_IMMERSIVE_DARK_MODE */,
            ref darkModeValue, sizeof(int));

        if (Content is FrameworkElement root)
            root.RequestedTheme = ElementTheme.Dark;

        // Same acrylic recipe as the companion panel, so the two read as one app.
        if (DesktopAcrylicController.IsSupported())
        {
            _backdropConfig = new SystemBackdropConfiguration
            {
                IsInputActive = true,
                Theme = SystemBackdropTheme.Dark
            };
            _acrylicController = new DesktopAcrylicController { Kind = DesktopAcrylicKind.Base };
            _acrylicController.TintColor = Windows.UI.Color.FromArgb(255, 24, 24, 27);
            _acrylicController.TintOpacity = 0.50f;
            _acrylicController.LuminosityOpacity = 0.78f;
            _acrylicController.FallbackColor = Windows.UI.Color.FromArgb(255, 28, 28, 30);
            _acrylicController.AddSystemBackdropTarget(this.As<ICompositionSupportsSystemBackdrop>());
            _acrylicController.SetSystemBackdropConfiguration(_backdropConfig);
        }
        else
        {
            SystemBackdrop = new DesktopAcrylicBackdrop();
        }

        // Clicking away is a dismissal, like Escape — the draft is kept either way.
        // Taking the foreground shuffles activation around for a moment, so ignore
        // what arrives while the window is still coming up.
        Activated += (_, args) =>
        {
            if (args.WindowActivationState != WindowActivationState.Deactivated) return;
            if ((DateTimeOffset.UtcNow - _shownAt).TotalMilliseconds < 300) return;
            Dismiss();
        };

        appWindow.Hide();
    }

    /// <summary>
    /// Shows the field and puts the caret in it. <paramref name="windowToRestore"/> is
    /// the foreground window captured at chord time, before anything of Vayme's could
    /// have taken it.
    /// </summary>
    public void ShowForRequest(IntPtr windowToRestore)
    {
        // A second chord while the field is already up is a request to put it away.
        if (_isVisible)
        {
            Dismiss();
            return;
        }

        _windowToRestore = windowToRestore;
        _isVisible = true;
        _shownAt = DateTimeOffset.UtcNow;
        _workArea = GetWorkAreaUnderCursor();

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.Show();
        Activate();

        // Activate() alone loses to the foreground lock: the click that would have
        // earned us the foreground never happened — the user pressed a hotkey while
        // working somewhere else. Without this the field appears but swallows nothing,
        // and the typing goes to whatever was already in front.
        NativeMethods.ForceForeground(hwnd);

        FitWindowToContent();

        // Focus has to be asked for explicitly: the box is not the window's default
        // focus target, and without this the first keystrokes land nowhere.
        InputBox.Focus(FocusState.Programmatic);
        InputBox.SelectionStart = InputBox.Text.Length;
    }

    /// <summary>
    /// Hides the field and hands the foreground back. The draft is kept deliberately —
    /// a half-written request that vanishes because the user tabbed away is worse than
    /// one that is still there when they come back. <see cref="Submit"/> clears it.
    /// </summary>
    public void Dismiss()
    {
        if (!_isVisible) return;
        _isVisible = false;

        AppWindow.GetFromWindowId(
            Win32Interop.GetWindowIdFromWindow(
                WindowNative.GetWindowHandle(this))).Hide();

        if (_windowToRestore != IntPtr.Zero)
            NativeMethods.SetForegroundWindow(_windowToRestore);
        _windowToRestore = IntPtr.Zero;
    }

    private void OnInputKeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Escape)
        {
            e.Handled = true;
            Dismiss();
            return;
        }

        if (e.Key != VirtualKey.Enter) return;

        // Shift+Enter is a newline. Read the physical key rather than the routed
        // args, which carry no modifier state.
        bool shiftHeld = NativeMethods.IsKeyDown(NativeMethods.VK_LSHIFT) ||
                         NativeMethods.IsKeyDown(NativeMethods.VK_RSHIFT);
        if (shiftHeld) return;

        e.Handled = true;
        Submit();
    }

    private void Submit()
    {
        var text = InputBox.Text.Trim();
        if (text.Length == 0) return;

        // Put the field away before the turn starts, so the response and the status
        // pill aren't competing with it for the user's attention.
        InputBox.Text = "";
        Dismiss();
        RequestSubmitted?.Invoke(text);
    }

    /// <summary>
    /// Sizes the window to exactly its content and centres it on the monitor under
    /// the cursor.
    /// </summary>
    private void FitWindowToContent()
    {
        if (!_isVisible) return;

        double dipWidth = ContentStack.ActualWidth > 0 ? ContentStack.ActualWidth : ContentStack.Width;
        double dipHeight = ContentStack.ActualHeight;
        if (dipWidth <= 0 || dipHeight <= 0) return;

        double scale = RootGrid.XamlRoot?.RasterizationScale ?? 1.0;
        int w = (int)Math.Ceiling(dipWidth * scale);
        int h = (int)Math.Ceiling((dipHeight + 1) * scale);

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(Win32Interop.GetWindowIdFromWindow(hwnd));
        appWindow.ResizeClient(new Windows.Graphics.SizeInt32(w, h));

        // ResizeClient still sets aside room for a title bar this window doesn't have,
        // so it hands back a client area taller than the one asked for — 31px on
        // Windows 11, and the reason the box first came up with a band of dead space
        // under the hint. Measure the overshoot and take it back rather than
        // hard-coding a caption height that isn't ours to predict.
        int overshoot = appWindow.ClientSize.Height - h;
        if (overshoot != 0)
            appWindow.ResizeClient(new Windows.Graphics.SizeInt32(w, h - overshoot));

        NativeMethods.GetWindowRect(hwnd, out var outer);
        int outerW = outer.Right - outer.Left;
        int outerH = outer.Bottom - outer.Top;

        int x = _workArea.Left + Math.Max(0, (_workArea.Width - outerW) / 2);
        int y = _workArea.Top + Math.Max(0, (_workArea.Height - outerH) / 2);

        appWindow.Move(new Windows.Graphics.PointInt32(x, y));
    }

    private static NativeMethods.RECT GetWorkAreaUnderCursor()
    {
        if (NativeMethods.GetCursorPos(out var pt))
        {
            IntPtr hMon = NativeMethods.MonitorFromPoint(pt, NativeMethods.MONITOR_DEFAULTTONEAREST);
            var info = new NativeMethods.MONITORINFOEX
            {
                cbSize = (uint)System.Runtime.InteropServices.Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
            };
            if (hMon != IntPtr.Zero && NativeMethods.GetMonitorInfo(hMon, ref info))
                return info.rcWork;
        }

        var workArea = new NativeMethods.RECT();
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref workArea, 0);
        return workArea;
    }
}
