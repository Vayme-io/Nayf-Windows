using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Globalization;
using System.Runtime.InteropServices;
using System.Threading;

namespace NayfWindows;

/// <summary>
/// The action toast: a chip that drops in under the status pill when Vayme has finished
/// doing something on the user's behalf — an event added to their calendar, a track
/// started, an issue filed — says what it did, and leaves after a few seconds. Port of
/// the Mac's NayfActionToastPresenter (NayfActionToast.swift).
///
/// <para>It exists because a spoken "done" is gone the moment it's said. The toast is
/// the receipt: it names the thing that changed, so the user can see Vayme understood
/// them without going and looking.</para>
///
/// <para>Built like <see cref="NativeStatusPillWindow"/> — a layered, click-through,
/// top-most tool window drawn with GDI+ on its own thread — and it sits below the pill's
/// footprint rather than beside it, so the two never have to know about each other.</para>
/// </summary>
public sealed class NayfActionToast : IDisposable
{
    /// <summary>
    /// The live presenter, set by <see cref="Start"/>. Static because the callers are:
    /// the agent tool executor's methods are static, and region focus has no route to
    /// the window either. Null before startup and after shutdown, when the static
    /// entry points below quietly do nothing.
    /// </summary>
    private static NayfActionToast? _shared;

    // Segoe Fluent Icons code points, all checked by rendering them rather than by
    // trusting a chart. Calendar and Code are the same glyphs the companion panel
    // already uses for those two integrations, and Memory is the one on the memory
    // tab's empty state — so a toast names a thing with the icon the panel names it with.
    private const string CalendarGlyph = "\uE787";
    private const string CodeGlyph     = "\uE943";
    private const string MemoryGlyph   = "\uE81C";
    private const string MusicGlyph    = "\uE8D6";
    private const string RegionGlyph   = "\uE7A8";
    private const string WarningGlyph  = "\uE7BA";
    private const string CheckGlyph    = "\uE8FB";

    private readonly CompanionManager _companionManager;

    /// <summary>See <see cref="NativeStatusPillWindow"/> — the Mac's units describe a
    /// laptop screen, and the whole design is taken up together so it claims the same
    /// share of a desktop monitor that it does there.</summary>
    private const float DesignScale = 1.25f;

    // Layout in the Mac's units, multiplied by DesignScale and then the monitor's DPI
    // factor when drawn.
    private const float CardWidth = 440f;
    private const float CardHeight = 56f;      // 11 padding + 34 icon tile + 11 padding
    private const float CardCorner = 18f;

    private const float LeadingPadding = 12f;
    private const float TrailingPadding = 14f;
    private const float IconTile = 34f;
    private const float IconCorner = 10f;
    private const float IconTextGap = 11f;
    private const float CheckDiameter = 20f;
    private const float TextCheckGap = 10f;

    private const float TitleFontSize = 13.5f;
    private const float SubtitleFontSize = 12f;
    private const float IconFontSize = 16f;
    private const float CheckFontSize = 11f;

    private const float TitleLineHeight = 19f;
    private const float SubtitleLineHeight = 17f;
    private const float LineGap = 2f;

    /// <summary>
    /// How far down the screen the card settles. The Mac tucks its toast against the top
    /// edge, just under the notch; on Windows that edge belongs to the status pill, which
    /// is 28 tall and casts a shadow past that. This clears both — including during the
    /// entry animation, which starts <see cref="EntryRise"/> higher than it ends.
    /// </summary>
    private const float TopReserve = 50f;

    /// <summary>Slack around the card inside the bitmap: shadow, antialiased edges, and
    /// the overshoot the spring adds at the end of its travel.</summary>
    private const float BitmapMargin = 18f;

    /// <summary>How long the card stays up once it has arrived.</summary>
    private static readonly TimeSpan VisibleDuration = TimeSpan.FromSeconds(4);

    // Entry copies the Mac's SwiftUI spring, including the settle at 0.82 damping.
    private const float SpringResponse = 0.42f;
    private const float SpringDamping = 0.82f;
    private const float EnterScale = 0.92f;
    private const float EntryRise = 10f;

    /// <summary>The Mac leaves on an easeIn rather than a spring — it should look like
    /// it's done rather than like it's being pulled away.</summary>
    private const float RetractSeconds = 0.22f;

