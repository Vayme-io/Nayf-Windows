using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Drawing.Text;
using System.Runtime.InteropServices;
using System.Threading;
using Microsoft.UI.Dispatching;

namespace NayfWindows;

/// <summary>
/// Full-screen transparent overlay drawn with Win32 UpdateLayeredWindow +
/// GDI+. Supports true per-pixel alpha so the cursor triangle glows against
/// any background without any black rectangle artifact.
///
/// One instance per monitor. Mirrors OverlayWindow.swift.
/// </summary>
public sealed class NativeOverlayWindow : IDisposable
{
    private readonly CompanionManager _companionManager;
    private readonly NativeMethods.RECT _monitorRect;

    private IntPtr _hwnd = IntPtr.Zero;
    private Thread? _messageThread;
    private NativeMethods.WndProc? _wndProcDelegate;

    // Cursor state
    private float _buddyX;
    private float _buddyY;
    private float _targetX;
    private float _targetY;

    private System.Threading.Timer? _renderTimer;
    private readonly Random _rng = new();

    // Constants for the layered window update
    private const int WS_EX_LAYERED = 0x00080000;
    private const int WS_EX_TRANSPARENT = 0x00000020;
    private const int WS_EX_TOOLWINDOW = 0x00000080;
    private const int WS_EX_NOACTIVATE = 0x08000000;
    private const int WS_EX_TOPMOST = 0x00000008;
    private const int WS_POPUP = unchecked((int)0x80000000);
    private const uint ULW_ALPHA = 0x02;
    private const byte AC_SRC_OVER = 0x00;
    private const byte AC_SRC_ALPHA = 0x01;

    public NativeOverlayWindow(CompanionManager companionManager, NativeMethods.RECT monitorRect)
    {
        _companionManager = companionManager;
        _monitorRect = monitorRect;
        _buddyX = monitorRect.Width / 2f;
        _buddyY = monitorRect.Height / 2f;
        _targetX = _buddyX;
        _targetY = _buddyY;

        _companionManager.PropertyChanged += OnCompanionPropertyChanged;
    }

    public void Start()
    {
        // Run the Win32 message loop and rendering on a dedicated STA thread
        _messageThread = new Thread(RunWindowThread)
        {
            Name = "NayfOverlayWindow",
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
            cbSize = (uint)Marshal.SizeOf<NativeMethods.WNDCLASSEX>(),
            style = 0,
            lpfnWndProc = _wndProcDelegate,
            hInstance = NativeMethods.GetModuleHandle(null),
            lpszClassName = $"NayfOverlay_{_monitorRect.Left}_{_monitorRect.Top}"
        };
        NativeMethods.RegisterClassEx(ref wndClass);

        _hwnd = NativeMethods.CreateWindowEx(
            WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE | WS_EX_TOPMOST,
            wndClass.lpszClassName, "NayfOverlay",
            WS_POPUP,
            _monitorRect.Left, _monitorRect.Top,
            _monitorRect.Width, _monitorRect.Height,
            IntPtr.Zero, IntPtr.Zero,
            NativeMethods.GetModuleHandle(null), IntPtr.Zero);

        if (_hwnd == IntPtr.Zero) return;

        // Show the window (it's invisible because it has zero content until first draw)
        ShowWindow(_hwnd, 4 /* SW_SHOWNOACTIVATE */);
        NativeMethods.SetWindowPos(_hwnd, NativeMethods.HWND_TOPMOST, 0, 0, 0, 0,
            NativeMethods.SWP_NOMOVE | NativeMethods.SWP_NOSIZE | NativeMethods.SWP_NOACTIVATE);

        // Draw first frame
        RenderFrame();

        // 60fps render timer
        _renderTimer = new System.Threading.Timer(_ => RenderFrame(), null,
            TimeSpan.FromMilliseconds(16), TimeSpan.FromMilliseconds(16));

        // Win32 message loop
        while (NativeMethods.GetMessage(out var msg, IntPtr.Zero, 0, 0))
        {
            NativeMethods.TranslateMessage(ref msg);
            NativeMethods.DispatchMessage(ref msg);
        }
    }

