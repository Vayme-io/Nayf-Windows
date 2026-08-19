using System;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.IO;

namespace NayfWindows;

/// <summary>
/// Draws the idle agent avatar: the chip that sits in the corner while a task's card is
/// collapsed. Port of <c>avatarTile</c> in the Mac's AgentResultPanel.swift — a near-black
/// tile in the spirit of an M-series die shot, with a diagonal accent wash rising from the
/// bottom-left, a lit metallic rim, Nayf's mark glowing in the task colour, and a soft
/// accent glow around the whole chip.
///
/// <para>The glow is the reason this is drawn by hand rather than in XAML. It spills well
/// outside the tile into empty space, which needs a window with per-pixel alpha; a WinUI
/// window with an acrylic backdrop paints its whole rectangle and can only ever give the
/// chip a grey slab to sit on.</para>
///
/// <para>Kept apart from the window that shows it so the artwork can be rendered straight
/// to a file and looked at without running the app.</para>
/// </summary>
public static class NayfAgentAvatarArt
{
    /// <summary>
    /// The avatar window's footprint, and the Mac's collapsed size. Mostly empty: the tile
    /// is half this, and the rest is the room the glow and the float need.
    /// </summary>
    public const float CanvasSize = 88f;

    /// <summary>The chip itself, centred in the canvas.</summary>
    public const float TileSize = 44f;
    private const float TileRadius = 15f;

    /// <summary>
    /// Clear space kept around the tile in the sprite. Has to cover the widest glow (16)
    /// plus the float (3.5) without reaching the canvas edge, or the glow stops being a
    /// fade and becomes a cut.
    /// </summary>
    private const float GlowMargin = 18f;

    /// <summary>The cached sprite's size — the tile plus its glow margin on each side.</summary>
    public const float SpriteSize = TileSize + GlowMargin * 2f;

    /// <summary>
    /// The chip drifts up and down rather than sitting still, so an idle agent reads as
    /// waiting rather than as a stuck graphic. The Mac's numbers: a ~3.9s cycle, and a
    /// per-card phase so two stacked chips don't bob in lockstep.
    /// </summary>
    public const float BobAmplitude = 3.5f;
    public const float BobRadiansPerSecond = 1.6f;

    /// <summary>Near-black chip body — the die.</summary>
    private static readonly Color BodyColor = Color.FromArgb(255, 7, 9, 14);

    private const float GlyphSize = 24f;
    private const float RimWidth = 1.2f;

    /// <summary>
    /// How square the corners are. 2 is a plain circular arc; above that eases the join
    /// where the curve meets the straight edge, the way Apple's continuous corners do —
    /// which at a radius of 15 on a 44pt tile is most of the corner. Same treatment as
    /// <see cref="NayfActionToast"/> and the status pill.
    /// </summary>
    private const float CornerExponent = 2.2f;

    private static Bitmap? _mark;
    private static readonly object _markGate = new();

    /// <summary>
    /// Renders the chip — glow, body, wash, rim and mark — into a transparent bitmap of
    /// <see cref="SpriteSize"/> at <paramref name="scale"/>. Nothing here changes frame to
    /// frame, so the window renders this once per accent and then only moves it.
    /// </summary>
    public static Bitmap RenderSprite(double scale, Color accent)
    {
        int size = (int)Math.Ceiling(SpriteSize * scale);
        float tile = (float)(TileSize * scale);
        float margin = (float)(GlowMargin * scale);
        float radius = (float)(TileRadius * scale);

        var sprite = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(sprite);
        g.SmoothingMode = SmoothingMode.AntiAlias;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;

        // Left at the default on purpose: CompositingQuality.HighQuality means
        // gamma-corrected, which blends in linear light. Every layer below is accent at a
        // low alpha over a near-black body, and blending those in linear light makes each
        // one land far brighter than the SwiftUI original — the wash stops being a wash
        // and floods the whole tile in accent.
        g.Clear(Color.Transparent);

        using var path = RoundedRect(margin, margin, tile, tile, radius);

        // Glow first and widest-first, so the tight bright one lands on top of the broad
        // faint one rather than under it.
        DrawGlow(g, size, path, accent, (float)(16 * scale), 0.32f);
        DrawGlow(g, size, path, accent, (float)(8 * scale), 0.60f);

        using (var body = new SolidBrush(BodyColor))
            g.FillPath(body, path);

        // The wash: accent rising out of the bottom-left corner and gone by the top-right.
        using (var wash = new LinearGradientBrush(
                   new PointF(margin, margin + tile),
                   new PointF(margin + tile, margin),
                   Color.Transparent, Color.Transparent))
        {
            wash.InterpolationColors = new ColorBlend
            {
                Colors = [Tint(accent, 0.45f), Tint(accent, 0.10f), Tint(accent, 0f)],
                Positions = [0f, 0.55f, 1f]
            };
            g.FillPath(wash, path);
        }

        // The lit edge, running bright white through the accent to a dimmer white, so the
        // chip catches the light from one corner the way a machined edge does.
        float rim = (float)(RimWidth * scale);
        using (var rimPath = RoundedRect(margin + rim / 2f, margin + rim / 2f,
                                         tile - rim, tile - rim, radius - rim / 2f))
        using (var rimBrush = new LinearGradientBrush(
                   new PointF(margin + tile, margin),
                   new PointF(margin, margin + tile),
                   Color.White, Color.White))
        {
            rimBrush.InterpolationColors = new ColorBlend
            {
                Colors = [Tint(Color.White, 0.85f), Tint(accent, 0.9f), Tint(Color.White, 0.45f)],
                Positions = [0f, 0.5f, 1f]
            };
            using var pen = new Pen(rimBrush, rim);
            g.DrawPath(pen, rimPath);
        }

        DrawMark(g, size, scale, accent);
        return sprite;
    }

