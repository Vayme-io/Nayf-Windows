using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// The capabilities showcase: ask Nayf what it can do and a card floats up in the middle of
/// the screen listing the answer, which Nayf then reads out, lighting each line as it gets
/// to it. Port of the Mac's NayfCapabilitiesShowcase.swift.
///
/// <para>It exists because nothing else in the app ever says what the app is for. A user who
/// only knows about push-to-talk never finds out that Nayf will work through a whole job, or
/// remember things, or point at the button instead of pressing it.</para>
///
/// <para>Built like <see cref="NayfActionToast"/> — a layered, click-through, non-activating,
/// top-most window drawn with GDI+ on its own thread — because it has the same job: to be
/// looked at, over whatever the user was already doing, without taking the keyboard or the
/// mouse away from them. The Mac's panel is <c>ignoresMouseEvents</c> for the same reason,
/// and WS_EX_TRANSPARENT is how Windows spells it.</para>
/// </summary>
public sealed class NayfCapabilitiesShowcase : IDisposable
{
    /// <summary>
    /// The live window, set by <see cref="Start"/>. Null before startup and after shutdown,
    /// when the entry points below quietly do nothing — the showcase is a nicety, and a
    /// missing window must never take a turn down with it.
    /// </summary>
    public static NayfCapabilitiesShowcase? Shared { get; private set; }

    private readonly CompanionManager _companionManager;

    /// <summary>See <see cref="NayfActionToast"/> — the Mac's units describe a laptop screen,
    /// and the whole design is taken up together so it claims the same share of a desktop
    /// monitor that it does there.</summary>
    private const float DesignScale = 1.25f;

    /// <summary>
    /// How far above the middle of the screen the card sits. The Mac's +40: dead centre reads
    /// as a modal waiting to be dismissed, and this one isn't waiting for anything.
    /// </summary>
    private const float AboveCentre = 40f;

    // Entry copies the Mac's SwiftUI spring; the exit is a plain easeIn, so it looks like it
    // is done rather than like it is being pulled away.
    private const float SpringResponse = 0.42f;
    private const float SpringDamping = 0.82f;
    private const float EnterScale = 0.94f;
    private const float RetractSeconds = 0.22f;

    /// <summary>The row highlight's own spring — quicker than the card's, because it fires
    /// six times and the card only twice.</summary>
    private const float RowResponse = 0.32f;
    private const float RowDamping = 0.8f;

    /// <summary>How long a row takes to step back once another one is lit. The Mac's
    /// easeInOut(0.25).</summary>
    private const float RecedeSeconds = 0.25f;

    /// <summary>
    /// When a spring is close enough to its target to be called arrived. Without this the
    /// card would go on re-rendering at 60fps forever over the last thousandth of its travel,
    /// which nobody can see and the CPU can.
    /// </summary>
    private const float SettleEpsilon = 0.002f;

    private readonly object _gate = new();
    private bool _visible;
    private int _highlight = -1;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private NativeMethods.WndProc? _wndProcDelegate;
    private System.Threading.Timer? _renderTimer;
    private System.Threading.Timer? _topmostTimer;
    private int _renderGuard; // GDI+ isn't reentrant; a long frame must not fire twice

    private DateTimeOffset _lastFrameTime = DateTimeOffset.UtcNow;

    /// <summary>0 = gone, 1 = arrived. Springs up, then eases back out.</summary>
    private float _presence;
    private float _presenceVelocity;
    /// <summary>0 to 1 across the retraction, once the card has been sent away.</summary>
    private float _retract = 1f;
    private float _retractFrom;
    private bool _wasShowing;
    private bool _isWindowShown;

    // Per-row animation, touched only on the render thread.
    private readonly float[] _emphasis = new float[NayfCapabilities.All.Count];
    private readonly float[] _emphasisVelocity = new float[NayfCapabilities.All.Count];
    private readonly float[] _recede = new float[NayfCapabilities.All.Count];

    /// <summary>The card as last drawn, reused for every frame that doesn't change it — which
    /// is most of them, since a spoken line runs for seconds and the rows settle in half of
    /// one.</summary>
    private Bitmap? _card;
    private int _cardHighlight = int.MinValue;
    private Color _cardAccent;
    private float _cardScale = -1f;

    private int _windowX, _windowY;
    private int _bitmapWidth, _bitmapHeight;
    private float _scale = 1f;

    // What the window is currently showing, so a frame that would change nothing can be
    // skipped outright rather than redrawn identically.
    private float _shownGrow = -1f;
    private int _shownAlpha = -1;

    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const int WS_POPUP          = unchecked((int)0x80000000);
    private const uint ULW_ALPHA        = 0x02;
    private const byte AC_SRC_OVER      = 0x00;
    private const byte AC_SRC_ALPHA     = 0x01;
    private const int SW_HIDE           = 0;
    private const int SW_SHOWNOACTIVATE = 4;
    private const uint WM_CLOSE         = 0x0010;
    private const uint WM_DESTROY       = 0x0002;

