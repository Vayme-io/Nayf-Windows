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
/// A small always-on-top widget pinned over the bottom-left of the taskbar —
/// "where Nayf lives". Shows the Nayf logo plus a live voice indicator (an
/// audio-reactive equalizer while listening/speaking, pulsing dots while
/// thinking). Clicking it opens the Nayf panel. A GDI layered window, so it has
/// no border and blends onto the taskbar. Inspired by the Mac notch HUD.
/// </summary>
public sealed class TaskbarWidgetWindow : IDisposable
{
    public event Action? Tapped;

    private readonly CompanionManager _companionManager;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly Image? _logo = LoadLogo();

    private static Image? LoadLogo()
    {
        try
        {
            var path = System.IO.Path.Combine(AppContext.BaseDirectory, "Assets", "NayfIcon.png");
            return System.IO.File.Exists(path) ? Image.FromFile(path) : null;
        }
        catch { return null; }
    }

    private const int W = 226;
    private const int H = 40;

    // Smoothing state for the equalizer so it swells/settles instead of jittering.
    private float _smoothedLevel;
    private readonly float[] _barHeights = new float[5];

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private NativeMethods.WndProc? _wndProcDelegate;
    private System.Threading.Timer? _renderTimer;
    private System.Threading.Timer? _topmostTimer;
    private int _originX, _originY;

    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const uint ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;
    private const uint WM_LBUTTONUP = 0x0202;
    private const uint WM_MOUSEMOVE = 0x0200;
    private const uint WM_MOUSELEAVE = 0x02A3;
    private const uint TME_LEAVE = 0x00000002;

    private bool _hovering;

    public TaskbarWidgetWindow(CompanionManager companionManager)
    {
        _companionManager = companionManager;
    }

    /// <summary>An anchor rect at the widget's position, for opening the panel above it.</summary>
    public NativeMethods.RECT AnchorRect => new()
    {
        Left = _originX,
        Top = _originY,
        Right = _originX + W,
        Bottom = _originY + H
    };

    public void Start()
    {
        _messageThread = new Thread(RunWindowThread) { Name = "NayfTaskbarWidget", IsBackground = true };
        _messageThread.SetApartmentState(ApartmentState.STA);
        _messageThread.Start();
    }

