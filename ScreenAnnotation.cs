using System;
using System.Collections.Generic;
using System.Drawing;
using System.Drawing.Drawing2D;

namespace NayfWindows;

/// <summary>The shape an annotation draws on the user's screen.</summary>
public enum AnnotationKind
{
    /// <summary>Dashed rounded rectangle around <see cref="ScreenAnnotation.Bounds"/>.</summary>
    Box,

    /// <summary>Dashed ellipse inscribed in <see cref="ScreenAnnotation.Bounds"/>.</summary>
    Oval,

    /// <summary>A line ending in an arrowhead, from <see cref="ScreenAnnotation.ArrowStart"/>
    /// to <see cref="ScreenAnnotation.ArrowEnd"/>.</summary>
    Arrow
}

/// <summary>
/// One mark Nayf draws over the desktop while explaining something — an outline around a
/// control, or an arrow from one place to another. Several can be on screen at once, each
/// with its own beat in a sequence, so a highlight can trace on and its arrow follow.
///
/// <para><b>Coordinate space:</b> virtual screen coordinates — the same space
/// <c>GetCursorPos</c> reports and the same space <see cref="ScreenCaptureUtility"/> maps
/// screenshot pixels back into. Not monitor-local: each annotation window subtracts its own
/// monitor's origin when it draws, so one list can be handed to every monitor unchanged.</para>
///
/// <para>Immutable once created. <see cref="CreatedAt"/> is the clock the animation runs off,
/// so a render thread can ask for the current progress at any moment without anything having
/// to tick it forward.</para>
/// </summary>
public sealed class ScreenAnnotation
{
    /// <summary>How long an outline takes to trace itself on. Matches the Mac's
    /// <c>.easeInOut(duration: 0.7)</c>.</summary>
    public const double DefaultTraceDuration = 0.7;

    /// <summary>When the label starts fading in — just before the outline finishes, so the
    /// caption lands with the shape rather than after a pause.</summary>
    private const double LabelFadeDelay = 0.55;

    /// <summary>How long the label takes to fade in.</summary>
    private const double LabelFadeDuration = 0.25;

    public AnnotationKind Kind { get; init; }

    /// <summary>The region outlined by a <see cref="AnnotationKind.Box"/> or
    /// <see cref="AnnotationKind.Oval"/>. Unused by an arrow.</summary>
    public RectangleF Bounds { get; init; }

    /// <summary>
    /// Where an arrow's tail begins. Null means "auto-place it just off the target" — a short
    /// look-here arrow — which is resolved at draw time by <see cref="ArrowTailFor"/>, because
    /// only the window drawing it knows which monitor it has room on.
    /// </summary>
    public PointF? ArrowStart { get; init; }

    /// <summary>Where an arrow's head lands.</summary>
    public PointF ArrowEnd { get; init; }

    /// <summary>Short caption drawn in a chip beside the shape, e.g. "the export button".</summary>
    public string? Label { get; init; }

    /// <summary>
    /// Seconds to wait before this annotation starts tracing. This is what turns a set of
    /// marks into a sequence — the cursor arrives, the outline traces, then the arrow draws —
    /// instead of everything animating at once the moment it is added.
    /// </summary>
    public double AppearDelay { get; init; }

    /// <summary>How long this annotation's own trace takes.</summary>
    public double TraceDuration { get; init; } = DefaultTraceDuration;

    /// <summary>The instant the animation clock starts. Set once, at construction.</summary>
    public DateTimeOffset CreatedAt { get; init; } = DateTimeOffset.UtcNow;

    /// <summary>
    /// How much of the outline is drawn right now, 0 to 1, on an ease-in-out curve.
    ///
    /// Smoothstep (3t² − 2t³) rather than SwiftUI's cubic bezier: the two are visually
    /// indistinguishable over 0.7s, and it's the same curve the cursor's flight arc already
    /// uses in <see cref="NativeOverlayWindow"/>.
    /// </summary>
    public double TraceProgress
    {
        get
        {
            double elapsed = ElapsedSeconds - AppearDelay;
            if (elapsed <= 0) return 0;
            if (elapsed >= TraceDuration) return 1;

            double t = elapsed / TraceDuration;
            return t * t * (3 - 2 * t);
        }
    }

