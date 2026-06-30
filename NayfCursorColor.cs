using System;
using System.Drawing;
using System.IO;
using System.Text.Json;

namespace NayfWindows;

/// <summary>The selectable colors for Nayf's cursor buddy. Mirrors NayfCursorColor.swift.</summary>
public enum NayfCursorColor
{
    Blue,
    Red,
    Yellow,
    Green
}

public static class NayfCursorColorExtensions
{
    /// <summary>The fully-saturated color used to fill the cursor and highlights.</summary>
    public static Color ToDrawingColor(this NayfCursorColor c) => c switch
    {
        NayfCursorColor.Red => Color.FromArgb(255, 255, 82, 82),
        NayfCursorColor.Yellow => Color.FromArgb(255, 255, 199, 46),
        NayfCursorColor.Green => Color.FromArgb(255, 77, 209, 133),
        _ => Color.FromArgb(255, 69, 143, 255), // blue
    };
}

/// <summary>
/// Lightweight persisted app settings (currently just the cursor color),
/// stored as JSON in %LOCALAPPDATA%\Nayf\settings.json.
/// </summary>
public static class NayfSettings
{
    private static readonly string Dir = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Nayf");
    private static readonly string FilePath = Path.Combine(Dir, "settings.json");

    public static NayfCursorColor LoadCursorColor()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                using var doc = JsonDocument.Parse(File.ReadAllText(FilePath));
                if (doc.RootElement.TryGetProperty("cursorColor", out var v) &&
                    Enum.TryParse<NayfCursorColor>(v.GetString(), out var color))
                    return color;
            }
        }
        catch { /* fall back to default */ }
        return NayfCursorColor.Blue;
    }

    public static void SaveCursorColor(NayfCursorColor color)
    {
        try
        {
            Directory.CreateDirectory(Dir);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(new { cursorColor = color.ToString() }));
        }
        catch { /* non-fatal */ }
    }
}