    private IntPtr WndProc(IntPtr hWnd, uint msg, IntPtr wParam, IntPtr lParam)
    {
        return NativeMethods.DefWindowProc(hWnd, msg, wParam, lParam);
    }

    private void RenderFrame()
    {
        if (_hwnd == IntPtr.Zero) return;

        // Update cursor tracking
        if (NativeMethods.GetCursorPos(out var pt))
        {
            bool onThisMonitor =
                pt.X >= _monitorRect.Left && pt.X < _monitorRect.Left + _monitorRect.Width &&
                pt.Y >= _monitorRect.Top && pt.Y < _monitorRect.Top + _monitorRect.Height;

            if (onThisMonitor)
            {
                _targetX = pt.X - _monitorRect.Left + 20f;
                _targetY = pt.Y - _monitorRect.Top + 20f;
            }
        }

        // Spring interpolation towards target
        const float spring = 0.18f;
        _buddyX += (_targetX - _buddyX) * spring;
        _buddyY += (_targetY - _buddyY) * spring;

        int w = _monitorRect.Width;
        int h = _monitorRect.Height;

        using var bitmap = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(bitmap);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.TextRenderingHint = TextRenderingHint.ClearTypeGridFit;
        g.Clear(Color.Transparent);

        var voiceState = _companionManager.VoiceState;

        // --- Draw the blue cursor triangle ---
        DrawCursor(g, _buddyX, _buddyY, voiceState);

        // --- Draw voice state UI next to cursor ---
        switch (voiceState)
        {
            case CompanionVoiceState.Listening:
                DrawWaveform(g, _buddyX + 25, _buddyY - 30, _companionManager.AudioPowerLevel);
                break;
            case CompanionVoiceState.Processing:
                DrawProcessingDots(g, _buddyX + 22, _buddyY - 30);
                break;
            case CompanionVoiceState.Responding:
                var responseText = _companionManager.StreamingResponseText;
                if (!string.IsNullOrEmpty(responseText))
                    DrawResponseBubble(g, _buddyX + 28, _buddyY - 55, responseText);
                break;
        }

        // --- Draw pointing bubble if Claude pointed at something ---
        var pointPos = _companionManager.DetectedElementPosition;
        if (pointPos.HasValue)
        {
            float px = pointPos.Value.X - _monitorRect.Left;
            float py = pointPos.Value.Y - _monitorRect.Top;
            DrawSonarRing(g, px, py);

            var label = _companionManager.DetectedElementBubbleText ?? "Here";
            DrawPointingBubble(g, px + 16, py - 36, label);
        }

        // Apply to the layered window
        ApplyLayeredWindow(bitmap);
    }

    private static void DrawCursor(Graphics g, float x, float y, CompanionVoiceState state)
    {
        var glowColor = state == CompanionVoiceState.Idle
            ? Color.FromArgb(40, 0, 122, 255)
            : Color.FromArgb(80, 0, 122, 255);

        // Outer glow
        using var glowBrush = new SolidBrush(glowColor);
        g.FillEllipse(glowBrush, x - 14, y - 14, 32, 32);

        // Triangle cursor pointing right (like an arrow cursor)
        var pts = new PointF[]
        {
            new(x,      y),
            new(x + 18, y + 8),
            new(x,      y + 18)
        };

        using var triangleBrush = new SolidBrush(Color.FromArgb(230, 0, 122, 255));
        g.FillPolygon(triangleBrush, pts);

        // Thin white outline for legibility on dark backgrounds
        using var outlinePen = new Pen(Color.FromArgb(80, 255, 255, 255), 0.8f);
        g.DrawPolygon(outlinePen, pts);
    }