    /// <summary>Nayf's mark, tinted to the task colour and glowing in it.</summary>
    private static void DrawMark(Graphics g, int size, double scale, Color accent)
    {
        int glyph = (int)Math.Round(GlyphSize * scale);
        using var mask = LoadMark(glyph);
        if (mask == null) return;

        int left = (size - glyph) / 2;
        int top = (size - glyph) / 2;

        // The mark's own glow, on the same widest-first order as the chip's.
        using (var canvas = new Bitmap(size, size, PixelFormat.Format32bppArgb))
        {
            using (var cg = Graphics.FromImage(canvas))
            {
                cg.Clear(Color.Transparent);
                cg.DrawImageUnscaled(mask, left, top);
            }

            using (var wide = BlurAlpha(canvas, (int)Math.Round(12 * scale * BlurRadiusFactor), accent, 0.5f))
                g.DrawImageUnscaled(wide, 0, 0);
            using (var tight = BlurAlpha(canvas, (int)Math.Round(7 * scale * BlurRadiusFactor), accent, 0.9f))
                g.DrawImageUnscaled(tight, 0, 0);
        }

        using var tinted = Colorize(mask, accent, 1f);
        g.DrawImageUnscaled(tinted, left, top);
    }

    /// <summary>
    /// Blurs the shape and lays it down as a coloured haze. A box blur run three times is
    /// close enough to a Gaussian at these radii that the difference doesn't survive being
    /// drawn at 32% opacity.
    /// </summary>
    private static void DrawGlow(Graphics g, int size, GraphicsPath shape,
                                 Color color, float radius, float opacity)
    {
        int box = (int)Math.Round(radius * BlurRadiusFactor);
        if (box < 1) return;

        using var mask = new Bitmap(size, size, PixelFormat.Format32bppArgb);
        using (var mg = Graphics.FromImage(mask))
        {
            mg.SmoothingMode = SmoothingMode.AntiAlias;
            mg.Clear(Color.Transparent);
            using var brush = new SolidBrush(color);
            mg.FillPath(brush, shape);
        }

        using var blurred = BlurAlpha(mask, box, color, opacity);
        g.DrawImageUnscaled(blurred, 0, 0);
    }

    /// <summary>
    /// SwiftUI's shadow radius is not a box-blur radius — it describes a Gaussian about
    /// twice as tight. Matching it by eye rather than by formula, since what has to agree
    /// is the picture, not the maths.
    /// </summary>
    private const float BlurRadiusFactor = 0.6f;

