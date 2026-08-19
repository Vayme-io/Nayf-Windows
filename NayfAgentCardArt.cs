using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace NayfWindows;

/// <summary>Which of the card's three controls a point lands on.</summary>
public enum AgentCardTarget { None, Dismiss, Copy, FollowUp }

/// <summary>Everything the card draws from. Read on the render thread, written on the UI one.</summary>
public sealed class AgentCardModel
{
    public string Title = "";
    public string Summary = "";
    public string CopyLabel = "Copy";
    public Color Accent = Color.FromArgb(255, 51, 143, 255);
    public bool Listening;

    /// <summary>Which control the pointer is over, so it can light up under the cursor.</summary>
    public AgentCardTarget Hover = AgentCardTarget.None;

    /// <summary>How far the summary is scrolled, in device pixels.</summary>
    public float ScrollOffset;

    public AgentCardModel Clone() => (AgentCardModel)MemberwiseClone();
}

/// <summary>
/// Where the card's controls ended up, in device pixels relative to the window's top-left.
/// The window hit-tests against these rather than recomputing the layout.
/// </summary>
public sealed class AgentCardLayout
{
    public RectangleF Dismiss;
    public RectangleF Copy;
    public RectangleF FollowUp;
    public RectangleF Summary;

    /// <summary>How far the summary can scroll before its last line is flush with the bottom.</summary>
    public float ScrollMax;
}

/// <summary>
/// Draws the opened agent result card — the Windows counterpart of <c>expandedCard</c> in
/// the Mac's AgentResultPanel.swift. A near-black slab tinted with the task colour, a title
/// with a "Done" pill and a close button, the summary, and two actions: a small Copy pill
/// and the follow-up bar.
///
/// <para>Dressed in CompanionPanelWindow's tokens rather than the Mac's raw values, because
/// the panel is where this same task already appears as a tile and the two are often on
/// screen together. The card is that tile grown up: the panel's accent ladder for the wash
/// and the border, its three text tiers, its two surface tints, its round icon button, its
/// status pill, and its full-width accent CTA for the follow-up.</para>
///
/// <para>Drawn by hand rather than in XAML for the corners. The Mac rounds this card at 18
/// with continuous curvature; DWM rounds a top-level window at 8 and cannot be asked for
/// anything else, and a system backdrop paints the whole window rectangle behind the XAML
/// tree — so a card rounded at 18 in XAML just leaves a squarer slab showing past its
/// corners. Per-pixel alpha is the only way to get the shape, and that means drawing it.</para>
///
/// <para>Kept apart from the window that shows it so the artwork can be rendered straight
/// to a file and looked at without running the app — the same split as
/// <see cref="NayfAgentAvatarArt"/>.</para>
/// </summary>
public static class NayfAgentCardArt
{
    // MARK: - Metrics (Mac AgentResultCardMetrics + expandedCard)

    public const float Width = 340f;
    public const float Height = 232f;

    private const float Padding = 16f;
    private const float CornerRadius = 18f;
    private const float RowSpacing = 12f;
    private const float RimWidth = 1f;

    /// <summary>Header row height: the close button is the tallest thing in it.</summary>
    private const float HeaderHeight = 26f;

    private const float TitleFontSize = 14f;
    private const float StatusFontSize = 11.5f;
    private const float SummaryFontSize = 13f;
    private const float CopyFontSize = 12f;
    private const float FollowUpFontSize = 13f;

    /// <summary>
    /// Extra leading between summary lines. The Mac asks for <c>.lineSpacing(3)</c>; the
    /// panel asks for <c>LineHeight="19"</c> on 13pt body copy, which is a hair tighter,
    /// and it is the panel this card is trying to look like.
    /// </summary>
    private const float SummaryLineGap = 2f;

    /// <summary>
    /// Gutter kept clear down the right of the summary for the scroll indicator. Reserved
    /// whether or not the text overflows: taking it only when it scrolls would reflow the
    /// paragraph the moment a line pushed it over, which reads as a glitch.
    /// </summary>
    private const float ScrollGutter = 8f;

    private const float StatusPillHeight = 22f;
    private const float DismissDiameter = 26f;

