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
/// labelled "Screen 0", "Screen 1", etc. — matches the Mac's
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
                        $"Screen {i}: {rect.Width}x{rect.Height} at ({rect.Left},{rect.Top}) " +
                        $"-> {imgW}x{imgH}, {imageData.Length / 1024} KB");
                    results.Add(new CapturedScreenshot(
                        ImageData: imageData,
                        ScreenIndex: i,
                        // Numbered from 0, because this label is the only place the model
                        // learns what to write in a [POINT:...:screenN] tag or a step's
                        // "screen" argument, and both are matched against ScreenIndex.

                        // Labelling the first image "Screen 1" while the prompt asked for
                        // screen0 left the model to guess which of the two numbering schemes
                        // a tag meant, and a wrong guess draws on the wrong monitor.
                        ScreenLabel: $"Screen {i}, {imgW}x{imgH}",
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

    /// <summary>
    /// Captures one rectangle of the desktop, in virtual-screen pixels — the region the user
    /// circled with the region-focus hold. Mirrors the Mac's
    /// <c>CompanionScreenCaptureUtility.captureRegionAsJPEG</c>.
    ///
    /// The rectangle is clamped to the desktop first: a loop drawn off the edge of the screen
    /// would otherwise ask BitBlt for pixels that do not exist, and come back black.
    /// </summary>
    public static Task<byte[]> CaptureRegionAsync(Rectangle region)
    {
        return Task.Run(() =>
        {
            int virtualLeft = NativeMethods.GetSystemMetrics(NativeMethods.SM_XVIRTUALSCREEN);
            int virtualTop = NativeMethods.GetSystemMetrics(NativeMethods.SM_YVIRTUALSCREEN);
            var desktop = new Rectangle(
                virtualLeft, virtualTop,
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CXVIRTUALSCREEN),
                NativeMethods.GetSystemMetrics(NativeMethods.SM_CYVIRTUALSCREEN));

            var clamped = Rectangle.Intersect(region, desktop);
            if (clamped.Width < 1 || clamped.Height < 1)
                throw new InvalidOperationException($"Region {region} is off screen");

            var (imageData, imgW, imgH) =
                CaptureScreenRegion(clamped.X, clamped.Y, clamped.Width, clamped.Height);
            Logger.Log("ScreenCapture",
                $"Region {clamped.Width}x{clamped.Height} at {clamped.X},{clamped.Y} " +
                $"-> {imgW}x{imgH}, {imageData.Length / 1024} KB");
            return imageData;
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
                    // Vayme's windows back while the other is still reading the screen.
                    lock (CaptureExclusionLock)
                    {
                        var excluded = ExcludeOwnWindowsFromCapture();
                        try
                        {
                            BitBlt(bitmapDC, 0, 0, width, height, desktopDC, x, y, SRCCOPY);
                        }
                        finally
                        {
                            RestoreOwnWindowsToCapture(excluded);
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
    /// Takes Vayme's own windows out of the screen for the duration of one capture, so
    /// the model is shown the user's screen rather than Vayme's reaction to it — the
    /// cursor buddy, the status pill, the typed-request field, and the annotations Vayme
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
    /// video calls included. Vayme being invisible in a Teams share is not what was asked
    /// for, and the Mac's filter is scoped to its own captures too.
    ///
    /// Swept across the process's windows rather than set once by each window as it is
    /// created: the list of Vayme's windows keeps growing, and one that forgets to opt in
    /// fails silently — the screenshot simply comes back with Vayme in it. Measured on
    /// Windows 11: the affinity takes effect on the BitBlt immediately following it with
    /// no settling delay, and can be set from a thread that does not own the window,
    /// which this is — the buddy and the pill each run their own message loop, and
    /// capture runs on the thread pool.
    ///
    /// Only windows that are actually on screen are touched. A hidden window cannot appear
    /// in a screenshot, so excluding it buys nothing — and several of Vayme's windows are
    /// hidden shells that have never painted a frame (OverlayWindow is one per monitor,
    /// created only to satisfy WinUI's XAML partial class and hidden immediately). Their
    /// composition surface is blank, so anything that prompts the compositor to present one
    /// puts a white rectangle on the user's screen.
    /// </summary>
    /// <returns>The windows actually excluded, to be passed back to
    /// <see cref="RestoreOwnWindowsToCapture"/>.</returns>
    private static List<IntPtr> ExcludeOwnWindowsFromCapture()
    {
        uint ownProcessId = (uint)Environment.ProcessId;
        var excluded = new List<IntPtr>();

        EnumWindows((hwnd, _) =>
        {
            NativeMethods.GetWindowThreadProcessId(hwnd, out uint processId);
            if (processId != ownProcessId || !IsWindowVisible(hwnd)) return true;

            if (SetWindowDisplayAffinity(hwnd, WDA_EXCLUDEFROMCAPTURE)) excluded.Add(hwnd);
            // Read the error before anything else has a chance to overwrite it.
            else ReportExclusionFailure(hwnd, Marshal.GetLastWin32Error());
            return true;
        }, IntPtr.Zero);

        if (_loggedExclusion) return excluded;
        _loggedExclusion = true;
        Logger.Log("ScreenCapture", $"Hiding {excluded.Count} of Vayme's own windows from capture");
        return excluded;
    }

    /// <summary>
    /// Puts back exactly the windows this capture took out, rather than clearing the
    /// affinity process-wide: a second capture may be running concurrently, and a window
    /// shown after the sweep was never excluded in the first place.
    /// </summary>
    private static void RestoreOwnWindowsToCapture(List<IntPtr> excluded)
    {
        foreach (IntPtr hwnd in excluded)
            SetWindowDisplayAffinity(hwnd, WDA_NONE);
    }

    /// <summary>
    /// WDA_EXCLUDEFROMCAPTURE needs Windows 10 2004; the project's floor is 1809, where
    /// the call fails and Vayme stays visible to itself. Worth saying once per window —
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

    [DllImport("user32.dll")]
    private static extern bool IsWindowVisible(IntPtr hwnd);

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
