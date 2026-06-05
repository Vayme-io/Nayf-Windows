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
    // Where the cursor tip sits inside the bitmap
    private const int ANCHOR_X = 24;
    private const int ANCHOR_Y = 150;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private NativeMethods.WndProc? _wndProcDelegate;

    // Spring-smoothed buddy position (absolute screen coords)
    private float _buddyX;
    private float _buddyY;

    private System.Threading.Timer? _renderTimer;
    private System.Threading.Timer? _topmostTimer;
    private readonly Random _rng = new();

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

        // ── 1. Spring-smooth toward the real mouse position ───────────────────
        if (NativeMethods.GetCursorPos(out var pt))
        {
            // Tighter spring (0.35) = snappier tracking, less perceived lag
            const float spring = 0.35f;
            _buddyX += (pt.X - _buddyX) * spring;
            _buddyY += (pt.Y - _buddyY) * spring;
        }

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

        // Cursor is always drawn at the anchor point inside the bitmap
        DrawCursor(g, ANCHOR_X, ANCHOR_Y, state);

        switch (state)
        {
            case CompanionVoiceState.Listening:
                DrawWaveform(g, ANCHOR_X + 25, ANCHOR_Y - 30, _companionManager.AudioPowerLevel);
                break;
            case CompanionVoiceState.Processing:
                DrawProcessingDots(g, ANCHOR_X + 22, ANCHOR_Y - 30);
                break;
            case CompanionVoiceState.Responding:
                var text = _companionManager.StreamingResponseText;
                if (!string.IsNullOrEmpty(text))
                    DrawResponseBubble(g, ANCHOR_X + 28, ANCHOR_Y - 55, text);
                break;
        }

        // Pointing sonar — convert absolute screen coords to bitmap-local coords
        var pos = _companionManager.DetectedElementPosition;
        if (pos.HasValue)
        {
            float lx = pos.Value.X - wx;
            float ly = pos.Value.Y - wy;
            if (lx >= 0 && lx < BW && ly >= 0 && ly < BH)
            {
                DrawSonarRing(g, lx, ly);
                DrawPointingBubble(g, lx + 16, ly - 36,
                    _companionManager.DetectedElementBubbleText ?? "Here");
            }
        }

        ApplyLayeredWindow(bitmap, wx, wy);
    }

    // ── Drawing helpers ───────────────────────────────────────────────────────

    private static void DrawCursor(Graphics g, float x, float y, CompanionVoiceState state)
    {
        int glowAlpha = state == CompanionVoiceState.Idle ? 40 : 80;
        using var glow = new SolidBrush(Color.FromArgb(glowAlpha, 0, 122, 255));
        g.FillEllipse(glow, x - 14, y - 14, 32, 32);

        var pts = new PointF[] { new(x, y), new(x + 18, y + 8), new(x, y + 18) };
        using var fill = new SolidBrush(Color.FromArgb(230, 0, 122, 255));
        g.FillPolygon(fill, pts);
        using var outline = new Pen(Color.FromArgb(80, 255, 255, 255), 0.8f);
        g.DrawPolygon(outline, pts);
    }

    private void DrawWaveform(Graphics g, float x, float y, float power)
    {
        float[] heights = [4, 8, 14, 8, 4];
        for (int i = 0; i < heights.Length; i++)
        {
            float h = heights[i] + power * 16 + (float)(_rng.NextDouble() * 6 * power);
            using var b = new SolidBrush(Color.FromArgb(220, 0, 122, 255));
            g.FillRoundedRect(b, x + i * 6, y - h / 2, 3, h, 2);
        }
    }

    private static void DrawProcessingDots(Graphics g, float x, float y)
    {
        int active = (int)(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 400 % 3);
        for (int i = 0; i < 3; i++)
        {
            using var b = new SolidBrush(Color.FromArgb(i == active ? 220 : 80, 0, 122, 255));
            g.FillEllipse(b, x + i * 9, y, 6, 6);
        }
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

    private static void DrawPointingBubble(Graphics g, float x, float y, string label)
    {
        using var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        var sz = g.MeasureString(label, font);
        using var bg = new SolidBrush(Color.FromArgb(200, 0, 90, 200));
        g.FillRoundedRect(bg, x, y, sz.Width + 20, sz.Height + 12, 8);
        using var tb = new SolidBrush(Color.White);
        g.DrawString(label, font, tb, x + 10, y + 6);
    }

    private static void DrawSonarRing(Graphics g, float cx, float cy)
    {
        float phase  = (DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() % 1200) / 1200f;
        float radius = 10 + phase * 30;
        int   alpha  = (int)((1f - phase) * 160);
        using var pen = new Pen(Color.FromArgb(alpha, 0, 122, 255), 2);
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
