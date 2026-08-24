using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// The floating chip an agent task collapses to while it waits to be read again. Port of
/// the Mac's collapsed avatar in AgentResultPanel.swift, drawn by
/// <see cref="NayfAgentAvatarArt"/>.
///
/// <para>A layered, per-pixel-alpha window rather than a WinUI one, for the same reason
/// the status pill and the action toast are: the chip's glow spills into clear space
/// around it and it drifts up and down inside its own frame. A WinUI window paints its
/// whole rectangle with an acrylic backdrop, which turns all of that clear space into a
/// grey slab and the chip into a tile stuck on top of it.</para>
///
/// <para>Unlike those two, this window is NOT click-through: the whole point of the chip
/// is that it can be reached. Layered windows hit-test on alpha, so the clear margin
/// passes clicks to whatever is behind it without any of that being asked for.</para>
/// </summary>
public sealed class NativeAgentAvatarWindow : IDisposable
{
    /// <summary>Raised on the window's own thread when the chip is clicked.</summary>
    public event Action? Clicked;

    private readonly object _gate = new();
    private readonly float _phase;

    /// <summary>
    /// One window class per chip. A shared name would look tidier, but RegisterClassEx
    /// fails for the second chip and CreateWindowEx then quietly hands it the FIRST
    /// chip's WndProc — every click on the newer chip would open the older one's card,
    /// and dismissing that one would leave the survivor calling into a collected delegate.
    /// </summary>
    private readonly string _className = "NayfAgentAvatar_" + Guid.NewGuid().ToString("N");

    // Guarded by _gate: written from the UI thread, read by the render thread.
    private bool _visible;
    private int _offsetY;
    private Color _accent = Color.FromArgb(255, 51, 143, 255);
    private NativeMethods.RECT _workArea;
    private float _scale = 1f;
    private bool _placementDirty = true;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private NativeMethods.WndProc? _wndProcDelegate;
    private System.Threading.Timer? _renderTimer;
    private int _renderGuard; // GDI+ isn't reentrant; a long frame must not fire twice

    private readonly System.Diagnostics.Stopwatch _clock = System.Diagnostics.Stopwatch.StartNew();

    // Render-thread only.
    private Bitmap? _sprite;
    private Color _spriteAccent;
    private float _spriteScale;

    // Built once per window rather than per frame — but NOT shared between windows. GDI+
    // objects throw "object is currently in use elsewhere" if two threads touch one at the
    // same time, and every chip renders on its own thread.
    private readonly ImageAttributes _spriteAttributes = CreateSpriteAttributes();

    private static ImageAttributes CreateSpriteAttributes()
    {
        var attrs = new ImageAttributes();
        attrs.SetWrapMode(WrapMode.TileFlipXY);
        return attrs;
    }
    private int _canvasPx;
    private int _windowX, _windowY;
    private bool _isWindowShown;
    private bool _disposed;

    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const int WS_POPUP          = unchecked((int)0x80000000);

    private const int SW_HIDE           = 0;
    private const int SW_SHOWNOACTIVATE = 4;

    private const uint WM_LBUTTONUP     = 0x0202;
    private const uint WM_MOUSEACTIVATE = 0x0021;
    private const int  MA_NOACTIVATE    = 3;

    /// <summary>Asks the window's own thread to tear itself down — see <see cref="Dispose"/>.</summary>
    private const uint WM_APP_TEARDOWN  = 0x8000 + 0x21;