    /// <summary>The card body — the same near-black as the idle chip's die.</summary>
    private static readonly Color BodyColor = Color.FromArgb(255, 8, 10, 15);

    // MARK: - Palette (CompanionPanelWindow.xaml)
    //
    // The panel's own tokens, so the card reads as the same app rather than as something
    // that happens to float next to it. The three text tiers and the two surface tints are
    // its ResourceDictionary; the accent strengths are the ones AgentTaskStore hands the
    // agent tiles in the panel, which show this card's content in its collapsed form.

    private static readonly Color TextPrimary = Color.FromArgb(255, 0xF5, 0xF5, 0xF7);
    private static readonly Color TextSecondary = Color.FromArgb(255, 0xA1, 0xA1, 0xA6);
    private static readonly Color TextTertiary = Color.FromArgb(255, 0x6E, 0x6E, 0x73);

    /// <summary>SurfaceLow (#0AFFFFFF) — the quiet chrome: pills, wells, the follow-up's rest state.</summary>
    private const float SurfaceLow = 10f / 255f;

    /// <summary>SurfaceMid (#12FFFFFF) — a control that wants to be seen: the round icon buttons.</summary>
    private const float SurfaceMid = 18f / 255f;

    /// <summary>What either surface lifts to under the pointer. The panel has no token for
    /// this because WinUI's PointerOver visual state supplies it; this is that lift.</summary>
    private const float SurfaceHot = 30f / 255f;

    /// <summary>Hairline (#14FFFFFF) — every border in the panel.</summary>
    private const float HairlineAlpha = 20f / 255f;

    /// <summary>
    /// The card's outer edge. Deliberately stronger than <see cref="HairlineAlpha"/>: the
    /// panel's hairlines separate one dark surface from another dark surface, whereas this
    /// one has to separate the card from whatever wallpaper is behind it. This is the lift
    /// DWM's own window border carries, which is what the panel's edge is.
    /// </summary>
    private const float WindowEdgeAlpha = 80f / 255f;

    // AgentTaskStore's tile ladder: TileFillBrush(28), TileBorderBrush(56).
    private const float AccentWash = 28f / 255f;
    private const float AccentBorder = 56f / 255f;

    /// <summary>The panel's destructive red — the Allow button on the danger prompt.</summary>
    private static readonly Color ListeningRed = Color.FromArgb(255, 0xFF, 0x45, 0x3A);

    // MARK: - Fonts
    //
    // Faces rather than families, best first, because these go to GDI: it substitutes a
    // face it hasn't got instead of failing, and NayfGdiText walks the list until one comes
    // back as itself. Windows ships semibold as its own face rather than as a weight of the
    // regular one, so the list asks both ways round before settling for plain Segoe UI.

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

    // MARK: - Rendering

