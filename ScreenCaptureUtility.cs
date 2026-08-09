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
                    // Serialised so that two overlapping captures cannot have one put
                    // Nayf's windows back while the other is still reading the screen.
                    lock (CaptureExclusionLock)
                    {
                        SetOwnWindowCaptureExclusion(true);
                        try
                        {
                            BitBlt(bitmapDC, 0, 0, width, height, desktopDC, x, y, SRCCOPY);
                        }
                        finally
                        {
                            SetOwnWindowCaptureExclusion(false);
                        }
                    }
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
    /// Takes Nayf's own windows out of the screen for the duration of one capture, so
    /// the model is shown the user's screen rather than Nayf's reaction to it — the
    /// cursor buddy, the status pill, the typed-request field, and the annotations Nayf
    /// draws itself. That last one is the reason this matters: without it the model sees
    /// its own highlight on the next screenshot and reads it as part of the user's UI.
    ///
    /// The Mac does this by filtering its own windows out of the SCContentFilter
    /// (<c>CompanionScreenCaptureUtility.swift:201-204</c>). BitBlt takes no such filter,
    /// but WDA_EXCLUDEFROMCAPTURE removes a window from the composited surface that every
    /// capture path reads from, which reaches the same result without rewriting capture.
    ///
    /// Applied and taken back per capture rather than left on permanently, because the
    /// affinity hides the window from *all* capture — the user's screen recordings and
    /// video calls included. Nayf being invisible in a Teams share is not what was asked
    /// for, and the Mac's filter is scoped to its own captures too.
    ///
    /// Swept across the process's windows rather than set once by each window as it is
    /// created: the list of Nayf's windows keeps growing, and one that forgets to opt in
    /// fails silently — the screenshot simply comes back with Nayf in it. Measured on
    /// Windows 11: the affinity takes effect on the BitBlt immediately following it with
    /// no settling delay, and can be set from a thread that does not own the window,
    /// which this is — the buddy and the pill each run their own message loop, and
    /// capture runs on the thread pool.
    /// </summary>
    private static void SetOwnWindowCaptureExclusion(bool excluded)
    {
        uint affinity = excluded ? WDA_EXCLUDEFROMCAPTURE : WDA_NONE;
        uint ownProcessId = (uint)Environment.ProcessId;
        int count = 0;

        EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId != ownProcessId) return true;

            if (SetWindowDisplayAffinity(hwnd, affinity)) count++;
            // Read the error before anything else has a chance to overwrite it.
            else ReportExclusionFailure(hwnd, Marshal.GetLastWin32Error());
            return true;
        }, IntPtr.Zero);

        if (!excluded || _loggedExclusion) return;
        _loggedExclusion = true;
        Logger.Log("ScreenCapture", $"Hiding {count} of Nayf's own windows from capture");
    }

    /// <summary>
    /// WDA_EXCLUDEFROMCAPTURE needs Windows 10 2004; the project's floor is 1809, where
    /// the call fails and Nayf stays visible to itself. Worth saying once per window —
    /// but only once, since a capture happens on every turn.
    /// </summary>
    private static void ReportExclusionFailure(IntPtr hwnd, int error)
    {
        lock (_reportedExclusionFailures)
        {
            if (!_reportedExclusionFailures.Add(hwnd)) return;
        }
        Logger.Log("ScreenCapture",
            $"Could not hide window {hwnd:X} from capture (error {error}); it will appear in screenshots");
    }

    private static readonly object CaptureExclusionLock = new();
    private static readonly HashSet<IntPtr> _reportedExclusionFailures = new();
    private static bool _loggedExclusion;

    private const uint WDA_NONE = 0x00;
    private const uint WDA_EXCLUDEFROMCAPTURE = 0x11;

    private delegate bool EnumWindowsProc(IntPtr hwnd, IntPtr lParam);

    [DllImport("user32.dll")]
    private static extern bool EnumWindows(EnumWindowsProc callback, IntPtr lParam);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool SetWindowDisplayAffinity(IntPtr hwnd, uint dwAffinity);

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
