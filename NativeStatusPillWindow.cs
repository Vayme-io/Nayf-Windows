using System;
using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// The status pill: a floating capsule that appears near the top of the screen while
/// Vayme is listening, thinking, speaking or running a task, and folds away when it's
/// done. Port of the Mac's NayfVoiceStatusPill.swift.
///
/// This replaced an earlier port of the Mac's notch HUD, which hung off the top edge of
/// the screen as a square-topped bar of fixed width. The Mac retired that design along
/// with the notch handles, and the shape it moved to says something the old one didn't:
/// a capsule detached from every edge reads as a readout floating over the desktop,
/// where a bar welded to the top edge reads as a piece of system chrome the user might
/// be able to click. This one is neither — it never takes a click.
///
/// Built like <see cref="NativeOverlayWindow"/>: a layered, click-through, top-most
/// tool window drawn with GDI+ on its own thread, so it floats above every app and
/// can never take focus away from what the user is typing into.
/// </summary>
public sealed class NativeStatusPillWindow : IDisposable
{
    private readonly CompanionManager _companionManager;

    /// <summary>
    /// The Mac's numbers describe a pill sized against a laptop screen and a menu bar.
    /// Reused as-is on a desktop monitor they'd draw something noticeably small, so every
    /// measurement is taken up together — the proportions stay exactly the Mac's, the
    /// pill just claims the same share of the screen it does there.
    /// </summary>
    private const float DesignScale = 1.25f;

    // Layout at 96 DPI, in the Mac's units. Multiplied by DesignScale and then by the
    // monitor's DPI factor when drawn, because the window is sized in physical pixels
    // and the app is PerMonitorV2.
    private const float PillHeight = 32f;
    private const float PillPaddingX = 16f;
    private const float IndicatorWidth = 34f;
    /// <summary>
    /// The Mac's HStack spacing (12) either side of a Spacer with a minimum length of
    /// 14. Under .fixedSize() the Spacer collapses to exactly that minimum, so this is
    /// the whole gap rather than a floor on it.
    /// </summary>
    private const float TitleIndicatorGap = 12f + 14f + 12f;
    private const float TitleFontSize = 12.5f;
    /// <summary>
    /// How wide the title may get before it is ellipsised. The Mac lets .fixedSize()
    /// take whatever width the text asks for and leaves the hosting panel to clip it;
    /// a mission like "Looking through your open windows" would run most of the way
    /// across a desktop monitor, so it is truncated here instead of overrunning.
    /// </summary>
    private const float MaxTitleWidth = 240f;
    /// <summary>
    /// Slack around the pill inside the bitmap, on every side now that it floats:
    /// antialiased edges, the drop shadow, and the bit of overshoot the spring adds on
    /// the way in.
    /// </summary>
    private const float BitmapMargin = 16f;
    /// <summary>
    /// The gap between the top of the work area and the top of the pill. The Mac sits
    /// its pill immediately under the menu bar, which is what detaches it from the
    /// screen edge; Windows has no menu bar, so the gap has to be drawn rather than
    /// inherited — without it this is the old bar again.
    /// </summary>
    private const float TopMargin = 16f;

    /// <summary>How long the pill takes to settle onto a new state, per the Mac's easeInOut.</summary>
    private const float LookTransitionSeconds = 0.2f;

    // The Mac scales the pill up from 0.9 anchored at its top edge and springs it into
    // place rather than sliding it — response/damping copied from its SwiftUI spring,
    // including the slight overshoot at 0.78 that gives it the little settle at the end.
    private const float SpringResponse = 0.34f;
    private const float SpringDamping = 0.78f;
    private const float EnterScale = 0.9f;

    /// <summary>
    /// The Mac's near-black, which is a touch blue rather than neutral. Opaque: the
    /// depth in this design comes from the accent wash and the lit rim, and a
    /// translucent capsule floating over arbitrary Windows content picks up whatever
    /// is behind it instead — the one thing a status readout must not do is become
    /// hard to read because of what the user happens to have open.
    /// </summary>
    private static readonly Color PillFill = Color.FromArgb(255, 8, 10, 15);
    /// <summary>Pure white, as the Mac sets it — the panel's softer #F5F5F7 disappears
    /// against a fill this dark at this size.</summary>
    private static readonly Color TitleColor = Color.FromArgb(255, 255, 255, 255);

    /// <summary>How far the drop shadow reaches, in the Mac's units.</summary>
    private const int ShadowLayers = 5;
    private const float ShadowStep = 1.5f;

    /// <summary>
    /// Windows 11's current UI face. The older "Segoe UI Semibold" is the Windows 8
    /// design and reads noticeably more cramped next to the Mac's SF Pro; the Variable
    /// Text cut is the closest thing shipped with Windows. Fallbacks cover Windows 10.
    /// </summary>
    private static readonly FontFamily TitleFamily = ResolveTitleFamily();

