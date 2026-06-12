using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// Small (500×300) transparent layered window that moves with the cursor
/// via SetWindowPos each frame. Drawing a tiny bitmap is ~20× faster than
/// blitting the full virtual screen, which was causing the lag.
///
/// The cursor buddy is drawn at a fixed anchor point inside the bitmap.
/// The window position is offset so the anchor lands on the buddy's
/// spring-smoothed screen position.
/// </summary>
public sealed class NativeOverlayWindow : IDisposable
{
    private readonly CompanionManager _companionManager;

    // Small bitmap dimensions — big enough for cursor + response bubble
    private const int BW = 520;
    private const int BH = 300;
    // Where the cursor sits inside the bitmap
    private const int ANCHOR_X = 32;
    private const int ANCHOR_Y = 150;

    // Matches Mac's DS.Colors.overlayCursorBlue (#3380FF)
    private static readonly Color CursorBlue = Color.FromArgb(0x33, 0x80, 0xFF);

    // First-launch "hey! I'm Nayf" welcome bubble — matches Mac's
    // OverlayWindow welcome sequence timing.
    private const string WelcomeMessage = "hey! I'm Nayf";
    private readonly DateTimeOffset _startTime = DateTimeOffset.UtcNow;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private NativeMethods.WndProc? _wndProcDelegate;

    // Spring-smoothed buddy position (absolute screen coords)
    private float _buddyX;
    private float _buddyY;

    // Exponentially-smoothed voice intensity (0-1) driving the cursor's
    // breathing glow during Listening — matches Mac's asymmetric EMA.
    private float _smoothedVoiceIntensity;

    // ── Buddy navigation (flying to point at detected UI elements) ────────────
    // Matches Mac's BuddyNavigationMode / animateBezierFlightArc.
    private enum BuddyMode { FollowingCursor, Navigating, Pointing }
    private BuddyMode _buddyMode = BuddyMode.FollowingCursor;

    // The detected-element position we're currently navigating to / pointing
    // at, so we don't re-trigger a flight every frame for the same target.
    private System.Drawing.PointF? _activeTarget;

    // Bezier flight state (absolute screen coords)
    private float _flightStartX, _flightStartY;
    private float _flightControlX, _flightControlY;
    private float _flightEndX, _flightEndY;
    private DateTimeOffset _flightStartTime;
    private double _flightDurationSeconds;
    private bool _isReturningFlight;
    private System.Drawing.PointF _cursorPosWhenFlightStarted;

    // Scale pulse applied to the cursor during flight — grows to ~1.3x at the
    // arc's midpoint and shrinks back to 1.0x on landing.
    private float _buddyFlightScale = 1f;

    // How long the buddy holds at the target before flying back.
    private DateTimeOffset _pointingStartTime;
    private const double PointingDwellSeconds = 4.0;

    private System.Threading.Timer? _renderTimer;
    private System.Threading.Timer? _topmostTimer;

    private const int WS_EX_LAYERED     = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW  = 0x00000080;
    private const int WS_EX_NOACTIVATE  = 0x08000000;
    private const int WS_EX_TOPMOST     = 0x00000008;
    private const int WS_POPUP          = unchecked((int)0x80000000);
    private const uint ULW_ALPHA        = 0x02;
    private const byte AC_SRC_OVER      = 0x00;
    private const byte AC_SRC_ALPHA     = 0x01;

