using System.Collections.Generic;

namespace NayfWindows;

/// <summary>
/// What Vayme tells the user it can do, when they ask. Read out loud by
/// <see cref="NayfCapabilitiesShowcase"/> and drawn by
/// <see cref="NayfCapabilitiesShowcaseArt"/>.
///
/// <para>Kept apart from both because it is neither a window nor a drawing — it is the one
/// place in the app that claims what the app does, and everything on it has to still be true.
/// Anything added here has to be something a user can go and do straight afterwards.</para>
/// </summary>
public static class NayfCapabilities
{
    /// <summary>One ability: how it's drawn, and the sentence Vayme says about it.</summary>
    /// <param name="Glyph">Segoe Fluent Icons code point.</param>
    /// <param name="Title">The card's line.</param>
    /// <param name="Blurb">The card's caption under it.</param>
    /// <param name="SpokenLine">The fuller sentence, which is what the user actually hears.</param>
    public sealed record Item(string Glyph, string Title, string Blurb, string SpokenLine);

    public const string Headline = "Here's what I can do";
    public const string Subhead = "Hold Ctrl+Alt and just ask me anything";

    public const string Opener = "Here's what I can do.";
    public const string Closer = "Just hold Control and Alt, and ask me anything.";

    /// <summary>
    /// The abilities, in the order they're read out.
    ///
    /// <para>Not the same six as the Mac's. Two of those aren't here: dictating into any text
    /// field, which this version has no equivalent of, and Skills, which hasn't been ported.
    /// In their place are the two things this version does that the Mac showcase never had to
    /// mention — the guided walkthrough, where Vayme outlines a control and lets the user press
    /// it themselves, and holding Shift to circle one part of the screen. A card advertising
    /// features the app doesn't have would be worse than no card at all.</para>
    ///
    /// <para>The glyphs are written as escapes rather than pasted in because they live in the
    /// private use area, where most editors show them as an empty box and some tools drop them
    /// silently. Every one was picked by rendering it rather than off a chart, same rule as
    /// <see cref="NayfActionToast"/>'s.</para>
    /// </summary>
    public static readonly IReadOnlyList<Item> All =
    [
        new("\uE7F4", "See your screen", "Ask about anything you're looking at",
            "I can see whatever's on your screen — ask me about an error, a web page, " +
            "anything you're looking at. Hold Shift and circle part of it if you want me " +
            "to look at just that."),

        new("\uE7C4", "Run things on your PC", "Open apps, play music, sort out files",
            "I can run things on your PC — open an app, put some music on, tidy up a folder. " +
            "Anything that would change something gets your say-so first."),

        new("\uE787", "Manage your day", "Calendar events, issues and pull requests",
            "I'll put events in your calendar, and keep an eye on your issues and pull requests."),

        new("\uE8B0", "Show you where to click", "I point at the spot, you press it",
            "If you'd rather do it yourself, I'll outline the exact spot on screen and talk " +
            "you through it, one step at a time. I never touch your mouse."),

        new("\uE945", "Handle whole tasks", "Multi-step jobs, saved as agents",
            "Give me a goal and I'll work through it step by step, then save it as an agent " +
            "so you can pick it up again later."),

        new("\uE81C", "Remember what matters", "Memory that carries between conversations",
            "And I remember the things that matter to you, so you never have to explain " +
            "yourself twice.")
    ];
}
