using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// A full-monitor transparent layered window that draws Vayme's screen annotations — the
/// outlines and arrows it puts over the desktop while explaining something.
///
/// <para>This is a second overlay, alongside <see cref="NativeOverlayWindow"/>. That one is a
/// 520×300 bitmap that chases the cursor, which is right for the cursor and impossible for an
/// outline around an arbitrary control or an arrow spanning half the screen. So annotations get
/// their own surface: one window per monitor, fixed to that monitor's bounds, never moved.</para>
///
/// <para><b>Click-through.</b> <c>WS_EX_TRANSPARENT</c> is not optional here. The whole point of
/// an annotation is that the user acts on the thing under it, so every click has to pass
/// straight through the mark to the app beneath.</para>
///
/// <para><b>Idle cost.</b> Annotations animate for under a second and then hold, sometimes for
/// minutes. A layered window keeps showing its last frame until it is given a new one, so once
/// every mark has finished tracing the render timer stops entirely and the picture just stays
/// on screen. Nothing redraws again until the list changes.</para>
/// </summary>
public sealed class AnnotationOverlayWindow : IDisposable
{
    private readonly IScreenAnnotationSource _annotationSource;

    /// <summary>This window's monitor, in virtual screen coordinates.</summary>
    private readonly Rectangle _monitorBounds;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;

    // ── Drawing surface, allocated once ──────────────────────────────────────
    // A monitor-sized bitmap is far too big to allocate per frame (a 4K surface is 33 MB),
    // so the pixels live in a DIB section that GDI+ draws into directly and
    // UpdateLayeredWindow reads straight out of — no copy anywhere in the frame path.
    private IntPtr _dibSection = IntPtr.Zero;
    private IntPtr _memoryDC = IntPtr.Zero;
    private IntPtr _previousBitmap = IntPtr.Zero;
    private Bitmap? _surface;
    private Graphics? _graphics;

    private System.Threading.Timer? _renderTimer;
    private System.Threading.Timer? _topmostTimer;
    private int _renderGuard; // prevents overlapping render ticks (GDI+ isn't reentrant)

    /// <summary>Bumped every time a new annotation list is published. Compared before the
    /// render loop is allowed to stop, so a set that lands mid-frame can't be missed.</summary>
    private long _publishedGeneration;

    private static readonly TimeSpan FrameInterval = TimeSpan.FromMilliseconds(16);

    // ── Style ────────────────────────────────────────────────────────────────

    /// <summary>Outline stroke, matching the Mac's 2.5pt dashed trace.</summary>
    private const float OutlineStrokeWidth = 2.5f;

    /// <summary>Arrows are drawn a little heavier than outlines, and solid.</summary>
    private const float ArrowStrokeWidth = 3f;

    /// <summary>Dash pattern in pixels — 9 on, 6 off, as on the Mac.</summary>
    private const float DashOnLength = 9f;
    private const float DashOffLength = 6f;

    private const float ArrowHeadLength = 15f;
    private const double ArrowHeadSpread = Math.PI / 7;

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

    private const string WindowClassName = "NayfAnnotationOverlay";

    // The class is process-wide, so every monitor's window shares one registration — and one
    // delegate, which must stay referenced for as long as any window can receive a message.
    private static readonly object ClassRegistrationLock = new();
    private static NativeMethods.WndProc? _sharedWindowProcedure;
    private static bool _isClassRegistered;

    public AnnotationOverlayWindow(IScreenAnnotationSource annotationSource, Rectangle monitorBounds)
    {
        _annotationSource = annotationSource;
        _monitorBounds = monitorBounds;
    }