    private static FontFamily ResolveTitleFamily()
    {
        foreach (var name in new[] { "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI" })
        {
            try
            {
                var family = new FontFamily(name);
                // A family that can't render Regular would throw from the render
                // thread instead, taking the bar out with no visible explanation.
                if (family.IsStyleAvailable(FontStyle.Regular)) return family;
            }
            catch (ArgumentException) { /* not installed — try the next one */ }
        }
        return FontFamily.GenericSansSerif;
    }

    /// <summary>Which indicator the bar is showing. Cross-faded when it changes.</summary>
    private enum Indicator { None, Bars, Dots }

    /// <summary>
    /// Everything about the bar that changes when Vayme's state does. Held as a value so
    /// the previous one can be kept around and cross-faded out, the way the Mac's
    /// implicit animation on `value: state` does.
    /// </summary>
    private readonly record struct PillLook(string Title, Color Accent, Indicator Indicator, float Amplitude);

    private PillLook _look;
    private PillLook _previousLook;
    private float _lookTransition = 1f;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private NativeMethods.WndProc? _wndProcDelegate;
    private System.Threading.Timer? _renderTimer;
    private System.Threading.Timer? _topmostTimer;
    private int _renderGuard; // GDI+ isn't reentrant; a long frame must not fire twice

    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private DateTimeOffset _lastFrameTime = DateTimeOffset.UtcNow;

    /// <summary>0 = gone, 1 = fully present. Drives both the scale and the fade.</summary>
    private float _presence;
    private float _presenceVelocity;
    private bool _isWindowShown;

    /// <summary>
    /// The mic level, smoothed with a fast attack and a slow release. The raw value
    /// jitters frame to frame; without this the bars flicker instead of reacting.
    /// </summary>
    private float _smoothedLevel;

    // Which monitor the pill is drawn on, chosen when it appears and then left alone —
    // it would be maddening for the pill to hop monitors mid-sentence because the mouse
    // moved. The window's own position and size are recomputed every frame, because the
    // pill is only as wide as what it currently says.
    private NativeMethods.RECT _workArea;
    private int _windowX, _windowY;
    private int _bitmapWidth, _bitmapHeight;
    private float _scale = 1f;

    // Rasterised titles, keyed by their text — each one only as wide as it needs to be,
    // since that width is also what decides how wide the pill is. Only ever touched from
    // the render thread.
    private readonly Dictionary<string, Bitmap> _titleSprites = new();
    private float _spriteFontPx;
    private int _spriteHeight;

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

    public NativeStatusPillWindow(CompanionManager companionManager)
    {
        _companionManager = companionManager;
    }

