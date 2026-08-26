using System;
using System.Drawing;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NayfWindows;

/// <summary>The selectable colors for Vayme's cursor buddy. Mirrors NayfCursorColor.swift.</summary>
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
/// Lightweight persisted app settings, stored as JSON in
/// %LOCALAPPDATA%\Vayme\settings.json.
/// </summary>
public static class NayfSettings
{
    private static readonly string FilePath = AppPaths.InDataDirectory("settings.json");

    /// <summary>
    /// Everything the file holds, in one object.
    ///
    /// The file is rewritten whole on every change, so the settings have to be saved
    /// together — writing just the one that changed would drop the others.
    /// </summary>
    private sealed class Stored
    {
        [JsonPropertyName("cursorColor")] public string? CursorColor { get; set; }
        [JsonPropertyName("roastMode")] public bool RoastMode { get; set; }
    }

    private static Stored? _cache;

    private static Stored Current()
    {
        if (_cache != null) return _cache;
        try
        {
            if (File.Exists(FilePath))
                _cache = JsonSerializer.Deserialize<Stored>(File.ReadAllText(FilePath));
        }
        catch { /* fall back to defaults */ }
        return _cache ??= new Stored();
    }

    private static void Persist()
    {
        try
        {
            Directory.CreateDirectory(AppPaths.DataDirectory);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(Current()));
        }
        catch { /* non-fatal */ }
    }

    public static NayfCursorColor LoadCursorColor() =>
        Enum.TryParse<NayfCursorColor>(Current().CursorColor, out var color)
            ? color
            : NayfCursorColor.Blue;

    public static void SaveCursorColor(NayfCursorColor color)
    {
        Current().CursorColor = color.ToString();
        Persist();
    }

    /// <summary>Whether Vayme talks like it's fond of you and unimpressed by your desktop.</summary>
    public static bool LoadRoastMode() => Current().RoastMode;

    public static void SaveRoastMode(bool enabled)
    {
        Current().RoastMode = enabled;
        Persist();
    }
}