    public void Start()
    {
        _annotationSource.ScreenAnnotationsChanged += HandleAnnotationsChanged;

        _messageThread = new Thread(RunWindowThread)
        {
            Name = $"NayfAnnotationOverlay {_monitorBounds.X},{_monitorBounds.Y}",
            IsBackground = true
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    private void RunWindowThread()
    {
        EnsureWindowClassRegistered();

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
            WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            WindowClassName, "NayfAnnotations",
            WS_POPUP,
            _monitorBounds.X, _monitorBounds.Y, _monitorBounds.Width, _monitorBounds.Height,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero)
        {
            Logger.Log("Annotations",
                $"CreateWindowEx failed for monitor {_monitorBounds} (err {Marshal.GetLastWin32Error()})");
            return;
        }

        ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
        AssertTopmost();

        CreateDrawingSurface();

        // Paint the empty surface once so the window starts out fully transparent rather than
        // showing whatever was in the DIB when it was allocated.
        RenderFrame();

        // Starts stopped. Frames are only spent while something is actually animating.
        _renderTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _renderGuard, 1) == 1) return;
            try { RenderFrame(); } finally { Interlocked.Exchange(ref _renderGuard, 0); }
        }, null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);

        if (_annotationSource.ScreenAnnotations.Count > 0) ResumeRendering();

        _topmostTimer = new System.Threading.Timer(_ => AssertTopmost(), null,
            TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        // The window is gone; stop the timers and let any frame already in flight finish
        // before the surface it is drawing into is freed.
        _renderTimer?.Dispose();
        _renderTimer = null;
        _topmostTimer?.Dispose();
        _topmostTimer = null;
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
    /// Teardown runs here rather than in <see cref="Dispose"/> because both
    /// <c>DestroyWindow</c> and <c>PostQuitMessage</c> have to be called on the thread that
    /// owns the window, and Dispose is called from the UI thread. Dispose posts
    /// <c>WM_CLOSE</c>; this is where it lands.
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

    private void AssertTopmost()
    {
        if (_hwnd == IntPtr.Zero) return;
        NativeMethods.SetWindowPos(_hwnd, OverlayZOrder.InsertAfter(), 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    // ── Render loop ──────────────────────────────────────────────────────────

    private void HandleAnnotationsChanged()
    {
        // The increment must happen before the resume — see PauseRendering.
        Interlocked.Increment(ref _publishedGeneration);
        ResumeRendering();
    }

    private void ResumeRendering() => _renderTimer?.Change(TimeSpan.Zero, FrameInterval);

    /// <summary>
    /// Stops the render loop. <paramref name="renderedGeneration"/> is the list version the
    /// frame that decided to stop was drawing; if a newer one was published while the timer was
    /// being stopped, its own resume ran before the stop and would otherwise be swallowed, so
    /// the loop is started again here.
    /// </summary>
    private void PauseRendering(long renderedGeneration)
    {
        _renderTimer?.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        if (Interlocked.Read(ref _publishedGeneration) != renderedGeneration) ResumeRendering();
    }

    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero || _graphics == null) return;

        long renderedGeneration = Interlocked.Read(ref _publishedGeneration);
        var annotations = _annotationSource.ScreenAnnotations;

        _graphics.Clear(Color.Transparent);

        // Annotations are in virtual screen coordinates; this shifts them into this monitor's
        // surface, so one list can be handed to every window and each draws its own share.
        // A mark for another monitor is dropped by DrawAnnotation; one that straddles the
        // seam is drawn by both windows and GDI+ clips each to its own half.
        _graphics.TranslateTransform(-_monitorBounds.X, -_monitorBounds.Y);

        bool anythingStillAnimating = false;
        foreach (var annotation in annotations)
        {
            if (!annotation.IsAnimationComplete) anythingStillAnimating = true;
            DrawAnnotation(_graphics, annotation);
        }

        _graphics.ResetTransform();
        ApplyLayeredWindow();

        if (!anythingStillAnimating) PauseRendering(renderedGeneration);
    }

    // ── Drawing ──────────────────────────────────────────────────────────────

    private void DrawAnnotation(Graphics g, ScreenAnnotation annotation)
    {
        // Each overlay draws its own monitor's share, and for the outline the clip in
        // RenderFrame is enough on its own. The caption is not clipped — it is clamped to
        // stay on screen — so a mark on another monitor would have its chip dragged back
        // into view here and the user would see the same caption on every screen.
        if (!BelongsToThisMonitor(annotation)) return;

        double traceProgress = annotation.TraceProgress;

        // Nothing at all until this annotation's beat starts — otherwise a delayed mark would
        // sit on screen fully drawn while it waits for its turn to animate.
        if (traceProgress <= 0) return;

        if (annotation.Kind == AnnotationKind.Arrow) DrawArrow(g, annotation, traceProgress);
        else DrawOutline(g, annotation, traceProgress);

        DrawLabel(g, annotation);
    }

    private static void DrawOutline(Graphics g, ScreenAnnotation annotation, double traceProgress)
    {
        using var outline = ScreenAnnotation.OutlinePath(annotation.Bounds, annotation.Kind);
        if (outline.PointCount < 2) return;

        using var traced = ScreenAnnotation.PartialPath(outline, traceProgress);
        if (traced.PointCount < 2) return;

        StrokeWithGlow(g, traced, OutlineStrokeWidth, isDashed: true);
    }

    private void DrawArrow(Graphics g, ScreenAnnotation annotation, double traceProgress)
    {
        PointF end = annotation.ArrowEnd;
        PointF start = annotation.ArrowStart ?? ScreenAnnotation.ArrowTailFor(end, _monitorBounds);

        using var arrow = BuildArrowPath(start, end);
        if (arrow.PointCount < 2) return;

        using var traced = ScreenAnnotation.PartialPath(arrow, traceProgress);
        if (traced.PointCount < 2) return;

        StrokeWithGlow(g, traced, ArrowStrokeWidth, isDashed: false);
    }

    /// <summary>
    /// The shaft followed by the two head strokes, as three separate figures. The order
    /// matters: <see cref="ScreenAnnotation.PartialPath"/> walks figures in sequence, so the
    /// shaft draws itself first and the head snaps on at the end, as it does on the Mac.
    /// </summary>
    private static GraphicsPath BuildArrowPath(PointF start, PointF end)
    {
        var path = new GraphicsPath();
        path.StartFigure();
        path.AddLine(start, end);

        double angle = Math.Atan2(end.Y - start.Y, end.X - start.X);

        var leftBarb = new PointF(
            (float)(end.X - ArrowHeadLength * Math.Cos(angle - ArrowHeadSpread)),
            (float)(end.Y - ArrowHeadLength * Math.Sin(angle - ArrowHeadSpread)));
        var rightBarb = new PointF(
            (float)(end.X - ArrowHeadLength * Math.Cos(angle + ArrowHeadSpread)),
            (float)(end.Y - ArrowHeadLength * Math.Sin(angle + ArrowHeadSpread)));

        path.StartFigure();
        path.AddLine(end, leftBarb);
        path.StartFigure();
        path.AddLine(end, rightBarb);
        return path;
    }

    /// <summary>
    /// Strokes a path in the cursor colour with a soft halo behind it. GDI+ has no shadow, so
    /// the Mac's blue glow is faked the way the cursor overlay's bubble halo already is — the
    /// same path stroked wider and fainter underneath.
    /// </summary>
    private static void StrokeWithGlow(Graphics g, GraphicsPath path, float strokeWidth, bool isDashed)
    {
        Color accent = NativeOverlayWindow.CursorBlue;

        for (int haloPass = 2; haloPass >= 1; haloPass--)
        {
            float haloWidth = strokeWidth + haloPass * 3.5f;
            using var haloPen = CreateStrokePen(Color.FromArgb(30, accent), haloWidth, isDashed);
            g.DrawPath(haloPen, path);
        }

        using var pen = CreateStrokePen(accent, strokeWidth, isDashed);
        g.DrawPath(pen, path);
    }

    private static Pen CreateStrokePen(Color color, float width, bool isDashed)
    {
        var pen = new Pen(color, width)
        {
            StartCap = LineCap.Round,
            EndCap = LineCap.Round,
            LineJoin = LineJoin.Round
        };

        if (isDashed)
        {
            // GDI+ dash lengths are multiples of the pen width, so the pixel lengths are
            // divided back out — otherwise a halo pass would get longer dashes than the
            // stroke it sits behind and the two would drift out of phase.
            pen.DashPattern = new[] { DashOnLength / width, DashOffLength / width };
            pen.DashCap = DashCap.Round;
        }

        return pen;
    }

    /// <summary>
    /// The caption chip: white text on a filled capsule, sitting just outside the mark it
    /// belongs to. Fades in on its own curve once the outline has mostly traced on.
    /// </summary>
    private void DrawLabel(Graphics g, ScreenAnnotation annotation)
    {
        if (string.IsNullOrWhiteSpace(annotation.Label)) return;

        double opacity = annotation.LabelOpacity;
        if (opacity <= 0.01) return;

        // A mark straddling two monitors is drawn by both, each clipping its own half. Only
        // the one holding its centre captions it — otherwise the clamp below would place a
        // chip on each side of the seam.
        if (!((RectangleF)_monitorBounds).Contains(LabelAnchorFor(annotation))) return;

        using var font = new Font("Segoe UI", 12f, FontStyle.Bold, GraphicsUnit.Pixel);
        SizeF textSize = g.MeasureString(annotation.Label, font);
        float chipWidth = textSize.Width + 18;
        float chipHeight = textSize.Height + 8;

        PointF chipCenter = LabelCenterFor(annotation, chipHeight);

        // Keep the whole chip on this monitor so a mark near an edge doesn't get its caption
        // clipped in half.
        float halfWidth = chipWidth / 2;
        float clampedX = Math.Clamp(chipCenter.X,
            _monitorBounds.Left + halfWidth + 8, Math.Max(_monitorBounds.Right - halfWidth - 8,
                _monitorBounds.Left + halfWidth + 8));
        float clampedY = Math.Clamp(chipCenter.Y,
            _monitorBounds.Top + chipHeight, Math.Max(_monitorBounds.Bottom - chipHeight,
                _monitorBounds.Top + chipHeight));

        float chipLeft = clampedX - halfWidth;
        float chipTop = clampedY - chipHeight / 2;
        float cornerRadius = chipHeight / 2;

        int alpha = (int)Math.Round(255 * opacity);

        using var shadowBrush = new SolidBrush(Color.FromArgb((int)(alpha * 0.25), Color.Black));
        g.FillRoundedRect(shadowBrush, chipLeft, chipTop + 1.5f, chipWidth, chipHeight, cornerRadius);

        using var chipBrush = new SolidBrush(Color.FromArgb(alpha, NativeOverlayWindow.CursorBlue));
        g.FillRoundedRect(chipBrush, chipLeft, chipTop, chipWidth, chipHeight, cornerRadius);

        using var textBrush = new SolidBrush(Color.FromArgb(alpha, Color.White));
        g.DrawString(annotation.Label, font, textBrush, chipLeft + 9, chipTop + 4);
    }

    /// <summary>
    /// Whether any part of a mark falls on this overlay's monitor. Inflated a little because
    /// the glow is stroked outside the path, so a mark just off the edge still shows here.
    /// </summary>
    private bool BelongsToThisMonitor(ScreenAnnotation annotation)
    {
        var extent = ExtentOf(annotation);
        extent.Inflate(24f, 24f);
        return ((RectangleF)_monitorBounds).IntersectsWith(extent);
    }

    /// <summary>
    /// The rectangle a mark occupies: its bounds, or for an arrow the box its two ends span.
    /// An arrow with no explicit tail is measured from its head alone — the tail it gets
    /// drawn with is placed relative to a monitor, which is the question being asked here.
    /// </summary>
    private static RectangleF ExtentOf(ScreenAnnotation annotation)
    {
        if (annotation.Kind != AnnotationKind.Arrow) return annotation.Bounds;

        PointF end = annotation.ArrowEnd;
        PointF start = annotation.ArrowStart ?? end;
        return RectangleF.FromLTRB(
            Math.Min(start.X, end.X), Math.Min(start.Y, end.Y),
            Math.Max(start.X, end.X), Math.Max(start.Y, end.Y));
    }

    /// <summary>The one point that decides which monitor owns a mark's caption.</summary>
    private static PointF LabelAnchorFor(ScreenAnnotation annotation)
        => annotation.Kind == AnnotationKind.Arrow
            ? annotation.ArrowStart ?? annotation.ArrowEnd
            : new PointF(annotation.Bounds.Left + annotation.Bounds.Width / 2,
                         annotation.Bounds.Top + annotation.Bounds.Height / 2);

    /// <summary>
    /// Where a caption sits: centred above an outline, or beside an arrow's tail. Flipped to
    /// the other side of an outline when there is no room above it.
    /// </summary>
    private PointF LabelCenterFor(ScreenAnnotation annotation, float chipHeight)
    {
        if (annotation.Kind == AnnotationKind.Arrow)
        {
            PointF tail = annotation.ArrowStart
                ?? ScreenAnnotation.ArrowTailFor(annotation.ArrowEnd, _monitorBounds);
            return new PointF(tail.X, tail.Y - 18);
        }

        RectangleF bounds = annotation.Bounds;
        float centerX = bounds.Left + bounds.Width / 2;
        float aboveY = bounds.Top - 16;

        bool hasRoomAbove = aboveY - chipHeight / 2 >= _monitorBounds.Top + 4;
        return new PointF(centerX, hasRoomAbove ? aboveY : bounds.Bottom + 16);
    }

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
                // Negative height = top-down rows, which is the order GDI+ writes in. A
                // bottom-up DIB would come out mirrored.
                biHeight = -_monitorBounds.Height,
                biPlanes = 1,
                biBitCount = 32,
                biCompression = 0 // BI_RGB
            };

            _dibSection = CreateDIBSection(screenDC, ref header, 0 /* DIB_RGB_COLORS */,
                out IntPtr pixels, IntPtr.Zero, 0);
            if (_dibSection == IntPtr.Zero || pixels == IntPtr.Zero)
            {
                Logger.Log("Annotations", $"CreateDIBSection failed for monitor {_monitorBounds}");
                return;
            }

            _memoryDC = CreateCompatibleDC(screenDC);
            _previousBitmap = SelectObject(_memoryDC, _dibSection);

            // Premultiplied, because that is what UpdateLayeredWindow's AC_SRC_ALPHA expects.
            // Straight alpha would survive the fully-opaque and fully-transparent pixels and
            // wash out everything in between — which here is every antialiased edge and the
            // whole glow.
            _surface = new Bitmap(_monitorBounds.Width, _monitorBounds.Height,
                _monitorBounds.Width * 4, PixelFormat.Format32bppPArgb, pixels);
            _graphics = Graphics.FromImage(_surface);
            _graphics.SmoothingMode = SmoothingMode.AntiAlias;
            // Not ClearType: subpixel antialiasing has no alpha to give a layered window, and
            // renders as opaque grey fringing on a transparent surface.
            _graphics.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
        }
    }

    private void ReleaseDrawingSurface()
    {
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
        // Constant — the window never leaves its monitor, so this repeats the same position
        // rather than repositioning anything.
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

    public void Dispose()
    {
        _annotationSource.ScreenAnnotationsChanged -= HandleAnnotationsChanged;

        IntPtr hwnd = _hwnd;
        // Cleared first so a frame already in flight stops drawing into a window that is
        // about to be destroyed.
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