    public void Start()
    {
        _messageThread = new Thread(RunWindowThread)
        {
            Name = "NayfStatusPill",
            IsBackground = true
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    private void RunWindowThread()
    {
        _wndProcDelegate = WndProc;
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize        = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc   = _wndProcDelegate,
            hInstance     = NativeMethods.GetModuleHandle(null),
            lpszClassName = "NayfStatusPill"
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
            WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "NayfStatusPill", "NayfStatusPill",
            WS_POPUP,
            0, 0, 1, 1,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        _renderTimer = new System.Threading.Timer(_ =>
        {
            if (Interlocked.Exchange(ref _renderGuard, 1) == 1) return;
            try { RenderFrame(); } finally { Interlocked.Exchange(ref _renderGuard, 0); }
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
        => NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);

    // MARK: - Frame

    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero) return;

        var now = DateTimeOffset.UtcNow;
        float elapsed = (float)(now - _lastFrameTime).TotalSeconds;
        _lastFrameTime = now;

        var voiceState = _companionManager.VoiceState;
        string? runningTool = _companionManager.AgentManager.RunningToolLabel;
        // A mission outlives the tools under it — it is set before the first one runs and
        // held until Vayme has finished speaking — so it keeps the pill up across the gaps
        // between tool calls, where a tool-only test would blink it off and on.
        bool shouldShow = voiceState != CompanionVoiceState.Idle
                          || runningTool != null
                          || _companionManager.AgentManager.MissionText != null;

        // Nothing to say and nothing left on screen — the common case by far, so it
        // costs one property read and returns before touching GDI.
        if (!shouldShow && _presence <= 0f)
        {
            HideWindowIfShown();
            return;
        }

        // Pick the screen at the moment the pill appears. From then on it stays put.
        bool isFirstFrame = !_isWindowShown;
        if (isFirstFrame) PickMonitor();

        AdvanceLook(voiceState, runningTool, elapsed, isFirstFrame);
        AdvanceSpring(shouldShow ? 1f : 0f, elapsed);

        // Settled out of sight — stop drawing rather than fading forever on a spring
        // that only ever approaches zero.
        if (!shouldShow && _presence < 0.004f && MathF.Abs(_presenceVelocity) < 0.02f)
        {
            _presence = 0f;
            _presenceVelocity = 0f;
            HideWindowIfShown();
            return;
        }

        // The pill hugs its title, so "Listening" and "Searching the web…" are different
        // widths and the capsule has to grow between them. Interpolating on the same
        // curve that cross-fades the text means the two read as one movement rather than
        // a resize that happens to coincide with a relabel.
        float pillWidth = Lerp(PillWidthFor(_previousLook.Title), PillWidthFor(_look.Title), LookMix);
        LayoutForWidth(pillWidth);

        using var bitmap = new Bitmap(_bitmapWidth, _bitmapHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // Treat whole coordinates as pixel corners rather than pixel centres. On the
        // default setting an edge at y=0 falls through the middle of the top row and
        // only half-covers it, which shows up as a pale seam along the screen edge.
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        // Grayscale antialiasing, not ClearType: subpixel rendering writes colour
        // fringes and no usable alpha, which a layered window composites as dirt.
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        DrawPill(g, pillWidth);

        // Paint before the first ShowWindow, so the pill never flashes empty.
        if (!_isWindowShown)
            NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
                _windowX, _windowY, _bitmapWidth, _bitmapHeight,
                NativeMethods.SWP_NOACTIVATE);

        ApplyLayeredWindow(bitmap, _windowX, _windowY);

        if (!_isWindowShown)
        {
            ShowWindow(_hwnd, SW_SHOWNOACTIVATE);
            _isWindowShown = true;
        }
    }

    /// <summary>
    /// Integrates a damped spring toward <paramref name="target"/>. Sub-stepped so a
    /// dropped frame can't overshoot into a wobble, which the stiff response invites.
    /// </summary>
    private void AdvanceSpring(float target, float elapsed)
    {
        const float omega = 2f * MathF.PI / SpringResponse;
        float remaining = MathF.Min(elapsed, 0.1f);

        while (remaining > 0f)
        {
            float dt = MathF.Min(remaining, 1f / 240f);
            remaining -= dt;

            float accel = -omega * omega * (_presence - target)
                          - 2f * SpringDamping * omega * _presenceVelocity;
            _presenceVelocity += accel * dt;
            _presence += _presenceVelocity * dt;
        }
    }

    private void HideWindowIfShown()
    {
        if (!_isWindowShown) return;
        ShowWindow(_hwnd, SW_HIDE);
        _isWindowShown = false;
    }

    /// <summary>
    /// Settles on whichever monitor the cursor is on and reads its DPI, so the pill is
    /// the same physical size everywhere. Uses the work area rather than the monitor
    /// bounds, so a taskbar docked to the top pushes the pill below it instead of being
    /// covered by it.
    /// </summary>
    private void PickMonitor()
    {
        _workArea = new NativeMethods.RECT
        {
            Left = 0,
            Top = 0,
            Right = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
            Bottom = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)
        };
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
                if (NativeMethods.GetMonitorInfo(monitor, ref info)) _workArea = info.rcWork;

                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                    _scale = dpiX / 96f;
            }
        }