    public NayfCapabilitiesShowcase(CompanionManager companionManager)
    {
        _companionManager = companionManager;
    }

    public void Start()
    {
        Shared = this;
        _messageThread = new Thread(RunWindowThread)
        {
            Name = "NayfCapabilitiesShowcase",
            IsBackground = true
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    // MARK: - What the rest of the app calls

    /// <summary>Brings the card up with nothing highlighted yet.</summary>
    public void Show()
    {
        lock (_gate)
        {
            _visible = true;
            _highlight = -1;
        }
    }

    /// <summary>Lights the row Nayf is speaking about. Null clears the highlight without
    /// taking the card down.</summary>
    public void SetHighlight(int? index)
    {
        lock (_gate) _highlight = index ?? -1;
    }

    public void Hide()
    {
        lock (_gate)
        {
            _visible = false;
            _highlight = -1;
        }
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
            lpszClassName = "NayfCapabilitiesShowcase"
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
            WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "NayfCapabilitiesShowcase", "NayfCapabilitiesShowcase",
            WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        _renderTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _renderGuard, 1) == 1) return;
            try { RenderFrame(); }
            catch (Exception ex) { Logger.Log("Capabilities", $"frame failed: {ex.Message}"); }
            finally { Interlocked.Exchange(ref _renderGuard, 0); }
        }, null, TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16));

        _topmostTimer = new System.Threading.Timer(_ =>
        {
            if (_hwnd != IntPtr.Zero && _isWindowShown)
                NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
                    NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);
        }, null, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(2));

        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        // Ends the loop above once the window is gone, so the thread retires with it.
        if (msg == WM_DESTROY)
        {
            NativeMethods.PostQuitMessage(0);
            return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    // MARK: - Frame

    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero) return;

        var now = DateTimeOffset.UtcNow;
        float elapsed = (float)(now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        bool shouldShow;
        int highlight;
        lock (_gate)
        {
            shouldShow = _visible;
            highlight = _highlight;
        }

        // Nothing up and nothing left to fade — the case that holds all day, so it costs one
        // lock and a comparison before returning.
        if (!shouldShow && _retract >= 1f)
        {
            HideWindowIfShown();
            return;
        }

        bool firstFrame = !_isWindowShown;
        if (firstFrame) PositionOnMonitorUnderCursor();

        float visual = AdvancePresence(shouldShow, elapsed);
        bool rowsMoved = AdvanceRows(highlight, elapsed);

        // The spring overshoots past 1 on the way in; opacity must not.
        int alpha = (int)MathF.Round(255 * Math.Clamp(visual, 0f, 1f));
        float grow = EnterScale + (1f - EnterScale) * visual;

        // Landing within a thousandth of full size and then resampling the card anyway would
        // trade the text's crispness for nothing anyone can see.
        if (MathF.Abs(grow - 1f) < SettleEpsilon) grow = 1f;

        var accent = _companionManager.SelectedCursorColor.ToDrawingColor();
        bool cardStale = _card == null || highlight != _cardHighlight ||
                         accent != _cardAccent || _scale != _cardScale || rowsMoved;

        if (!cardStale && !firstFrame && grow == _shownGrow && alpha == _shownAlpha) return;

        if (cardStale)
        {
            _card?.Dispose();
            _card = NayfCapabilitiesShowcaseArt.Render(BuildModel(accent), _scale);
            _cardHighlight = highlight;
            _cardAccent = accent;
            _cardScale = _scale;
        }

        using var frame = Compose(_card!, grow);

        // Painted before the first ShowWindow, so the card never flashes empty.
        if (firstFrame)
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
                _windowX, _windowY, _bitmapWidth, _bitmapHeight,
                NativeMethods.SWP_NOACTIVATE);

        ApplyLayeredWindow(frame, _windowX, _windowY, alpha);
        _shownGrow = grow;
        _shownAlpha = alpha;

        if (firstFrame)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _isWindowShown = true;
        }
    }

    private CapabilitiesShowcaseModel BuildModel(Color accent)
    {
        var rows = new CapabilityRowModel[NayfCapabilities.All.Count];
        for (int i = 0; i < rows.Length; i++)
            rows[i] = new CapabilityRowModel
            {
                Glyph = NayfCapabilities.All[i].Glyph,
                Title = NayfCapabilities.All[i].Title,
                Blurb = NayfCapabilities.All[i].Blurb,
                Emphasis = _emphasis[i],
                Recede = _recede[i]
            };

        return new CapabilitiesShowcaseModel
        {
            Accent = accent,
            Headline = NayfCapabilities.Headline,
            Subhead = NayfCapabilities.Subhead,
            Rows = rows
        };
    }

    /// <summary>
    /// Puts the card into the window's bitmap at <paramref name="grow"/> of its size, about
    /// its own centre.
    /// </summary>
    /// <remarks>
    /// At full size the card is blitted whole-pixel and untouched: the text was rasterised by
    /// GDI onto this exact pixel grid, and resampling it — even by a factor of one — is how
    /// crisp text turns soft. Only the fraction of a second the entry lasts goes through the
    /// interpolator, which is what SwiftUI's scaleEffect does too.
    /// </remarks>
    private Bitmap Compose(Bitmap card, float grow)
    {
        var frame = new Bitmap(_bitmapWidth, _bitmapHeight, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(frame))
        {
            g.Clear(Color.Transparent);

            if (grow >= 1f)
            {
                g.DrawImageUnscaled(card, 0, 0);
            }
            else
            {
                g.InterpolationMode = InterpolationMode.HighQualityBilinear;
                g.PixelOffsetMode = PixelOffsetMode.Half;

                float w = card.Width * grow, h = card.Height * grow;
                g.DrawImage(card, (_bitmapWidth - w) / 2f, (_bitmapHeight - h) / 2f, w, h);
            }
        }

        Premultiply(frame);
        return frame;
    }

    /// <summary>
    /// Moves the card toward present or absent and hands back how visible it is now. Two
    /// curves, because they say different things: a spring on the way in that overshoots
    /// slightly and settles, and a plain easeIn on the way out.
    /// </summary>
    private float AdvancePresence(bool shouldShow, float elapsed)
    {
        if (shouldShow)
        {
            if (!_wasShowing)
            {
                // Caught mid-retreat by a second showing. The spring picks up from what is
                // actually on screen rather than from where the last one left _presence,
                // which is the difference between resuming and jumping.
                if (_retract > 0f && _retract < 1f) _presence = _retractFrom * RetractCurve(_retract);
                else if (_retract >= 1f) _presence = 0f;
                _presenceVelocity = 0f;
                _retract = 0f;
                _wasShowing = true;
            }

            AdvanceSpring(ref _presence, ref _presenceVelocity, 1f, elapsed,
                          SpringResponse, SpringDamping);
            return _presence;
        }

        if (_wasShowing)
        {
            _retractFrom = _presence;
            _retract = 0f;
            _wasShowing = false;
        }

        _retract = MathF.Min(1f, _retract + elapsed / RetractSeconds);
        return _retractFrom * RetractCurve(_retract);
    }

    /// <summary>
    /// Steps every row toward being lit or being stepped back, and says whether any of them
    /// actually moved — which is what decides if the card has to be drawn again.
    /// </summary>
    private bool AdvanceRows(int highlight, float elapsed)
    {
        bool moved = false;

        for (int i = 0; i < _emphasis.Length; i++)
        {
            float emphasisTarget = i == highlight ? 1f : 0f;
            float recedeTarget = highlight >= 0 && i != highlight ? 1f : 0f;

            if (MathF.Abs(_emphasis[i] - emphasisTarget) > SettleEpsilon ||
                MathF.Abs(_emphasisVelocity[i]) > SettleEpsilon)
            {
                AdvanceSpring(ref _emphasis[i], ref _emphasisVelocity[i], emphasisTarget,
                              elapsed, RowResponse, RowDamping);
                moved = true;
            }
            else if (_emphasis[i] != emphasisTarget)
            {
                _emphasis[i] = emphasisTarget;
                _emphasisVelocity[i] = 0f;
                moved = true;
            }

            if (_recede[i] != recedeTarget)
            {
                float step = elapsed / RecedeSeconds;
                _recede[i] = recedeTarget > _recede[i]
                    ? MathF.Min(recedeTarget, _recede[i] + step)
                    : MathF.Max(recedeTarget, _recede[i] - step);
                moved = true;
            }
        }

        return moved;
    }

    /// <summary>How much is left at <paramref name="t"/> through the retraction. Quadratic,
    /// so it lets go gently and then goes — SwiftUI's easeIn, near enough.</summary>
    private static float RetractCurve(float t) => 1f - t * t;

    /// <summary>
    /// Integrates a damped spring toward <paramref name="target"/>. Sub-stepped so a dropped
    /// frame can't overshoot into a wobble, which these responses invite.
    /// </summary>
    private static void AdvanceSpring(ref float value, ref float velocity, float target,
                                      float elapsed, float response, float damping)
    {
        float omega = 2f * MathF.PI / response;
        float remaining = MathF.Min(elapsed, 0.1f);

        while (remaining > 0f)
        {
            float dt = MathF.Min(remaining, 1f / 240f);
            remaining -= dt;

            float accel = -omega * omega * (value - target) - 2f * damping * omega * velocity;
            velocity += accel * dt;
            value += velocity * dt;
        }
    }

    private void HideWindowIfShown()
    {
        if (!_isWindowShown) return;

        ShowWindow(_hwnd, SW_HIDE);
        _isWindowShown = false;
        _presence = 0f;
        _presenceVelocity = 0f;
        _shownGrow = -1f;
        _shownAlpha = -1;

        // Every row starts dark again next time, and the card that was drawn for the last
        // showing is a few megabytes to be holding on to until there is one.
        Array.Clear(_emphasis);
        Array.Clear(_emphasisVelocity);
        Array.Clear(_recede);
        _card?.Dispose();
        _card = null;
        _cardHighlight = int.MinValue;
    }

    /// <summary>
    /// Centres the card on whichever monitor the cursor is on and reads that monitor's DPI,
    /// so it is the same physical size everywhere. Work area rather than monitor bounds, so
    /// the taskbar isn't counted as screen the card can sit in the middle of.
    /// </summary>
    private void PositionOnMonitorUnderCursor()
    {
        var workArea = new NativeMethods.RECT();
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref workArea, 0);
        _scale = 1f;

        if (NativeMethods.GetCursorPos(out var cursor))
        {
            IntPtr monitor = NativeMethods.MonitorFromPoint(cursor, NativeMethods.MONITOR_DEFAULTTONEAREST);
            if (monitor != IntPtr.Zero)
            {
                var info = new NativeMethods.MONITORINFOEX
                {
                    cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>()
                };
                if (NativeMethods.GetMonitorInfo(monitor, ref info)) workArea = info.rcWork;

                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                    _scale = dpiX / 96f;
            }
        }

        _scale *= DesignScale;

        float margin = NayfCapabilitiesShowcaseArt.ShadowMargin * 2f;
        _bitmapWidth = (int)MathF.Ceiling((NayfCapabilitiesShowcaseArt.Width + margin) * _scale);
        _bitmapHeight = (int)MathF.Ceiling(
            (NayfCapabilitiesShowcaseArt.HeightFor(NayfCapabilities.All.Count) + margin) * _scale);

        _windowX = workArea.Left + (workArea.Right - workArea.Left - _bitmapWidth) / 2;
        _windowY = workArea.Top + (workArea.Bottom - workArea.Top - _bitmapHeight) / 2
                   - (int)(AboveCentre * _scale);
    }

    // MARK: - Layered window update

    /// <summary>
    /// Scales each colour channel by its own alpha, in place. GDI+ works in straight alpha
    /// and UpdateLayeredWindow reads premultiplied, and nothing in between converts — see
    /// <see cref="NativeAgentCardWindow"/>, where the same pass is what stops the antialiased
    /// corners ringing white.
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

    /// <summary>
    /// Hands the frame to the compositor. The card's own fade rides on
    /// <c>SourceConstantAlpha</c> rather than being drawn into the bitmap: it multiplies the
    /// per-pixel alpha already there, so one number fades the whole card — shadow, rim,
    /// hinted text and all — without a second render pass to bake it in.
    /// </summary>
    private void ApplyLayeredWindow(Bitmap bitmap, int windowX, int windowY, int alpha)
    {
        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDC    = CreateCompatibleDC(screenDC);
        IntPtr hBmp     = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp   = SelectObject(memDC, hBmp);

        var size  = new SIZE  { cx = bitmap.Width, cy = bitmap.Height };
        var ptSrc = new PT    { x = 0, y = 0 };
        var ptDst = new PT    { x = windowX, y = windowY };
        var blend = new BLEND
        {
            BlendOp = AC_SRC_OVER,
            SourceConstantAlpha = (byte)Math.Clamp(alpha, 0, 255),
            AlphaFormat = AC_SRC_ALPHA
        };

        UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref size,
                            memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, oldBmp);
        DeleteObject(hBmp);
        DeleteDC(memDC);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
    }

    public void Dispose()
    {
        if (ReferenceEquals(Shared, this)) Shared = null;
        _renderTimer?.Dispose();
        _topmostTimer?.Dispose();
        _card?.Dispose();
        _card = null;
        if (_hwnd != IntPtr.Zero)
        {
            // Posted, not called: DestroyWindow only works from the thread that owns the
            // window, and Dispose runs on whoever is shutting the app down. WM_CLOSE lands
            // on the message thread, DefWindowProc destroys the window there, and the
            // WM_DESTROY above quits the pump.
            PostMessage(_hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
            _hwnd = IntPtr.Zero;
        }
    }

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

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PT    { public int x,  y;  }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
}