    private void DrawWaveform(Graphics g, float x, float y, float power)
    {
        float[] heights = [4, 8, 14, 8, 4];
        for (int i = 0; i < heights.Length; i++)
        {
            float noise = (float)(_rng.NextDouble() * 6 * power);
            float barH = heights[i] + power * 16 + noise;
            using var brush = new SolidBrush(Color.FromArgb(220, 0, 122, 255));
            g.FillRoundedRect(brush, x + i * 6, y - barH / 2, 3, barH, 2);
        }
    }

    private static void DrawProcessingDots(Graphics g, float x, float y)
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        int activeDot = (int)(ms / 400 % 3);
        for (int i = 0; i < 3; i++)
        {
            int alpha = i == activeDot ? 220 : 80;
            using var brush = new SolidBrush(Color.FromArgb(alpha, 0, 122, 255));
            g.FillEllipse(brush, x + i * 9, y, 6, 6);
        }
    }

    private static void DrawResponseBubble(Graphics g, float x, float y, string text)
    {
        const int maxWidth = 340;
        using var font = new Font("Segoe UI", 12f, FontStyle.Regular, GraphicsUnit.Point);
        var textSize = g.MeasureString(text, font, maxWidth);

        float bw = Math.Min(textSize.Width + 24, maxWidth + 24);
        float bh = textSize.Height + 18;

        // Clamp to screen
        if (x + bw > 1920) x -= bw + 40;
        if (y < 0) y = 4;

        // Background
        using var bgBrush = new SolidBrush(Color.FromArgb(220, 28, 28, 30));
        g.FillRoundedRect(bgBrush, x, y, bw, bh, 10);

        // Border
        using var borderPen = new Pen(Color.FromArgb(80, 58, 58, 60), 0.8f);
        g.DrawRoundedRect(borderPen, x, y, bw, bh, 10);

        // Text
        using var textBrush = new SolidBrush(Color.White);
        g.DrawString(text, font, textBrush, new RectangleF(x + 12, y + 9, bw - 24, bh - 18));
    }

    private static void DrawPointingBubble(Graphics g, float x, float y, string label)
    {
        using var font = new Font("Segoe UI", 11f, FontStyle.Bold, GraphicsUnit.Point);
        var textSize = g.MeasureString(label, font);
        float bw = textSize.Width + 20;
        float bh = textSize.Height + 12;

        using var bgBrush = new SolidBrush(Color.FromArgb(200, 0, 90, 200));
        g.FillRoundedRect(bgBrush, x, y, bw, bh, 8);

        using var textBrush = new SolidBrush(Color.White);
        g.DrawString(label, font, textBrush, x + 10, y + 6);
    }

    private static void DrawSonarRing(Graphics g, float cx, float cy)
    {
        long ms = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
        float phase = (ms % 1200) / 1200f;
        float radius = 10 + phase * 30;
        int alpha = (int)((1f - phase) * 160);

        using var pen = new Pen(Color.FromArgb(alpha, 0, 122, 255), 2);
        g.DrawEllipse(pen, cx - radius, cy - radius, radius * 2, radius * 2);
    }

    private void ApplyLayeredWindow(Bitmap bitmap)
    {
        IntPtr screenDC = NativeMethods.GetDC(IntPtr.Zero);
        IntPtr memDC = CreateCompatibleDC(screenDC);
        IntPtr hBitmap = bitmap.GetHbitmap(Color.FromArgb(0));
        IntPtr oldBitmap = SelectObject(memDC, hBitmap);

        var size = new SIZE { cx = _monitorRect.Width, cy = _monitorRect.Height };
        var ptSrc = new POINT2 { x = 0, y = 0 };
        var ptDst = new POINT2 { x = _monitorRect.Left, y = _monitorRect.Top };
        var blend = new BLENDFUNCTION
        {
            BlendOp = AC_SRC_OVER,
            BlendFlags = 0,
            SourceConstantAlpha = 255,
            AlphaFormat = AC_SRC_ALPHA
        };

        UpdateLayeredWindow(_hwnd, screenDC, ref ptDst, ref size, memDC, ref ptSrc, 0, ref blend, ULW_ALPHA);

        SelectObject(memDC, oldBitmap);
        DeleteObject(hBitmap);
        DeleteDC(memDC);
        NativeMethods.ReleaseDC(IntPtr.Zero, screenDC);
    }

    private void OnCompanionPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        // The render timer picks up state changes on its next tick — no action needed here
    }

    public void Dispose()
    {
        _renderTimer?.Dispose();
        _companionManager.PropertyChanged -= OnCompanionPropertyChanged;
        if (_hwnd != IntPtr.Zero)
        {
            NativeMethods.DestroyWindow(_hwnd);
            NativeMethods.PostQuitMessage(0);
            _hwnd = IntPtr.Zero;
        }
    }

    // P/Invoke
    [DllImport("user32.dll")]
    private static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst,
        ref POINT2 pptDst, ref SIZE psize, IntPtr hdcSrc, ref POINT2 pptSrc,
        uint crKey, ref BLENDFUNCTION pblend, uint dwFlags);

    [DllImport("gdi32.dll")]
    private static extern IntPtr CreateCompatibleDC(IntPtr hdc);

    [DllImport("gdi32.dll")]
    private static extern IntPtr SelectObject(IntPtr hdc, IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteObject(IntPtr hObject);

    [DllImport("gdi32.dll")]
    private static extern bool DeleteDC(IntPtr hdc);

    [DllImport("user32.dll")]
    private static extern bool ShowWindow(IntPtr hWnd, int nCmdShow);

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT2 { public int x, y; }

    [StructLayout(LayoutKind.Sequential)]
    private struct BLENDFUNCTION
    {
        public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat;
    }
}

