using System;
using System.Drawing;
using System.Threading.Tasks;

namespace NayfWindows;

/// <summary>
/// Region focus: hold Shift, circle something on screen, let go, and the next answer is about
/// exactly that. Mirrors <c>NayfRegionFocusController</c> in <c>NayfRegionFocus.swift</c>.
///
/// <para>Left Shift rather than the Mac's ⌥: it is the key the user asked for, and unlike Alt
/// it carries no menu-bar activation to suppress. The cost is that Shift is genuinely busy, so
/// the hold is guarded on two sides — any other key cancels it, and so does any mouse button,
/// since Shift+click and Shift+drag both begin with exactly this hold.</para>
///
/// <para>Drawing and asking are one gesture rather than two. The microphone opens the moment
/// the loop does, so the user can say "what does this mean?" while they are still circling it,
/// and the crop is paired with what they said when they let go. Saying nothing is also allowed
/// — the crop then waits for their next question.</para>
///
/// <para>The overlay lives only for the length of the hold. It comes down <i>before</i> the
/// screen is captured, because it is drawn on the very screen being photographed: capture it
/// a moment too early and Nayf is looking at its own ink instead of what the ink surrounds.</para>
/// </summary>
public sealed class RegionFocusController : IDisposable
{
    private readonly CompanionManager _companionManager;
    private RegionFocusOverlayWindow? _overlay;

    /// <summary>
    /// Space left around what the user circled. A loop is drawn <i>around</i> a thing, roughly,
    /// and cropping to the exact extent of the line shaves the edges off whatever it was meant
    /// to contain.
    /// </summary>
    private const int RegionPadding = 10;

    /// <summary>
    /// Below this, in either direction, the "region" is a twitch of the hand rather than a
    /// gesture — a crop that small holds nothing worth looking at.
    /// </summary>
    private const int MinimumRegionSize = 8;

    /// <summary>
    /// Long enough for the overlay to actually leave the screen. Destroying the window returns
    /// before the compositor has finished with it, and the pixels are read straight off the
    /// desktop — so without this pause the trail can still be in the crop.
    /// </summary>
    private static readonly TimeSpan OverlaySettleDelay = TimeSpan.FromMilliseconds(70);

    public RegionFocusController(CompanionManager companionManager) =>
        _companionManager = companionManager;

    /// <summary>The hold was confirmed: put the overlay up and start listening.</summary>
    public void Begin()
    {
        if (_overlay != null) return; // already circling

        _overlay = RegionFocusOverlayWindow.Open();
        if (_overlay == null)
        {
            Logger.Log("RegionFocus", "Could not open the overlay — ignoring the hold");
            return;
        }

        Logger.Log("RegionFocus", "Lasso opened");
        _companionManager.BeginRegionFocusVoiceCapture();
    }

    /// <summary>Shift was released: crop what was circled and hand it over.</summary>
    public async void Finish()
    {
        var overlay = _overlay;
        if (overlay == null) return;
        _overlay = null;

        bool hasBounds = overlay.TryGetDrawnBounds(out RectangleF drawn);
        overlay.Dispose();

        if (!hasBounds || drawn.Width < MinimumRegionSize || drawn.Height < MinimumRegionSize)
        {
            // They held the key without drawing anything, or clicked partway through and were
            // never drawing at all. Nothing to answer about, so the recording that opened with
            // the loop is dropped rather than sent on its own.
            Logger.Log("RegionFocus", "Nothing drawn — cancelling");
            _companionManager.CancelRegionFocusVoiceCapture();
            return;
        }

        var region = Rectangle.FromLTRB(
            (int)MathF.Floor(drawn.Left) - RegionPadding,
            (int)MathF.Floor(drawn.Top) - RegionPadding,
            (int)MathF.Ceiling(drawn.Right) + RegionPadding,
            (int)MathF.Ceiling(drawn.Bottom) + RegionPadding);

        await Task.Delay(OverlaySettleDelay);

        try
        {
            var regionImage = await ScreenCaptureUtility.CaptureRegionAsync(region);
            Logger.Log("RegionFocus", $"Captured {region.Width}x{region.Height} region");
            _companionManager.FinishRegionFocusVoiceCapture(regionImage);
        }
        catch (Exception ex)
        {
            Logger.Log("RegionFocus", $"Capture failed: {ex.Message}");
            _companionManager.CancelRegionFocusVoiceCapture();
        }
    }

    /// <summary>Escape, or Alt turning out to be part of a shortcut: drop the whole thing.</summary>
    public void Cancel()
    {
        if (_overlay == null) return;

        Logger.Log("RegionFocus", "Lasso cancelled");
        _overlay.Dispose();
        _overlay = null;
        _companionManager.CancelRegionFocusVoiceCapture();
    }

    public void Dispose()
    {
        _overlay?.Dispose();
        _overlay = null;
    }
}