    /// <summary>
    /// Draws the whole card into a straight-alpha bitmap of <see cref="Width"/> ×
    /// <see cref="Height"/> at <paramref name="scale"/>, and reports where its controls
    /// landed so the window can hit-test them.
    /// </summary>
    public static Bitmap Render(AgentCardModel model, double scale, out AgentCardLayout layout)
    {
        float s = (float)scale;
        int pxWidth = (int)MathF.Ceiling(Width * s);
        int pxHeight = (int)MathF.Ceiling(Height * s);

        layout = new AgentCardLayout();

        var canvas = new Bitmap(pxWidth, pxHeight, PixelFormat.Format32bppArgb);

        // Text is queued while the shapes go down and drawn in one pass afterwards, by GDI
        // rather than GDI+ — see NayfGdiText for why. It has to be the last thing to touch
        // the bitmap, and nothing may hold a Graphics on it when it runs.
        using var text = new NayfGdiText();
        using var titleFont = text.CreateFont(SemiboldFaces, TitleFontSize * s);
        // The status label is regular rather than semibold: it is TextSecondary grey now,
        // and grey semibold at 11.5px reads as smudged rather than as emphasis.
        using var statusFont = text.CreateFont(RegularFaces, StatusFontSize * s);
        using var summaryFont = text.CreateFont(RegularFaces, SummaryFontSize * s);
        using var copyFont = text.CreateFont(RegularFaces, CopyFontSize * s);
        using var followUpFont = text.CreateFont(SemiboldFaces, FollowUpFontSize * s);

        using (var g = Graphics.FromImage(canvas))
        {
            g.SmoothingMode = SmoothingMode.AntiAlias;
            g.PixelOffsetMode = PixelOffsetMode.HighQuality;

            // CompositingQuality is left at the default on purpose. HighQuality means
            // gamma-corrected, which blends in linear light — and the wash below is accent
            // at a low alpha over a near-black body, which in linear light lands far
            // brighter than the SwiftUI original. Same trap as the idle chip's.
            g.Clear(Color.Transparent);

            float radius = CornerRadius * s;
            using var body = RoundedRect(0f, 0f, pxWidth, pxHeight, radius);

            using (var fill = new SolidBrush(BodyColor))
                g.FillPath(fill, body);

            // The wash: the task colour flat across the whole slab, at the strength the
            // panel washes the matching agent tile. Flat rather than the diagonal gradient
            // the Mac uses — a gradient is the one thing on this card the panel never does,
            // and side by side it was what gave the card away as a different surface.
            using (var wash = new SolidBrush(Tint(model.Accent, AccentWash)))
                g.FillPath(wash, body);

            // The edge, in two flat passes: the panel's hairline, which is what separates a
            // window from the wallpaper behind it, and then the tile's accent border over
            // the top so the task colour reaches the boundary.
            float rim = RimWidth * s;
            using (var rimPath = RoundedRect(rim / 2f, rim / 2f,
                                             pxWidth - rim, pxHeight - rim, radius - rim / 2f))
            {
                using (var pen = new Pen(Tint(Color.White, WindowEdgeAlpha), rim))
                    g.DrawPath(pen, rimPath);
                using (var pen = new Pen(Tint(model.Accent, AccentBorder), rim))
                    g.DrawPath(pen, rimPath);
            }

            float pad = Padding * s;
            float contentLeft = pad;
            float contentRight = pxWidth - pad;
            float contentWidth = contentRight - contentLeft;

            DrawHeader(g, text, model, layout, titleFont, statusFont, s, contentLeft, contentRight);

            // Laid out from the bottom up: both action rows are fixed height, and what is
            // left over between them and the header is however much summary fits.
            float followUpHeight = MathF.Ceiling(followUpFont.Height + 24f * s);
            float followUpTop = pxHeight - pad - followUpHeight;
            layout.FollowUp = new RectangleF(contentLeft, followUpTop, contentWidth, followUpHeight);

            float copyHeight = MathF.Ceiling(copyFont.Height + 14f * s);
            float copyTop = followUpTop - RowSpacing * s - copyHeight;

            float summaryTop = pad + HeaderHeight * s + RowSpacing * s;
            float summaryHeight = copyTop - RowSpacing * s - summaryTop;
            layout.Summary = new RectangleF(contentLeft, summaryTop, contentWidth, summaryHeight);

            DrawSummary(g, text, model, layout, summaryFont, s);
            DrawCopy(g, text, model, layout, copyFont, s, contentLeft, copyTop, copyHeight);
            DrawFollowUp(g, text, model, layout, followUpFont, s);
        }

        text.Flush(canvas);
        return canvas;
    }