    /// <summary>How faded in the label chip is right now, 0 to 1, on an ease-out curve.</summary>
    public double LabelOpacity
    {
        get
        {
            double elapsed = ElapsedSeconds - AppearDelay - LabelFadeDelay;
            if (elapsed <= 0) return 0;
            if (elapsed >= LabelFadeDuration) return 1;

            double t = elapsed / LabelFadeDuration;
            return 1 - (1 - t) * (1 - t);
        }
    }

    /// <summary>
    /// When this annotation stops changing. Past this point it is a still image, which is why
    /// the overlay can stop rendering entirely rather than redrawing an identical frame 60
    /// times a second for as long as the mark stays up.
    /// </summary>
    public double TotalAnimationSeconds =>
        AppearDelay + Math.Max(TraceDuration, LabelFadeDelay + LabelFadeDuration);

    /// <summary>True once nothing about this annotation will change again.</summary>
    public bool IsAnimationComplete => ElapsedSeconds >= TotalAnimationSeconds;

    private double ElapsedSeconds => (DateTimeOffset.UtcNow - CreatedAt).TotalSeconds;

    // ── Factories ────────────────────────────────────────────────────────────

    /// <summary>
    /// An outline around <paramref name="bounds"/>, in whichever shape suits the target —
    /// see <see cref="HighlightKindFor"/>.
    ///
    /// Pass <paramref name="kind"/> when the bounds being drawn are not the target's own.
    /// A caller that pads its rectangle outward so the line clears the control is still
    /// pointing at a control of the original size, and the shape should describe that
    /// rather than flip to a rounded box because a few pixels of breathing room crossed a
    /// threshold.
    /// </summary>
    public static ScreenAnnotation Outline(RectangleF bounds, string? label = null, double appearDelay = 0,
                                           AnnotationKind? kind = null)
        => new()
        {
            Kind = kind ?? HighlightKindFor(bounds),
            Bounds = bounds,
            Label = label,
            AppearDelay = appearDelay
        };

    /// <summary>
    /// An arrow ending at <paramref name="end"/>. A null <paramref name="start"/> gets a short
    /// tail placed automatically next to the target; an explicit one draws the full from→to
    /// arrow that shows a drag.
    /// </summary>
    public static ScreenAnnotation ArrowTo(PointF end, PointF? start = null, string? label = null,
                                           double appearDelay = 0)
        => new()
        {
            Kind = AnnotationKind.Arrow,
            ArrowEnd = end,
            ArrowStart = start,
            Label = label,
            AppearDelay = appearDelay
        };

    // ── Geometry ─────────────────────────────────────────────────────────────

    /// <summary>
    /// Picks the outline that matches the target's own form. A small, near-square target — an
    /// icon, a dot, a resize handle — reads better as an ellipse; anything wider or larger gets
    /// a rounded rectangle sized to it, rather than a circle imposed on a toolbar button.
    /// </summary>
    public static AnnotationKind HighlightKindFor(RectangleF bounds)
    {
        float aspectRatio = bounds.Width / Math.Max(bounds.Height, 1f);
        bool isSmall = Math.Max(bounds.Width, bounds.Height) < 44f;

        if (isSmall && aspectRatio > 0.75f && aspectRatio < 1.33f) return AnnotationKind.Oval;
        return AnnotationKind.Box;
    }

    /// <summary>
    /// How round a box's corners are: proportional to the target, capped at 9px. A fixed
    /// radius would turn a small control into a pill and leave a large panel looking square.
    /// </summary>
    public static float CornerRadiusFor(RectangleF bounds)
        => Math.Min(9f, Math.Min(bounds.Width, bounds.Height) * 0.22f);

    /// <summary>
    /// The outline path for a box or oval, ready to be traced. Returns an empty path for an
    /// arrow, which has no enclosing shape.
    /// </summary>
    public static GraphicsPath OutlinePath(RectangleF bounds, AnnotationKind kind)
    {
        var path = new GraphicsPath();
        if (bounds.Width <= 0 || bounds.Height <= 0) return path;

        if (kind == AnnotationKind.Oval)
        {
            path.AddEllipse(bounds);
            return path;
        }

        if (kind != AnnotationKind.Box) return path;

        float radius = CornerRadiusFor(bounds);
        if (radius < 0.5f)
        {
            path.AddRectangle(bounds);
            return path;
        }

        float diameter = radius * 2;
        path.AddArc(bounds.Left, bounds.Top, diameter, diameter, 180, 90);
        path.AddArc(bounds.Right - diameter, bounds.Top, diameter, diameter, 270, 90);
        path.AddArc(bounds.Right - diameter, bounds.Bottom - diameter, diameter, diameter, 0, 90);
        path.AddArc(bounds.Left, bounds.Bottom - diameter, diameter, diameter, 90, 90);
        path.CloseFigure();
        return path;
    }

