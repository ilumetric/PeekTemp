using System.Text.Json;
using System.Text.Json.Serialization;

namespace PeekTemp;

internal enum WidgetLayout { TwoRows, OneRow }

internal enum WidgetAnchor { Tray, Left }

internal sealed class Settings
{
    private static readonly JsonSerializerOptions s_json = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() },
    };

    public WidgetLayout Layout { get; set; } = WidgetLayout.TwoRows;
    public bool ShowBackground { get; set; } = true;
    public bool ColorizeTemperature { get; set; } = true;
    public bool Fahrenheit { get; set; }
    public int UpdateIntervalMs { get; set; } = 1000;

    /// <summary>Which taskbar edge the widget is attached to.</summary>
    public WidgetAnchor Anchor { get; set; } = WidgetAnchor.Tray;

    /// <summary>Distance from the anchor in DIP (set by dragging the widget).</summary>
    public double Offset { get; set; }

    /// <summary>Preferred GPU name; null picks the discrete GPU automatically.</summary>
    public string? Gpu { get; set; }

    public Language Language { get; set; } = Language.System;

    /// <summary>On manual start, ask for admin rights when PawnIO is installed (needed for CPU temperature).</summary>
    public bool AutoElevate { get; set; } = true;

    // Temperature thresholds in °C: warm = yellow, hot = red.
    public double CpuWarm { get; set; } = 80; // Ryzen 7000 / Core 13-14 routinely sit at 70-80 °C
    public double CpuHot { get; set; } = 90;
    public double GpuWarm { get; set; } = 75;
    public double GpuHot { get; set; } = 85;

    [JsonIgnore]
    public static string FilePath { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "PeekTemp", "settings.json");

    public static Settings Load()
    {
        try
        {
            if (File.Exists(FilePath))
            {
                var settings = JsonSerializer.Deserialize<Settings>(File.ReadAllText(FilePath), s_json);
                if (settings != null)
                {
                    settings.Normalize();
                    return settings;
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Failed to read settings, using defaults", ex);
        }
        return new Settings();
    }

    public void Save()
    {
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, s_json));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Error("Failed to save settings", ex);
        }
    }

    private void Normalize()
    {
        UpdateIntervalMs = Math.Clamp(UpdateIntervalMs, 250, 10_000);
        if (double.IsNaN(Offset) || double.IsInfinity(Offset)) Offset = 0;
        if (CpuHot < CpuWarm) CpuHot = CpuWarm;
        if (GpuHot < GpuWarm) GpuHot = GpuWarm;
    }
}
