using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// The full-screen surface the user draws on while holding Shift to circle part of their
/// screen. Mirrors <c>NayfLassoView</c> in <c>NayfRegionFocus.swift</c>.
///
/// <para><b>The line erases itself.</b> Each sample stays solid for <see cref="FadeHold"/>,
/// then fades to nothing over <see cref="FadeOut"/> and is dropped. So the stroke reads as a
/// comet tail that follows the cursor rather than a line that keeps growing — you can circle
/// something twice, or scribble over it, without the screen filling up with ink. The shape is
/// remembered even after it has faded: <see cref="TryGetDrawnBounds"/> reports every point
/// ever sampled, so the crop covers the whole gesture and not just the part still glowing.</para>
///
/// <para><b>Click-through.</b> Nothing here is interactive — the user is moving the cursor,
/// not pressing anything — so the window keeps <c>WS_EX_TRANSPARENT</c> and takes no input at
/// all. The cursor is polled instead of tracked through mouse messages, which means the app
/// underneath carries on receiving its own input while a lasso is open. That is the whole
/// difference from the Mac, where the panel takes mouse events for itself.</para>
///
/// <para><b>One monitor.</b> The one the cursor was on when the hold was confirmed, as on the
/// Mac. A window spanning the whole virtual desktop would put a multi-monitor bitmap through
/// <c>UpdateLayeredWindow</c> sixty times a second for as long as the key is held.</para>
///
/// <para>Separate from <see cref="AnnotationOverlayWindow"/> — which is per-monitor, permanent
/// and stops rendering when nothing moves — because this one is transient, single, and
/// animating for its entire life. They share the layered-window technique, not a lifecycle.</para>
/// </summary>
public sealed class RegionFocusOverlayWindow : IDisposable
{
    /// <summary>One cursor sample and the moment it was taken, so it can fade with age.</summary>
    private readonly record struct TrailPoint(PointF Position, double Birth);

    /// <summary>The visible comet tail, oldest first. Pruned by the render loop.</summary>
    private readonly List<TrailPoint> _trail = new();

    /// <summary>
    /// The extent of every point ever sampled, kept as a running box rather than a list: the
    /// crop is the only thing that reads it, and it only ever needs the corners.
    /// </summary>
    private float _minX, _minY, _maxX, _maxY;
    private int _drawnPointCount;

    /// <summary>Guards the box above, which the render thread writes and the UI thread reads.</summary>
    private readonly object _boundsLock = new();

    private readonly Rectangle _monitorBounds;
    private readonly Stopwatch _clock = Stopwatch.StartNew();

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private System.Threading.Timer? _renderTimer;
    private int _renderGuard;

    private PointF? _lastSample;

    /// <summary>
    /// Set if any mouse button went down while the lasso was open, which means the Shift was
    /// modifying a click rather than opening a lasso. Written by the render thread, read by
    /// the UI thread once the hold ends.
    /// </summary>
    private volatile bool _mouseButtonWasPressed;

    // ── Drawing surface, allocated once ──────────────────────────────────────
    private IntPtr _dibSection = IntPtr.Zero;
    private IntPtr _memoryDC = IntPtr.Zero;
    private IntPtr _previousBitmap = IntPtr.Zero;
    private Bitmap? _surface;
    private Graphics? _graphics;

    // Pens are built once and re-coloured per segment. Every segment of the trail is stroked
    // at its own alpha, so allocating them per segment would mean thousands of pens a second.
    private Pen? _corePen;
    private Pen? _innerHaloPen;
    private Pen? _outerHaloPen;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    /// <summary>How long a point stays fully opaque before it starts to disappear.</summary>
    private const double FadeHold = 0.35;

    /// <summary>How long the fade itself takes, once it starts.</summary>
    private const double FadeOut = 0.45;

    private const double PointLifetime = FadeHold + FadeOut;

    /// <summary>
    /// Samples closer together than this are dropped. Combined with the curve smoothing it
    /// takes the shake out of a hand-drawn loop without the line lagging behind the cursor.
    /// </summary>
    private const float MinimumSampleDistance = 4f;

    private const float StrokeWidth = 3f;

    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const int WS_POPUP          = unchecked((int)0x80000000);

    private const uint ULW_ALPHA        = 0x02;
    private const byte AC_SRC_OVER      = 0x00;
    private const byte AC_SRC_ALPHA     = 0x01;
    private const int SW_SHOWNOACTIVATE = 4;

    private const uint WM_DESTROY = 0x0002;
    private const uint WM_CLOSE   = 0x0010;

    private const string WindowClassName = "NayfRegionFocusOverlay";