    /// <summary>
    /// Where an arrow leaving a highlighted target should start: the point on that outline's
    /// edge facing the destination, pushed out slightly, so the tail never runs back through
    /// the shape it is leaving.
    ///
    /// <para>The two kinds need different maths — scaling the centre→destination ray until it
    /// crosses a rectangle's nearer side is not the same as until it crosses an ellipse — and a
    /// rectangle's formula applied to a circle puts the tail visibly inside the ring at the
    /// diagonals.</para>
    ///
    /// <para>Falls back to the centre when the destination sits inside the outline, since there
    /// is then no edge between the two points to start from.</para>
    /// </summary>
    public static PointF ArrowOrigin(RectangleF paddedBounds, AnnotationKind kind, PointF destination)
    {
        var center = new PointF(
            paddedBounds.Left + paddedBounds.Width / 2f,
            paddedBounds.Top + paddedBounds.Height / 2f);

        double deltaX = destination.X - center.X;
        double deltaY = destination.Y - center.Y;
        if (Math.Abs(deltaX) < 0.001 && Math.Abs(deltaY) < 0.001) return center;

        double halfWidth = Math.Max(paddedBounds.Width / 2f, 0.001);
        double halfHeight = Math.Max(paddedBounds.Height / 2f, 0.001);

        double edgeScale;
        if (kind == AnnotationKind.Oval)
        {
            // Solve (s·dx / halfWidth)² + (s·dy / halfHeight)² = 1 for s — the point where the
            // ray pierces the ellipse.
            double normalizedX = deltaX / halfWidth;
            double normalizedY = deltaY / halfHeight;
            double magnitude = Math.Sqrt(normalizedX * normalizedX + normalizedY * normalizedY);
            if (magnitude < 0.001) return center;
            edgeScale = 1 / magnitude;
        }
        else
        {
            // Scale the ray until it meets the rectangle's edge: the smaller of the two axis
            // crossings is the side it actually exits through.
            double horizontalScale = Math.Abs(deltaX) > 0.001
                ? halfWidth / Math.Abs(deltaX) : double.MaxValue;
            double verticalScale = Math.Abs(deltaY) > 0.001
                ? halfHeight / Math.Abs(deltaY) : double.MaxValue;
            edgeScale = Math.Min(horizontalScale, verticalScale);
        }

        if (double.IsNaN(edgeScale) || double.IsInfinity(edgeScale) || edgeScale > 1) return center;

        // Push a little past the edge so the tail reads as leaving the shape, not touching it.
        const double clearanceInPixels = 6;
        double rayLength = Math.Sqrt(deltaX * deltaX + deltaY * deltaY);
        double clearanceScale = rayLength > 0.001 ? clearanceInPixels / rayLength : 0;
        double totalScale = Math.Min(edgeScale + clearanceScale, 1);

        return new PointF(
            (float)(center.X + deltaX * totalScale),
            (float)(center.Y + deltaY * totalScale));
    }

    /// <summary>
    /// Auto-placed tail for a look-here arrow: a short diagonal offset from the target, flipped
    /// toward whichever side has room on <paramref name="monitorBounds"/> so the tail never
    /// falls off the edge of the screen the target is on.
    /// </summary>
    public static PointF ArrowTailFor(PointF end, RectangleF monitorBounds)
    {
        const float tailOffset = 64f;
        float offsetX = end.X - monitorBounds.Left < 90f ? tailOffset : -tailOffset;
        float offsetY = end.Y - monitorBounds.Top < 90f ? tailOffset : -tailOffset;
        return new PointF(end.X + offsetX, end.Y + offsetY);
    }

