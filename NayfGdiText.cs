using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Imaging;
using System.Runtime.InteropServices;

namespace NayfWindows;

/// <summary>
/// Draws text with GDI onto the straight-alpha bitmaps the layered windows are built from.
///
/// <para>GDI+ draws every other part of those bitmaps, but not the text: its rasterizer is
/// visibly softer than GDI's at UI sizes, and <c>DrawString</c> takes float coordinates, so
/// a line laid out at y = 63.7 is resampled across two rows of pixels. Every line lands on
/// a different subpixel phase and blurs by a different amount. GDI hints the outlines and
/// snaps each origin to a whole pixel, which is what the rest of Windows looks like.</para>
///
/// <para>The catch is that GDI knows nothing about alpha: it blends the glyph into the RGB
/// it finds and leaves zero in the fourth byte, which on a layered window means the text
/// comes out as holes punched through the card. So the alpha plane is saved before the text
/// goes down and written back after — see <see cref="Flush"/>. Nothing else in the pass
/// touches alpha, and no text is drawn near the rounded corners, which are the only pixels
/// that aren't fully opaque.</para>
///
/// <para>Measurement needs a device context with the font selected into it, which is why
/// this owns one and why laying out has to go through the same object that draws.</para>
/// </summary>
internal sealed class NayfGdiText : IDisposable
{
    private readonly IntPtr _measureDc;
    private readonly List<Run> _runs = new();
    private readonly List<GdiFont> _fonts = new();

    public NayfGdiText()
    {
        _measureDc = CreateCompatibleDC(IntPtr.Zero);
        if (_measureDc == IntPtr.Zero) throw new InvalidOperationException("no measurement DC");
    }

    // MARK: - Fonts

    public const int WeightRegular = 400;
    public const int WeightSemibold = 600;

    /// <summary>
    /// Builds a font from the first candidate GDI actually honours. GDI substitutes a face
    /// it doesn't have silently rather than failing, so each one is selected and asked what
    /// it turned into; a name that isn't the one requested means the substitution happened
    /// and the next candidate gets a turn.
    /// </summary>
    /// <param name="candidates">Face name and weight, best first.</param>
    /// <param name="pixelSize">Em size in device pixels.</param>
    public GdiFont CreateFont((string Face, int Weight)[] candidates, float pixelSize)
    {
        int height = -Math.Max(1, (int)MathF.Round(pixelSize));

        GdiFont? last = null;
        foreach (var (face, weight) in candidates)
        {
            var font = Realize(face, weight, height);
            if (font == null) continue;

            last?.Dispose();
            last = font;

            if (font.ActualFace.StartsWith(face, StringComparison.OrdinalIgnoreCase))
                break;
        }

        // Nothing resolved cleanly: the last one GDI gave us still draws, and a substituted
        // Segoe UI is a better outcome than the card rendering without a summary on it.
        last ??= Realize("", WeightRegular, height)
                 ?? throw new InvalidOperationException("no usable font");

        _fonts.Add(last);
        return last;
    }

    private GdiFont? Realize(string face, int weight, int height)
    {
        var logical = new LOGFONTW
        {
            lfHeight = height,
            lfWeight = weight,
            lfCharSet = DEFAULT_CHARSET,
            lfOutPrecision = OUT_TT_PRECIS,

            // Hinted grayscale rather than ClearType. WinUI antialiases its own text this
            // way, and subpixel fringes on a near-black slab read as colour noise.
            lfQuality = ANTIALIASED_QUALITY,
            lfFaceName = face
        };

        IntPtr handle = CreateFontIndirectW(ref logical);
        if (handle == IntPtr.Zero) return null;

        IntPtr previous = SelectObject(_measureDc, handle);
        var name = new System.Text.StringBuilder(64);
        GetTextFaceW(_measureDc, name.Capacity, name);
        GetTextMetricsW(_measureDc, out var metrics);
        SelectObject(_measureDc, previous);

        return new GdiFont(handle, name.ToString(), metrics.tmHeight, metrics.tmAscent);
    }

    // MARK: - Measurement