    public NativeOverlayWindow(CompanionManager companionManager)
    {
        _companionManager = companionManager;

        // Seed buddy at primary screen centre
        _buddyX = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN) / 2f;
        _buddyY = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN) / 2f;
    }

    public void Start()
    {
        _messageThread = new Thread(RunWindowThread)
        {
            Name = "NayfOverlay",
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
            lpszClassName = "NayfSmallOverlay"
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        // Create the small window at the current buddy position
        int wx = (int)(_buddyX - ANCHOR_X);
        int wy = (int)(_buddyY - ANCHOR_Y);
        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
            WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "NayfSmallOverlay", "NayfOverlay",
            WS_POPUP,
            wx, wy, BW, BH,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        RenderFrame();

        // 60 fps render + move
        _renderTimer = new System.Threading.Timer(_ => RenderFrame(), null,
            TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16));

        // Re-assert topmost every 2 s
        _topmostTimer = new System.Threading.Timer(_ =>
        {
            if (_hwnd != IntPtr.Zero)
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

    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero) return;

        // ── 1. Position the buddy — follow the cursor, or fly to/from a
        //      detected UI element — matches Mac's BuddyNavigationMode. ───────
        UpdateBuddyPosition();

        // ── 2. Move the window so the anchor sits on the buddy position ───────
        int wx = (int)(_buddyX - ANCHOR_X);
        int wy = (int)(_buddyY - ANCHOR_Y);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST,
            wx, wy, 0, 0,
            NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // ── 3. Draw the small bitmap ──────────────────────────────────────────
        using var bitmap = new Bitmap(BW, BH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode     = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        var state = _companionManager.VoiceState;

        // Smooth the raw audio power level toward a 0-1 intensity, matching
        // Mac's asymmetric EMA (fast attack while listening, slow decay otherwise).
        UpdateVoiceIntensity(state);

        // ── Welcome sequence timeline — matches Mac's OverlayWindow ───────────
        // t∈[0,2.0]s: cursor fades in (easeIn). t=2.0s: welcome bubble starts.
        double elapsed = (DateTimeOffset.UtcNow - _startTime).TotalSeconds;
        double cursorT = Math.Clamp(elapsed / 2.0, 0.0, 1.0);
        float cursorOpacity = (float)(cursorT * cursorT);

        // The breathing circle cursor cross-fades out while processing,
        // replaced by the spinner — matches Mac's BlueCursorView.
        if (state == CompanionVoiceState.Processing)
            DrawSpinner(g, ANCHOR_X, ANCHOR_Y, cursorOpacity);
        else
            DrawCursor(g, ANCHOR_X, ANCHOR_Y, _smoothedVoiceIntensity, cursorOpacity, _buddyFlightScale);

        DrawWelcomeBubble(g, ANCHOR_X, ANCHOR_Y, elapsed);

        if (state == CompanionVoiceState.Responding)
        {
            var text = _companionManager.StreamingResponseText;
            if (!string.IsNullOrEmpty(text))
                DrawResponseBubble(g, ANCHOR_X + 28, ANCHOR_Y - 55, text);
        }

        // While pointing at a detected element, the buddy itself has flown
        // there — draw the sonar ring and speech bubble around its anchor.
        if (_buddyMode == BuddyMode.Pointing)
        {
            DrawSonarRing(g, ANCHOR_X, ANCHOR_Y);
            DrawPointingBubble(g, ANCHOR_X + 16, ANCHOR_Y - 36,
                _companionManager.DetectedElementBubbleText ?? "Here");
        }

        ApplyLayeredWindow(bitmap, wx, wy);
    }

    // ── Buddy navigation ─────────────────────────────────────────────────────

    /// <summary>
    /// Updates the buddy's absolute screen position for this frame —
    /// spring-following the cursor, flying out to a detected element, or
    /// holding while pointing at it. Matches Mac's BuddyNavigationMode.
    /// </summary>
    private void UpdateBuddyPosition()
    {
        switch (_buddyMode)
        {
            case BuddyMode.FollowingCursor:
                var target = _companionManager.DetectedElementPosition;
                if (target.HasValue && target != _activeTarget)
                {
                    StartNavigatingToElement(target.Value);
                }
                else if (NativeMethods.GetCursorPos(out var pt))
                {
                    // Tighter spring (0.35) = snappier tracking, less perceived lag.
                    // Offset to the right so the buddy doesn't sit directly on the cursor.
                    const float spring = 0.35f;
                    const float cursorOffsetX = 30f;
                    _buddyX += (pt.X + cursorOffsetX - _buddyX) * spring;
                    _buddyY += (pt.Y - _buddyY) * spring;
                }
                break;

            case BuddyMode.Navigating:
                UpdateFlight();
                break;

            case BuddyMode.Pointing:
                // During the forward dwell, cursor movement doesn't interrupt —
                // the buddy completes its full point-and-hold before flying back.
                if ((DateTimeOffset.UtcNow - _pointingStartTime).TotalSeconds >= PointingDwellSeconds)
                    StartFlyingBackToCursor();
                break;
        }
    }

    /// <summary>
    /// Starts animating the buddy toward a detected UI element — matches Mac's
    /// startNavigatingToElement. Offsets the target so the buddy lands beside
    /// the element rather than directly on top of it.
    /// </summary>
    private void StartNavigatingToElement(System.Drawing.PointF target)
    {
        _activeTarget = target;
        BeginFlight(target.X + 8, target.Y + 12, isReturning: false);
    }

    /// <summary>
    /// Flies the buddy back to the current cursor position after pointing is
    /// done — matches Mac's startFlyingBackToCursor.
    /// </summary>
    private void StartFlyingBackToCursor()
    {
        if (!NativeMethods.GetCursorPos(out var pt))
        {
            _buddyMode = BuddyMode.FollowingCursor;
            _activeTarget = null;
            _companionManager.ClearDetectedElementLocation();
            return;
        }

        const float cursorOffsetX = 30f;
        BeginFlight(pt.X + cursorOffsetX, pt.Y, isReturning: true);
    }

    /// <summary>
    /// Sets up a quadratic-bezier flight from the buddy's current position to
    /// (endX, endY), with a parabolic arc and duration scaled by distance —
    /// matches Mac's animateBezierFlightArc (clamped 0.6s–1.4s).
    /// </summary>
    private void BeginFlight(float endX, float endY, bool isReturning)
    {
        _flightStartX = _buddyX;
        _flightStartY = _buddyY;
        _flightEndX = endX;
        _flightEndY = endY;

        float dx = _flightEndX - _flightStartX;
        float dy = _flightEndY - _flightStartY;
        float distance = MathF.Sqrt(dx * dx + dy * dy);
        _flightDurationSeconds = Math.Clamp(distance / 800.0, 0.6, 1.4);

        float midX = (_flightStartX + _flightEndX) / 2f;
        float midY = (_flightStartY + _flightEndY) / 2f;
        float arcHeight = Math.Min(distance * 0.2f, 80f);
        _flightControlX = midX;
        _flightControlY = midY - arcHeight;

        if (NativeMethods.GetCursorPos(out var pt))
            _cursorPosWhenFlightStarted = new System.Drawing.PointF(pt.X, pt.Y);

        _flightStartTime = DateTimeOffset.UtcNow;
        _isReturningFlight = isReturning;
        _buddyMode = BuddyMode.Navigating;
    }

    /// <summary>
    /// Advances the in-progress bezier flight by one frame — moves the buddy
    /// along the arc, pulses its scale, and transitions to the next mode on
    /// arrival. During the return flight, a large cursor movement cancels the
    /// flight and snaps straight back to following — matches Mac's
    /// cancelNavigationAndResumeFollowing.
    /// </summary>
    private void UpdateFlight()
    {
        if (_isReturningFlight && NativeMethods.GetCursorPos(out var pt))
        {
            float movedDx = pt.X - _cursorPosWhenFlightStarted.X;
            float movedDy = pt.Y - _cursorPosWhenFlightStarted.Y;
            if (MathF.Sqrt(movedDx * movedDx + movedDy * movedDy) > 100f)
            {
                _buddyFlightScale = 1f;
                _buddyMode = BuddyMode.FollowingCursor;
                _activeTarget = null;
                _companionManager.ClearDetectedElementLocation();
                return;
            }
        }

        double linear = Math.Clamp(
            (DateTimeOffset.UtcNow - _flightStartTime).TotalSeconds / _flightDurationSeconds, 0.0, 1.0);

        // Smoothstep ease-in-out: 3t² - 2t³
        double t = linear * linear * (3.0 - 2.0 * linear);
        float oneMinusT = (float)(1.0 - t);

        // Quadratic bezier: B(t) = (1-t)²·P0 + 2(1-t)t·P1 + t²·P2
        _buddyX = oneMinusT * oneMinusT * _flightStartX
                + 2f * oneMinusT * (float)t * _flightControlX
                + (float)(t * t) * _flightEndX;
        _buddyY = oneMinusT * oneMinusT * _flightStartY
                + 2f * oneMinusT * (float)t * _flightControlY
                + (float)(t * t) * _flightEndY;

        // Scale pulse: peaks at ~1.3x at the arc's midpoint, lands at 1.0x.
        _buddyFlightScale = 1f + (float)Math.Sin(linear * Math.PI) * 0.3f;

        if (linear >= 1.0)
        {
            _buddyX = _flightEndX;
            _buddyY = _flightEndY;
            _buddyFlightScale = 1f;

            if (_isReturningFlight)
            {
                _buddyMode = BuddyMode.FollowingCursor;
                _activeTarget = null;
                _companionManager.ClearDetectedElementLocation();
            }
            else
            {
                _buddyMode = BuddyMode.Pointing;
                _pointingStartTime = DateTimeOffset.UtcNow;
            }
        }
    }

    // ── Drawing helpers ───────────────────────────────────────────────────────

    /// <summary>
    /// Smooths the raw audio power level toward a target intensity using an
    /// asymmetric EMA — fast attack while listening, slow decay otherwise.
    /// Mirrors Mac's BlueCursorView onChange/voiceState handlers.
    /// </summary>
    private void UpdateVoiceIntensity(CompanionVoiceState state)
    {
        float target = state == CompanionVoiceState.Listening
            ? Math.Min(MathF.Pow(Math.Max(_companionManager.AudioPowerLevel * 3f, 0f), 0.5f), 1f)
            : 0f;

        bool isRising = target > _smoothedVoiceIntensity;
        float factor = isRising ? 0.4f : 0.07f;
        _smoothedVoiceIntensity += (target - _smoothedVoiceIntensity) * factor;
    }

    /// <summary>
    /// The blue glowing circle cursor — matches Mac's BlueCursorView circle.
    /// An outer blurred glow ring breathes with voice intensity, and a solid
    /// inner circle (with its own glow halo) grows gently with it.
    /// </summary>
    private static void DrawCursor(Graphics g, float x, float y, float voiceIntensity, float opacity, float scale = 1f)
    {
        if (opacity <= 0f) return;

        // Outer glow ring
        float outerRadius = ((16 + voiceIntensity * 28) / 2f + 4 + voiceIntensity * 4) * scale;
        int outerAlpha = (int)((0.25f + voiceIntensity * 0.45f) * 255f * opacity);
        DrawGlow(g, x, y, outerRadius, outerAlpha);

        // Glow halo behind the solid inner circle (SwiftUI shadow equivalent)
        float shadowRadius = (8 + voiceIntensity * 12) * scale;
        DrawGlow(g, x, y, shadowRadius, (int)(200 * opacity));

        // Solid inner circle
        float innerDiameter = (10 + voiceIntensity * 3) * scale;
        using var fill = new SolidBrush(Color.FromArgb((int)(255 * opacity), CursorBlue));
        g.FillEllipse(fill, x - innerDiameter / 2, y - innerDiameter / 2, innerDiameter, innerDiameter);
    }

    /// <summary>Soft radial glow used to approximate SwiftUI's blur/shadow modifiers.</summary>
    private static void DrawGlow(Graphics g, float cx, float cy, float radius, int alpha)
    {
        if (radius <= 0.5f) return;
        using var path = new GraphicsPath();
        path.AddEllipse(cx - radius, cy - radius, radius * 2, radius * 2);
        using var brush = new PathGradientBrush(path)
        {
            CenterColor = Color.FromArgb(Math.Clamp(alpha, 0, 255), CursorBlue),
            SurroundColors = new[] { Color.FromArgb(0, CursorBlue) }
        };
        g.FillPath(brush, path);
    }

    /// <summary>
    /// Rotating arc spinner shown while processing — matches Mac's
    /// BlueCursorSpinnerView (a trimmed circle with an angular gradient stroke).
    /// </summary>
    private static void DrawSpinner(Graphics g, float x, float y, float opacity)
    {
        if (opacity <= 0f) return;

        const float diameter = 14f;
        const float sweepDegrees = 252f; // 0.7 of a full circle (trim 0.15-0.85)

        DrawGlow(g, x, y, diameter / 2 + 6, (int)(150 * opacity));

        float rotation = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 800) / 800f * 360f;

        const int segments = 48;
        for (int i = 0; i < segments; i++)
        {
            float t = (float)i / segments;
            float startAngle = rotation - 90f + t * sweepDegrees;
            float sweep = sweepDegrees / segments + 0.5f;
            int alpha = (int)(t * 255f * opacity);
            using var pen = new Pen(Color.FromArgb(alpha, CursorBlue), 2.5f)
            {
                StartCap = LineCap.Round,
                EndCap = LineCap.Round
            };
            g.DrawArc(pen, x - diameter / 2, y - diameter / 2, diameter, diameter, startAngle, sweep);
        }
    }

    /// <summary>
    /// First-launch "hey! I'm Nayf" welcome bubble — matches Mac's
    /// OverlayWindow welcome sequence: bubble fades in at t=2.0s, types out
    /// one character every 0.03s, holds for 2s, then fades out by t=4.95s.
    /// </summary>
    private static void DrawWelcomeBubble(Graphics g, float anchorX, float anchorY, double elapsed)
    {
        const double bubbleStart    = 2.0;
        const double fadeInDuration = 0.4;
        const double charInterval   = 0.03;
        const double holdDuration   = 2.0;
        const double fadeOutDuration = 0.5;

        double t = elapsed - bubbleStart;
        if (t < 0) return;

        double typingDuration = WelcomeMessage.Length * charInterval;
        double end = typingDuration + holdDuration + fadeOutDuration;
        if (t > end) return;

        float bubbleOpacity;
        if (t < fadeInDuration)
        {
            double f = t / fadeInDuration;
            bubbleOpacity = (float)(f * f);
        }
        else if (t < typingDuration + holdDuration)
        {
            bubbleOpacity = 1f;
        }
        else
        {
            double f = (t - (typingDuration + holdDuration)) / fadeOutDuration;
            bubbleOpacity = (float)((1 - f) * (1 - f));
        }

        int charsToShow = t < typingDuration
            ? (int)(t / charInterval)
            : WelcomeMessage.Length;
        charsToShow = Math.Clamp(charsToShow, 0, WelcomeMessage.Length);
        if (charsToShow <= 0 || bubbleOpacity <= 0f) return;

        string text = WelcomeMessage.Substring(0, charsToShow);

        using var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
        var sz = g.MeasureString(text, font);
        float bw = sz.Width + 16;
        float bh = sz.Height + 8;

        // Bubble's left edge sits 10pt right of the cursor, vertically
        // centered 18pt below it — matches Mac's placement.
        float x = anchorX + 10;
        float y = anchorY + 18 - bh / 2;

        for (int i = 3; i >= 1; i--)
        {
            using var glow = new SolidBrush(Color.FromArgb((int)(35 * bubbleOpacity), CursorBlue));
            g.FillRoundedRect(glow, x - i * 2, y - i * 2, bw + i * 4, bh + i * 4, 6 + i * 2);
        }

        using var bg = new SolidBrush(Color.FromArgb((int)(255 * bubbleOpacity), CursorBlue));
        g.FillRoundedRect(bg, x, y, bw, bh, 6);
        using var tb = new SolidBrush(Color.FromArgb((int)(255 * bubbleOpacity), Color.White));
        g.DrawString(text, font, tb, x + 8, y + 4);
    }

    private static void DrawResponseBubble(Graphics g, float x, float y, string text)
    {
        const int maxW = 340;
        using var font = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point);
        var sz = g.MeasureString(text, font, maxW);
        float bw = Math.Min(sz.Width + 24, maxW + 24);
        float bh = sz.Height + 18;
        if (y < 0) y = 4;
        using var bg = new SolidBrush(Color.FromArgb(220, 28, 28, 30));
        g.FillRoundedRect(bg, x, y, bw, bh, 10);
        using var border = new Pen(Color.FromArgb(80, 58, 58, 60), 0.8f);
        g.DrawRoundedRect(border, x, y, bw, bh, 10);
        using var tb = new SolidBrush(Color.White);
        g.DrawString(text, font, tb, new RectangleF(x + 12, y + 9, bw - 24, bh - 18));
    }

    /// <summary>
    /// Speech bubble shown when the buddy points at a UI element — matches
    /// Mac's navigation bubble (cornerRadius 6, white text, blue glow shadow).
    /// </summary>
    private static void DrawPointingBubble(Graphics g, float x, float y, string label)
    {
        using var font = new Font("Segoe UI", 11f, FontStyle.Regular, GraphicsUnit.Point);
        var sz = g.MeasureString(label, font);
        float bw = sz.Width + 16;
        float bh = sz.Height + 8;

        // Soft glow halo behind the bubble (SwiftUI shadow equivalent)
        for (int i = 3; i >= 1; i--)
        {
            using var glow = new SolidBrush(Color.FromArgb(35, CursorBlue));
            g.FillRoundedRect(glow, x - i * 2, y - i * 2, bw + i * 4, bh + i * 4, 6 + i * 2);
        }

        using var bg = new SolidBrush(CursorBlue);
        g.FillRoundedRect(bg, x, y, bw, bh, 6);
        using var tb = new SolidBrush(Color.White);
        g.DrawString(label, font, tb, x + 8, y + 4);
    }

    /// <summary>
    /// Pulsing sonar ring around a pointed-at element — matches Mac's
    /// 1.4s ease-out expansion from 8pt to 44pt with opacity decaying 0.7→0.
    /// </summary>
    private static void DrawSonarRing(Graphics g, float cx, float cy)
    {
        const float cycleMs = 1400f;
        float phase = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % (long)cycleMs) / cycleMs;
        float easeOut = 1f - (1f - phase) * (1f - phase);
        float radius = 8 + easeOut * 36; // 8pt -> 44pt
        int alpha = (int)(0.7f * (1f - phase) * 255f);
        using var pen = new Pen(Color.FromArgb(alpha, CursorBlue), 1.5f);
        g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
    }

    // ── Layered window update ─────────────────────────────────────────────────

    private void ApplyLayeredWindow(Bitmap bitmap, int wx, int wy)
    {
        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDC    = CreateCompatibleDC(screenDC);
        IntPtr hBmp     = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp   = SelectObject(memDC, hBmp);

        var size  = new SIZE  { cx = BW, cy = BH };
        var ptSrc = new PT    { x = 0,  y = 0 };
        var ptDst = new PT    { x = wx, y = wy };
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
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            NativeMethods.PostQuitMessage(0);
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
    [DllImport("user32.dll")] private static extern bool ShowWindow(IntPtr hWnd, int n);

    [StructLayout(LayoutKind.Sequential)] private struct SIZE  { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PT    { public int x,  y;  }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
}

/// <summary>Creates a single overlay window spanning all monitors.</summary>
public sealed class OverlayWindowManager : IDisposable
{
    private readonly NativeOverlayWindow _overlay;
    public OverlayWindowManager(CompanionManager companionManager)
        => _overlay = new NativeOverlayWindow(companionManager);
    public void CreateOverlaysForAllMonitors() => _overlay.Start();
    public void Dispose() => _overlay.Dispose();
}

/// <summary>GDI+ rounded-rectangle helpers.</summary>
internal static class GraphicsExtensions
{
    public static void FillRoundedRect(this Graphics g, Brush b, float x, float y, float w, float h, float r)
    { using var p = RR(x, y, w, h, r); g.FillPath(b, p); }
    public static void DrawRoundedRect(this Graphics g, Pen p, float x, float y, float w, float h, float r)
    { using var path = RR(x, y, w, h, r); g.DrawPath(p, path); }
    private static GraphicsPath RR(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x,       y,       r*2, r*2, 180, 90);
        path.AddArc(x+w-r*2, y,       r*2, r*2, 270, 90);
        path.AddArc(x+w-r*2, y+h-r*2, r*2, r*2,   0, 90);
        path.AddArc(x,       y+h-r*2, r*2, r*2,  90, 90);
        path.CloseFigure();
        return path;
    }
}
