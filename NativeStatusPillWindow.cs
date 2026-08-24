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
/// The status pill: a dark bar that drops from the top edge of the screen while Vayme
/// is listening, thinking, speaking or running a task, and slides back up when it's
/// done. Port of the Mac's NotchStatusHUD (NotchHandleView.swift).
///
/// On the Mac the pill also has a resting state that hangs under the notch and opens
/// the panel when clicked. Windows has no notch and Vayme already has a taskbar button
/// for that, so only the live HUD is ported — it exists while Vayme is doing something
/// and not a moment longer.
///
/// Built like <see cref="NativeOverlayWindow"/>: a layered, click-through, top-most
/// tool window drawn with GDI+ on its own thread, so it floats above every app and
/// can never take focus away from what the user is typing into.
/// </summary>
public sealed class NativeStatusPillWindow : IDisposable
{
    private readonly CompanionManager _companionManager;

    /// <summary>
    /// The Mac's numbers describe a bar that fills about a fifth of a laptop screen.
    /// Reused as-is on a desktop monitor they'd draw a sliver, so every measurement is
    /// taken up together — the proportions stay exactly the Mac's, the bar just claims
    /// the same share of the screen it does there.
    /// </summary>
    private const float DesignScale = 1.25f;

    // Layout at 96 DPI, in the Mac's units. Multiplied by DesignScale and then by the
    // monitor's DPI factor when drawn, because the window is sized in physical pixels
    // and the app is PerMonitorV2.
    private const float PillWidth = 320f;
    private const float PillHeight = 28f;
    private const float PillCornerRadius = 16f;
    private const float PillPaddingX = 18f;
    private const float IndicatorWidth = 38f;
    /// <summary>
    /// The Mac's HStack spacing (12) either side of a Spacer with a minimum length of
    /// 18 — so the title can never crowd the indicator by more than this.
    /// </summary>
    private const float TitleIndicatorGap = 12f + 18f + 12f;
    private const float TitleFontSize = 13f;
    /// <summary>
    /// Slack around the pill inside the bitmap: antialiased edges, the drop shadow,
    /// and the bit of overshoot the spring adds on the way in.
    /// </summary>
    private const float BitmapMargin = 16f;

    /// <summary>How long the bar takes to settle onto a new state, per the Mac's easeInOut.</summary>
    private const float LookTransitionSeconds = 0.25f;

    // The Mac scales the pill up from 0.9 anchored at its top edge and springs it into
    // place rather than sliding it — response/damping copied from its SwiftUI spring,
    // including the slight overshoot at 0.82 that gives it the little settle at the end.
    private const float SpringResponse = 0.42f;
    private const float SpringDamping = 0.82f;
    private const float EnterScale = 0.9f;

    /// <summary>
    /// How square the rounded corners are. 2 is a plain circular arc; a little above
    /// that eases the join where the curve meets the straight edge, the way Apple's
    /// continuous corners do. Much higher and the curve hugs the corner instead, which
    /// reads as a far smaller radius than it really is.
    /// </summary>
    private const float CornerExponent = 2.2f;

    // Matched to the companion panel rather than to the Mac's flat near-black bar, so
    // the two surfaces read as the same app. The panel is DesktopAcrylic tinted
    // (24,24,27); a layered GDI window can't host an acrylic backdrop, but at this
    // alpha the desktop tints through much the same way — see CompanionPanelWindow.
    private static readonly Color PillFill = Color.FromArgb(209, 24, 24, 27);
    /// <summary>The panel's Hairline token, #14FFFFFF.</summary>
    private static readonly Color PillBorder = Color.FromArgb(20, 255, 255, 255);
    /// <summary>The panel's TextPrimary token, #F5F5F7 — not pure white.</summary>
    private static readonly Color TitleColor = Color.FromArgb(255, 0xF5, 0xF5, 0xF7);

    /// <summary>How far the drop shadow reaches, in the Mac's units.</summary>
    private const int ShadowLayers = 5;
    private const float ShadowStep = 1.5f;

    /// <summary>How far the bar is drawn past the top of the screen, to be clipped there.</summary>
    private const float TopOverhang = 2f;

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

    // Where the pill is drawn, chosen when it appears and then left alone — it would
    // be maddening for the bar to hop monitors mid-sentence because the mouse moved.
    private int _windowX, _windowY;
    private int _bitmapWidth, _bitmapHeight;
    private float _scale = 1f;

    // Rasterised titles, keyed by their text. Only ever touched from the render thread.
    private readonly Dictionary<string, Bitmap> _titleSprites = new();
    private float _spriteFontPx;
    private int _spriteWidth, _spriteHeight;

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
        if (isFirstFrame) PositionOnMonitorUnderCursor();

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

