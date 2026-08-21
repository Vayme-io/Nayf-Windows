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

    // There is deliberately no ForDisplay here any more. It cleaned a half-arrived reply
    // so a tag caught mid-delta wouldn't flash up as "[MISSION-CONTIN" — a problem that
    // only exists if the answer is being typed onto the screen as it streams, and nothing
    // does that now. Clean runs once, on the finished text.
}