    public float Measure(string text, GdiFont font)
    {
        if (text.Length == 0) return 0f;

        IntPtr previous = SelectObject(_measureDc, font.Handle);
        GetTextExtentPoint32W(_measureDc, text, text.Length, out var size);
        SelectObject(_measureDc, previous);
        return size.cx;
    }

    // MARK: - Drawing

    /// <summary>
    /// Queues a line for the text pass. <paramref name="y"/> is the top of the line box, as
    /// with <c>DrawString</c>. An ink colour carrying alpha is resolved against whatever the
    /// line lands on when the queue is flushed, because GDI cannot blend it itself.
    /// </summary>
    public void Add(string text, GdiFont font, float x, float y, Color ink, RectangleF? clip = null)
    {
        if (text.Length == 0) return;
        _runs.Add(new Run
        {
            Text = text,
            Font = font,
            X = (int)MathF.Round(x),
            Y = (int)MathF.Round(y),
            Ink = ink,
            Clip = clip
        });
    }

    /// <summary>
    /// Draws everything queued onto <paramref name="canvas"/>, with the alpha channel put
    /// back the way GDI found it. Must be called with no <see cref="Graphics"/> alive on the
    /// bitmap — this takes its own, because GDI needs sole use of the device context.
    /// </summary>
    public void Flush(Bitmap canvas)
    {
        if (_runs.Count == 0) return;

        var bounds = new Rectangle(0, 0, canvas.Width, canvas.Height);
        var alpha = new byte[canvas.Width * canvas.Height];

        // One pass for both jobs: alpha as it stands, and the colour under each run, which
        // has to be read before any glyph covers it.
        var data = canvas.LockBits(bounds, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* scan = (byte*)data.Scan0;
                for (int y = 0; y < canvas.Height; y++)
                {
                    byte* row = scan + (long)y * data.Stride;
                    int offset = y * canvas.Width;
                    for (int x = 0; x < canvas.Width; x++)
                        alpha[offset + x] = row[x * 4 + 3];
                }

                foreach (var run in _runs)
                    run.Resolved = run.Ink.A == 255
                        ? run.Ink
                        : Blend(run.Ink, Sample(scan, data.Stride, canvas.Width, canvas.Height,
                                                run.X, run.Y + run.Font.Height / 2));
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }

        using (var g = Graphics.FromImage(canvas))
        {
            IntPtr hdc = g.GetHdc();
            try
            {
                SetBkMode(hdc, TRANSPARENT);
                SetTextAlign(hdc, TA_LEFT | TA_TOP | TA_NOUPDATECP);

                foreach (var run in _runs)
                {
                    SelectClipRgn(hdc, IntPtr.Zero);
                    if (run.Clip is RectangleF clip)
                        IntersectClipRect(hdc,
                            (int)MathF.Floor(clip.Left), (int)MathF.Floor(clip.Top),
                            (int)MathF.Ceiling(clip.Right), (int)MathF.Ceiling(clip.Bottom));

                    SelectObject(hdc, run.Font.Handle);
                    SetTextColor(hdc, run.Resolved.B << 16 | run.Resolved.G << 8 | run.Resolved.R);
                    ExtTextOutW(hdc, run.X, run.Y, 0, IntPtr.Zero,
                                run.Text, (uint)run.Text.Length, IntPtr.Zero);
                }

                SelectClipRgn(hdc, IntPtr.Zero);
            }
            finally
            {
                g.ReleaseHdc(hdc);
            }
        }

        data = canvas.LockBits(bounds, ImageLockMode.ReadWrite, PixelFormat.Format32bppArgb);
        try
        {
            unsafe
            {
                byte* scan = (byte*)data.Scan0;
                for (int y = 0; y < canvas.Height; y++)
                {
                    byte* row = scan + (long)y * data.Stride;
                    int offset = y * canvas.Width;
                    for (int x = 0; x < canvas.Width; x++)
                        row[x * 4 + 3] = alpha[offset + x];
                }
            }
        }
        finally
        {
            canvas.UnlockBits(data);
        }

        _runs.Clear();
    }

    private static unsafe Color Sample(byte* scan, int stride, int width, int height, int x, int y)
    {
        x = Math.Clamp(x, 0, width - 1);
        y = Math.Clamp(y, 0, height - 1);
        byte* pixel = scan + (long)y * stride + x * 4;
        return Color.FromArgb(255, pixel[2], pixel[1], pixel[0]);
    }

