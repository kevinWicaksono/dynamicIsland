using System;
using System.IO;
using System.Text.Json;

namespace DynamicIsland;

/// <summary>
/// The user's preferences, saved in %AppData%\DynamicIsland\settings.json.
/// Delete that folder to reset everything.
/// </summary>
public class AppSettings
{
    public double Scale { get; set; } = 1.0;               // island size multiplier
    public double BorderThickness { get; set; } = 2;       // also the progress-bar thickness
    public string BorderColor { get; set; } = "#FF0000";
    public string IslandColor { get; set; } = "#1E1E1E";
    public string TextColor { get; set; } = "#FFFFF0";
    public string FontFamily { get; set; } = "Segoe UI";
    public bool Use24Hour { get; set; }
    public double CompactOpacity { get; set; } = 0.9;      // island opacity while collapsed (0.3 .. 1)
    public double ExpandedOpacity { get; set; } = 0.85;     // island opacity while expanded (0.3 .. 1)
    public double PositionX { get; set; } = 50;            // 0..100 % across the screen (50 = centred)
    public double OffsetY { get; set; } = 15;              // px below the top of the screen
    public bool ShowSpotifyLyrics { get; set; } = true;
    public bool FrostedGlass { get; set; } = false;

    private static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "DynamicIsland", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var s = JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath));
                if (s != null)
                {
                    s.Scale = Math.Clamp(s.Scale, 0.7, 1.6);
                    s.BorderThickness = Math.Clamp(s.BorderThickness, 0, 8);
                    s.PositionX = Math.Clamp(s.PositionX, 0, 100);
                    s.CompactOpacity = Math.Clamp(s.CompactOpacity, 0.3, 1);
                    s.ExpandedOpacity = Math.Clamp(s.ExpandedOpacity, 0.3, 1);
                    s.OffsetY = Math.Clamp(s.OffsetY, 0, 300);
                    if (string.IsNullOrWhiteSpace(s.FontFamily)) s.FontFamily = "Segoe UI";
                    return s;
                }
            }
        }
        catch { /* corrupt file → fall back to defaults */ }
        return new AppSettings();
    }

    public static void Save(AppSettings s)
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            File.WriteAllText(FilePath, JsonSerializer.Serialize(s));
        }
        catch { /* not being able to save is not fatal */ }
    }
}