    private const float CornerExponent = 2.2f;

    // The Mac chip is near-black (8,10,15). This one takes the companion panel's tint
    // instead, for the same reason the status pill does: three surfaces that appear over
    // the user's desktop within seconds of each other have to read as one app.
    private static readonly Color CardFill = Color.FromArgb(209, 24, 24, 27);
    private static readonly Color TitleColor = Color.FromArgb(255, 0xF5, 0xF5, 0xF7);
    /// <summary>The Mac's white at 62% — alpha here scales glyph coverage, see <see cref="CoverageToAlpha"/>.</summary>
    private static readonly Color SubtitleColor = Color.FromArgb(158, 255, 255, 255);
    private static readonly Color IconColor = Color.FromArgb(255, 255, 255, 255);
    private static readonly Color CheckColor = Color.FromArgb(235, 255, 255, 255);

    /// <summary>The status pill's "speaking" orange, reused for the one toast that
    /// reports a failure — the cursor colour would congratulate the user on it.</summary>
    private static readonly Color WarningAccent = Color.FromArgb(255, 255, 140, 56);

    private const int ShadowLayers = 5;
    private const float ShadowStep = 1.5f;

    private static readonly string TitleFace = ResolveFace(
        "Segoe UI Variable Text Semibold", "Segoe UI Semibold", "Segoe UI");
    private static readonly string BodyFace = ResolveFace(
        "Segoe UI Variable Text", "Segoe UI");
    private static readonly string IconFace = ResolveFace(
        "Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol");

