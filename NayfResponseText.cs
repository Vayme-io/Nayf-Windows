using System;
using System.Text.RegularExpressions;

namespace NayfWindows;

/// <summary>
/// Strips the control tags Nayf's replies carry — <c>[POINT: ...]</c>, <c>[MISSION: ...]</c>
/// and <c>[MISSION-CONTINUE]</c> — from anything the user reads or hears.
///
/// <para>The tags are instructions to the app, not part of the answer: POINT drives the
/// cursor, the MISSION pair names the job for the status pill and files it on the Agents
/// list. One place for the removal so the panel, the TTS and the saved summary can't
/// drift apart on which of them counts as text.</para>
/// </summary>
public static class NayfResponseText
{
    /// <summary>Every tag opener, in the exact spelling the system prompt asks for.</summary>
    private static readonly string[] TagOpeners =
        ["[POINT:", "[MISSION:", "[MISSION-CONTINUE:", "[MISSION-CONTINUE]"];

    /// <summary>Removes every complete tag from a finished reply.</summary>
    public static string Clean(string text)
    {
        if (string.IsNullOrEmpty(text)) return "";

        text = Regex.Replace(text, @"\[POINT:[^\]]+\]", "");
        text = Regex.Replace(text, @"\[MISSION:[^\]]*\]", "");
        // Both the labelled form and the bare marker an older prompt asked for.
        text = Regex.Replace(text, @"\[MISSION-CONTINUE[^\]]*\]", "");
        return text.Trim();
    }

    /// <summary>
    /// Cleans a reply that is still streaming. A tag arrives one delta at a time, so for
    /// a few frames it is a fragment no pattern matches — which is how "[MISSION-CONTIN"
    /// ends up on screen. An unclosed bracket is held back while what follows it could
    /// still turn into a tag, and released as ordinary text once it plainly can't.
    /// </summary>
    public static string ForDisplay(string partial)
    {
        var cleaned = Clean(partial);

        int open = cleaned.LastIndexOf('[');
        if (open < 0 || cleaned.IndexOf(']', open) >= 0) return cleaned;

        var tail = cleaned[open..];
        foreach (var opener in TagOpeners)
        {
            // Either the fragment is still short of a full opener, or it is a tag that
            // has opened and not yet closed.
            if (opener.StartsWith(tail, StringComparison.Ordinal) ||
                tail.StartsWith(opener, StringComparison.Ordinal))
                return cleaned[..open].TrimEnd();
        }

        return cleaned;
    }
}