        _scale *= DesignScale;
    }

    /// <summary>
    /// Sizes the bitmap around a pill of the given width and re-centres the window on
    /// the chosen monitor. Runs every frame: the pill changes width whenever its title
    /// does, and staying centred through that is the whole point of re-deriving it
    /// rather than picking a size once.
    /// </summary>
    private void LayoutForWidth(float pillWidth)
    {
        float margin = BitmapMargin * _scale;
        _bitmapWidth = (int)MathF.Ceiling(pillWidth + margin * 2f);
        _bitmapHeight = (int)MathF.Ceiling(PillHeight * _scale + margin * 2f);
        _windowX = _workArea.Left + (_workArea.Right - _workArea.Left - _bitmapWidth) / 2;
        // The pill sits TopMargin below the work area; the bitmap carries BitmapMargin of
        // slack above it, so the window starts that much higher.
        _windowY = _workArea.Top + (int)MathF.Round((TopMargin - BitmapMargin) * _scale);
    }

    /// <summary>
    /// How wide the capsule has to be to hold <paramref name="title"/> — the Mac's
    /// .fixedSize() on an HStack, worked out by hand. With no title it collapses to
    /// just the indicator and its padding.
    /// </summary>
    private float PillWidthFor(string title)
    {
        float padding = PillPaddingX * _scale;
        float indicator = IndicatorWidth * _scale;

        var sprite = TitleSprite(title);
        if (sprite == null) return padding * 2f + indicator;

        return padding * 2f + sprite.Width + TitleIndicatorGap * _scale + indicator;
    }

    // MARK: - Drawing

    /// <summary>
    /// Works out what the bar should be showing, and starts a cross-fade whenever that
    /// changes. Without it "Listening" hard-cuts to "Thinking" and the accent jumps
    /// teal-to-purple in a single frame, which is the one thing the Mac never does.
    /// </summary>
    private void AdvanceLook(CompanionVoiceState state, string? runningTool, float elapsed, bool isFirstFrame)
    {
        var look = ComputeLook(state, runningTool);

        // Only the amplitude differs frame to frame while listening; that's a live
        // value, not a state change, so it must not restart the cross-fade.
        bool changed = look.Title != _look.Title
                       || look.Accent != _look.Accent
                       || look.Indicator != _look.Indicator;

        if (isFirstFrame)
        {
            // The Mac builds the HUD fresh when voice starts, so there's no previous
            // state to animate from — it just appears already showing the right thing.
            _look = look;
            _previousLook = look;
            _lookTransition = 1f;
            return;
        }

        if (changed)
        {
            _previousLook = _look;
            _look = look;
            _lookTransition = 0f;
        }
        else
        {
            _look = look;
            _lookTransition = MathF.Min(1f, _lookTransition + elapsed / LookTransitionSeconds);
        }
    }

    /// <summary>
    /// How far the hand-over from the previous look to the current one has got, eased.
    /// Everything that changes with the state — title, accent, indicator, and the width
    /// of the capsule itself — moves on this one number, so they move together.
    /// </summary>
    private float LookMix => _lookTransition * _lookTransition * (3f - 2f * _lookTransition);

    private PillLook ComputeLook(CompanionVoiceState state, string? runningTool)
    {
        // A named mission outranks the tool running under it. The tool label is the more
        // precise answer to "what is happening this second", but it changes every couple of
        // seconds, and a pill that rewrites itself that often reads as agitated rather than
        // informative. The mission names the whole job and holds still for it; the per-tool
        // detail is in the panel's step list for anyone who wants it.
        // Escaped rather than written literally: this file once shipped a mangled ellipsis
        // to the pill, where it is the one non-ASCII character the user actually reads.
        const string Ellipsis = "\u2026";

        string? mission = _companionManager.AgentManager.MissionText;
        string title = mission != null
            ? mission + Ellipsis
            : runningTool != null
            ? runningTool + Ellipsis
            // Below a running tool, above the bare state: once Vayme has said out loud that
            // it's on it, repeating "Thinking" back at the user reads as stuck. Same accent
            // and indicator either way -- this is a deeper phase of Processing, not a new
            // state.
            : _companionManager.DeepThinkingLabel ?? state switch
            {
                CompanionVoiceState.Listening => "Listening",
                CompanionVoiceState.Processing => "Thinking",
                CompanionVoiceState.Responding => "Speaking",
                CompanionVoiceState.AwaitingUserStep => "Your turn",
                _ => ""
            };

        // A running tool means Vayme is working, whatever the voice state says — bars
        // while it's talking through it, pulsing dots while it's heads-down.
        bool bars = runningTool != null
            ? state == CompanionVoiceState.Responding
            : state is CompanionVoiceState.Listening or CompanionVoiceState.Responding;

        var indicator = state == CompanionVoiceState.Idle && runningTool == null
            ? Indicator.None
            : bars ? Indicator.Bars : Indicator.Dots;

        // Only live mic input has an amplitude to react to; TTS doesn't expose one,
        // so speaking animates at a steady lively level (as it does on the Mac).
        float amplitude = state == CompanionVoiceState.Listening && runningTool == null
            ? SmoothLevel(_companionManager.AudioPowerLevel)
            : 0.16f;

        return new PillLook(title, AccentFor(state, runningTool), indicator, amplitude);
    }

    private void DrawPill(Graphics g, float pillWidth)
    {
        float pillHeight = PillHeight * _scale;
        float left = BitmapMargin * _scale;
        float top = BitmapMargin * _scale;

        // Grows into place from 90% about its own top edge, matching the Mac's
        // scaleEffect(anchor: .top) — it unfolds downward from where it will settle.
        float grow = EnterScale + (1f - EnterScale) * _presence;
        var savedTransform = g.Save();
        g.TranslateTransform(left + pillWidth / 2f, top);
        g.ScaleTransform(grow, grow);
        g.TranslateTransform(-(left + pillWidth / 2f), -top);

        // The spring overshoots past 1 on the way in; opacity must not.
        int alpha = (int)(255 * Math.Clamp(_presence, 0f, 1f));

        // Ease the hand-over between states, so nothing in the pill changes abruptly.
        float mix = LookMix;

        // A capsule: the radius is half the height, so both ends are full caps and the
        // corners are exact semicircles. This is why the superellipse the old bar used
        // is gone — at this radius a "continuous" corner has no straight edge left to
        // ease into, so it only distorted the caps.
        float radius = pillHeight / 2f;

        // The accent tints the glass and lights the rim, so it cross-fades with
        // everything else rather than snapping teal to purple in a single frame.
        var accent = BlendColor(_previousLook.Accent, _look.Accent, mix);

        using (var path = RoundedRect(left, top, pillWidth, pillHeight, radius))
        {
            DrawShadow(g, path, left, top, pillWidth, pillHeight, alpha);

            using var fill = new SolidBrush(Scaled(PillFill, alpha));
            g.FillPath(fill, path);

            // A wash of the accent across the capsule, strongest at the bottom-left and
            // gone by the top-right. It is what keeps a near-black pill from reading as
            // a flat slab, and it is the only place the state's colour appears at any
            // size — the indicator is five small bars.
            using var wash = DiagonalGradient(left, top, pillWidth, pillHeight, fromBottomLeading: true,
                new[]
                {
                    Fade(accent, 0.22f * alpha / 255f),
                    Fade(accent, 0.05f * alpha / 255f),
                    Fade(accent, 0f)
                });
            g.FillPath(wash, path);
        }

        // A lit rim, bright at the top-right, through the accent, dim at the bottom-left.
        // SwiftUI's strokeBorder draws inside the shape, so the path is inset by half the
        // pen — stroking the outline itself would put half the rim outside the capsule,
        // where it would blur into whatever is behind.
        float penWidth = MathF.Max(1f, _scale);
        using (var rim = DiagonalGradient(left, top, pillWidth, pillHeight, fromBottomLeading: false,
            new[]
            {
                Fade(Color.White, 0.45f * alpha / 255f),
                Fade(accent, 0.60f * alpha / 255f),
                Fade(Color.White, 0.15f * alpha / 255f)
            }))
        using (var pen = new Pen(rim, penWidth))
        using (var borderPath = RoundedRect(
                   left + penWidth / 2f, top + penWidth / 2f,
                   pillWidth - penWidth, pillHeight - penWidth,
                   radius - penWidth / 2f))
        {
            g.DrawPath(pen, borderPath);
        }

        // Title on the left, indicator on the right, same as the Mac's HStack.
        float titleLeft = left + PillPaddingX * _scale;
        DrawTitle(g, _previousLook.Title, titleLeft, top, (int)(alpha * (1f - mix)));
        DrawTitle(g, _look.Title, titleLeft, top, (int)(alpha * mix));

        float indicatorCenterX = left + pillWidth - (PillPaddingX + IndicatorWidth / 2f) * _scale;
        float indicatorCenterY = top + pillHeight / 2f;
        double t = _clock.Elapsed.TotalSeconds;

        DrawIndicator(g, _previousLook, indicatorCenterX, indicatorCenterY,
                      (int)(alpha * (1f - mix)), t);
        DrawIndicator(g, _look, indicatorCenterX, indicatorCenterY,
                      (int)(alpha * mix), t);

        g.Restore(savedTransform);
    }

    /// <summary>
    /// A two-point linear gradient across the capsule, either from its bottom-left
    /// corner to its top-right or the other way — the two diagonals the Mac's
    /// LinearGradients run along.
    /// </summary>
    private static LinearGradientBrush DiagonalGradient(
        float x, float y, float w, float h, bool fromBottomLeading, Color[] colors)
    {
        var start = fromBottomLeading ? new PointF(x, y + h) : new PointF(x + w, y);
        var end   = fromBottomLeading ? new PointF(x + w, y) : new PointF(x, y + h);

        var positions = new float[colors.Length];
        for (int i = 0; i < colors.Length; i++) positions[i] = i / (float)(colors.Length - 1);

        var brush = new LinearGradientBrush(start, end, colors[0], colors[^1])
        {
            // GDI+ samples half a pixel beyond each end of the span. Without a mirrored
            // tile that lands as a hairline of the opposite stop along the capsule's edge.
            WrapMode = WrapMode.TileFlipXY
        };
        brush.InterpolationColors = new ColorBlend(colors.Length)
        {
            Colors = colors,
            Positions = positions
        };
        return brush;
    }

    /// <summary>Straight-line blend between two accents, used across a state change.</summary>
    private static Color BlendColor(Color from, Color to, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(255,
            (int)(from.R + (to.R - from.R) * t),
            (int)(from.G + (to.G - from.G) * t),
            (int)(from.B + (to.B - from.B) * t));
    }

    /// <summary>SwiftUI's <c>.opacity()</c> on a colour: sets alpha outright.</summary>
    private static Color Fade(Color c, float opacity) =>
        Color.FromArgb((int)Math.Clamp(opacity * 255f, 0f, 255f), c.R, c.G, c.B);

    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    /// <summary>
    /// A soft shadow, approximated by stacking the capsule's own outline at growing
    /// sizes — GDI+ has no blur. The Mac's panel doesn't cast one, because it sits under
    /// a menu bar against a known dark strip; here the pill floats over whatever the
    /// user has open, and without a shadow it loses its edge entirely against a light
    /// document.
    ///
    /// Clipped to everything *outside* the capsule: while the pill is fading in, its
    /// fill is still translucent, and shadow left underneath would darken the pill
    /// rather than the desktop.
    /// </summary>
    private void DrawShadow(Graphics g, GraphicsPath pillPath, float left, float top,
                            float pillWidth, float pillHeight, int alpha)
    {
        if (alpha <= 0) return;

        // Generous bounds: the caller has a scale transform in effect, so this rect is
        // in world units and only has to be big enough to cover the whole bitmap.
        using var outside = new Region(
            new RectangleF(-_bitmapWidth, -_bitmapHeight, _bitmapWidth * 3, _bitmapHeight * 3));
        outside.Exclude(pillPath);
        g.Clip = outside;

        for (int layer = ShadowLayers; layer >= 1; layer--)
        {
            float spread = layer * ShadowStep * _scale;
            // Weighted downward, the way a light from above throws it. Even now the pill
            // is detached from the screen edge, a shadow sitting evenly all round reads
            // as a glow rather than as height.
            float shadowTop = top - spread * 0.4f;
            float shadowHeight = pillHeight + spread * 1.4f;
            using var brush = new SolidBrush(
                Color.FromArgb(alpha * 11 / 255, 0, 0, 0));
            using var path = RoundedRect(
                left - spread, shadowTop, pillWidth + spread * 2f, shadowHeight, shadowHeight / 2f);
            g.FillPath(brush, path);
        }

        // Nothing else in the frame is clipped, so resetting is enough — and it avoids
        // holding a saved Region, which is a GDI handle, sixty times a second.
        g.ResetClip();
    }

    /// <summary>
    /// Fades a colour by the pill's overall presence.
    /// </summary>
    /// <remarks>
    /// No premultiplication here, even though UpdateLayeredWindow wants premultiplied
    /// alpha: the bitmap is cleared to transparent black, so GDI+'s own source-over
    /// already writes colour × alpha. Doing it a second time by hand measurably
    /// over-darkens the bar (0.87 effective opacity where 0.82 was asked for).
    /// </remarks>
    private static Color Scaled(Color c, int alpha) =>
        Color.FromArgb(Math.Clamp(c.A * alpha / 255, 0, 255), c.R, c.G, c.B);

    private void DrawTitle(Graphics g, string title, float x, float top, int alpha)
    {
        if (alpha <= 0 || title.Length == 0) return;

        var sprite = TitleSprite(title);
        if (sprite == null) return;

        // The two titles cross-fade during a state change, so the sprite is composited at
        // a fraction of its own alpha rather than drawn outright.
        using var attributes = new ImageAttributes();
        var fade = new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0, 255) / 255f };
        attributes.SetColorMatrix(fade);

        // Whole pixels only. The sprite's crispness comes from glyphs aligned to the pixel
        // grid, and landing it on a half pixel would resample that straight back out.
        var target = new Rectangle((int)MathF.Round(x), (int)MathF.Round(top),
                                   sprite.Width, sprite.Height);
        g.DrawImage(sprite, target, 0, 0, sprite.Width, sprite.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>
    /// Rasterises one title through GDI instead of GDI+, and hands back a straight-ARGB
    /// sprite of it.
    ///
    /// At 100% scaling the label has around twelve pixels of cap height, half what the
    /// Mac's panel gives the same design, so the rasteriser cannot add detail — it can
    /// only choose how to spend those pixels. GDI+ spends them on smoothing and the label
    /// reads soft; GDI's own grayscale rasteriser spends them on contrast and it reads
    /// blocky. ClearType is asked for here to sample coverage three times per pixel
    /// horizontally, and <see cref="CoverageToAlpha"/> then averages each triplet back
    /// down to one value: the extra horizontal resolution survives as a better-estimated
    /// edge, while the colour fringes — which a layered window would composite as dirt —
    /// average away before they ever reach the alpha channel.
    ///
    /// GDI cannot draw with an alpha channel at all, hence the white-on-black mask.
    ///
    /// The sprite is exactly as wide as the text, because that width is also what decides
    /// how wide the capsule is drawn — the pill hugs its title rather than reserving a
    /// fixed column for it.
    ///
    /// Cached because building one costs two DCs, a measure, a bitmap and a full pixel
    /// walk, and the title changes a few times per turn against sixty frames a second.
    /// </summary>
    private Bitmap? TitleSprite(string title)
    {
        if (title.Length == 0) return null;

        // Font size follows the monitor, so a move between displays invalidates every
        // sprite built for the old one.
        float fontPx = TitleFontSize * _scale;
        int height = (int)MathF.Ceiling(PillHeight * _scale);
        if (height <= 0) return null;

        if (fontPx != _spriteFontPx || height != _spriteHeight)
        {
            foreach (var stale in _titleSprites.Values) stale.Dispose();
            _titleSprites.Clear();
            _spriteFontPx = fontPx;
            _spriteHeight = height;
        }

        if (_titleSprites.TryGetValue(title, out var cached)) return cached;

        IntPtr font = CreateFontW(-(int)MathF.Round(fontPx), 0, 0, 0, FW_SEMIBOLD,
                                  0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY,
                                  0, TitleFamily.Name);
        if (font == IntPtr.Zero) return null;

        try
        {
            int width = MeasureTitle(title, font, height);
            if (width <= 0) return null;

            var sprite = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var sg = Graphics.FromImage(sprite))
            {
                // Opaque black, so every byte GDI leaves behind is glyph coverage and
                // nothing else — the alpha byte it zeroes included.
                sg.Clear(Color.Black);

                IntPtr hdc = sg.GetHdc();
                try
                {
                    IntPtr previousFont = SelectObject(hdc, font);
                    SetBkMode(hdc, TRANSPARENT_BK);
                    SetTextColor(hdc, 0x00FFFFFF);

                    var box = new NativeMethods.RECT
                    {
                        Left = 0,
                        Top = 0,
                        Right = width,
                        Bottom = height
                    };

                    // Centred by GDI on its own cell metrics. Doing the arithmetic here
                    // off GDI+'s ascent and descent would be a pixel out, because those
                    // no longer describe the face GDI actually mapped. DT_END_ELLIPSIS
                    // only bites when MeasureTitle hit the cap.
                    DrawTextW(hdc, title, title.Length, ref box,
                              DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

                    SelectObject(hdc, previousFont);
                }
                finally { sg.ReleaseHdc(hdc); }
            }

            CoverageToAlpha(sprite);
            _titleSprites[title] = sprite;
            return sprite;
        }
        finally { DeleteObject(font); }
    }

    /// <summary>
    /// How wide <paramref name="title"/> wants to be, capped at <see cref="MaxTitleWidth"/>
    /// so one long mission can't stretch the capsule across the monitor. Measured through
    /// GDI with the same font that will draw it, because GDI+'s metrics describe a
    /// different mapping of the face and would leave the text a pixel or two clipped.
    /// </summary>
    private int MeasureTitle(string title, IntPtr font, int height)
    {
        using var scratch = new Bitmap(1, 1, PixelFormat.Format32bppArgb);
        using var sg = Graphics.FromImage(scratch);

        IntPtr hdc = sg.GetHdc();
        try
        {
            IntPtr previousFont = SelectObject(hdc, font);
            var box = new NativeMethods.RECT { Left = 0, Top = 0, Right = 0, Bottom = height };
            DrawTextW(hdc, title, title.Length, ref box,
                      DT_LEFT | DT_SINGLELINE | DT_NOPREFIX | DT_CALCRECT);
            SelectObject(hdc, previousFont);

            // A pixel of slack: DT_CALCRECT reports advance widths, and the antialiased
            // edge of the last glyph reaches just past where the next one would start.
            int measured = box.Right - box.Left + 1;
            return Math.Min(measured, (int)MathF.Ceiling(MaxTitleWidth * _scale));
        }
        finally { sg.ReleaseHdc(hdc); }
    }

    /// <summary>
    /// Turns the white-on-black mask GDI produced into a straight-ARGB sprite: coverage
    /// becomes the alpha channel and every pixel takes the title colour. Straight rather
    /// than premultiplied because GDI+ composites it, and GDI+ premultiplies on the way
    /// out — the same reasoning as <see cref="Scaled"/>.
    /// </summary>
    private static void CoverageToAlpha(Bitmap sprite)
    {
        var area = new Rectangle(0, 0, sprite.Width, sprite.Height);
        var data = sprite.LockBits(area, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[data.Stride * sprite.Height];
            Marshal.Copy(data.Scan0, pixels, 0, pixels.Length);

            for (int i = 0; i < pixels.Length; i += 4)
            {
                // ClearType wrote one coverage per colour stripe. Averaging the three is
                // what turns that back into a single edge estimate — better resolved than
                // grayscale antialiasing would give, and with the fringes cancelled out.
                pixels[i + 3] = (byte)((pixels[i] + pixels[i + 1] + pixels[i + 2] + 1) / 3);
                pixels[i]     = TitleColor.B;
                pixels[i + 1] = TitleColor.G;
                pixels[i + 2] = TitleColor.R;
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { sprite.UnlockBits(data); }
    }

    private void DrawIndicator(Graphics g, PillLook look, float centerX, float centerY, int alpha, double t)
    {
        if (alpha <= 0) return;
        alpha = Math.Clamp(alpha, 0, 255);

        switch (look.Indicator)
        {
            case Indicator.Bars:
                DrawEqualizerBars(g, centerX, centerY, look.Accent, look.Amplitude, alpha, t);
                break;
            case Indicator.Dots:
                DrawThinkingDots(g, centerX, centerY, look.Accent, alpha, t);
                break;
        }
    }

    /// <summary>
    /// Rises quickly with the voice and falls back slowly, so the bars answer a
    /// syllable immediately but don't collapse in the gaps between words.
    /// </summary>
    private float SmoothLevel(float raw)
    {
        float k = raw > _smoothedLevel ? 0.45f : 0.12f;
        _smoothedLevel += (raw - _smoothedLevel) * k;
        return _smoothedLevel;
    }

    /// <summary>
    /// Teal while listening, purple while thinking, orange while speaking — the same
    /// three accents the Mac HUD uses, so the two apps read identically at a glance.
    ///
    /// Waiting on the user borrows the listening teal: both are the same message, that the
    /// floor is theirs and Vayme is the one waiting.
    /// </summary>
    private static Color AccentFor(CompanionVoiceState state, string? runningTool)
    {
        if (runningTool != null)
            return state == CompanionVoiceState.Responding
                ? Color.FromArgb(255, 140, 56)
                : Color.FromArgb(158, 107, 245);

        return state switch
        {
            CompanionVoiceState.Listening or CompanionVoiceState.AwaitingUserStep
                => Color.FromArgb(38, 209, 189),
            CompanionVoiceState.Responding => Color.FromArgb(255, 140, 56),
            _ => Color.FromArgb(158, 107, 245)
        };
    }

    /// <summary>
    /// Five bars whose height comes mostly from how loudly the user is speaking, with
    /// a small free-running shimmer so they stay alive during silence. The ×3 gain and
    /// square-root curve lift quiet speech into a visible range and compress shouting —
    /// the raw mic level on its own barely moves.
    /// </summary>
    private void DrawEqualizerBars(Graphics g, float centerX, float centerY, Color color,
                                   float amplitude, int alpha, double t)
    {
        ReadOnlySpan<float> barWeights = stackalloc float[] { 0.55f, 0.8f, 1.0f, 0.8f, 0.55f };

        float raw = Math.Clamp(amplitude, 0f, 1f);
        float level = MathF.Min(MathF.Pow(MathF.Max(raw * 3f, 0f), 0.5f), 1f);

        float barWidth = 3f * _scale;
        float spacing = 3f * _scale;
        float totalWidth = barWeights.Length * barWidth + (barWeights.Length - 1) * spacing;
        float x = centerX - totalWidth / 2f;

        using var brush = new SolidBrush(Color.FromArgb(alpha, color));
        for (int index = 0; index < barWeights.Length; index++)
        {
            double phase = index * 0.85;
            float wave = (float)(Math.Sin(t * 9 + phase) * 0.5 + 0.5);

            float voiceHeight = level * barWeights[index] * 15f;
            float shimmerHeight = wave * (1.5f + level * 3f);
            float height = (3f + voiceHeight + shimmerHeight) * _scale;

            using var path = RoundedRect(x, centerY - height / 2f, barWidth, height, barWidth / 2f);
            g.FillPath(brush, path);
            x += barWidth + spacing;
        }
    }

    /// <summary>Three dots pulsing in a staggered sequence — the "thinking" indicator.</summary>
    private void DrawThinkingDots(Graphics g, float centerX, float centerY, Color color, int alpha, double t)
    {
        const int dotCount = 3;
        float diameter = 6f * _scale;
        float spacing = 5f * _scale;
        float totalWidth = dotCount * diameter + (dotCount - 1) * spacing;
        float x = centerX - totalWidth / 2f;

        for (int index = 0; index < dotCount; index++)
        {
            double phase = index * 0.6;
            float raw = (float)(Math.Sin(t * 5 + phase) * 0.5 + 0.5);
            float scale = 0.55f + raw * 0.55f;
            float dotAlpha = 0.45f + raw * 0.55f;
            float size = diameter * scale;

            using var brush = new SolidBrush(
                Color.FromArgb((int)(alpha * dotAlpha), color));
            g.FillEllipse(brush,
                x + (diameter - size) / 2f, centerY - size / 2f, size, size);
            x += diameter + spacing;
        }
    }

    /// <summary>
    /// The capsule, and the equalizer bars inside it. Clamping the radius to half the
    /// smaller side is what makes this a capsule when handed the pill's own height:
    /// the two arcs on each end meet, leaving no straight edge between them.
    /// </summary>
    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Min(r, MathF.Min(w / 2f, h / 2f));
        var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }

    // MARK: - Layered window update

    private void ApplyLayeredWindow(Bitmap bitmap, int windowX, int windowY)
    {
        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDC    = CreateCompatibleDC(screenDC);
        IntPtr hBmp     = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp   = SelectObject(memDC, hBmp);

        var size  = new SIZE  { cx = _bitmapWidth, cy = _bitmapHeight };
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

    public void Dispose()
    {
        _renderTimer?.Dispose();
        _topmostTimer?.Dispose();
        foreach (var sprite in _titleSprites.Values) sprite.Dispose();
        _titleSprites.Clear();
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            _hwnd = IntPtr.Zero;
        }
    }

    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref PT pptDst, ref SIZE psize, IntPtr hdcSrc, ref PT pptSrc,
        uint crKey, ref BLEND pblend, uint dwFlags);
    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr h);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);

    // Title rasterisation — see TitleSprite for why the label goes through GDI.
    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontW(int height, int width, int escapement,
        int orientation, int weight, uint italic, uint underline, uint strikeOut,
        uint charSet, uint outPrecision, uint clipPrecision, uint quality,
        uint pitchAndFamily, string faceName);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)]
    private static extern int DrawTextW(IntPtr hdc, string text, int count,
        ref NativeMethods.RECT rect, uint format);

    private const int FW_SEMIBOLD = 600;
    private const uint DEFAULT_CHARSET = 1;
    private const uint CLEARTYPE_QUALITY = 5;
    private const int TRANSPARENT_BK = 1;
    private const uint DT_LEFT = 0x0000;
    private const uint DT_VCENTER = 0x0004;
    private const uint DT_SINGLELINE = 0x0020;
    private const uint DT_NOPREFIX = 0x0800;
    private const uint DT_END_ELLIPSIS = 0x8000;
    /// <summary>Measure instead of drawing — fills the rect with what the text needs.</summary>
    private const uint DT_CALCRECT = 0x0400;
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PT    { public int x,  y;  }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
}