        DrawPill(g);

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
    /// Centres the pill on the top edge of whichever monitor the cursor is on, and
    /// reads that monitor's DPI so the bar is the same physical size everywhere. Uses
    /// the work area rather than the monitor bounds, so a taskbar docked to the top
    /// doesn't sit on top of it.
    /// </summary>
    private void PositionOnMonitorUnderCursor()
    {
        var workArea = new NativeMethods.RECT
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
                if (NativeMethods.GetMonitorInfo(monitor, ref info)) workArea = info.rcWork;

                if (GetDpiForMonitor(monitor, MDT_EFFECTIVE_DPI, out uint dpiX, out _) == 0 && dpiX > 0)
                    _scale = dpiX / 96f;
            }
        }

        _scale *= DesignScale;

        _bitmapWidth = (int)MathF.Ceiling((PillWidth + BitmapMargin * 2) * _scale);
        _bitmapHeight = (int)MathF.Ceiling((PillHeight + BitmapMargin) * _scale);
        _windowX = workArea.Left + (workArea.Right - workArea.Left - _bitmapWidth) / 2;
        _windowY = workArea.Top;
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

    private void DrawPill(Graphics g)
    {
        float pillWidth = PillWidth * _scale;
        float pillHeight = PillHeight * _scale;
        float left = BitmapMargin * _scale;
        const float top = 0f; // flush with the top edge of the screen

        // Grows into place from 90% about its own top edge, so it reads as unfolding
        // out of the edge rather than being flown in from somewhere off-screen.
        float grow = EnterScale + (1f - EnterScale) * _presence;
        var savedTransform = g.Save();
        g.TranslateTransform(left + pillWidth / 2f, top);
        g.ScaleTransform(grow, grow);
        g.TranslateTransform(-(left + pillWidth / 2f), -top);

        // The spring overshoots past 1 on the way in; opacity must not.
        int alpha = (int)(255 * Math.Clamp(_presence, 0f, 1f));

        // Ease the hand-over between states, so nothing in the bar changes abruptly.
        float mix = _lookTransition * _lookTransition * (3f - 2f * _lookTransition);

        // Radius is clamped to half the height, which is what SwiftUI does with the
        // Mac's 16 on a 28-tall bar — the bottom is a full cap either way.
        float radius = MathF.Min(PillCornerRadius, PillHeight / 2f) * _scale;

        // Start the shape above the screen edge and let it clip there. The bar must sit
        // hard against the top with nothing showing through; drawing it flush leaves
        // the top row at the mercy of rounding, and the top edge is square anyway, so
        // the overhang costs nothing. It also takes the top hairline off-screen, which
        // is where the Mac's is too — swallowed by the notch.
        float overhang = TopOverhang * _scale;
        float drawnTop = top - overhang;
        float drawnHeight = pillHeight + overhang;

        using (var path = BottomRoundedRect(left, drawnTop, pillWidth, drawnHeight, radius))
        {
            DrawShadow(g, path, left, drawnTop, pillWidth, drawnHeight, radius, alpha);

            using var fill = new SolidBrush(Scaled(PillFill, alpha));
            g.FillPath(fill, path);
        }

        // Inset by half the pen width so the hairline lands inside the shape instead
        // of straddling the edge, where half of it would blur into the background.
        using (var border = new Pen(Scaled(PillBorder, alpha), 1f))
        using (var borderPath = BottomRoundedRect(left + 0.5f, drawnTop, pillWidth - 1f, drawnHeight - 0.5f, radius))
        {
            g.DrawPath(border, borderPath);
        }

        // Title on the left, indicator on the right, same as the Mac's HStack.
        float titleLeft = left + PillPaddingX * _scale;
        float titleWidth = pillWidth - (PillPaddingX * 2 + IndicatorWidth + TitleIndicatorGap) * _scale;

        if (titleWidth > 0)
        {
            DrawTitle(g, _previousLook.Title, titleLeft, top, titleWidth, pillHeight,
                      (int)(alpha * (1f - mix)));
            DrawTitle(g, _look.Title, titleLeft, top, titleWidth, pillHeight,
                      (int)(alpha * mix));
        }

        float indicatorCenterX = left + pillWidth - (PillPaddingX + IndicatorWidth / 2) * _scale;
        float indicatorCenterY = top + pillHeight / 2f;
        double t = _clock.Elapsed.TotalSeconds;

        DrawIndicator(g, _previousLook, indicatorCenterX, indicatorCenterY,
                      (int)(alpha * (1f - mix)), t);
        DrawIndicator(g, _look, indicatorCenterX, indicatorCenterY,
                      (int)(alpha * mix), t);

        g.Restore(savedTransform);
    }

    /// <summary>
    /// The soft shadow the companion panel casts, approximated by stacking the pill's
    /// own outline at growing sizes — GDI+ has no blur. Clipped to everything *outside*
    /// the pill, because the fill is translucent now and shadow left underneath it
    /// would darken the glass instead of the desktop.
    /// </summary>
    private void DrawShadow(Graphics g, GraphicsPath pillPath, float left, float top,
                            float pillWidth, float pillHeight, float radius, int alpha)
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
            using var brush = new SolidBrush(
                Color.FromArgb(alpha * 11 / 255, 0, 0, 0));
            using var path = BottomRoundedRect(
                left - spread, top, pillWidth + spread * 2f, pillHeight + spread, radius + spread);
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

    private void DrawTitle(Graphics g, string title, float x, float top, float width, float height, int alpha)
    {
        if (alpha <= 0 || title.Length == 0) return;

        var sprite = TitleSprite(title, (int)MathF.Ceiling(width), (int)MathF.Ceiling(height));
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
    /// Cached because building one costs a bitmap, a DC and a full pixel walk, and the
    /// title changes a few times per turn against sixty frames a second.
    /// </summary>
    private Bitmap? TitleSprite(string title, int width, int height)
    {
        if (width <= 0 || height <= 0) return null;

        // Font size follows the monitor, so a move between displays invalidates every
        // sprite built for the old one.
        float fontPx = TitleFontSize * _scale;
        if (fontPx != _spriteFontPx || width != _spriteWidth || height != _spriteHeight)
        {
            foreach (var stale in _titleSprites.Values) stale.Dispose();
            _titleSprites.Clear();
            _spriteFontPx = fontPx;
            _spriteWidth = width;
            _spriteHeight = height;
        }

        if (_titleSprites.TryGetValue(title, out var cached)) return cached;

        var sprite = new Bitmap(width, height, PixelFormat.Format32bppArgb);
        using (var sg = Graphics.FromImage(sprite))
        {
            // Opaque black, so every byte GDI leaves behind is glyph coverage and nothing
            // else — the alpha byte it zeroes included.
            sg.Clear(Color.Black);

            IntPtr hdc = sg.GetHdc();
            try
            {
                IntPtr font = CreateFontW(-(int)MathF.Round(fontPx), 0, 0, 0, FW_SEMIBOLD,
                                          0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY,
                                          0, TitleFamily.Name);
                if (font == IntPtr.Zero) return null;

                IntPtr previousFont = SelectObject(hdc, font);
                SetBkMode(hdc, TRANSPARENT_BK);
                SetTextColor(hdc, 0x00FFFFFF);

                // DrawString used to inset the text by about a sixth of an em inside its
                // layout rectangle. That padding is a GDI+ quirk rather than a design
                // choice, but reproducing it is what keeps the pill's left padding looking
                // exactly as it did before the label changed rasterisers.
                var box = new NativeMethods.RECT
                {
                    Left = (int)MathF.Round(fontPx / 6f),
                    Top = 0,
                    Right = width,
                    Bottom = height
                };

                // Centred by GDI on its own cell metrics. The old code did the arithmetic
                // itself off GDI+'s ascent and descent, which no longer describe the face
                // GDI actually mapped -- integer heights mean it is a whole pixel smaller.
                DrawTextW(hdc, title, title.Length, ref box,
                          DT_LEFT | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX | DT_END_ELLIPSIS);

                SelectObject(hdc, previousFont);
                DeleteObject(font);
            }
            finally { sg.ReleaseHdc(hdc); }
        }

        CoverageToAlpha(sprite);
        _titleSprites[title] = sprite;
        return sprite;
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
    /// The pill's shape: square across the top so it reads as hanging off the screen
    /// edge, rounded along the bottom. Mirrors the Mac's UnevenRoundedRectangle with
    /// its .continuous corner style, which is a superellipse rather than an arc — a
    /// plain arc meets the straight edge at a visible kink at this size.
    /// </summary>
    private static GraphicsPath BottomRoundedRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Min(r, MathF.Min(w / 2f, h / 2f));

        const int steps = 16;
        var points = new PointF[2 + (steps + 1) * 2];
        int i = 0;
        points[i++] = new PointF(x, y);
        points[i++] = new PointF(x + w, y);

        // Bottom-right, sweeping from the right edge round to the bottom edge.
        for (int s = 0; s <= steps; s++)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + w - r + r * cx, y + h - r + r * cy);
        }

        // Bottom-left, mirrored, sweeping from the bottom edge round to the left edge.
        for (int s = steps; s >= 0; s--)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + r - r * cx, y + h - r + r * cy);
        }

        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }

    /// <summary>
    /// A point on the unit superellipse quarter, from (1,0) at t=0 to (0,1) at t=1.
    /// With <see cref="CornerExponent"/> of 2 this is exactly a circular arc; higher
    /// values flatten the flanks toward Apple's continuous corner.
    /// </summary>
    private static (float X, float Y) SuperellipsePoint(float t)
    {
        double angle = t * Math.PI / 2.0;
        double p = 2.0 / CornerExponent;
        return ((float)Math.Pow(Math.Cos(angle), p), (float)Math.Pow(Math.Sin(angle), p));
    }

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
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [DllImport("shcore.dll")]
    private static extern int GetDpiForMonitor(IntPtr hmonitor, int dpiType, out uint dpiX, out uint dpiY);
    private const int MDT_EFFECTIVE_DPI = 0;

    [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PT    { public int x,  y;  }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
}
