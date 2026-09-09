using System.Text.Json;

namespace WriterCore;

/// <summary>
/// Mirrors the "Reset Defaults" values from the original WinForms app (Form1.cs,
/// resetDefaultsToolStripMenuItem_Click) - the author's own "safe settings" baseline.
/// </summary>
public sealed class WriterSettings
{
    public double Scale = 0.2;
    public double BedWidth = 210;
    public double BedHeight = 210;
    public double OffsetX = 45;
    public double OffsetY = 45;
    // Mechanical calibration: how far the pen tip sits from the nozzle the machine was
    // homed/zeroed against (e.g. pen mounted 10mm right of the nozzle -> ToolOffsetX = -10
    // so the sent GCode coordinate is shifted left, landing the pen where the nozzle would
    // have been). Added to every emitted GCode X/Y, not to the preview (which shows the
    // intended design, unaffected by where the physical toolhead happens to be mounted).
    public double ToolOffsetX = -10;
    public double ToolOffsetY = 10;
    public double LineSpacing = 0;
    public double LetterSpacing = 0;
    public double TravelSpeed = 100; // mm/s, converted to GCode F (mm/min) internally
    public double DrawSpeed = 40;
    public double ZSpeed = 40;
    public bool HomeX = true;
    public bool HomeY = true;
    public bool HomeZ = true;
    public bool LaserMode = false;
    public string PenUp = "12";   // Z height in pen mode, raw GCode line in laser mode (e.g. "M5")
    public string PenDown = "8";
    public double InitialClearance = 30; // Z height for the one-off lift right after homing (pen mode only) - extra margin before the first travel move, e.g. when the bed sits higher than expected after homing
    public bool DryRun = false;

    // WriterSettings uses public fields (not properties) - IncludeFields is required or
    // System.Text.Json silently serializes/deserializes nothing.
    private static readonly JsonSerializerOptions JsonOptions = new() { WriteIndented = true, IncludeFields = true };

    /// <summary>
    /// Loads settings from a JSON file (e.g. settings.json next to the executable), creating
    /// it with the defaults above if it doesn't exist yet - so bed size/offset/etc. are edited
    /// there instead of recompiling. CLI flags / GUI controls still override these afterwards.
    /// </summary>
    public static WriterSettings Load(string settingsPath)
    {
        if (File.Exists(settingsPath))
        {
            var loaded = JsonSerializer.Deserialize<WriterSettings>(File.ReadAllText(settingsPath));
            if (loaded is not null) return loaded;
        }

        var defaults = new WriterSettings();
        File.WriteAllText(settingsPath, JsonSerializer.Serialize(defaults, JsonOptions));
        return defaults;
    }
}