    private void RunWindowThread()
    {
        _wndProcDelegate = WndProc;
        var wndClass = new NativeMethods.WNDCLASSEX
        {
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            lpfnWndProc = _wndProcDelegate,
            hInstance = NativeMethods.GetModuleHandle(null),
            // Without a class cursor Windows never changes the pointer over the
            // window, so the "app starting" spinner sticks forever on hover.
            hCursor = NativeMethods.LoadCursor(IntPtr.Zero, NativeMethods.IDC_HAND),
            lpszClassName = "NayfTaskbarWidget"
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        // Sit in the taskbar strip at the bottom-left.
        int screenH = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
        var wa = new NativeMethods.RECT();
        NativeMethods.SystemParametersInfo(NativeMethods.SPI_GETWORKAREA, 0, ref wa, 0);
        int taskbarH = screenH - wa.Bottom;
        if (taskbarH <= 0) taskbarH = 48;
        // Sit just right of the Windows weather/widgets button in the corner.
        _originX = 210;
        _originY = wa.Bottom + (taskbarH - H) / 2;

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            "NayfTaskbarWidget", "NayfTaskbarWidget",
            WS_POPUP,
            _originX, _originY, W, H,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        RenderFrame();
        // ~33 fps for smooth equalizer animation.
        _renderTimer = new System.Threading.Timer(_ => RenderFrame(), null,
            TimeSpan.FromMilliseconds(30), TimeSpan.FromMilliseconds(30));
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
    {
        switch (msg)
        {
            case WM_LBUTTONUP:
                Tapped?.Invoke();
                return IntPtr.Zero;

            case WM_MOUSEMOVE:
                if (!_hovering)
                {
                    _hovering = true;
                    var tme = new TRACKMOUSEEVENT
                    {
                        cbSize = (uint)Marshal.SizeOf<TRACKMOUSEEVENT>(),
                        dwFlags = TME_LEAVE,
                        hwndTrack = _hwnd,
                        dwHoverTime = 0
                    };
                    TrackMouseEvent(ref tme);
                }
                return IntPtr.Zero;

            case WM_MOUSELEAVE:
                _hovering = false;
                return IntPtr.Zero;
        }
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero) return;

        using var bitmap = new Bitmap(W, H, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        // ClearType fringes badly on per-pixel-alpha windows — use grayscale AA.
        g.TextRenderingHint = TextRenderingHint.AntiAliasGridFit;
        g.Clear(Color.Transparent);

        var accent = NativeOverlayWindow.CursorBlue;
        var state = _companionManager.VoiceState;
        float t = (float)_clock.Elapsed.TotalSeconds;
        int cy = H / 2;

        // No visible background — but layered windows only hit-test non-transparent
        // pixels, so paint an almost-invisible fill (≈1% alpha) across the whole
        // chip to keep the entire area clickable.
        using (var hit = new SolidBrush(Color.FromArgb(3, 0, 0, 0)))
            g.FillRoundedRect(hit, 0, 0, W, H, 11);

        // ── Logo: the Nayf mark (static) ──
        DrawLogo(g, 21, cy, accent, 1f);

        // ── Two-line label: "Nayf" + live status subtitle ──
        string subtitle = state switch
        {
            CompanionVoiceState.Listening => "Listening…",
            CompanionVoiceState.Processing => "Thinking…",
            CompanionVoiceState.Responding => "Speaking…",
            _ => "Ready"
        };
        using (var title = new Font("Segoe UI", 9.75f, FontStyle.Bold, GraphicsUnit.Point))
        using (var titleBrush = new SolidBrush(Color.FromArgb(244, 244, 244, 248)))
            g.DrawString("Nayf", title, titleBrush, 41, cy - 12);
        using (var sub = new Font("Segoe UI", 7.75f, FontStyle.Regular, GraphicsUnit.Point))
        using (var subBrush = new SolidBrush(Color.FromArgb(165, 190, 190, 200)))
            g.DrawString(subtitle, sub, subBrush, 42, cy + 1.5f);

        // ── Right side: live voice indicator, or a calm idle waveform ──
        int rx = W - 60;
        switch (state)
        {
            case CompanionVoiceState.Listening:
                // Green + audio-reactive while the user is speaking. AudioPowerLevel
                // is now a device peak (0–1); a modest gain makes it lively.
                DrawEqualizer(g, rx, cy, Color.FromArgb(255, 52, 199, 89),
                    Math.Clamp(_companionManager.AudioPowerLevel * 2.2f, 0f, 1f), t);
                break;
            case CompanionVoiceState.Responding:
                DrawEqualizer(g, rx, cy, Color.FromArgb(255, 255, 140, 40), 0.55f, t);
                break;
            case CompanionVoiceState.Processing:
                DrawThinkingDots(g, rx, cy, Color.FromArgb(255, 158, 107, 245), t);
                break;
            default:
                DrawIdleWave(g, rx, cy, accent, t);
                break;
        }

        ApplyLayeredWindow(bitmap, _originX, _originY);
    }

    /// <summary>Draws the Nayf logo centered at (x, cy), scaled by the breath factor.</summary>
    private void DrawLogo(Graphics g, float x, float cy, Color accent, float scale)
    {
        if (_logo == null)
        {
            // Fallback: a glowing orb in the cursor color.
            using var core = new SolidBrush(accent);
            float cr = 6.5f * scale;
            g.FillEllipse(core, x - cr, cy - cr, cr * 2, cr * 2);
            return;
        }

        float size = 24f * scale;
        var prev = g.InterpolationMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.DrawImage(_logo, x - size / 2, cy - size / 2, size, size);
        g.InterpolationMode = prev;
    }

    /// <summary>A slow, low, traveling-wave shimmer shown while idle — Nayf "breathing".</summary>
    private static void DrawIdleWave(Graphics g, int x, int cy, Color color, float t)
    {
        const int bars = 5, barW = 3, gap = 4;
        for (int i = 0; i < bars; i++)
        {
            float wave = (float)(Math.Sin(t * 2.1 - i * 0.7) * 0.5 + 0.5); // 0..1, slow
            float h = 4 + wave * 8;
            int a = (int)(110 + wave * 95);
            using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(a, 0, 255), color));
            float bx = x + i * (barW + gap);
            g.FillRoundedRect(brush, bx, cy - h / 2, barW, h, barW / 2f);
        }
    }