    private static Color Blend(Color ink, Color background)
    {
        float a = ink.A / 255f;
        return Color.FromArgb(255,
            (byte)MathF.Round(ink.R * a + background.R * (1f - a)),
            (byte)MathF.Round(ink.G * a + background.G * (1f - a)),
            (byte)MathF.Round(ink.B * a + background.B * (1f - a)));
    }

    public void Dispose()
    {
        foreach (var font in _fonts) font.Dispose();
        _fonts.Clear();
        if (_measureDc != IntPtr.Zero) DeleteDC(_measureDc);
    }

    private sealed class Run
    {
        public string Text = "";
        public GdiFont Font = null!;
        public int X;
        public int Y;
        public Color Ink;
        public Color Resolved;
        public RectangleF? Clip;
    }

    // MARK: - Interop

    private const int DEFAULT_CHARSET = 1;
    private const int OUT_TT_PRECIS = 4;
    private const int ANTIALIASED_QUALITY = 4;
    private const int TRANSPARENT = 1;
    private const uint TA_LEFT = 0;
    private const uint TA_TOP = 0;
    private const uint TA_NOUPDATECP = 0;

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct LOGFONTW
    {
        public int lfHeight, lfWidth, lfEscapement, lfOrientation, lfWeight;
        public byte lfItalic, lfUnderline, lfStrikeOut, lfCharSet,
                    lfOutPrecision, lfClipPrecision, lfQuality, lfPitchAndFamily;
        [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 32)] public string lfFaceName;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct TEXTMETRICW
    {
        public int tmHeight, tmAscent, tmDescent, tmInternalLeading, tmExternalLeading,
                   tmAveCharWidth, tmMaxCharWidth, tmWeight, tmOverhang,
                   tmDigitizedAspectX, tmDigitizedAspectY;
        public char tmFirstChar, tmLastChar, tmDefaultChar, tmBreakChar;
        public byte tmItalic, tmUnderlined, tmStruckOut, tmPitchAndFamily, tmCharSet;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SIZE { public int cx, cy; }

    [DllImport("gdi32.dll")] private static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern bool DeleteDC(IntPtr hdc);
    [DllImport("gdi32.dll")] private static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] private static extern int SetBkMode(IntPtr hdc, int mode);
    [DllImport("gdi32.dll")] private static extern int SetTextColor(IntPtr hdc, int color);
    [DllImport("gdi32.dll")] private static extern uint SetTextAlign(IntPtr hdc, uint align);
    [DllImport("gdi32.dll")] private static extern int SelectClipRgn(IntPtr hdc, IntPtr region);
    [DllImport("gdi32.dll")] private static extern int IntersectClipRect(IntPtr hdc, int left, int top, int right, int bottom);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern IntPtr CreateFontIndirectW(ref LOGFONTW logical);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextMetricsW(IntPtr hdc, out TEXTMETRICW metrics);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern int GetTextFaceW(IntPtr hdc, int count, System.Text.StringBuilder face);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool GetTextExtentPoint32W(IntPtr hdc, string text, int length, out SIZE size);

    [DllImport("gdi32.dll", CharSet = CharSet.Unicode)]
    private static extern bool ExtTextOutW(IntPtr hdc, int x, int y, uint options, IntPtr rect,
                                           string text, uint length, IntPtr spacing);
}

/// <summary>A realized GDI font, with the vertical metrics the layout is built from.</summary>
internal sealed class GdiFont : IDisposable
{
    public IntPtr Handle { get; }

    /// <summary>The face GDI actually gave us, which may not be the one asked for.</summary>
    public string ActualFace { get; }

    /// <summary>Ascent plus descent — the line box, without external leading.</summary>
    public int Height { get; }

    public int Ascent { get; }

    public GdiFont(IntPtr handle, string actualFace, int height, int ascent)
    {
        Handle = handle;
        ActualFace = actualFace;
        Height = height;
        Ascent = ascent;
    }

    public void Dispose()
    {
        if (Handle != IntPtr.Zero) DeleteObject(Handle);
    }

    [DllImport("gdi32.dll")] private static extern bool DeleteObject(IntPtr obj);
}
