using System;
using System.Collections.Generic;
using System.Linq;

namespace NayfWindows;

/// <summary>
/// Owns the floating agent result cards — which tasks currently have one on screen, and
/// where each one sits in the top-right stack. The Mac keeps this in a dictionary of
/// panel controllers on <c>AgentResultPanelManager</c>; the shape here is the same, minus
/// the singleton.
///
/// <para>Every method must be called on the UI thread: that is the thread the cards
/// marshal their windows' events back to.</para>
/// </summary>
public sealed class NayfAgentCardHost
{
    /// <summary>
    /// How many cards may be up at once. Each one is 88pt of the top-right corner even
    /// collapsed, and a column of them stops being a set of results and starts being a
    /// wall. The oldest gives way when a fourth arrives.
    /// </summary>
    private const int MaxCards = 3;

    // MARK: - Metrics (Mac AgentResultCardMetrics)
    //
    // Here rather than on the card, because both of a card's two windows position
    // themselves from these and neither owns the other — the opened card and the chip it
    // collapses to have to land in exactly the same corner, or opening one would appear to
    // move it.

    public const int TopInset = 8;
    public const int RightInset = 12;

    /// <summary>
    /// Clear space between one card in the stack and the next. Nothing overlaps, unlike the
    /// Mac's 64pt pitch — the chips here have a glow that spills 18pt past the tile on every
    /// side, and overlapping two of those muddies both.
    /// </summary>
    public const int StackGap = 8;

    /// <summary>In stack order, top-down.</summary>
    private readonly List<NayfAgentCard> _cards = new();

    /// <summary>Raised when the user asks to continue a task from its card.</summary>
    public event Action<Guid>? FollowUpRequested;

    public bool IsOpen(Guid taskId) => _cards.Any(c => c.TaskId == taskId);

    /// <summary>
    /// Puts <paramref name="task"/> on screen, or refreshes its card in place if it
    /// already has one — a task continued in the background updates where the user left
    /// it rather than jumping to the front of the stack.
    /// </summary>
    public void Show(SavedAgentTask task)
    {
        var existing = _cards.FirstOrDefault(c => c.TaskId == task.Id);
        if (existing != null)
        {
            existing.ShowTask(task, existing.OffsetY);
            Relayout();
            return;
        }

        while (_cards.Count >= MaxCards)
            _cards[^1].Dismiss();

        NayfAgentCard card;
        try
        {
            card = new NayfAgentCard();
        }
        catch (Exception ex)
        {
            // A card is a convenience on top of a task that is already saved and already
            // listed in the panel. Failing to build one is worth a log line, not the
            // assistant going down with it.
            Logger.Log("AgentCard", $"failed to create: {ex.Message}");
            return;
        }

        card.Dismissed += OnCardDismissed;
        card.FollowUpRequested += id => FollowUpRequested?.Invoke(id);

        // A card opening or folding away changes how much room it takes, and everything
        // below it has to move by that difference.
        card.StackHeightChanged += Relayout;

        // Newest at the top of the stack, where the eye lands first — the older cards
        // shuffle down to make room. The new card is shown before they move, because
        // positioning reads a work area it only picks up when it is shown.
        _cards.Insert(0, card);
        card.ShowTask(task, 0);
        Relayout();
    }

    /// <summary>
    /// Clears the "listening" state on every card. The follow-up is armed on one card but
    /// finishes as an ordinary turn, so there is nothing to route the ending back to the
    /// card it started from — and only one can be listening at a time anyway.
    /// </summary>
    public void EndFollowUp()
    {
        foreach (var card in _cards) card.EndFollowUp();
    }

    public void CloseAll()
    {
        foreach (var card in _cards.ToList()) card.Dismiss();
    }

    private void OnCardDismissed(NayfAgentCard card)
    {
        if (!_cards.Remove(card)) return;
        card.Dismissed -= OnCardDismissed;
        card.StackHeightChanged -= Relayout;
        Relayout();

        // Destroyed rather than pooled: unlike the panel and the typed-request field,
        // which are one apiece and hidden between uses, a card belongs to a task and
        // there is no bound on how many tasks a session runs through.
        card.Close();
    }

    /// <summary>
    /// Walks the stack top-down and gives every card the offset the ones above it leave it.
    ///
    /// <para>Measured rather than a fixed pitch per position, because a card is two windows
    /// of very different heights — 88pt collapsed to a chip, 232 opened — and one pitch
    /// cannot fit both. It used to be 96, which is the collapsed size, so the moment a card
    /// opened it swallowed the chip below it: a stray coloured dot sitting in the middle of
    /// the card, belonging to a task the user wasn't looking at.</para>
    /// </summary>
    private void Relayout()
    {
        int offset = 0;
        foreach (var card in _cards)
        {
            card.MoveToOffset(offset);
            offset += card.StackHeight + StackGap;
        }
    }
}