    private static void DrawHeader(Graphics g, NayfGdiText text, AgentCardModel model,
                                   AgentCardLayout layout, GdiFont titleFont, GdiFont statusFont,
                                   float s, float contentLeft, float contentRight)
    {
        float top = Padding * s;
        float rowHeight = HeaderHeight * s;

        // Close button, hard against the right edge.
        float dismiss = DismissDiameter * s;
        var dismissRect = new RectangleF(contentRight - dismiss, top, dismiss, dismiss);
        layout.Dismiss = dismissRect;

        // The panel's RoundIconButton: SurfaceMid behind a TextSecondary glyph.
        bool dismissHot = model.Hover == AgentCardTarget.Dismiss;
        using (var back = new SolidBrush(Tint(Color.White, dismissHot ? SurfaceHot : SurfaceMid)))
            g.FillEllipse(back, dismissRect);
        DrawXMark(g, dismissRect, dismissHot ? TextPrimary : TextSecondary, 9f * s, 1.4f * s);

        // "Done", built like the panel's header status pill: a neutral SurfaceLow capsule
        // carrying a small coloured dot and a TextSecondary label. The panel says status
        // with the dot and keeps the pill itself quiet, so the card does too — it used to
        // tint the whole pill accent and set the label in accent, which shouted next to it.
        const string status = "Done";
        float dot = 7f * s;
        float statusWidth = 9f * s + dot + 6f * s + text.Measure(status, statusFont) + 11f * s;
        float statusHeight = StatusPillHeight * s;
        var statusRect = new RectangleF(dismissRect.Left - 8f * s - statusWidth,
                                        top + (rowHeight - statusHeight) / 2f,
                                        statusWidth, statusHeight);

        using (var pill = new SolidBrush(Tint(Color.White, SurfaceLow)))
        using (var pillPath = RoundedRect(statusRect.X, statusRect.Y,
                                          statusRect.Width, statusRect.Height,
                                          statusHeight / 2f))
            g.FillPath(pill, pillPath);

        using (var dotBrush = new SolidBrush(model.Accent))
            g.FillEllipse(dotBrush, statusRect.X + 9f * s,
                          statusRect.Y + (statusHeight - dot) / 2f, dot, dot);

        text.Add(status, statusFont,
                 statusRect.X + 9f * s + dot + 6f * s,
                 statusRect.Y + (statusRect.Height - statusFont.Height) / 2f,
                 TextSecondary);

        // The title takes whatever is left.
        float titleWidth = statusRect.Left - 6f * s - contentLeft;
        string title = Truncate(text, model.Title, titleFont, titleWidth);
        text.Add(title, titleFont,
                 contentLeft, top + (rowHeight - titleFont.Height) / 2f,
                 TextPrimary);
    }

    private static void DrawSummary(Graphics g, NayfGdiText text, AgentCardModel model,
                                    AgentCardLayout layout, GdiFont font, float s)
    {
        var viewport = layout.Summary;
        if (viewport.Height <= 0f) return;

        float textWidth = viewport.Width - ScrollGutter * s;
        var lines = Wrap(text, model.Summary, font, textWidth);

        float step = font.Height + SummaryLineGap * s;
        float total = lines.Count * step;
        layout.ScrollMax = MathF.Max(0f, total - viewport.Height);

        float offset = Math.Clamp(model.ScrollOffset, 0f, layout.ScrollMax);

        // TextSecondary, which is what the panel sets body copy in — including the summary
        // on this task's own tile. The title above it is TextPrimary, and that pair is the
        // whole hierarchy the panel has.
        var ink = TextSecondary;
        float y = viewport.Y - offset;
        foreach (var line in lines)
        {
            // Whole lines above and below the viewport still cost a shaping pass, and a
            // long summary is a lot of them. The ones that straddle an edge are clipped.
            if (y + step >= viewport.Y && y <= viewport.Bottom)
                text.Add(line, font, viewport.X, y, ink, viewport);
            y += step;
        }

        if (layout.ScrollMax <= 0f) return;

        // A thumb rather than a full scroller: it is here to say the text continues, and
        // the card is too small to spend a real scrollbar's width on saying it.
        float trackHeight = viewport.Height;
        float thumbHeight = MathF.Max(18f * s, trackHeight * (viewport.Height / total));
        float travel = trackHeight - thumbHeight;
        float thumbTop = viewport.Y + travel * (offset / layout.ScrollMax);
        float thumbWidth = 3f * s;

        using var thumb = new SolidBrush(TextTertiary);
        using var thumbPath = RoundedRect(viewport.Right - thumbWidth, thumbTop,
                                          thumbWidth, thumbHeight, thumbWidth / 2f);
        g.FillPath(thumb, thumbPath);
    }

