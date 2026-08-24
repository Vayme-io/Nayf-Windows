using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NayfWindows;

/// <summary>One ability, and how lit it is right now.</summary>
public sealed class CapabilityRowModel
{
    /// <summary>Segoe Fluent Icons code point. Every one was picked by rendering it.</summary>
    public string Glyph = "";

    public string Title = "";
    public string Blurb = "";

    /// <summary>0 at rest, 1 while Vayme is saying this line.</summary>
    public float Emphasis;

    /// <summary>0 at full strength, 1 pushed back behind whichever row is lit.</summary>
    public float Recede;
}

/// <summary>Everything the card draws from. Read on the render thread, written on the UI one.</summary>
public sealed class CapabilitiesShowcaseModel
{
    public Color Accent = Color.FromArgb(255, 51, 143, 255);
    public string Headline = "";
    public string Subhead = "";
    public IReadOnlyList<CapabilityRowModel> Rows = Array.Empty<CapabilityRowModel>();
}

/// <summary>
/// Draws the capabilities card — the Windows counterpart of the Mac's
/// NayfCapabilitiesShowcase.swift. A list of the things Vayme can do, which Vayme reads out
/// loud, lighting each row as it reaches it.
///
/// <para>Dressed in CompanionPanelWindow's tokens for the same reason
/// <see cref="NayfAgentCardArt"/> is: this appears over the user's desktop seconds after the
/// panel or the status pill did, and three surfaces that don't agree with each other read as
/// three different applications.</para>
///
/// <para>Kept apart from the window that shows it so the artwork can be rendered straight to
/// a file and looked at without running the app.</para>
/// </summary>
public static class NayfCapabilitiesShowcaseArt
{
    // MARK: - Metrics (Mac NayfCapabilitiesShowcaseView)

    public const float Width = 440f;

    private const float Padding = 22f;
    private const float CornerRadius = 22f;
    private const float RimWidth = 1f;

    private const float HeaderTile = 40f;
    private const float HeaderTileCorner = 12f;
    private const float HeaderGap = 12f;
    private const float HeaderToRows = 16f;

    private const float RowGap = 8f;
    private const float RowCorner = 13f;
    private const float RowPaddingH = 12f;
    private const float RowPaddingV = 9f;
    private const float RowTile = 34f;
    private const float RowTileCorner = 10f;
    private const float RowTextGap = 13f;

    private const float RowHeight = RowPaddingV * 2f + RowTile;

    private const float HeadlineFontSize = 17f;
    private const float SubheadFontSize = 12f;
    private const float TitleFontSize = 13.5f;
    private const float BlurbFontSize = 11.5f;
    private const float RowGlyphSize = 15f;

    /// <summary>Gap between a row's two lines. The pair is centred against the tile beside them.</summary>
    private const float LineGap = 2f;

    /// <summary>
    /// Slack around the card for the shadow. Only the shadow lives out here — the card
    /// itself is inset by exactly this on every side.
    /// </summary>
    public const float ShadowMargin = 26f;

    private const int ShadowLayers = 7;
    private const float ShadowStep = 2.5f;

    // MARK: - Palette (CompanionPanelWindow.xaml, via NayfAgentCardArt)

    private static readonly Color BodyColor = Color.FromArgb(255, 8, 10, 15);
    private static readonly Color TextPrimary = Color.FromArgb(255, 0xF5, 0xF5, 0xF7);
    private static readonly Color TextSecondary = Color.FromArgb(255, 0xA1, 0xA1, 0xA6);

    /// <summary>SurfaceLow (#0AFFFFFF) — a row nobody is talking about.</summary>
    private const float SurfaceLow = 10f / 255f;

    /// <summary>SurfaceMid (#12FFFFFF) — the icon tile on a row nobody is talking about.</summary>
    private const float SurfaceMid = 18f / 255f;

    /// <summary>The panel's window edge, which is what separates a card from the wallpaper.</summary>
    private const float WindowEdgeAlpha = 80f / 255f;

    // The agent tile's ladder from AgentTaskStore: the card wears the quiet end of it, and a
    // lit row wears the loud end. The Mac's own numbers (0.14 fill, 0.6 border) sit between
    // these two, which is the same idea said in a palette this app doesn't otherwise use.
    private const float AccentWash = 28f / 255f;
    private const float AccentBorder = 56f / 255f;
    private const float LitRowFill = 46f / 255f;
    private const float LitRowBorder = 130f / 255f;

    /// <summary>How far a row that isn't being spoken about falls back. The Mac's 0.5.</summary>
    private const float RecedeFloor = 0.5f;

    // MARK: - Fonts
    //
    // Faces rather than families, best first — see NayfAgentCardArt for why GDI needs the
    // list rather than a family name and a weight.

