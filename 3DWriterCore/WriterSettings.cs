namespace WriterCore;

/// <summary>
/// Mirrors the "Reset Defaults" values from the original WinForms app (Form1.cs,
/// resetDefaultsToolStripMenuItem_Click) - the author's own "safe settings" baseline.
/// </summary>
public sealed class WriterSettings
{
    public double Scale = 0.2;
    public double BedWidth = 200;
    public double BedHeight = 200;
    public double OffsetX = 45;
    public double OffsetY = 45;
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
}
