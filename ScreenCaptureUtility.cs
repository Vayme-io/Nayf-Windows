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
                    var imageData = CaptureScreenRegion(rect.Left, rect.Top, rect.Width, rect.Height);
                    results.Add(new CapturedScreenshot(
                        ImageData: imageData,
                        ScreenIndex: i,
                        ScreenLabel: $"Screen {i + 1}"
                    ));
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"[ScreenCapture] Failed to capture monitor {i}: {ex.Message}");
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
            return CaptureScreenRegion(0, 0, width, height);
        });
    }

    private static byte[] CaptureScreenRegion(int x, int y, int width, int height)
    {
        IntPtr desktopDC = NativeMethods.GetDC(IntPtr.Zero);
        try
        {
            using var bitmap = new Bitmap(width, height, PixelFormat.Format32bppArgb);
            using var graphics = Graphics.FromImage(bitmap);
            IntPtr bitmapDC = graphics.GetHdc();
            try
            {
                BitBlt(bitmapDC, 0, 0, width, height, desktopDC, x, y, SRCCOPY);
            }
            finally
            {
                graphics.ReleaseHdc(bitmapDC);
            }

            // Encode as JPEG at 85% quality — same approach as the Mac version
            using var ms = new MemoryStream();
            var jpegEncoder = GetJpegEncoder();
            var encoderParams = new EncoderParameters(1);
            encoderParams.Param[0] = new EncoderParameter(Encoder.Quality, 85L);
            bitmap.Save(ms, jpegEncoder, encoderParams);
            return ms.ToArray();
        }
        finally
        {
            NativeMethods.ReleaseDC(IntPtr.Zero, desktopDC);
        }
    }

    private static List<NativeMethods.RECT> GetAllMonitorRects()
    {
        var rects = new List<NativeMethods.RECT>();
        NativeMethods.EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero,
            (hMonitor, hdc, ref rect, data) =>
            {
                var info = new NativeMethods.MONITORINFOEX { cbSize = (uint)Marshal.SizeOf<NativeMethods.MONITORINFOEX>() };
                if (NativeMethods.GetMonitorInfo(hMonitor, ref info))
                    rects.Add(info.rcMonitor);
                return true;
            }, IntPtr.Zero);

        if (rects.Count == 0)
        {
            // Fallback: use primary screen
            rects.Add(new NativeMethods.RECT
            {
                Left = 0, Top = 0,
                Right = NativeMethods.GetSystemMetrics(NativeMethods.SM_CXSCREEN),
                Bottom = NativeMethods.GetSystemMetrics(NativeMethods.SM_CYSCREEN)
            });
        }
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
