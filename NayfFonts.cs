using System;
using System.Drawing;
using Microsoft.UI.Xaml;

namespace NayfWindows;

/// <summary>
/// The typefaces the UI asks for, resolved to ones this PC actually has.
///
/// <para>Vayme is drawn in two entirely separate stacks: GDI for the overlay, the pill, the
/// toasts and the agent cards, and XAML for the panel and the sign-in window. The GDI side
/// has always named a fallback for every face it uses, because a missing face throws inside
/// <c>CreateFontW</c> on the render thread and takes the whole overlay down with it. The XAML
/// side named the faces literally, and XAML does not throw — it silently substitutes a font
/// that has no icon glyphs, and every icon in the panel renders as an empty rectangle.</para>
///
/// <para>Which is exactly what Windows 10 users saw. Both <i>Segoe Fluent Icons</i> and
/// <i>Segoe UI Variable</i> shipped with Windows 11; neither is on Windows 10, and neither is
/// redistributable, so they cannot be bundled. Windows 10 does have their predecessors, and
/// every glyph the panel uses is in the older icon font at the same code point — so the
/// substitution is invisible on the machines that need it.</para>
/// </summary>
public static class NayfFonts
{
    /// <summary>
    /// The icon face. Every glyph the panel draws lives in the E0xx-ECxx range, which
    /// <i>Segoe MDL2 Assets</i> and <i>Segoe Fluent Icons</i> share, so a Windows 10 machine
    /// gets the same icons in a slightly older drawing rather than a row of rectangles.
    /// </summary>
    public static string IconFamily { get; } = ResolveFace(
        "Segoe Fluent Icons", "Segoe MDL2 Assets", "Segoe UI Symbol");

    /// <summary>The UI text face.</summary>
    public static string UiFamily { get; } = ResolveFace(
        "Segoe UI Variable", "Segoe UI");

    /// <summary>
    /// Picks the first installed face from a preference list, falling back to whatever this
    /// machine calls its generic sans — which is not a good answer, but is always an answer.
    /// </summary>
    public static string ResolveFace(params string[] names)
    {
        foreach (var name in names)
        {
            try
            {
                using var family = new FontFamily(name);
                if (family.IsStyleAvailable(FontStyle.Regular)) return name;
            }
            catch (ArgumentException) { /* not installed — try the next one */ }
        }

        return FontFamily.GenericSansSerif.Name;
    }

    /// <summary>
    /// Puts the resolved faces where XAML can reach them, as <c>IconFont</c> and
    /// <c>UiFont</c>.
    ///
    /// <para>Must run before the first window is constructed. <c>StaticResource</c> is
    /// resolved once while the XAML is parsed, so a key added after a window has loaded is a
    /// key that window never sees — and the failure is the same silent substitution this
    /// class exists to prevent.</para>
    /// </summary>
    public static void PublishAsApplicationResources()
    {
        var resources = Application.Current.Resources;
        resources["IconFont"] = IconFamily;
        resources["UiFont"] = UiFamily;

        Logger.Log("Fonts", $"icons='{IconFamily}' ui='{UiFamily}'");
    }
}
