using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using WinRT.Interop;

namespace NayfWindows;

/// <summary>
/// A 1x1 invisible window kept alive off-screen for two reasons: WinUI 3 shuts
/// the app down when it sees zero open windows, and this window owns Vayme's
/// taskbar button so Vayme sits among the user's other programs like any other
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

    /// <summary>
    /// Raised when the user closes Vayme from its taskbar button — the right-click menu's
    /// "Close window". Vayme has no visible window there to close, so this means the same
    /// thing as Quit in the tray, and the app routes it to the same place.
    /// </summary>
    public event Action? CloseRequested;

    /// <summary>
    /// Lets this window close. Set by the app when the user has actually asked Vayme to
    /// quit, and only then.
    ///
    /// Closing is refused by default so nothing can shut Vayme down behind the user's back.
    /// But <c>Application.Exit()</c> shuts the app down by asking every window to close, and
    /// a window that always refuses refuses that too — which is why quitting used to do
    /// nothing at all.
    /// </summary>
    public bool AllowClose { get; set; }

    // Held in a field so the GC can't collect the delegate Windows calls back into.
    private readonly SubclassProc _subclassProc;

    private const uint WM_SYSCOMMAND = 0x0112;
    private const uint WM_CLOSE = 0x0010;
    private const int SC_RESTORE = 0xF120;
    private const int SC_MINIMIZE = 0xF020;
    private const int SC_CLOSE = 0xF060;
    private const int SC_MASK = 0xFFF0; // low 4 bits of wParam are reserved by the system

    // Hovering a taskbar button shows a preview of the window behind it. This window is 1x1
    // and parked off-screen, so DWM had nothing to capture and the preview came up empty —
    // beside every other app's it read as Vayme being broken. A window with nothing worth
    // showing can hand DWM its own bitmap instead of being captured, which is what these are.
    private const uint WM_DWMSENDICONICTHUMBNAIL = 0x0323;
    private const int DWMWA_FORCE_ICONIC_REPRESENTATION = 7;
    private const int DWMWA_HAS_ICONIC_BITMAP = 10;
    private const int DWMWA_DISALLOW_PEEK = 11;

    public AnchorWindow()
    {
        InitializeComponent();

        var hwnd = WindowNative.GetWindowHandle(this);
        var appWindow = AppWindow.GetFromWindowId(
            Microsoft.UI.Win32Interop.GetWindowIdFromWindow(hwnd));

        // Size 1x1 and park it far off-screen
        appWindow.Resize(new Windows.Graphics.SizeInt32(1, 1));
        appWindow.Move(new Windows.Graphics.PointInt32(-32000, -32000));

        // Show a taskbar button so Vayme can be found and pinned like any other
        // program. This window is what the button belongs to.
        appWindow.IsShownInSwitchers = true;
        appWindow.SetIcon(Path.Combine(AppContext.BaseDirectory, "Assets", "NayfIcon.ico"));

        // Draw our own taskbar preview rather than letting DWM capture this window, which is
        // 1x1 and off-screen and so previewed as an empty popup. Peek is turned off in the
        // same breath: it dims the desktop to reveal the real window, and revealing a 1x1
        // window at -32000,-32000 would just blank the screen for as long as the hover lasts.
        int on = 1;
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_FORCE_ICONIC_REPRESENTATION, ref on, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_HAS_ICONIC_BITMAP, ref on, sizeof(int));
        NativeMethods.DwmSetWindowAttribute(hwnd, DWMWA_DISALLOW_PEEK, ref on, sizeof(int));

        // Remove title bar and border
        var presenter = OverlappedPresenter.Create();
        presenter.IsMaximizable = false;
        presenter.IsMinimizable = false;
        presenter.IsResizable = false;
        presenter.SetBorderAndTitleBar(hasBorder: false, hasTitleBar: false);
        appWindow.SetPresenter(presenter);

        // Refuse to close, so the user can't accidentally shut down the app — unless they
        // have deliberately asked to quit, in which case this window closing is what ends
        // the app: WinUI shuts down when the last window goes.
        Closed += (_, e) => e.Handled = !AllowClose;

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
        // "Close window" in the taskbar button's right-click menu, which arrives either as
        // WM_CLOSE or as the system-menu command behind it. Both used to be swallowed by the
        // blanket refusal below, so the menu item did nothing whatsoever — the user picked
        // Close and Vayme just carried on. Picking it is as deliberate as picking Quit in the
        // tray, so it now means the same thing and goes to the same shutdown.
        if (!AllowClose &&
            (msg == WM_CLOSE ||
             (msg == WM_SYSCOMMAND && (int)(wParam.ToInt64() & SC_MASK) == SC_CLOSE)))
        {
            CloseRequested?.Invoke();
            return IntPtr.Zero;
        }

        // DWM asking what this window looks like, because we told it not to bother capturing.
        // HIWORD is the widest bitmap it will take, LOWORD the tallest.
        if (msg == WM_DWMSENDICONICTHUMBNAIL)
        {
            long size = lParam.ToInt64();
            SendIconicThumbnail(hwnd, (int)((size >> 16) & 0xFFFF), (int)(size & 0xFFFF));
            return IntPtr.Zero;
        }

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

    /// <summary>
    /// Paints Vayme's mark on a dark card and hands it to DWM as this window's taskbar
    /// preview, at the size DWM asked for.
    ///
    /// It has to be a 32-bit top-down DIB section — DWM will not take the device-dependent
    /// bitmap <c>Bitmap.GetHbitmap()</c> hands back — which is the same kind of surface the
    /// overlay windows draw into.
    /// </summary>
    private static void SendIconicThumbnail(IntPtr hwnd, int maxWidth, int maxHeight)
    {
        if (maxWidth <= 0 || maxHeight <= 0) return;

        // Fill the box DWM asked for rather than fitting a shape inside it: it already asks
        // in the proportions the taskbar preview wants, and anything smaller is letterboxed.
        int width = maxWidth;
        int height = maxHeight;

        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr dib = IntPtr.Zero;
        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = width,
                // Negative height = top-down rows, the order GDI+ writes in. A bottom-up
                // DIB would come out mirrored.
                biHeight = -height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            dib = CreateDIBSection(screenDC, ref header, 0 /* DIB_RGB_COLORS */,
                out IntPtr pixels, IntPtr.Zero, 0);
            if (dib == IntPtr.Zero || pixels == IntPtr.Zero)
            {
                Logger.Log("Taskbar", "CreateDIBSection failed for the taskbar preview");
                return;
            }

            DrawIconicThumbnail(pixels, width, height);
            GdiFlush(); // GDI+ buffers; make sure every pixel has landed before DWM reads them

            int hr = DwmSetIconicThumbnail(hwnd, dib, 0);
            if (hr != 0) Logger.Log("Taskbar", $"DwmSetIconicThumbnail failed: 0x{hr:X8}");
        }
        catch (Exception ex)
        {
            // A missing icon or a failed allocation is not worth taking the app down for —
            // the worst case is the empty preview we already had.
            Logger.Log("Taskbar", $"Taskbar preview failed: {ex.Message}");
        }
        finally
        {
            // DWM copies the bitmap, so it is ours to free either way.
            if (dib != IntPtr.Zero) DeleteObject(dib);
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    private static void DrawIconicThumbnail(IntPtr pixels, int width, int height)
    {
        using var surface = new Bitmap(width, height, width * 4,
            PixelFormat.Format32bppPArgb, pixels);
        using var g = Graphics.FromImage(surface);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;

        // The panel's own ground, so the preview looks like the app it opens.
        var bounds = new Rectangle(0, 0, width, height);
        using (var ground = new LinearGradientBrush(bounds,
                   Color.FromArgb(255, 28, 28, 30), Color.FromArgb(255, 16, 16, 18), 90f))
        {
            g.FillRectangle(ground, bounds);
        }

        using (var hairline = new Pen(Color.FromArgb(20, 255, 255, 255)))
        {
            g.DrawRectangle(hairline, 0, 0, width - 1, height - 1);
        }

        string iconPath = Path.Combine(AppContext.BaseDirectory, "Assets", "NayfIcon.ico");
        if (!File.Exists(iconPath)) return;

        int side = Math.Max(16, (int)(Math.Min(width, height) * 0.5));

        // Take the largest frame in the .ico and scale it down, rather than asking for the
        // frame nearest the size we want: the mark lands between the sizes stored in the
        // file, and scaling one up from 48px is visibly soft at preview size.
        using var icon = new Icon(iconPath, 256, 256);
        using var mark = icon.ToBitmap();
        g.DrawImage(mark, (width - side) / 2, (height - side) / 2, side, side);
    }

    private delegate IntPtr SubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam,
        IntPtr idSubclass, IntPtr refData);

    [DllImport("comctl32.dll", SetLastError = true)]
    private static extern bool SetWindowSubclass(IntPtr hwnd, SubclassProc callback,
        IntPtr idSubclass, IntPtr refData);

    [DllImport("comctl32.dll")]
    private static extern IntPtr DefSubclassProc(IntPtr hwnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetIconicThumbnail(IntPtr hwnd, IntPtr hbitmap, uint flags);

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)]
    private struct BITMAPINFOHEADER
    {
        public uint biSize;
        public int biWidth;
        public int biHeight;
        public ushort biPlanes;
        public ushort biBitCount;
        public uint biCompression;
        public uint biSizeImage;
        public int biXPelsPerMeter;
        public int biYPelsPerMeter;
        public uint biClrUsed;
        public uint biClrImportant;
    }
}