    private static readonly (string Face, int Weight)[] RegularFaces =
    [
        ("Segoe UI Variable Text", NayfGdiText.WeightRegular),
        ("Segoe UI", NayfGdiText.WeightRegular)
    ];

    private static readonly (string Face, int Weight)[] SemiboldFaces =
    [
        ("Segoe UI Variable Text Semibold", NayfGdiText.WeightRegular),
        ("Segoe UI Variable Text", NayfGdiText.WeightSemibold),
        ("Segoe UI Semibold", NayfGdiText.WeightRegular),
        ("Segoe UI", NayfGdiText.WeightSemibold)
    ];

    private static readonly (string Face, int Weight)[] IconFaces =
    [
        ("Segoe Fluent Icons", NayfGdiText.WeightRegular),
        ("Segoe MDL2 Assets", NayfGdiText.WeightRegular)
    ];

    // MARK: - Size

    /// <summary>The card's height for a given number of rows, in design units.</summary>
    public static float HeightFor(int rows) =>
        Padding * 2f + HeaderTile + HeaderToRows +
        rows * RowHeight + Math.Max(0, rows - 1) * RowGap;

    // MARK: - Rendering

    /// <summary>
    /// Draws the card into a straight-alpha bitmap at <paramref name="scale"/>, with
    /// <see cref="ShadowMargin"/> of slack on every side for the shadow.
    /// </summary>
    public static Bitmap Render(CapabilitiesShowcaseModel model, float scale)
    {
        float cardWidth = Width * scale;
        float cardHeight = HeightFor(model.Rows.Count) * scale;
        float margin = ShadowMargin * scale;

        int pxWidth = (int)MathF.Ceiling(cardWidth + margin * 2f);
        int pxHeight = (int)MathF.Ceiling(cardHeight + margin * 2f);

        var canvas = new Bitmap(pxWidth, pxHeight, PixelFormat.Format32bppArgb);

        // Text is queued while the shapes go down and drawn in one pass afterwards, by GDI
        // rather than GDI+ — see NayfGdiText. It has to be the last thing to touch the
        // bitmap, and nothing may hold a Graphics on it when it runs.
        using var text = new NayfGdiText();
        using var headlineFont = text.CreateFont(SemiboldFaces, HeadlineFontSize * scale);
        using var subheadFont = text.CreateFont(RegularFaces, SubheadFontSize * scale);
        using var titleFont = text.CreateFont(SemiboldFaces, TitleFontSize * scale);
        using var blurbFont = text.CreateFont(RegularFaces, BlurbFontSize * scale);
        using var iconFont = text.CreateFont(IconFaces, RowGlyphSize * scale);

        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // CompositingQuality is left at the default on purpose: HighQuality blends in
            // linear light, and every wash below is a low-alpha accent over a near-black
            // body, which in linear light lands far brighter than intended. Same trap as
            // the idle chip's and the agent card's.
            g.Clear(Color.Transparent);

            float radius = CornerRadius * scale;
            using var body = RoundedRect(margin, margin, cardWidth, cardHeight, radius);

            DrawShadow(g, margin, cardWidth, cardHeight, radius, scale, pxWidth, pxHeight);

            using (var fill = new SolidBrush(BodyColor))
                g.FillPath(fill, body);
            using (var wash = new SolidBrush(Tint(model.Accent, AccentWash)))
                g.FillPath(wash, body);

            float rim = RimWidth * scale;
            using (var rimPath = RoundedRect(margin + rim / 2f, margin + rim / 2f,
                                             cardWidth - rim, cardHeight - rim, radius - rim / 2f))
            {
                using (var pen = new Pen(Tint(Color.White, WindowEdgeAlpha), rim))
                    g.DrawPath(pen, rimPath);
                using (var pen = new Pen(Tint(model.Accent, AccentBorder), rim))
                    g.DrawPath(pen, rimPath);
            }

            float left = margin + Padding * scale;
            float right = margin + cardWidth - Padding * scale;
            float y = margin + Padding * scale;

            DrawHeader(g, text, model, headlineFont, subheadFont, scale, left, right, y);

            y += (HeaderTile + HeaderToRows) * scale;
            foreach (var row in model.Rows)
            {
                DrawRow(g, text, model.Accent, row, titleFont, blurbFont, iconFont,
                        scale, left, right, y);
                y += (RowHeight + RowGap) * scale;
            }
        }