    /// <summary>
    /// A stable phase from the task id, so two stacked chips drift out of step with each
    /// other instead of pulsing together like a pair of indicator lights.
    /// </summary>
    public NativeAgentAvatarWindow(Guid taskId)
    {
        var bytes = taskId.ToByteArray();
        _phase = (bytes[0] + bytes[8]) / 510f * 2f * MathF.PI;

        _messageThread = new Thread(RunWindowThread)
        {
            IsBackground = true,
            Name = "NayfAgentAvatar"
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    // MARK: - Presentation

    /// <summary>
    /// Puts the chip on screen in the task's colour, <paramref name="offsetY"/> points down
    /// the stack.
    ///
    /// <para>The work area and DPI scale are handed in rather than looked up here, so the
    /// chip lands on exactly the screen the opened card came from. Working them out
    /// independently would mean deciding again, on every frame, which monitor to use — and
    /// the chip would then walk between monitors with the cursor while the card it belongs
    /// to stayed where it was put.</para>
    /// </summary>
    public void Show(Color accent, int offsetY, NativeMethods.RECT workArea, double scale)
    {
        lock (_gate)
        {
            _accent = accent;
            _offsetY = offsetY;
            _workArea = workArea;
            _scale = scale > 0 ? (float)scale : 1f;
            _visible = true;
            _placementDirty = true;
        }
    }

    public void MoveToOffset(int offsetY)
    {
        lock (_gate)
        {
            _offsetY = offsetY;
            _placementDirty = true;
        }
    }

    public void Hide()
    {
        lock (_gate) _visible = false;
    }

    /// <summary>
    /// Whether the cursor is on the chip. Deliberately not the whole window: most of the
    /// window is the clear margin the glow needs, and treating that as the chip would open
    /// the card from an inch away from anything the user can see. Padded by the float's
    /// travel so the target doesn't slide out from under a still cursor.
    /// </summary>
    public bool ContainsCursor()
    {
        lock (_gate) { if (!_visible) return false; }
        if (_hwnd == IntPtr.Zero) return false;
        if (!NativeMethods.GetCursorPos(out var pt)) return false;
        if (!NativeMethods.GetWindowRect(_hwnd, out var rect)) return false;

        float scale = _scale;
        float half = (NayfAgentAvatarArt.TileSize / 2f + NayfAgentAvatarArt.BobAmplitude) * scale;
        float cx = (rect.Left + rect.Right) / 2f;
        float cy = (rect.Top + rect.Bottom) / 2f;

        return pt.X >= cx - half && pt.X <= cx + half &&
               pt.Y >= cy - half && pt.Y <= cy + half;
    }

    // MARK: - Window

    private void RunWindowThread()
    {
        _wndProcDelegate = WndProc;
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc   = _wndProcDelegate,
            hInstance     = NativeMethods.GetModuleHandle(null),
            lpszClassName = _className
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            _className, "Vayme Agent",
            WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        _renderTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _renderGuard, 1) == 1) return;
            try { RenderFrame(); }
            catch (Exception ex) { Logger.Log("AgentAvatar", $"frame failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _renderGuard, 0); }
        }, null, TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16));

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }

        // Only once the loop is done and the window with it, or the atom stays claimed.
        UnregisterClass(_className, NativeMethods.GetModuleHandle(null));
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        switch (msg)
        {
            // Clicking the chip opens its card; it must not also pull the foreground off
            // whatever the user was typing into, which is the whole reason the window is
            // WS_EX_NOACTIVATE in the first place.
            case WM_MOUSEACTIVATE:
                return MA_NOACTIVATE;

            case WM_LBUTTONUP:
                Clicked?.Invoke();
                return IntPtr.Zero;

            case WM_APP_TEARDOWN:
                NativeMethods.DestroyWindow(hWnd);
                NativeMethods.PostQuitMessage(0);
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // MARK: - Frame

    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero || _disposed) return;

        bool visible;
        int offsetY;
        Color accent;
        NativeMethods.RECT workArea;
        float scale;
        bool placementDirty;
        lock (_gate)
        {
            visible = _visible;
            offsetY = _offsetY;
            accent = _accent;
            workArea = _workArea;
            scale = _scale;
            placementDirty = _placementDirty;
            _placementDirty = false;
        }

        if (!visible)
        {
            if (_isWindowShown)
            {
                ShowWindow(_hwnd, SW_HIDE);
                _isWindowShown = false;
            }
            return;
        }

        if (placementDirty || _canvasPx == 0) Place(offsetY, workArea, scale);

        if (_sprite == null || _spriteAccent != accent || Math.Abs(_spriteScale - scale) > 0.001f)
        {
            _sprite?.Dispose();
            _sprite = NayfAgentAvatarArt.RenderSprite(scale, accent);
            _spriteAccent = accent;
            _spriteScale = scale;
        }

        // Pure vertical drift, the chip's design left alone — the Mac's `bob`.
        float seconds = (float)_clock.Elapsed.TotalSeconds;
        float bob = MathF.Sin(seconds * NayfAgentAvatarArt.BobRadiansPerSecond + _phase)
                    * NayfAgentAvatarArt.BobAmplitude * scale;

        using var canvas = new Bitmap(_canvasPx, _canvasPx, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(canvas))
        {
            g.Clear(Color.Transparent);
            float inset = (_canvasPx - _sprite.Width) / 2f;
            DrawSprite(g, inset, inset + bob);
        }

        Premultiply(canvas);
        ApplyLayeredWindow(canvas, _windowX, _windowY);

        if (!_isWindowShown)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _isWindowShown = true;
        }

        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
    }

    /// <summary>
    /// Blits the sprite at a fractional offset. Rounding the bob to whole pixels gave the
    /// chip only seven positions across its 3.9s cycle — it moved 3.3 times a second, which
    /// reads as a stutter however fast the frames arrive. Bilinear at PixelOffsetMode.Half
    /// is lossless at whole offsets (the rim's peak stays put), and TileFlipXY stops GDI+
    /// sampling past the source rect and fringing the canvas edge.
    /// </summary>
    private void DrawSprite(Graphics g, float x, float y)
    {
        g.InterpolationMode = InterpolationMode.Bilinear;
        g.PixelOffsetMode = PixelOffsetMode.Half;

        // The ImageAttributes overload has no float-rectangle form; the destination has to
        // go in as a parallelogram — upper-left, upper-right, lower-left.
        float w = _sprite!.Width, h = _sprite.Height;
        var dest = new[] { new PointF(x, y), new PointF(x + w, y), new PointF(x, y + h) };
        g.DrawImage(_sprite, dest, new RectangleF(0, 0, w, h), GraphicsUnit.Pixel, _spriteAttributes);
    }

    /// <summary>
    /// Pins the chip to the top-right of the card's work area, on the same insets as the
    /// opened card — the two are one card in two states and must not appear to jump as it
    /// opens.
    /// </summary>
    private void Place(int offsetY, NativeMethods.RECT workArea, float scale)
    {
        if (workArea.Right == workArea.Left)
            NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref workArea, 0);

        _canvasPx = (int)MathF.Ceiling(NayfAgentAvatarArt.CanvasSize * scale);
        _windowX = workArea.Right - (int)(NayfAgentCardHost.RightInset * scale) - _canvasPx;
        _windowY = workArea.Top + (int)((NayfAgentCardHost.TopInset + offsetY) * scale);
    }

    // MARK: - Layered window update

    /// <summary>
    /// Scales each colour channel by its own alpha, in place.
    ///
    /// <para>GDI+ works in straight alpha and UpdateLayeredWindow reads premultiplied, and
    /// nothing in between converts. Handing it straight alpha makes the compositor add the
    /// full colour where it should add a fraction of it, so anything semi-transparent comes
    /// out at full strength — which for this chip means its glows stop being glows and
    /// flood the tile in solid accent.</para>
    /// </summary>
    private static unsafe void Premultiply(Bitmap bitmap)
    {
        var rect = new Rectangle(0, 0, bitmap.Width, bitmap.Height);
        var data = bitmap.LockBits(rect, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            for (int y = 0; y < data.Height; y++)
            {
                byte* row = (byte*)data.Scan0 + y * data.Stride;
                for (int x = 0; x < data.Width; x++)
                {
                    byte* px = row + x * 4;   // B, G, R, A
                    int a = px[3];
                    if (a == 255) continue;
                    if (a == 0) { px[0] = px[1] = px[2] = 0; continue; }
                    px[0] = (byte)(px[0] * a / 255);
                    px[1] = (byte)(px[1] * a / 255);
                    px[2] = (byte)(px[2] * a / 255);
                }
            }
        }
        finally { bitmap.UnlockBits(data); }
    }

    private void ApplyLayeredWindow(Bitmap bitmap, int windowX, int windowY)
    {
        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDC    = CreateCompatibleDC(screenDC);
        IntPtr hBmp     = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp   = SelectObject(memDC, hBmp);

        var size  = new SIZE  { cx = bitmap.Width, cy = bitmap.Height };
        var ptSrc = new PT    { x = 0, y = 0 };
        var ptDst = new PT    { x = windowX, y = windowY };
        var blend = new BLEND { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };

        UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref size,
                            memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, oldBmp);
        DeleteObject(hBmp);
        DeleteDC(memDC);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
    }

    /// <summary>
    /// Unlike the pill and the toast, which are one apiece and live as long as the app,
    /// there is a chip per task and they are thrown away as tasks are dismissed. So the
    /// teardown has to be complete: a window can only be destroyed by the thread that
    /// created it — calling DestroyWindow from here just fails — and the message loop has
    /// to be told to stop, or every task the user ever runs leaves a thread behind.
    /// </summary>
    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _renderTimer?.Dispose();
        _renderTimer = null;

        // The sprite belongs to the render thread; the timer is stopped above, so nothing
        // is left to touch it, but hand the last frame's bitmap over before the thread ends.
        if (_hwnd != IntPtr.Zero)
        {
            PostMessage(_hwnd, WM_APP_TEARDOWN, IntPtr.Zero, IntPtr.Zero);
            _hwnd = IntPtr.Zero;
        }

        _messageThread?.Join(TimeSpan.FromMilliseconds(500));
        _messageThread = null;

        _sprite?.Dispose();
        _sprite = null;
        _spriteAttributes.Dispose();
    }

    // MARK: - Interop

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

    private const byte AC_SRC_OVER  = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const int  ULW_ALPHA    = 0x02;

    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("user32.dll")]
    private static extern bool PostMessage(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam);

    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern bool UnregisterClass(string lpClassName, IntPtr hInstance);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, ref PT pptDst,
        ref SIZE psize, IntPtr hdcSrc, ref PT pptSrc, int crKey, ref BLEND pblend, int dwFlags);

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
}
