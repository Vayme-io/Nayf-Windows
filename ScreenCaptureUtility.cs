using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// Multi-monitor screenshot capture using Windows GDI BitBlt.
/// Returns JPEG-encoded screenshots for each connected display,
/// labelled "Screen 1", "Screen 2", etc. — matches the Mac's
/// ScreenCaptureKit multi-monitor output format.
/// </summary>
public static class ScreenCaptureUtility
{
    /// <summary>
    /// Captures screenshots from all connected monitors.
    /// Returns one CapturedScreenshot per display.
    /// </summary>
    public static Task<List<CapturedScreenshot>> CaptureAllScreensAsync()
    {
        return Task.Run(() =>
        {
            var results = new List<CapturedScreenshot>();
            var monitors = GetAllMonitorRects();

            for (int i = 0; i < monitors.Count; i++)
            {
                var rect = monitors[i];
                try
                {
                    var (imageData, imgW, imgH) =
                        CaptureScreenRegion(rect.Left, rect.Top, rect.Width, rect.Height);
                    Logger.Log("ScreenCapture",
                        $"Screen {i + 1}: {rect.Width}x{rect.Height} -> {imgW}x{imgH}, {imageData.Length / 1024} KB");
                    results.Add(new CapturedScreenshot(
                        ImageData: imageData,
                        ScreenIndex: i,
                        ScreenLabel: $"Screen {i + 1}",
                        ImageWidth: imgW,
                        ImageHeight: imgH,
                        MonitorLeft: rect.Left,
                        MonitorTop: rect.Top,
                        MonitorWidth: rect.Width,
                        MonitorHeight: rect.Height
                    ));
                }
                catch (Exception ex)
                {
                    Logger.Log("ScreenCapture", $"Failed to capture monitor {i}: {ex.Message}");
                }
            }

            return results;
        });
    }

    /// <summary>Captures the primary display only.</summary>
    public static Task<byte[]> CapturePrimaryScreenAsync()
    {
        return Task.Run(() =>
        {
            int width = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN);
            int height = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN);
            return CaptureScreenRegion(0, 0, width, height).data;
        });
    }

    // Claude downscales any image whose longest edge exceeds ~1568px, so
    // capturing larger than this just wastes upload bandwidth and latency.
    private const int MaxImageEdge = 1568;
    private const long JpegQuality = 70L;

    private static (byte[] data, int width, int height) CaptureScreenRegion(
        int x, int y, int width, int height)
    {
        IntPtr desktopDC = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using (var graphics = Graphics.FromImage(bitmap))
            {
                IntPtr bitmapDC = graphics.GetHdc();
                try
                {
                    BitBlt(bitmapDC, 0, 0, width, height, desktopDC, x, y, SRCCOPY);
                }
                finally
                {
                    graphics.ReleaseHdc(bitmapDC);
                }
            }

            // Downscale so the longest edge is at most MaxImageEdge before encoding.
            Bitmap? scaled = null;
            var toEncode = bitmap;
            if (Math.Max(width, height) > MaxImageEdge)
            {
                scaled = ScaleToMaxEdge(bitmap, MaxImageEdge);
                toEncode = scaled;
            }

            try
            {
                using var ms = new MemoryStream();
                var jpegEncoder = GetJpegEncoder();
                var encoderParams = new EncoderParameters(1);
                encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, JpegQuality);
                toEncode.Save(ms, jpegEncoder, encoderParams);
                return (ms.ToArray(), toEncode.Width, toEncode.Height);
            }
            finally
            {
                scaled?.Dispose();
            }
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, desktopDC);
        }
    }

    /// <summary>
    /// Returns a new bitmap scaled so its longest edge is at most
    /// <paramref name="maxEdge"/> px, preserving aspect ratio.
    /// </summary>
    private static Bitmap ScaleToMaxEdge(Bitmap source, int maxEdge)
    {
        double scale = (double)maxEdge / Math.Max(source.Width, source.Height);
        int newW = Math.Max(1, (int)Math.Round(source.Width * scale));
        int newH = Math.Max(1, (int)Math.Round(source.Height * scale));

        var dest = new Bitmap(newW, newH, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(dest);
        g.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, newW, newH);
        return dest;
    }

    private static List<NativeMethods.RECT> GetAllMonitorRects()
    {
        // Track which monitor is primary so we can list it first — Claude is told
        // "screen0 is the primary display", so the order here must match.
        var monitors = new List<(NativeMethods.RECT rect, bool isPrimary)>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMonitor, hdc, ref rect, data) =>
            {
                var info = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                {
                    bool isPrimary = (info.dwFlags & NativeMethods.MONITORINFOF_PRIMARY) != 0;
                    monitors.Add((info.rcMonitor, isPrimary));
                }
                return true;
            }, IntPtr.Zero);

        if (monitors.Count == 0)
        {
            // Fallback: use primary screen
            return new List<NativeMethods.RECT>
            {
                new NativeMethods.RECT
                {
                    Left = 0, Top = 0,
                    Right = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
                    Bottom = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)
                }
            };
        }

        // Primary first, rest in enumeration order.
        var rects = new List<NativeMethods.RECT>();
        foreach (var m in monitors) if (m.isPrimary) rects.Add(m.rect);
        foreach (var m in monitors) if (!m.isPrimary) rects.Add(m.rect);
        return rects;
    }

    private static ImageCodecInfo GetJpegEncoder()
    {
        foreach (var codec in ImageCodecInfo.GetImageEncoders())
        {
            if (codec.MimeType == "image/jpeg")
                return codec;
        }
        throw new InvalidOperationException("JPEG encoder not found");
    }

    private const uint SRCCOPY = 0x00CC0020;

    [DllImport("gdi32.dll")]
    private static extern bool BitBlt(IntPtr hdc, int nXDest, int nYDest, int nWidth, int nHeight,
        IntPtr hdcSrc, int nXSrc, int nYSrc, uint dwRop);
}