    /// <summary>
    /// Picks the first installed face from a preference list. A face that isn't there
    /// would otherwise fail inside CreateFontW on the render thread, where it takes the
    /// toast out with nothing to show for it.
    /// </summary>
    private static string ResolveFace(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                using var family = new FontFamily(name);
                if (family.IsStyleAvailable(FontStyle.Regular)) return name;
            }
            catch (ArgumentException) { /* not installed — try the next one */ }
        }
        return FontFamily.GenericSansSerif.Name;
    }

    /// <summary>One toast. Immutable, so the render thread can read it without a lock
    /// once it has the reference.</summary>
    private sealed record ToastContent(
        string Glyph, string Title, string? Subtitle, Color Accent, bool ShowCheck);

    private readonly object _gate = new();
    private ToastContent? _content;
    private DateTimeOffset _hideAt;

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
    /// <summary>0 to 1 across the retraction, once the toast's time is up.</summary>
    private float _retract = 1f;
    private float _retractFrom;
    private bool _wasShowing;
    private bool _isWindowShown;

    private int _windowX, _windowY;
    private int _bitmapWidth, _bitmapHeight;
    private float _scale = 1f;

    /// <summary>Which part of the card a sprite belongs to — it decides the face, weight,
    /// size, colour and box, so the text alone is enough to key the cache on.</summary>
    private enum SpriteKind { Title, Subtitle, Icon, Check }

    // Rasterised text, keyed by kind and content. Only ever touched from the render thread.
    private readonly Dictionary<(SpriteKind, string), Bitmap> _sprites = new();
    private float _spriteScale = -1f;
    /// <summary>Four sprites make a card, so this is a couple of dozen toasts' worth.</summary>
    private const int MaxCachedSprites = 64;

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

    public NayfActionToast(CompanionManager companionManager)
    {
        _companionManager = companionManager;
    }

    public void Start()
    {
        _shared = this;
        _messageThread = new Thread(RunWindowThread)
        {
            Name = "NayfActionToast",
            IsBackground = true
        };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    // MARK: - What the rest of the app calls

    /// <summary>
    /// Confirms something Vayme just did. Shows the chip in the user's cursor colour with
    /// a check on the end, and plays the completion chime — one call, so a caller can
    /// never end up with the sound and not the toast or the other way round.
    /// </summary>
    private static void ShowCompleted(string glyph, string title, string? subtitle)
    {
        var toast = _shared;
        if (toast == null) return;

        toast.Present(new ToastContent(
            glyph, title, subtitle,
            toast._companionManager.SelectedCursorColor.ToDrawingColor(),
            ShowCheck: true));
        NayfSoundPlayer.Shared.PlayTaskComplete();
    }

    /// <summary>
    /// Reports something that didn't work. Amber rather than the cursor colour, no check,
    /// and no chime — the completion sound on a failure is worse than silence.
    /// </summary>
    private static void ShowWarning(string glyph, string title, string? subtitle)
    {
        _shared?.Present(new ToastContent(glyph, title, subtitle, WarningAccent, ShowCheck: false));
    }

    /// <summary>An event landed in the user's calendar. Names it and says when it is.</summary>
    public static void ShowCalendarEventAdded(string summary, string? startDateTimeISO)
    {
        var when = FormatEventStart(startDateTimeISO);
        ShowCompleted(CalendarGlyph, "Added to your calendar",
                      when == null ? summary : $"{summary} · {when}");
    }

    public static void ShowCalendarEventRemoved() =>
        ShowCompleted(CalendarGlyph, "Removed from your calendar", "The event was deleted");

    public static void ShowIssueCreated(string title, string owner, string repo) =>
        ShowCompleted(CodeGlyph, "Issue created", $"{title} · {owner}/{repo}");

    public static void ShowNowPlaying(string track, string artist) =>
        ShowCompleted(MusicGlyph, "Now playing", artist.Length > 0 ? $"{track} · {artist}" : track);

    /// <summary>
    /// One toast for a batch of facts, not one per fact — a single exchange can produce
    /// several, and three chips in a row for one answer is noise rather than a receipt.
    /// </summary>
    public static void ShowMemorySaved(IReadOnlyList<string> facts)
    {
        if (facts.Count == 0) return;
        ShowCompleted(MemoryGlyph, "Saved to memory",
                      facts.Count == 1 ? facts[0] : $"{facts.Count} things noted");
    }

    /// <summary>
    /// The user circled something without saying anything. Without this the gesture has
    /// no visible result at all until they ask their next question, which reads as the
    /// lasso having failed.
    /// </summary>
    public static void ShowRegionFocused() =>
        ShowCompleted(RegionGlyph, "Region focused", "Hold Ctrl+Alt and ask about it");

    public static void ShowRegionCaptureFailed() =>
        ShowWarning(WarningGlyph, "Couldn't capture that region", "Try circling it again");

    /// <summary>
    /// Speech came back with no words. Said out loud in a chip because the alternative is
    /// what users actually reported: the pill lights up when they talk and then nothing
    /// happens, which is indistinguishable from Vayme having ignored them.
    /// </summary>
    public static void ShowNotUnderstood() =>
        ShowWarning(WarningGlyph, "Didn't catch that", "Hold Ctrl+Alt and say it again");

    /// <summary>
    /// Formats a calendar event's start the way the Mac does. Returns null when the
    /// Worker hands back something unparseable, in which case the subtitle is just the
    /// event's name — a wrong time is worse than no time.
    /// </summary>
    private static string? FormatEventStart(string? iso)
    {
        if (string.IsNullOrWhiteSpace(iso)) return null;
        if (!DateTimeOffset.TryParse(iso, CultureInfo.InvariantCulture,
                                     DateTimeStyles.AssumeLocal, out var start)) return null;

        // An all-day event arrives as a bare yyyy-MM-dd, which parses to midnight. Saying
        // "12:00 AM" about it would be inventing a time the user never chose.
        bool allDay = !iso.Contains('T', StringComparison.Ordinal);
        return allDay
            ? start.ToString("ddd, MMM d", CultureInfo.CurrentCulture)
            : $"{start:ddd, MMM d} · {start.ToString("h:mm tt", CultureInfo.CurrentCulture)}";
    }

    /// <summary>
    /// Puts a toast up, or replaces the one already showing. Replacing swaps the content
    /// and restarts the clock without re-animating: a second confirmation arriving while
    /// the first is still up should read as the chip updating, not flickering.
    /// </summary>
    private void Present(ToastContent content)
    {
        lock (_gate)
        {
            _content = content;
            _hideAt = DateTimeOffset.UtcNow + VisibleDuration;
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
            lpszClassName = "NayfActionToast"
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW |
            WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "NayfActionToast", "NayfActionToast",
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
                NativeMethods.SetWindowPos(_hwnd, OverlayZOrder.InsertAfter(), 0, 0, 0, 0,
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

        ToastContent? content;
        bool expired;
        lock (_gate)
        {
            content = _content;
            expired = content != null && now >= _hideAt;
        }

        bool shouldShow = content != null && !expired;

        // Nothing up and nothing left to fade — the case that holds all day, so it costs
        // one lock and a comparison before returning.
        if (!shouldShow && _retract >= 1f)
        {
            HideWindowIfShown();
            if (expired)
                lock (_gate)
                {
                    // Only if nothing arrived since; a toast raised microseconds ago
                    // must not be thrown away by the frame that retired its predecessor.
                    if (ReferenceEquals(_content, content)) _content = null;
                }
            _wasShowing = false;
            return;
        }

        if (content == null) return;

        bool isFirstFrame = !_isWindowShown;
        if (isFirstFrame) PositionOnMonitorUnderCursor();

        float visual = AdvancePresence(shouldShow, elapsed);

        using var bitmap = new Bitmap(_bitmapWidth, _bitmapHeight, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.TextRenderingHint = TextRenderingHint.AntiAlias;
        g.Clear(Color.Transparent);

        DrawCard(g, content, visual);

        // Paint before the first ShowWindow, so the chip never flashes empty.
        if (!_isWindowShown)
            NativeMethods.SetWindowPos(_hwnd, OverlayZOrder.InsertAfter(),
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
    /// Moves the chip toward present or absent and hands back how visible it is now.
    /// Two curves, because they say different things: a spring on the way in, which
    /// overshoots slightly and settles, and a plain easeIn on the way out.
    /// </summary>
    private float AdvancePresence(bool shouldShow, float elapsed)
    {
        if (shouldShow)
        {
            if (!_wasShowing)
            {
                // Caught mid-retreat by a new toast. The spring picks up from whatever is
                // actually on screen rather than from where the last one left _presence,
                // which is the difference between resuming and jumping.
                if (_retract > 0f && _retract < 1f) _presence = _retractFrom * RetractCurve(_retract);
                else if (_retract >= 1f) _presence = 0f;
                _presenceVelocity = 0f;
                _retract = 0f;
                _wasShowing = true;
            }

            AdvanceSpring(1f, elapsed);
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

    /// <summary>How much is left at <paramref name="t"/> through the retraction. Quadratic,
    /// so it lets go gently and then goes — SwiftUI's easeIn, near enough.</summary>
    private static float RetractCurve(float t) => 1f - t * t;

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
        _presence = 0f;
        _presenceVelocity = 0f;
    }

    /// <summary>
    /// Centres the card on whichever monitor the cursor is on and reads that monitor's
    /// DPI, so the chip is the same physical size everywhere. Work area rather than
    /// monitor bounds, so a taskbar docked to the top doesn't sit on top of it.
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

        _bitmapWidth = (int)MathF.Ceiling((CardWidth + BitmapMargin * 2) * _scale);
        _bitmapHeight = (int)MathF.Ceiling((TopReserve + CardHeight + BitmapMargin) * _scale);
        _windowX = workArea.Left + (workArea.Right - workArea.Left - _bitmapWidth) / 2;
        _windowY = workArea.Top;
    }

    // MARK: - Drawing

    private void DrawCard(Graphics g, ToastContent content, float visual)
    {
        float width = CardWidth * _scale;
        float height = CardHeight * _scale;
        float left = BitmapMargin * _scale;
        float top = TopReserve * _scale;

        // Grows into place from 92% about its own top edge and drops the last few pixels,
        // so it reads as arriving from behind the pill rather than being flown in.
        float grow = EnterScale + (1f - EnterScale) * visual;
        float rise = -EntryRise * _scale * (1f - visual);

        var saved = g.Save();
        g.TranslateTransform(left + width / 2f, top + rise);
        g.ScaleTransform(grow, grow);
        g.TranslateTransform(-(left + width / 2f), -top);

        // The spring overshoots past 1 on the way in; opacity must not.
        int alpha = (int)(255 * Math.Clamp(visual, 0f, 1f));
        float radius = CardCorner * _scale;
        var accent = content.Accent;

        using (var path = RoundedRect(left, top, width, height, radius))
        {
            DrawShadow(g, path, left, top, width, height, radius, alpha);

            using var fill = new SolidBrush(Scaled(CardFill, alpha));
            g.FillPath(fill, path);

            // The accent wash: a diagonal bloom of the cursor colour rising out of the
            // bottom-left corner. Clipped to the card, because a gradient brush fills a
            // rectangle and the corners are round.
            var previousClip = g.Clip;
            g.SetClip(path, CombineMode.Intersect);
            DrawAccentWash(g, left, top, width, height, accent, alpha);
            g.Clip = previousClip;
        }

        DrawRim(g, left, top, width, height, radius, accent, alpha);

        float iconLeft = left + LeadingPadding * _scale;
        float iconTop = top + (CardHeight - IconTile) / 2f * _scale;
        DrawIconTile(g, content.Glyph, iconLeft, iconTop, accent, alpha);

        float textLeft = iconLeft + (IconTile + IconTextGap) * _scale;
        float textRight = left + width - (TrailingPadding + CheckDiameter + TextCheckGap) * _scale;
        DrawText(g, content, textLeft, top, textRight - textLeft, alpha);

        if (content.ShowCheck)
        {
            float checkLeft = left + width - (TrailingPadding + CheckDiameter) * _scale;
            float checkTop = top + (CardHeight - CheckDiameter) / 2f * _scale;
            DrawCheckBadge(g, checkLeft, checkTop, alpha);
        }

        g.Restore(saved);
    }

    /// <summary>
    /// The soft shadow the companion panel casts, approximated by stacking the card's own
    /// outline at growing sizes — GDI+ has no blur. Clipped to everything <i>outside</i>
    /// the card, because the fill is translucent and shadow left underneath it would
    /// darken the glass rather than the desktop.
    /// </summary>
    private void DrawShadow(Graphics g, GraphicsPath cardPath, float left, float top,
                            float width, float height, float radius, int alpha)
    {
        if (alpha <= 0) return;

        // Generous bounds: a scale transform is in effect, so this rect is in world units
        // and only has to be big enough to cover the whole bitmap.
        using var outside = new Region(
            new RectangleF(-_bitmapWidth, -_bitmapHeight, _bitmapWidth * 3, _bitmapHeight * 3));
        outside.Exclude(cardPath);
        g.Clip = outside;

        for (int layer = ShadowLayers; layer >= 1; layer--)
        {
            float spread = layer * ShadowStep * _scale;
            using var brush = new SolidBrush(Color.FromArgb(alpha * 11 / 255, 0, 0, 0));
            using var path = RoundedRect(
                left - spread, top - spread, width + spread * 2f, height + spread * 2f, radius + spread);
            g.FillPath(brush, path);
        }

        g.ResetClip();
    }

    /// <summary>
    /// The accent bloom, bottom-leading to top-trailing. Every stop keeps the accent's own
    /// RGB — fading to a transparent black instead would drag a grey smear through the
    /// middle of the gradient, because GDI+ interpolates the colour channels whether the
    /// pixel is visible or not.
    /// </summary>
    private static void DrawAccentWash(Graphics g, float left, float top, float width, float height,
                                       Color accent, int alpha)
    {
        if (alpha <= 0) return;

        var area = new RectangleF(left, top, width, height);
        using var brush = new LinearGradientBrush(
            new PointF(left, top + height), new PointF(left + width, top),
            Color.Transparent, Color.Transparent);
        brush.InterpolationColors = new ColorBlend
        {
            Colors =
            [
                Color.FromArgb(alpha * 56 / 255, accent),  // 0.22
                Color.FromArgb(alpha * 13 / 255, accent),  // 0.05
                Color.FromArgb(0, accent)
            ],
            Positions = [0f, 0.5f, 1f]
        };
        brush.WrapMode = WrapMode.TileFlipXY;
        g.FillRectangle(brush, area);
    }

    /// <summary>
    /// The lit rim: white, through the accent, back to a dimmer white — the chip catching
    /// light from the top-right. Inset by half the pen width so the line lands inside the
    /// shape instead of straddling the edge, where half of it would blur into the desktop.
    /// </summary>
    private void DrawRim(Graphics g, float left, float top, float width, float height,
                         float radius, Color accent, int alpha)
    {
        if (alpha <= 0) return;

        float penWidth = MathF.Max(1f, MathF.Round(_scale));
        float inset = penWidth / 2f;

        using var brush = new LinearGradientBrush(
            new PointF(left + width, top), new PointF(left, top + height),
            Color.Transparent, Color.Transparent);
        brush.InterpolationColors = new ColorBlend
        {
            Colors =
            [
                Color.FromArgb(alpha * 140 / 255, 255, 255, 255), // 0.55
                Color.FromArgb(alpha * 179 / 255, accent),        // 0.7
                Color.FromArgb(alpha * 56 / 255, 255, 255, 255)   // 0.22
            ],
            Positions = [0f, 0.5f, 1f]
        };
        brush.WrapMode = WrapMode.TileFlipXY;

        using var pen = new Pen(brush, penWidth);
        using var path = RoundedRect(left + inset, top + inset,
                                     width - penWidth, height - penWidth, radius - inset);
        g.DrawPath(pen, path);
    }

    /// <summary>The leading tile: the accent as a gradient, the action's glyph on top of
    /// it, and a glow of the same colour underneath so it lifts off the card.</summary>
    private void DrawIconTile(Graphics g, string glyph, float x, float y, Color accent, int alpha)
    {
        if (alpha <= 0) return;

        float size = IconTile * _scale;
        float radius = IconCorner * _scale;

        // The Mac's shadow(accent 0.5, radius 6, y 2), stacked rather than blurred.
        for (int layer = 4; layer >= 1; layer--)
        {
            float spread = layer * 1.5f * _scale;
            using var glow = new SolidBrush(Color.FromArgb(alpha * 16 / 255, accent));
            using var path = RoundedRect(x - spread, y - spread + 2f * _scale,
                                         size + spread * 2f, size + spread * 2f, radius + spread);
            g.FillPath(glow, path);
        }

        using (var tile = RoundedRect(x, y, size, size, radius))
        using (var brush = new LinearGradientBrush(
                   new PointF(x, y), new PointF(x + size, y + size),
                   Color.FromArgb(alpha, accent),
                   Color.FromArgb(alpha * 184 / 255, accent)))
        {
            brush.WrapMode = WrapMode.TileFlipXY;
            g.FillPath(brush, tile);
        }

        DrawSprite(g, SpriteKind.Icon, glyph, x, y, alpha);
    }

    /// <summary>The trailing check: a soft white disc with the tick inside it.</summary>
    private void DrawCheckBadge(Graphics g, float x, float y, int alpha)
    {
        if (alpha <= 0) return;

        float size = CheckDiameter * _scale;

        using (var fill = new SolidBrush(Color.FromArgb(alpha * 31 / 255, 255, 255, 255))) // 0.12
            g.FillEllipse(fill, x, y, size, size);

        using (var pen = new Pen(Color.FromArgb(alpha * 71 / 255, 255, 255, 255), 1f)) // 0.28
            g.DrawEllipse(pen, x + 0.5f, y + 0.5f, size - 1f, size - 1f);

        DrawSprite(g, SpriteKind.Check, CheckGlyph, x, y, alpha);
    }

    /// <summary>
    /// Title over subtitle, the pair centred in the card. With no subtitle the title
    /// centres on its own — a lone line sitting high with empty space under it looks
    /// like the second line failed to load.
    /// </summary>
    private void DrawText(Graphics g, ToastContent content, float x, float cardTop, float width, int alpha)
    {
        if (alpha <= 0 || width <= 0) return;

        bool hasSubtitle = !string.IsNullOrWhiteSpace(content.Subtitle);
        float blockHeight = hasSubtitle
            ? (TitleLineHeight + LineGap + SubtitleLineHeight) * _scale
            : TitleLineHeight * _scale;
        float y = cardTop + (CardHeight * _scale - blockHeight) / 2f;

        DrawSprite(g, SpriteKind.Title, content.Title, x, y, alpha, (int)MathF.Ceiling(width));

        if (hasSubtitle)
            DrawSprite(g, SpriteKind.Subtitle, content.Subtitle!,
                       x, y + (TitleLineHeight + LineGap) * _scale, alpha, (int)MathF.Ceiling(width));
    }

    /// <summary>
    /// Composites one cached sprite at a fraction of its own alpha, so the whole card can
    /// fade as a unit.
    /// </summary>
    private void DrawSprite(Graphics g, SpriteKind kind, string text, float x, float y,
                            int alpha, int? width = null)
    {
        var sprite = Sprite(kind, text, width);
        if (sprite == null) return;

        using var attributes = new ImageAttributes();
        var fade = new ColorMatrix { Matrix33 = Math.Clamp(alpha, 0, 255) / 255f };
        attributes.SetColorMatrix(fade);

        // Whole pixels only. The sprite's crispness comes from glyphs aligned to the pixel
        // grid, and landing it on a half pixel would resample that straight back out.
        var target = new Rectangle((int)MathF.Round(x), (int)MathF.Round(y),
                                   sprite.Width, sprite.Height);
        g.DrawImage(sprite, target, 0, 0, sprite.Width, sprite.Height, GraphicsUnit.Pixel, attributes);
    }

    /// <summary>
    /// Rasterises one run of text through GDI rather than GDI+, and hands back a
    /// straight-ARGB sprite of it. Same reasoning as the status pill's TitleSprite: GDI+
    /// spends this many pixels on smoothing and the label reads soft, GDI's grayscale
    /// rasteriser spends them on contrast and it reads blocky, so ClearType is asked for
    /// to sample coverage three times per pixel and <see cref="CoverageToAlpha"/> averages
    /// the triplets back down — the extra horizontal resolution survives as a
    /// better-estimated edge, the colour fringes cancel before reaching the alpha channel.
    ///
    /// GDI cannot draw with an alpha channel at all, hence the white-on-black mask.
    /// </summary>
    private Bitmap? Sprite(SpriteKind kind, string text, int? width)
    {
        if (text.Length == 0) return null;

        // Everything is sized off the monitor, so moving between displays invalidates
        // every sprite built for the old one.
        if (_scale != _spriteScale)
        {
            foreach (var stale in _sprites.Values) stale.Dispose();
            _sprites.Clear();
            _spriteScale = _scale;
        }

        var key = (kind, text);
        if (_sprites.TryGetValue(key, out var cached)) return cached;

        // Titles come from a fixed set of seven, but subtitles are whatever the user named
        // their meeting or their playlist — so the cache is keyed on text that never stops
        // being new. Dropping the lot on the rare occasion it fills costs four rasterisations.
        if (_sprites.Count >= MaxCachedSprites)
        {
            foreach (var stale in _sprites.Values) stale.Dispose();
            _sprites.Clear();
        }

        var (face, weight, fontSize, color, boxHeight, format) = kind switch
        {
            SpriteKind.Title => (TitleFace, FW_SEMIBOLD, TitleFontSize, TitleColor,
                                 TitleLineHeight, DT_LEFT | DT_END_ELLIPSIS),
            SpriteKind.Subtitle => (BodyFace, FW_NORMAL, SubtitleFontSize, SubtitleColor,
                                    SubtitleLineHeight, DT_LEFT | DT_END_ELLIPSIS),
            SpriteKind.Icon => (IconFace, FW_NORMAL, IconFontSize, IconColor,
                                IconTile, DT_CENTER),
            _ => (IconFace, FW_NORMAL, CheckFontSize, CheckColor, CheckDiameter, DT_CENTER)
        };

        int spriteWidth = width ?? (int)MathF.Ceiling(boxHeight * _scale);
        int spriteHeight = (int)MathF.Ceiling(boxHeight * _scale);
        if (spriteWidth <= 0 || spriteHeight <= 0) return null;

        // Built before the bitmap, because bailing out after the Graphics exists means
        // disposing a bitmap the Graphics still holds a device context on.
        IntPtr font = CreateFontW(-(int)MathF.Round(fontSize * _scale), 0, 0, 0, weight,
                                  0, 0, 0, DEFAULT_CHARSET, 0, 0, CLEARTYPE_QUALITY, 0, face);
        if (font == IntPtr.Zero) return null;

        var sprite = new Bitmap(spriteWidth, spriteHeight, PixelFormat.Format32bppArgb);
        using (var sg = Graphics.FromImage(sprite))
        {
            // Opaque black, so every byte GDI leaves behind is glyph coverage and nothing
            // else — the alpha byte it zeroes included.
            sg.Clear(Color.Black);

            IntPtr hdc = sg.GetHdc();
            try
            {
                IntPtr previousFont = SelectObject(hdc, font);
                SetBkMode(hdc, TRANSPARENT_BK);
                SetTextColor(hdc, 0x00FFFFFF);

                var box = new NativeMethods.RECT
                {
                    Left = 0, Top = 0, Right = spriteWidth, Bottom = spriteHeight
                };
                DrawTextW(hdc, text, text.Length, ref box,
                          format | DT_VCENTER | DT_SINGLELINE | DT_NOPREFIX);

                SelectObject(hdc, previousFont);
            }
            finally { sg.ReleaseHdc(hdc); }
        }
        DeleteObject(font);

        CoverageToAlpha(sprite, color);
        _sprites[key] = sprite;
        return sprite;
    }

    /// <summary>
    /// Turns the white-on-black mask GDI produced into a straight-ARGB sprite: coverage
    /// becomes the alpha channel, scaled by the colour's own alpha, and every pixel takes
    /// the colour. Straight rather than premultiplied because GDI+ composites it, and
    /// GDI+ premultiplies on the way out — the same reasoning as <see cref="Scaled"/>.
    /// </summary>
    private static void CoverageToAlpha(Bitmap sprite, Color color)
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
                int coverage = (pixels[i] + pixels[i + 1] + pixels[i + 2] + 1) / 3;
                pixels[i + 3] = (byte)(coverage * color.A / 255);
                pixels[i]     = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
            }

            Marshal.Copy(pixels, 0, data.Scan0, pixels.Length);
        }
        finally { sprite.UnlockBits(data); }
    }

    /// <summary>
    /// Fades a colour by the card's overall presence.
    /// </summary>
    /// <remarks>
    /// No premultiplication here, even though UpdateLayeredWindow wants premultiplied
    /// alpha: the bitmap is cleared to transparent black, so GDI+'s own source-over
    /// already writes colour × alpha. Doing it a second time by hand over-darkens.
    /// </remarks>
    private static Color Scaled(Color c, int alpha) =>
        Color.FromArgb(Math.Clamp(c.A * alpha / 255, 0, 255), c.R, c.G, c.B);

    /// <summary>
    /// A rounded rectangle with Apple's continuous corners rather than plain arcs — a
    /// circular arc meets the straight edge at a visible kink at this radius, and the
    /// card's 18 is large enough for that to show.
    /// </summary>
    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Max(0f, MathF.Min(r, MathF.Min(w / 2f, h / 2f)));

        // Walked clockwise from the left edge, and every arc has to run in that same
        // direction: the four corners are one closed polygon, so an arc traversed
        // backwards doesn't mirror, it puts a chord across the corner it should round.
        const int steps = 16;
        var points = new PointF[(steps + 1) * 4];
        int i = 0;

        // Top-left, from the left edge round to the top edge.
        for (int s = 0; s <= steps; s++)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + r - r * cx, y + r - r * cy);
        }

        // Top-right, from the top edge round to the right edge.
        for (int s = 0; s <= steps; s++)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + w - r + r * cy, y + r - r * cx);
        }

        // Bottom-right, from the right edge round to the bottom edge.
        for (int s = 0; s <= steps; s++)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + w - r + r * cx, y + h - r + r * cy);
        }

        // Bottom-left, from the bottom edge round to the left edge.
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
    /// A point on the unit superellipse quarter, from (1,0) at t=0 to (0,1) at t=1. With
    /// <see cref="CornerExponent"/> of 2 this is exactly a circular arc; higher values
    /// flatten the flanks toward Apple's continuous corner.
    /// </summary>
    private static (float X, float Y) SuperellipsePoint(float t)
    {
        double angle = t * Math.PI / 2.0;
        double p = 2.0 / CornerExponent;
        return ((float)Math.Pow(Math.Cos(angle), p), (float)Math.Pow(Math.Sin(angle), p));
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
        if (ReferenceEquals(_shared, this)) _shared = null;
        _renderTimer?.Dispose();
        _topmostTimer?.Dispose();
        foreach (var sprite in _sprites.Values) sprite.Dispose();
        _sprites.Clear();
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

    // Text rasterisation — see Sprite for why the labels go through GDI.
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

    private const int FW_NORMAL = 400;
    private const int FW_SEMIBOLD = 600;
    private const uint DEFAULT_CHARSET = 1;
    private const uint CLEARTYPE_QUALITY = 5;
    private const int TRANSPARENT_BK = 1;
    private const uint DT_LEFT = 0x0000;
    private const uint DT_CENTER = 0x0001;
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