    private static readonly object ClassRegistrationLock = new();
    private static NativeMethods.WndProc? _sharedWindowProcedure;
    private static bool _isClassRegistered;

    private RegionFocusOverlayWindow(Rectangle monitorBounds) => _monitorBounds = monitorBounds;

    /// <summary>
    /// Opens the overlay on the monitor the cursor is currently on, and starts recording.
    /// Returns null if there is no monitor to draw on, which leaves the caller nothing to
    /// tear down.
    /// </summary>
    public static RegionFocusOverlayWindow? Open()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return null;

        IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
        var info = new NativeMethods.MONITORINFOEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
        };
        if (!NativeMethods.GetMonitorInfo(monitor, ref info)) return null;

        var bounds = Rectangle.FromLTRB(
            info.rcMonitor.Left, info.rcMonitor.Top, info.rcMonitor.Right, info.rcMonitor.Bottom);

        var overlay = new RegionFocusOverlayWindow(bounds);
        overlay.Start();
        return overlay;
    }

    private void Start()
    {
        _messageThread = new Thread(RunWindowThread)
        {
            Name = "NayfRegionFocusOverlay",
            IsBackground = true
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    /// <summary>
    /// The box containing every point the user drew, in virtual-screen pixels. False when
    /// they held the key without drawing anything worth cropping — a bare hold, or a twitch.
    /// </summary>
    public bool TryGetDrawnBounds(out RectangleF bounds)
    {
        lock (_boundsLock)
        {
            bounds = RectangleF.FromLTRB(_minX, _minY, _maxX, _maxY);
            return _drawnPointCount >= 2 && !_mouseButtonWasPressed;
        }
    }

    private void RunWindowThread()
    {
        EnsureWindowClassRegistered();

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
            WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            WindowClassName, "NayfRegionFocus",
            WS_POPUP,
            _monitorBounds.X, _monitorBounds.Y, _monitorBounds.Width, _monitorBounds.Height,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Logger.Log("RegionFocus",
                $"CreateWindowEx failed for {_monitorBounds} (err {Marshal.GetLastWin32Error()})");
            return;
        }

        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        CreateDrawingSurface();

        // Paint the empty surface once so the window starts out fully transparent rather than
        // showing whatever was in the DIB when it was allocated.
        RenderFrame();

        _renderTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _renderGuard, 1) == 1) return;
            try { RenderFrame(); } finally { Interlocked.Exchange(ref _renderGuard, 0); }
        }, null, TimeSpan.Zero, FrameInterval);

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        _renderTimer?.Dispose();
        _renderTimer = null;
        // Let any frame already in flight finish before the surface it draws into is freed.
        while (Interlocked.CompareExchange(ref _renderGuard, 1, 0) == 1) Thread.Sleep(1);
        ReleaseDrawingSurface();
    }

    private static void EnsureWindowClassRegistered()
    {
        lock (ClassRegistrationLock)
        {
            if (_isClassRegistered) return;

            _sharedWindowProcedure = HandleWindowMessage;
            var wndClass = new NativeMethods.WNDCLASSEX
            {
                cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
                lpfnWndProc   = _sharedWindowProcedure,
                hInstance     = NativeMethods.GetModuleHandle(null),
                lpszClassName = WindowClassName
            };
            NativeMethods.RegisterClassEx(ref wndClass);
            _isClassRegistered = true;
        }
    }

    /// <summary>
    /// Teardown lands here because <c>DestroyWindow</c> and <c>PostQuitMessage</c> both have
    /// to run on the thread that owns the window. <see cref="Dispose"/> posts WM_CLOSE from
    /// the UI thread; this is where it arrives.
    /// </summary>
    private static IntPtr HandleWindowMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            case WM_CLOSE:
                NativeMethods.DestroyWindow(hWnd);
                return IntPtr.Zero;

            case WM_DESTROY:
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }

        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // ── Render loop ──────────────────────────────────────────────────────────

    /// <summary>
    /// Sampling and fading share one loop. On the Mac they are separate — points arrive on
    /// mouse-moved events and a 30fps timer ages them out — but with the cursor polled rather
    /// than tracked there is one clock, and it may as well be the one already drawing frames.
    /// </summary>
    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero || _graphics == null) return;

        // A click means the Shift was modifying it, not opening a lasso. The trail is wiped on
        // the spot so it stops following a drag the user is trying to watch; the crop itself is
        // abandoned later, when the hold ends. Nothing more is drawn after this — the window
        // stays up, empty and click-through, until the hold ends and it comes down.
        if (_mouseButtonWasPressed) return;

        if (GlobalPushToTalkMonitor.IsAnyMouseButtonDown())
        {
            _mouseButtonWasPressed = true;
            _trail.Clear();
            _graphics.Clear(Color.Transparent);
            ApplyLayeredWindow();
            return;
        }

        SampleCursor();
        PruneFadedPoints();

        _graphics.Clear(Color.Transparent);
        _graphics.TranslateTransform(-_monitorBounds.X, -_monitorBounds.Y);
        DrawTrail(_graphics);
        _graphics.ResetTransform();

        ApplyLayeredWindow();
    }

    private void SampleCursor()
    {
        if (!NativeMethods.GetCursorPos(out var cursor)) return;
        var point = new PointF(cursor.X, cursor.Y);

        if (_lastSample is { } last &&
            MathF.Abs(point.X - last.X) < MinimumSampleDistance &&
            MathF.Abs(point.Y - last.Y) < MinimumSampleDistance)
        {
            return;
        }

        _lastSample = point;
        _trail.Add(new TrailPoint(point, _clock.Elapsed.TotalSeconds));

        lock (_boundsLock)
        {
            if (_drawnPointCount == 0)
            {
                _minX = _maxX = point.X;
                _minY = _maxY = point.Y;
            }
            else
            {
                _minX = MathF.Min(_minX, point.X);
                _minY = MathF.Min(_minY, point.Y);
                _maxX = MathF.Max(_maxX, point.X);
                _maxY = MathF.Max(_maxY, point.Y);
            }
            _drawnPointCount++;
        }
    }

    private void PruneFadedPoints()
    {
        double now = _clock.Elapsed.TotalSeconds;
        // The list is in time order, so everything expired is at the front and one scan from
        // the start finds all of it.
        int expired = 0;
        while (expired < _trail.Count && now - _trail[expired].Birth >= PointLifetime) expired++;
        if (expired > 0) _trail.RemoveRange(0, expired);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    private void DrawTrail(Graphics g)
    {
        if (_trail.Count < 2 || _corePen == null || _innerHaloPen == null || _outerHaloPen == null)
            return;

        Color accent = NativeOverlayWindow.CursorBlue;
        double now = _clock.Elapsed.TotalSeconds;

        for (int index = 1; index < _trail.Count; index++)
        {
            float alpha = FadeAlpha(now - _trail[index].Birth);
            if (alpha <= 0.01f) continue;

            var (start, control1, control2, end) = SmoothedSegment(index);

            // GDI+ has no shadow, so the Mac's blur is faked the way the rest of the app fakes
            // it — the same curve stroked wider and fainter underneath, dimming with the line.
            _outerHaloPen.Color = Color.FromArgb((int)(30 * alpha), accent);
            g.DrawBezier(_outerHaloPen, start, control1, control2, end);

            _innerHaloPen.Color = Color.FromArgb((int)(30 * alpha), accent);
            g.DrawBezier(_innerHaloPen, start, control1, control2, end);

            _corePen.Color = Color.FromArgb((int)(255 * alpha), accent);
            g.DrawBezier(_corePen, start, control1, control2, end);
        }
    }

    /// <summary>
    /// Opaque for the first <see cref="FadeHold"/> of a point's life, then a straight fade to
    /// nothing across <see cref="FadeOut"/>.
    /// </summary>
    private static float FadeAlpha(double age)
    {
        if (age <= FadeHold) return 1f;
        return Math.Max(0f, 1f - (float)((age - FadeHold) / FadeOut));
    }

    /// <summary>
    /// One smoothed piece of the trail: the part centred on point <paramref name="index"/>,
    /// running from the midpoint of the pair before it to the midpoint of the pair after,
    /// bending through the point itself. That is the quadratic-bézier midpoint trick, written
    /// as the equivalent cubic because GDI+ draws cubics.
    ///
    /// Cut into per-point pieces rather than drawn as one path so each piece can carry its own
    /// alpha — which is what makes the tail fade along its length instead of all at once —
    /// while the joins at the midpoints keep it reading as a single flowing curve.
    /// </summary>
    private (PointF start, PointF control1, PointF control2, PointF end) SmoothedSegment(int index)
    {
        int count = _trail.Count;
        PointF previous = _trail[index - 1].Position;
        PointF current = _trail[index].Position;

        PointF start = index == 1 ? previous : Midpoint(previous, current);
        PointF end = index == count - 1
            ? current
            : Midpoint(current, _trail[index + 1].Position);

        var control1 = new PointF(
            start.X + 2f / 3f * (current.X - start.X),
            start.Y + 2f / 3f * (current.Y - start.Y));
        var control2 = new PointF(
            end.X + 2f / 3f * (current.X - end.X),
            end.Y + 2f / 3f * (current.Y - end.Y));

        return (start, control1, control2, end);
    }

    private static PointF Midpoint(PointF a, PointF b) =>
        new((a.X + b.X) / 2f, (a.Y + b.Y) / 2f);

    // ── Layered window surface ───────────────────────────────────────────────

    private void CreateDrawingSurface()
    {
        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            var header = new BITMAPINFOHEADER
            {
                biSize = (uint)Marshal.SizeOf<BITMAPINFOHEADER>(),
                biWidth = _monitorBounds.Width,
                // Negative height = top-down rows, the order GDI+ writes in. A bottom-up DIB
                // would come out mirrored.
                biHeight = -_monitorBounds.Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            _dibSection = CreateDIBSection(screenDC, ref header, 0 /* DIB_RGB_COLORS */,
                out IntPtr pixels, IntPtr.Zero, 0);
            if (_dibSection == IntPtr.Zero || pixels == IntPtr.Zero)
            {
                Logger.Log("RegionFocus", $"CreateDIBSection failed for {_monitorBounds}");
                return;
            }

            _memoryDC = CreateCompatibleDC(screenDC);
            _previousBitmap = SelectObject(_memoryDC, _dibSection);

            // Premultiplied, because that is what UpdateLayeredWindow's AC_SRC_ALPHA expects.
            _surface = new Bitmap(_monitorBounds.Width, _monitorBounds.Height,
                _monitorBounds.Width * 4, PixelFormat.Format32bppPArgb, pixels);
            _graphics = Graphics.FromImage(_surface);
            _graphics.SmoothingMode = SmoothingMode.AntiAlias;

            _corePen = CreateStrokePen(StrokeWidth);
            _innerHaloPen = CreateStrokePen(StrokeWidth + 3.5f);
            _outerHaloPen = CreateStrokePen(StrokeWidth + 7f);
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    private static Pen CreateStrokePen(float width) => new(Color.Transparent, width)
    {
        StartCap = LineCap.Round,
        EndCap = LineCap.Round,
        LineJoin = LineJoin.Round
    };

    private void ReleaseDrawingSurface()
    {
        _corePen?.Dispose();       _corePen = null;
        _innerHaloPen?.Dispose();  _innerHaloPen = null;
        _outerHaloPen?.Dispose();  _outerHaloPen = null;

        _graphics?.Dispose();
        _graphics = null;
        _surface?.Dispose();
        _surface = null;

        if (_memoryDC != IntPtr.Zero)
        {
            if (_previousBitmap != IntPtr.Zero) SelectObject(_memoryDC, _previousBitmap);
            DeleteDC(_memoryDC);
            _memoryDC = IntPtr.Zero;
            _previousBitmap = IntPtr.Zero;
        }

        if (_dibSection != IntPtr.Zero)
        {
            DeleteObject(_dibSection);
            _dibSection = IntPtr.Zero;
        }
    }

    private void ApplyLayeredWindow()
    {
        if (_hwnd == IntPtr.Zero || _memoryDC == IntPtr.Zero) return;

        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);

        var size = new SIZE { cx = _monitorBounds.Width, cy = _monitorBounds.Height };
        var sourcePoint = new PT { x = 0, y = 0 };
        var destinationPoint = new PT { x = _monitorBounds.X, y = _monitorBounds.Y };
        var blend = new BLEND
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA
        };

        GdiFlush();
        UpdateLayeredWindow(_hwnd, screenDC, ref destinationPoint, ref size,
                            _memoryDC, ref sourcePoint, 0, ref blend, ULW_ALPHA);

        NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
    }

    /// <summary>
    /// Takes the overlay off the screen. Blocks until the window is really gone, because the
    /// caller's next move is to photograph the pixels underneath it — a capture that races
    /// teardown gets the trail in the crop.
    /// </summary>
    public void Dispose()
    {
        IntPtr hwnd = _hwnd;
        // Cleared first so a frame already in flight stops drawing into a window that is about
        // to be destroyed.
        _hwnd = IntPtr.Zero;

        if (hwnd != IntPtr.Zero) PostMessage(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
        _messageThread?.Join(TimeSpan.FromSeconds(1));
    }

    // ── Interop ──────────────────────────────────────────────────────────────

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref PT pptDst, ref SIZE psize, IntPtr hdcSrc, ref PT pptSrc,
        uint crKey, ref BLEND pblend, uint dwFlags);

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);
    [DllImport("user32.dll")] private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool GdiFlush();

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateDIBSection(IntPtr hdc, ref BITMAPINFOHEADER pbmi,
        uint usage, out IntPtr ppvBits, IntPtr hSection, uint offset);

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

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