    /// <summary>
    /// The first <paramref name="progress"/> fraction of <paramref name="source"/>, measured by
    /// arc length. This is what makes an outline appear to draw itself.
    ///
    /// <para>GDI+ has no trim-a-path primitive (SwiftUI's <c>.trim(from:to:)</c> has no
    /// equivalent), so the path is flattened to line segments and walked, accumulating length
    /// until the wanted fraction is reached and cutting the final segment part-way.</para>
    ///
    /// <para>A closed figure's flattened points do not repeat its start, so the start is
    /// appended back on — otherwise the closing segment of every box would be missing from the
    /// length total and the trace would finish early, leaving a gap in one corner.</para>
    ///
    /// <para>The caller owns the returned path.</para>
    /// </summary>
    public static GraphicsPath PartialPath(GraphicsPath source, double progress)
    {
        var traced = new GraphicsPath();
        if (progress <= 0 || source.PointCount < 2) return traced;

        using var flattened = (GraphicsPath)source.Clone();
        flattened.Flatten(null, 0.2f);
        if (flattened.PointCount < 2) return traced;

        var figures = SplitIntoFigures(flattened);

        double totalLength = 0;
        foreach (var figure in figures)
            for (int i = 1; i < figure.Count; i++)
                totalLength += DistanceBetween(figure[i - 1], figure[i]);

        if (totalLength < 0.001) return traced;

        double wantedLength = totalLength * Math.Min(progress, 1);
        double walkedLength = 0;

        foreach (var figure in figures)
        {
            var tracedPoints = new List<PointF> { figure[0] };

            for (int i = 1; i < figure.Count; i++)
            {
                if (walkedLength >= wantedLength) break;

                PointF from = figure[i - 1];
                PointF to = figure[i];
                double segmentLength = DistanceBetween(from, to);
                if (segmentLength < 0.0001) continue;

                double remainingLength = wantedLength - walkedLength;
                if (remainingLength >= segmentLength)
                {
                    tracedPoints.Add(to);
                    walkedLength += segmentLength;
                }
                else
                {
                    float fraction = (float)(remainingLength / segmentLength);
                    tracedPoints.Add(new PointF(
                        from.X + (to.X - from.X) * fraction,
                        from.Y + (to.Y - from.Y) * fraction));
                    walkedLength = wantedLength;
                }
            }

            if (tracedPoints.Count >= 2)
            {
                traced.StartFigure();
                traced.AddLines(tracedPoints.ToArray());
            }

            if (walkedLength >= wantedLength) break;
        }

        return traced;
    }

    /// <summary>
    /// Breaks a flattened path into its separate figures, re-adding the start point of any
    /// closed one so its closing segment is walked like every other segment.
    /// </summary>
    private static List<List<PointF>> SplitIntoFigures(GraphicsPath flattened)
    {
        var figures = new List<List<PointF>>();
        var points = flattened.PathPoints;
        var types = flattened.PathTypes;
        List<PointF>? currentFigure = null;

        for (int i = 0; i < points.Length; i++)
        {
            bool startsNewFigure = (types[i] & (byte)PathPointType.PathTypeMask) == (byte)PathPointType.Start;
            if (currentFigure == null || startsNewFigure)
            {
                currentFigure = new List<PointF>();
                figures.Add(currentFigure);
            }

            currentFigure.Add(points[i]);

            if ((types[i] & (byte)PathPointType.CloseSubpath) != 0 && currentFigure.Count > 1)
                currentFigure.Add(currentFigure[0]);
        }

        return figures;
    }

    private static double DistanceBetween(PointF a, PointF b)
    {
        double dx = b.X - a.X;
        double dy = b.Y - a.Y;
        return Math.Sqrt(dx * dx + dy * dy);
    }
}

/// <summary>
/// What an annotation overlay window needs from the app: the current list, and a nudge when it
/// changes. Kept to these two members so the window has no idea what a companion is — and so
/// the rendering can be driven from a test harness without one.
/// </summary>
public interface IScreenAnnotationSource
{
    /// <summary>
    /// The marks currently on screen. Replaced wholesale rather than mutated, so a render
    /// thread reading this while the UI thread publishes a new set always sees one complete
    /// list or the other.
    /// </summary>
    IReadOnlyList<ScreenAnnotation> ScreenAnnotations { get; }

    /// <summary>Raised after <see cref="ScreenAnnotations"/> is replaced.</summary>
    event Action? ScreenAnnotationsChanged;
}