    /// <summary>
    /// Five bars whose height swells with the (smoothed) audio level. The level
    /// uses a fast attack / slow release, each bar eases toward its target, and a
    /// bell profile makes the centre bars taller — so it feels fluid, not jittery.
    /// </summary>
    private void DrawEqualizer(Graphics g, int x, int cy, Color color, float amplitude, float t)
    {
        // Smooth the level: rise quickly, fall gently.
        float target = Math.Clamp(amplitude, 0f, 1f);
        float rate = target > _smoothedLevel ? 0.45f : 0.10f;
        _smoothedLevel += (target - _smoothedLevel) * rate;

        const int bars = 5, barW = 3, gap = 4;
        using var brush = new SolidBrush(color);
        for (int i = 0; i < bars; i++)
        {
            // Two overlaid waves at different rates → organic, non-repeating motion.
            float phase = i * 0.9f;
            float wobble = (float)((Math.Sin(t * 5.5 + phase) * 0.5 + 0.5) * 0.6
                                 + (Math.Sin(t * 3.1 + phase * 1.7) * 0.5 + 0.5) * 0.4);
            // Bell shape: centre bars respond more than the edges.
            float dist = Math.Abs(i - (bars - 1) / 2f) / ((bars - 1) / 2f);
            float bell = 0.55f + 0.45f * (1f - dist);
            float targetH = 3f + wobble * (2.5f + _smoothedLevel * 20f * bell);

            _barHeights[i] += (targetH - _barHeights[i]) * 0.30f; // per-bar easing
            float h = _barHeights[i];
            float bx = x + i * (barW + gap);
            g.FillRoundedRect(brush, bx, cy - h / 2, barW, h, barW / 2f);
        }
    }

    /// <summary>Three dots pulsing in a staggered sequence — the "thinking" indicator.</summary>
    private static void DrawThinkingDots(Graphics g, int x, int cy, Color color, float t)
    {
        const int dots = 3;
        for (int i = 0; i < dots; i++)
        {
            float phase = i * 0.6f;
            float raw = (float)(Math.Sin(t * 5 + phase) * 0.5 + 0.5); // 0..1
            float size = 4 + raw * 4;
            int alpha = (int)((0.45 + raw * 0.55) * 255);
            using var brush = new SolidBrush(Color.FromArgb(Math.Clamp(alpha, 0, 255), color));
            float dx = x + i * 12;
            g.FillEllipse(brush, dx, cy - size / 2, size, size);
        }
    }

    private void ApplyLayeredWindow(Bitmap bitmap, int wx, int wy)
    {
        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        IntPtr hBmp = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBmp = SelectObject(memDC, hBmp);

        var size = new SIZE { cx = W, cy = H };
        var ptSrc = new PT { x = 0, y = 0 };
        var ptDst = new PT { x = wx, y = wy };
        var blend = new BLEND { BlendOp = AC_SRC_OVER, SourceConstantAlpha = 255, AlphaFormat = AC_SRC_ALPHA };

        UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref size, memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, oldBmp);
        DeleteObject(hBmp);
        DeleteDC(memDC);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
    }

    public void Dispose()
    {
        _renderTimer?.Dispose();
        _topmostTimer?.Dispose();
        _logo?.Dispose();
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
    [DllImport("user32.dll")] private static extern bool TrackMouseEvent(ref TRACKMOUSEEVENT lpEventTrack);

    [StructLayout(LayoutKind.Sequential)] private struct SIZE { public int cx, cy; }
    [StructLayout(LayoutKind.Sequential)] private struct PT { public int x, y; }
    [StructLayout(LayoutKind.Sequential)]
    private struct BLEND { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }
    [StructLayout(LayoutKind.Sequential)]
    private struct TRACKMOUSEEVENT { public uint cbSize; public uint dwFlags; public IntPtr hwndTrack; public uint dwHoverTime; }
}