        text.Flush(canvas);
        return canvas;
    }

    private static readonly object ShadowGate = new();
    private static Bitmap? _shadow;
    private static (int Width, int Height, float Scale) _shadowKey;

    /// <summary>
    /// The soft shadow that lifts the card off the wallpaper, stacked rather than blurred —
    /// GDI+ has no blur.
    /// </summary>
    /// <remarks>
    /// <para>Nothing masks out the middle, even though seven layers stack up to a much darker
    /// patch there than the edge wants: the body goes down opaque immediately afterwards and
    /// covers every pixel of it. Clipping to a Region excluding the card path is the obvious
    /// way to write this, and it costs more than the whole rest of the card — GDI+ has to
    /// scan-convert a 64-point superellipse into a region and then test every span of every
    /// fill against it.</para>
    ///
    /// <para>Kept as a sprite because it is the same seven paths every time — it depends on
    /// the card's size and nothing else, not the accent and not which row is lit — and
    /// redrawing them is two thirds of a frame. Behind a lock because it's shared state that
    /// GDI+ will refuse to let two threads read at once, the way the avatar chip's
    /// ImageAttributes did.</para>
    /// </remarks>
    private static void DrawShadow(Graphics g, float margin, float width, float height,
                                   float radius, float scale, int pxWidth, int pxHeight)
    {
        lock (ShadowGate)
        {
            if (_shadow == null || _shadowKey != (pxWidth, pxHeight, scale))
            {
                _shadow?.Dispose();
                _shadow = new Bitmap(pxWidth, pxHeight, PixelFormat.Format32bppArgb);
                _shadowKey = (pxWidth, pxHeight, scale);

                using var sg = Graphics.FromImage(_shadow);
                sg.SmoothingMode = SmoothingMode.AntiAlias;
                sg.PixelOffsetMode = PixelOffsetMode.HighQuality;
                sg.Clear(Color.Transparent);

                using var brush = new SolidBrush(Color.FromArgb(13, 0, 0, 0));
                for (int layer = ShadowLayers; layer >= 1; layer--)
                {
                    float spread = layer * ShadowStep * scale;
                    using var path = RoundedRect(margin - spread, margin - spread + 4f * scale,
                                                 width + spread * 2f, height + spread * 2f,
                                                 radius + spread);
                    sg.FillPath(brush, path);
                }
            }

            g.DrawImageUnscaled(_shadow, 0, 0);
        }
    }

    /// <summary>
    /// Vayme's own mark in an accent tile, then the headline and the shortcut that gets Vayme
    /// listening — the one line on the card the user has to know to use any of the rest.
    /// </summary>
    private static void DrawHeader(Graphics g, NayfGdiText text, CapabilitiesShowcaseModel model,
                                   GdiFont headlineFont, GdiFont subheadFont,
                                   float scale, float left, float right, float top)
    {
        float tile = HeaderTile * scale;
        DrawAccentTile(g, left, top, tile, HeaderTileCorner * scale, model.Accent, 1f);

        // The mark is a template — only its alpha carries the shape — so it comes back as a
        // mask and is coloured white on the way in, the way the Mac sets NayfMenuBarIcon.
        float markSize = MathF.Round(22f * scale);
        using (var mark = NayfAgentAvatarArt.LoadMark((int)markSize, Color.White))
        {
            if (mark != null)
                g.DrawImage(mark, MathF.Round(left + (tile - markSize) / 2f),
                                  MathF.Round(top + (tile - markSize) / 2f));
        }

        float textLeft = left + tile + HeaderGap * scale;
        float block = headlineFont.Height + LineGap * scale + subheadFont.Height;
        float y = top + (tile - block) / 2f;

        var clip = new RectangleF(textLeft, top, right - textLeft, tile);
        text.Add(model.Headline, headlineFont, textLeft, y, TextPrimary, clip);
        text.Add(model.Subhead, subheadFont, textLeft,
                 y + headlineFont.Height + LineGap * scale, TextSecondary, clip);
    }

    /// <summary>
    /// One ability. Lighting it up is done entirely in colour: the Mac also scales the row
    /// to 1.03, which GDI can't do to text — NayfGdiText draws straight onto the bitmap,
    /// past any transform GDI+ has in effect — and at this size the accent doing the work
    /// on its own reads more clearly than a 3% nudge would have.
    /// </summary>
    private static void DrawRow(Graphics g, NayfGdiText text, Color accent, CapabilityRowModel row,
                                GdiFont titleFont, GdiFont blurbFont, GdiFont iconFont,
                                float scale, float left, float right, float top)
    {
        float height = RowHeight * scale;
        float lit = Math.Clamp(row.Emphasis, 0f, 1f);

        // Everything on a row fades together as it recedes, so a row Vayme has moved on from
        // steps back as one thing rather than coming apart into its pieces.
        float strength = 1f - (1f - RecedeFloor) * Math.Clamp(row.Recede, 0f, 1f);

        using (var background = RoundedRect(left, top, right - left, height, RowCorner * scale))
        {
            var fill = lit > 0f
                ? Blend(Tint(Color.White, SurfaceLow), Tint(accent, LitRowFill), lit)
                : Tint(Color.White, SurfaceLow);
            using (var brush = new SolidBrush(Fade(fill, strength)))
                g.FillPath(brush, background);

            if (lit > 0f)
                using (var pen = new Pen(Fade(Tint(accent, LitRowBorder * lit), strength), 1f * scale))
                    g.DrawPath(pen, background);
        }

        float tile = RowTile * scale;
        float tileLeft = left + RowPaddingH * scale;
        float tileTop = top + RowPaddingV * scale;

        if (lit > 0f)
        {
            using var rest = new SolidBrush(Fade(Tint(Color.White, SurfaceMid), strength * (1f - lit)));
            using var path = RoundedRect(tileLeft, tileTop, tile, tile, RowTileCorner * scale);
            g.FillPath(rest, path);
            DrawAccentTile(g, tileLeft, tileTop, tile, RowTileCorner * scale, accent, strength * lit);
        }
        else
        {
            using var rest = new SolidBrush(Fade(Tint(Color.White, SurfaceMid), strength));
            using var path = RoundedRect(tileLeft, tileTop, tile, tile, RowTileCorner * scale);
            g.FillPath(rest, path);
        }

        // The glyph goes from wearing the accent to sitting on it.
        var glyphInk = Fade(Blend(accent, Color.White, lit), strength);
        float glyphWidth = text.Measure(row.Glyph, iconFont);
        text.Add(row.Glyph, iconFont,
                 tileLeft + (tile - glyphWidth) / 2f,
                 tileTop + (tile - iconFont.Height) / 2f,
                 glyphInk);

        float textLeft = tileLeft + tile + RowTextGap * scale;
        float textRight = right - RowPaddingH * scale;
        float block = titleFont.Height + LineGap * scale + blurbFont.Height;
        float y = top + (height - block) / 2f;

        var clip = new RectangleF(textLeft, top, textRight - textLeft, height);
        text.Add(row.Title, titleFont, textLeft, y, Fade(TextPrimary, strength), clip);
        text.Add(row.Blurb, blurbFont, textLeft, y + titleFont.Height + LineGap * scale,
                 Fade(TextSecondary, strength), clip);
    }

    /// <summary>The accent as a diagonal gradient, at <paramref name="opacity"/> of itself.</summary>
    private static void DrawAccentTile(Graphics g, float x, float y, float size, float radius,
                                       Color accent, float opacity)
    {
        if (opacity <= 0f) return;

        int strong = (int)MathF.Round(255 * Math.Clamp(opacity, 0f, 1f));
        using var path = RoundedRect(x, y, size, size, radius);
        using var brush = new LinearGradientBrush(
            new PointF(x, y), new PointF(x + size, y + size),
            Color.FromArgb(strong, accent),
            Color.FromArgb(strong * 179 / 255, accent));
        brush.WrapMode = WrapMode.TileFlipXY;
        g.FillPath(brush, path);
    }

    // MARK: - Colour

    /// <summary><paramref name="color"/> at a fraction of full opacity.</summary>
    private static Color Tint(Color color, float alpha) =>
        Color.FromArgb((int)MathF.Round(255 * Math.Clamp(alpha, 0f, 1f)), color);

    /// <summary>Scales a colour's existing alpha, leaving its channels alone.</summary>
    private static Color Fade(Color color, float factor) =>
        Color.FromArgb((int)MathF.Round(color.A * Math.Clamp(factor, 0f, 1f)),
                       color.R, color.G, color.B);

    /// <summary>Mixes two colours, alpha included, <paramref name="t"/> of the way from a to b.</summary>
    private static Color Blend(Color a, Color b, float t)
    {
        t = Math.Clamp(t, 0f, 1f);
        return Color.FromArgb(
            (int)MathF.Round(a.A + (b.A - a.A) * t),
            (int)MathF.Round(a.R + (b.R - a.R) * t),
            (int)MathF.Round(a.G + (b.G - a.G) * t),
            (int)MathF.Round(a.B + (b.B - a.B) * t));
    }

    // MARK: - Geometry

    /// <summary>
    /// A rounded rectangle with Apple's continuous corners rather than plain arcs — at this
    /// radius a circular arc meets the straight edge at a visible kink. Same construction as
    /// <see cref="NayfActionToast"/>'s.
    /// </summary>
    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Max(0f, MathF.Min(r, MathF.Min(w / 2f, h / 2f)));

        const int steps = 16;
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

    private const float CornerExponent = 2.2f;

    private static (float X, float Y) SuperellipsePoint(float t)
    {
        double angle = t * Math.PI / 2.0;
        double p = 2.0 / CornerExponent;
        return ((float)Math.Pow(Math.Cos(angle), p), (float)Math.Pow(Math.Sin(angle), p));
    }
}