    /// <summary>
    /// Loads Nayf's mark at <paramref name="px"/> square. The asset is a template — only
    /// its alpha carries the shape — so it is kept as a mask and coloured on use.
    /// </summary>
    private static Bitmap? LoadMark(int px)
    {
        lock (_markGate)
        {
            if (_mark == null)
            {
                var path = Path.Combine(AssetDirectory, "Assets", "NayfMark.png");
                if (!File.Exists(path)) return null;
                // Copied out of the file's own stream: a Bitmap constructed from a path
                // keeps the file locked for as long as it lives.
                using var loaded = new Bitmap(path);
                _mark = new Bitmap(loaded);
            }
        }

        var scaled = new Bitmap(px, px, PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(scaled);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.Clear(Color.Transparent);
        lock (_markGate) g.DrawImage(_mark!, new Rectangle(0, 0, px, px));
        return scaled;
    }

    /// <summary>
    /// Where Assets sits. The app's own directory, unless a harness has pointed it
    /// somewhere else in order to render the chip to a file.
    /// </summary>
    public static string AssetDirectory { get; set; } = AppContext.BaseDirectory;

    /// <summary>Rebuilds <paramref name="source"/> in one flat colour, keeping its alpha.</summary>
    private static Bitmap Colorize(Bitmap source, Color color, float opacity)
    {
        var result = new Bitmap(source.Width, source.Height, PixelFormat.Format32bppArgb);
        var rect = new Rectangle(0, 0, source.Width, source.Height);

        var src = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        var dst = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            int count = source.Width * source.Height * 4;
            var pixels = new byte[count];
            System.Runtime.InteropServices.Marshal.Copy(src.Scan0, pixels, 0, count);

            // 32bppArgb is B, G, R, A in memory order.
            for (int i = 0; i < count; i += 4)
            {
                byte a = pixels[i + 3];
                pixels[i]     = color.B;
                pixels[i + 1] = color.G;
                pixels[i + 2] = color.R;
                pixels[i + 3] = (byte)Math.Clamp(a * opacity, 0f, 255f);
            }

            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, dst.Scan0, count);
        }
        finally
        {
            source.UnlockBits(src);
            result.UnlockBits(dst);
        }
        return result;
    }

    /// <summary>
    /// Blurs only the alpha channel and paints the result in one flat colour. Blurring the
    /// colour channels as well would drag the transparent black the bitmap is cleared to
    /// into the result and ring the glow with a dark edge.
    /// </summary>
    private static Bitmap BlurAlpha(Bitmap source, int radius, Color color, float opacity)
    {
        int w = source.Width, h = source.Height;
        var rect = new Rectangle(0, 0, w, h);

        var alpha = new byte[w * h];
        var src = source.LockBits(rect, ImageLockMode.ReadOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[w * h * 4];
            System.Runtime.InteropServices.Marshal.Copy(src.Scan0, pixels, 0, pixels.Length);
            for (int i = 0, p = 3; i < alpha.Length; i++, p += 4) alpha[i] = pixels[p];
        }
        finally { source.UnlockBits(src); }

        // Three passes of a box blur is the usual stand-in for a Gaussian.
        for (int pass = 0; pass < 3; pass++)
        {
            alpha = BoxBlurPass(alpha, w, h, radius, horizontal: true);
            alpha = BoxBlurPass(alpha, w, h, radius, horizontal: false);
        }

        var result = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        var dst = result.LockBits(rect, ImageLockMode.WriteOnly, PixelFormat.Format32bppArgb);
        try
        {
            var pixels = new byte[w * h * 4];
            for (int i = 0, p = 0; i < alpha.Length; i++, p += 4)
            {
                pixels[p]     = color.B;
                pixels[p + 1] = color.G;
                pixels[p + 2] = color.R;
                pixels[p + 3] = (byte)Math.Clamp(alpha[i] * opacity, 0f, 255f);
            }
            System.Runtime.InteropServices.Marshal.Copy(pixels, 0, dst.Scan0, pixels.Length);
        }
        finally { result.UnlockBits(dst); }

        return result;
    }

    /// <summary>One separable box-blur pass, clamping at the edges.</summary>
    private static byte[] BoxBlurPass(byte[] src, int w, int h, int radius, bool horizontal)
    {
        var dst = new byte[src.Length];
        int outer = horizontal ? h : w;
        int inner = horizontal ? w : h;
        int step  = horizontal ? 1 : w;

        for (int o = 0; o < outer; o++)
        {
            int line = horizontal ? o * w : o;
            int sum = 0;

            // Prime the running total with the window sitting off the left edge, which
            // clamps to the first sample.
            for (int k = -radius; k <= radius; k++)
                sum += src[line + Math.Clamp(k, 0, inner - 1) * step];

            int window = radius * 2 + 1;
            for (int i = 0; i < inner; i++)
            {
                dst[line + i * step] = (byte)(sum / window);
                sum -= src[line + Math.Clamp(i - radius, 0, inner - 1) * step];
                sum += src[line + Math.Clamp(i + radius + 1, 0, inner - 1) * step];
            }
        }

        return dst;
    }

    private static Color Tint(Color c, float opacity) =>
        Color.FromArgb((byte)Math.Clamp(255f * opacity, 0f, 255f), c.R, c.G, c.B);

    /// <summary>
    /// A rounded rectangle with continuous corners rather than plain arcs. Same
    /// construction as <see cref="NayfActionToast"/>'s: walked clockwise from the left
    /// edge, every arc in that same direction, because the four corners are one closed
    /// polygon and an arc traversed backwards puts a chord across the corner it should
    /// round.
    /// </summary>
    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Max(0f, MathF.Min(r, MathF.Min(w / 2f, h / 2f)));

        const int steps = 20;
        var points = new PointF[(steps + 1) * 4];
        int i = 0;

        for (int s = 0; s <= steps; s++)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + r - r * cx, y + r - r * cy);
        }
        for (int s = 0; s <= steps; s++)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + w - r + r * cy, y + r - r * cx);
        }
        for (int s = 0; s <= steps; s++)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + w - r + r * cx, y + h - r + r * cy);
        }
        for (int s = steps; s >= 0; s--)
        {
            var (cx, cy) = SuperellipsePoint(s / (float)steps);
            points[i++] = new PointF(x + r - r * cx, y + h - r + r * cy);
        }

        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }

    private static (float X, float Y) SuperellipsePoint(float t)
    {
        double angle = t * Math.PI / 2.0;
        double p = 2.0 / CornerExponent;
        return ((float)Math.Pow(Math.Cos(angle), p), (float)Math.Pow(Math.Sin(angle), p));
    }
}