    private static void DrawCopy(Graphics g, NayfGdiText text, AgentCardModel model,
                                 AgentCardLayout layout, GdiFont font, float s,
                                 float left, float top, float height)
    {
        float icon = 12f * s;
        float labelWidth = text.Measure(model.CopyLabel, font);
        float width = 12f * s + icon + 6f * s + labelWidth + 12f * s;

        var rect = new RectangleF(left, top, width, height);
        layout.Copy = rect;

        // The panel's secondary button: SurfaceMid behind a TextSecondary label, rounded
        // hard enough at this height to come out a capsule, same as its Cancel and Later.
        bool hot = model.Hover == AgentCardTarget.Copy;
        using (var back = new SolidBrush(Tint(Color.White, hot ? SurfaceHot : SurfaceMid)))
        using (var path = RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, height / 2f))
            g.FillPath(back, path);

        var ink = hot ? TextPrimary : TextSecondary;
        DrawCopyGlyph(g, new RectangleF(rect.X + 12f * s, rect.Y + (height - icon) / 2f, icon, icon),
                      ink, 1.2f * s);

        text.Add(model.CopyLabel, font,
                 rect.X + 12f * s + icon + 6f * s,
                 rect.Y + (height - font.Height) / 2f,
                 ink);
    }

    private static void DrawFollowUp(Graphics g, NayfGdiText text, AgentCardModel model,
                                     AgentCardLayout layout, GdiFont font, float s)
    {
        var rect = layout.FollowUp;
        bool hot = model.Hover == AgentCardTarget.FollowUp;

        // The panel's full-width primary button — a solid accent fill rounded at 10 with a
        // white semibold label — worn in the task's own colour rather than AccentLink, so
        // the card's one loud element is loud in the colour the rest of the card already
        // is. It used to be a white capsule with black text, which was the single thing
        // that made the card look like it came from a different app.
        var fillColor = model.Listening ? ListeningRed : model.Accent;
        if (hot) fillColor = Dim(fillColor, 0.12f);

        using (var back = new SolidBrush(fillColor))
        using (var path = RoundedRect(rect.X, rect.Y, rect.Width, rect.Height, 10f * s))
            g.FillPath(back, path);

        float glyph = 15f * s;
        var glyphRect = new RectangleF(rect.X + 16f * s, rect.Y + (rect.Height - glyph) / 2f, glyph, glyph);

        if (model.Listening) DrawStopGlyph(g, glyphRect, Color.White, fillColor);
        else DrawMicGlyph(g, glyphRect, Color.White);

        string label = model.Listening ? "Listening… tap to send" : "Follow up with agent";
        text.Add(label, font,
                 glyphRect.Right + 10f * s,
                 rect.Y + (rect.Height - font.Height) / 2f,
                 Color.White);

        if (model.Listening) return;

        float keys = 17f * s;
        DrawKeyboardGlyph(g, new RectangleF(rect.Right - 16f * s - keys,
                                            rect.Y + (rect.Height - keys * 0.7f) / 2f,
                                            keys, keys * 0.7f),
                          Tint(Color.White, 0.6f), 1.1f * s);
    }

    // MARK: - Glyphs
    //
    // Drawn rather than set in Segoe Fluent Icons. The Mac's are SF Symbols, and the
    // nearest Fluent glyphs sit on different baselines at different optical weights — five
    // hand-drawn shapes are less work than making five mismatched ones line up.

    private static void DrawXMark(Graphics g, RectangleF box, Color color, float size, float stroke)
    {
        float cx = box.X + box.Width / 2f;
        float cy = box.Y + box.Height / 2f;
        float h = size / 2f;

        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };
        g.DrawLine(pen, cx - h, cy - h, cx + h, cy + h);
        g.DrawLine(pen, cx + h, cy - h, cx - h, cy + h);
    }

    /// <summary>Two stacked pages, the back one clipped where the front covers it.</summary>
    private static void DrawCopyGlyph(Graphics g, RectangleF box, Color color, float stroke)
    {
        float s = box.Width;
        float r = s * 0.18f;
        var front = new RectangleF(box.X, box.Y + s * 0.26f, s * 0.74f, s * 0.74f);
        var back = new RectangleF(box.X + s * 0.26f, box.Y, s * 0.74f, s * 0.74f);

        using var pen = new Pen(color, stroke) { LineJoin = LineJoin.Round };

        using (var frontPath = RoundedRect(front.X, front.Y, front.Width, front.Height, r))
        {
            var saved = g.Save();
            // The back page only shows where the front one doesn't cover it — without this
            // the two outlines cross and the glyph reads as a grid at 12px.
            g.SetClip(frontPath, CombineMode.Exclude);
            using (var backPath = RoundedRect(back.X, back.Y, back.Width, back.Height, r))
                g.DrawPath(pen, backPath);
            g.Restore(saved);

            g.DrawPath(pen, frontPath);
        }
    }

    private static void DrawMicGlyph(Graphics g, RectangleF box, Color color)
    {
        float s = box.Width;
        float capsuleWidth = s * 0.40f;
        float capsuleHeight = s * 0.56f;
        float cx = box.X + s / 2f;

        using var brush = new SolidBrush(color);
        using (var capsule = RoundedRect(cx - capsuleWidth / 2f, box.Y,
                                         capsuleWidth, capsuleHeight, capsuleWidth / 2f))
            g.FillPath(brush, capsule);

        float stroke = s * 0.11f;
        using var pen = new Pen(color, stroke) { StartCap = LineCap.Round, EndCap = LineCap.Round };

        // The cradle, a half-turn of an ellipse the capsule sits inside.
        float cradle = s * 0.62f;
        g.DrawArc(pen, cx - cradle / 2f, box.Y + s * 0.24f, cradle, cradle * 0.86f, 20f, 140f);

        float stemTop = box.Y + s * 0.24f + cradle * 0.86f / 2f + cradle * 0.43f - stroke / 2f;
        g.DrawLine(pen, cx, stemTop, cx, box.Bottom);
    }

    /// <summary>A filled disc with a square knocked out of it — SF's stop.circle.fill.</summary>
    private static void DrawStopGlyph(Graphics g, RectangleF box, Color color, Color hole)
    {
        using (var brush = new SolidBrush(color))
            g.FillEllipse(brush, box);

        float inner = box.Width * 0.38f;
        using (var brush = new SolidBrush(hole))
        using (var square = RoundedRect(box.X + (box.Width - inner) / 2f,
                                        box.Y + (box.Height - inner) / 2f,
                                        inner, inner, inner * 0.22f))
            g.FillPath(brush, square);
    }

    private static void DrawKeyboardGlyph(Graphics g, RectangleF box, Color color, float stroke)
    {
        using var pen = new Pen(color, stroke) { LineJoin = LineJoin.Round };
        using (var frame = RoundedRect(box.X, box.Y, box.Width, box.Height, box.Height * 0.22f))
            g.DrawPath(pen, frame);

        using var brush = new SolidBrush(color);
        float key = MathF.Max(1f, box.Width * 0.09f);
        float rowOne = box.Y + box.Height * 0.31f;
        float rowTwo = box.Y + box.Height * 0.58f;

        for (int i = 0; i < 3; i++)
            g.FillRectangle(brush, box.X + box.Width * (0.22f + i * 0.23f), rowOne, key, key);

        g.FillRectangle(brush, box.X + box.Width * 0.30f, rowTwo, box.Width * 0.40f, key);
    }

    // MARK: - Text

    /// <summary>Breaks <paramref name="value"/> to <paramref name="maxWidth"/>, honouring its own newlines.</summary>
    private static List<string> Wrap(NayfGdiText text, string value, GdiFont font, float maxWidth)
    {
        var lines = new List<string>();
        if (value.Length == 0 || maxWidth <= 0f) return lines;

        foreach (var paragraph in value.Replace("\r\n", "\n").Split('\n'))
        {
            if (paragraph.Length == 0) { lines.Add(""); continue; }

            var words = paragraph.Split(' ');
            string current = "";

            foreach (var word in words)
            {
                string candidate = current.Length == 0 ? word : current + " " + word;
                if (text.Measure(candidate, font) <= maxWidth) { current = candidate; continue; }

                if (current.Length > 0) { lines.Add(current); current = ""; }

                // A single word too long for the column — a path, a URL — is broken
                // wherever it runs out of room rather than allowed to run off the edge.
                string rest = word;
                while (text.Measure(rest, font) > maxWidth && rest.Length > 1)
                {
                    int fit = 1;
                    while (fit < rest.Length && text.Measure(rest[..(fit + 1)], font) <= maxWidth) fit++;
                    lines.Add(rest[..fit]);
                    rest = rest[fit..];
                }
                current = rest;
            }

            lines.Add(current);
        }

        return lines;
    }

    /// <summary>Clips <paramref name="value"/> to one line of <paramref name="maxWidth"/>, with an ellipsis.</summary>
    private static string Truncate(NayfGdiText text, string value, GdiFont font, float maxWidth)
    {
        if (value.Length == 0 || maxWidth <= 0f) return "";
        if (text.Measure(value, font) <= maxWidth) return value;

        for (int length = value.Length - 1; length > 0; length--)
        {
            string candidate = value[..length].TrimEnd() + "…";
            if (text.Measure(candidate, font) <= maxWidth) return candidate;
        }
        return "…";
    }

    // MARK: - Shapes

    private static Color Tint(Color color, float opacity) =>
        Color.FromArgb((byte)Math.Clamp(255f * opacity, 0f, 255f), color.R, color.G, color.B);

    /// <summary>
    /// Takes <paramref name="amount"/> out of a filled button's colour for its hover state.
    /// Fluent's accent buttons go down rather than up under the pointer — PointerOver swaps
    /// AccentFillColorDefault for AccentFillColorSecondary, which is the same accent at 90%
    /// over the background — so the card's one filled button does too.
    /// </summary>
    private static Color Dim(Color color, float amount) =>
        Color.FromArgb(color.A,
                       (byte)(color.R * (1f - amount)),
                       (byte)(color.G * (1f - amount)),
                       (byte)(color.B * (1f - amount)));

    /// <summary>
    /// A rounded rectangle with continuous corners rather than plain arcs — the Mac's
    /// <c>style: .continuous</c>. Same construction as <see cref="NayfAgentAvatarArt"/>'s:
    /// walked clockwise from the left edge, every arc in that same direction, because the
    /// four corners are one closed polygon and an arc traversed backwards puts a chord
    /// across the corner it should round.
    /// </summary>
    private static GraphicsPath RoundedRect(float x, float y, float w, float h, float r)
    {
        r = MathF.Max(0f, MathF.Min(r, MathF.Min(w / 2f, h / 2f)));

        const int steps = 24;
        var points = new PointF[(steps + 1) * 4];
        int i = 0;

        for (int step = 0; step <= steps; step++)
        {
            var (cx, cy) = SuperellipsePoint(step / (float)steps);
            points[i++] = new PointF(x + r - r * cx, y + r - r * cy);
        }
        for (int step = 0; step <= steps; step++)
        {
            var (cx, cy) = SuperellipsePoint(step / (float)steps);
            points[i++] = new PointF(x + w - r + r * cy, y + r - r * cx);
        }
        for (int step = 0; step <= steps; step++)
        {
            var (cx, cy) = SuperellipsePoint(step / (float)steps);
            points[i++] = new PointF(x + w - r + r * cx, y + h - r + r * cy);
        }
        for (int step = steps; step >= 0; step--)
        {
            var (cx, cy) = SuperellipsePoint(step / (float)steps);
            points[i++] = new PointF(x + r - r * cx, y + h - r + r * cy);
        }

        var path = new GraphicsPath();
        path.AddPolygon(points);
        return path;
    }

    /// <summary>How square the corners are. 2 is a plain circular arc; above that eases
    /// the join where the curve meets the straight edge, the way Apple's continuous
    /// corners do.</summary>
    private const float CornerExponent = 2.2f;

    private static (float X, float Y) SuperellipsePoint(float t)
    {
        double angle = t * Math.PI / 2.0;
        double p = 2.0 / CornerExponent;
        return ((float)Math.Pow(Math.Cos(angle), p), (float)Math.Pow(Math.Sin(angle), p));
    }
}
