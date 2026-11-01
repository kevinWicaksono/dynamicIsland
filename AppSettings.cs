using System;
using System.IO;
using System.Text.Json;

namespace DynamicIsland;

/// <summary>
/// The user's appearance preferences. Saved as a tiny JSON file in
/// %AppData%\DynamicIsland\settings.json (only these six values – nothing else).
/// Delete that folder to reset everything.
/// </summary>
public class AppSettings
{
    public double Scale { get; set; } = 1.0;               // island size multiplier
    public double BorderThickness { get; set; } = 3;       // also the progress-bar thickness
    public string BorderColor { get; set; } = "#30D158";
    public string IslandColor { get; set; } = "#000000";
    public string TextColor { get; set; } = "#FFFFFF";
    public string FontFamily { get; set; } = "Segoe UI";
    // true = 24-hour clock, false = 12-hour with AM/PM. First run follows your Windows setting.
    public bool Use24Hour { get; set; } =
        System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.ShortTimePattern.Contains('H');
    public double CompactOpacity { get; set; } = 0.8;      // island opacity while collapsed (0.3 .. 1)
    public double ExpandedOpacity { get; set; } = 1.0;     // island opacity while expanded (0.3 .. 1)
    public double PositionX { get; set; } = 50;            // 0..100 % across the screen (50 = centred)
    public double OffsetY { get; set; } = 0;               // px below the top of the screen

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