/// <summary>
/// Manages one NativeOverlayWindow per connected monitor.
/// </summary>
public sealed class OverlayWindowManager : IDisposable
{
    private readonly CompanionManager _companionManager;
    private readonly System.Collections.Generic.List<NativeOverlayWindow> _overlays = new();

    public OverlayWindowManager(CompanionManager companionManager)
    {
        _companionManager = companionManager;
    }

    public void CreateOverlaysForAllMonitors()
    {
        var monitors = GetAllMonitorRects();
        foreach (var rect in monitors)
        {
            var overlay = new NativeOverlayWindow(_companionManager, rect);
            overlay.Start();
            _overlays.Add(overlay);
        }
    }

    private System.Collections.Generic.List<NativeMethods.RECT> GetAllMonitorRects()
    {
        var rects = new System.Collections.Generic.List<NativeMethods.RECT>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMonitor, hdc, ref rect, data) =>
            {
                var info = new NativeMethods.MONITORINFOEX
                    { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                    rects.Add(info.rcMonitor);
                return true;
            }, IntPtr.Zero);

        if (rects.Count == 0)
            rects.Add(new NativeMethods.RECT
            {
                Left = 0, Top = 0,
                Right = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
                Bottom = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)
            });
        return rects;
    }

    public void Dispose()
    {
        foreach (var o in _overlays) o.Dispose();
        _overlays.Clear();
    }
}

/// <summary>GDI+ extension helpers for drawing rounded rectangles.</summary>
internal static class GraphicsExtensions
{
    public static void FillRoundedRect(this Graphics g, Brush brush, float x, float y, float w, float h, float r)
    {
        using var path = RoundedRectPath(x, y, w, h, r);
        g.FillPath(brush, path);
    }

    public static void DrawRoundedRect(this Graphics g, Pen pen, float x, float y, float w, float h, float r)
    {
        using var path = RoundedRectPath(x, y, w, h, r);
        g.DrawPath(pen, path);
    }

    private static GraphicsPath RoundedRectPath(float x, float y, float w, float h, float r)
    {
        var path = new GraphicsPath();
        path.AddArc(x, y, r * 2, r * 2, 180, 90);
        path.AddArc(x + w - r * 2, y, r * 2, r * 2, 270, 90);
        path.AddArc(x + w - r * 2, y + h - r * 2, r * 2, r * 2, 0, 90);
        path.AddArc(x, y + h - r * 2, r * 2, r * 2, 90, 90);
        path.CloseFigure();
        return path;
    }
}
