using System.Globalization;
using WriterCliApp;
using WriterCore;

if (args.Contains("--self-test"))
{
    SelfTest.Run();
    return 0;
}

string? text = null;
string? textFile = null;
string font = "cursive";
string fontsDir = Path.Combine(AppContext.BaseDirectory, "fonts");
string outPath = "3dwriter.gcode";
bool listFonts = false;
var s = WriterSettings.Load(Path.Combine(AppContext.BaseDirectory, "settings.json"));

for (int i = 0; i < args.Length; i++)
{
    string Next() => i + 1 < args.Length ? args[++i] : throw new ArgumentException($"Missing value for {args[i]}");
    double NextD() => double.Parse(Next(), CultureInfo.InvariantCulture);

    switch (args[i])
    {
        case "--text": text = Next(); break;
        case "--text-file": textFile = Next(); break;
        case "--font": font = Next(); break;
        case "--fonts-dir": fontsDir = Next(); break;
        case "--out": outPath = Next(); break;
        case "--list-fonts": listFonts = true; break;
        case "--scale": s.Scale = NextD(); break;
        case "--bed-width": s.BedWidth = NextD(); break;
        case "--bed-height": s.BedHeight = NextD(); break;
        case "--offset-x": s.OffsetX = NextD(); break;
        case "--offset-y": s.OffsetY = NextD(); break;
        case "--tool-offset-x": s.ToolOffsetX = NextD(); break;
        case "--tool-offset-y": s.ToolOffsetY = NextD(); break;
        case "--line-spacing": s.LineSpacing = NextD(); break;
        case "--letter-spacing": s.LetterSpacing = NextD(); break;
        case "--travel-speed": s.TravelSpeed = NextD(); break;
        case "--draw-speed": s.DrawSpeed = NextD(); break;
        case "--z-speed": s.ZSpeed = NextD(); break;
        case "--pen-up": s.PenUp = Next(); break;
        case "--pen-down": s.PenDown = Next(); break;
        case "--initial-clearance": s.InitialClearance = NextD(); break;
        case "--blocked-margin-left": s.BlockedMarginLeft = NextD(); break;
        case "--blocked-margin-right": s.BlockedMarginRight = NextD(); break;
        case "--blocked-margin-top": s.BlockedMarginTop = NextD(); break;
        case "--blocked-margin-bottom": s.BlockedMarginBottom = NextD(); break;
        case "--no-home-x": s.HomeX = false; break;
        case "--no-home-y": s.HomeY = false; break;
        case "--no-home-z": s.HomeZ = false; break;
        case "--laser": s.LaserMode = true; s.PenUp = "M5"; s.PenDown = "M4"; break;
        case "--dry-run": s.DryRun = true; break;
        case "--help": case "-h": PrintHelp(); return 0;
        default: Console.Error.WriteLine($"Unknown argument: {args[i]}"); PrintHelp(); return 1;
    }
}

if (listFonts)
{
    if (!Directory.Exists(fontsDir)) { Console.Error.WriteLine($"Fonts dir not found: {fontsDir}"); return 1; }
    foreach (var f in Directory.GetFiles(fontsDir, "*.cmf").OrderBy(f => f))
        Console.WriteLine(Path.GetFileNameWithoutExtension(f));
    return 0;
}

text ??= textFile is not null ? File.ReadAllText(textFile) : null;
if (text is null)
{
    Console.Error.WriteLine("Provide text with --text \"...\" or --text-file <path>.\n");
    PrintHelp();
    return 1;
}

string fontPath = Path.Combine(fontsDir, font + ".cmf");
if (!File.Exists(fontPath))
{
    Console.Error.WriteLine($"Font not found: {fontPath} (try --list-fonts)");
    return 1;
}

var fontData = FontData.Load(fontPath);

try
{
    var result = GCodeGenerator.Generate(text, font, fontData, s);
    File.WriteAllText(outPath, result.GCode);
    Console.WriteLine($"Wrote {outPath}");
    if (result.OutOfBounds)
        Console.Error.WriteLine("Warning: pen/laser went out of the configured bed bounds.");
    return 0;
}
catch (UnsupportedCharacterException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}
catch (BlockedAreaException ex)
{
    Console.Error.WriteLine(ex.Message);
    return 1;
}

static void PrintHelp()
{
    Console.WriteLine("""
    3dwriter - generate GCode from text using stroke fonts (cross-platform CLI)

    Usage: 3dwriter --text "Hello" [options]

      --text <string>          Text to render (or use --text-file)
      --text-file <path>       Read text from a file instead
      --font <name>            Font name without extension (default: cursive)
      --fonts-dir <path>       Directory with .cmf font files (default: ./fonts)
      --out <path>             Output GCode file (default: 3dwriter.gcode)
      --list-fonts             List available fonts in --fonts-dir and exit

      Defaults below come from settings.json next to the binary (created there with
      these values on first run) - edit that file to change them permanently, or
      override per-run with the flags below.

      --scale <n>              Font scale (default: 0.2)
      --bed-width <mm>         (default: 210)
      --bed-height <mm>        (default: 210)
      --offset-x <mm>          (default: 45)
      --offset-y <mm>          (default: 45)
      --tool-offset-x <mm>     Mechanical calibration: pen tip position vs. the nozzle the
                               machine is homed against, e.g. pen 10mm right of nozzle -> -10 (default: -10)
      --tool-offset-y <mm>     Same, Y axis (default: 10)
      --line-spacing <units>   (default: 0)
      --letter-spacing <units> (default: 0)
      --travel-speed <mm/s>    (default: 100)
      --draw-speed <mm/s>      (default: 40)
      --z-speed <mm/s>         (default: 40)
      --pen-up <z-or-gcode>    (default: 12)
      --pen-down <z-or-gcode>  (default: 8)
      --initial-clearance <mm> One-off Z lift right after homing, pen mode only (default: 30)
      --blocked-margin-left <mm>   Margin from bed edges the pen mount can't reach (default: 16)
      --blocked-margin-right <mm>  Same, right edge (default: 30)
      --blocked-margin-top <mm>    Same, top edge (default: 6)
      --blocked-margin-bottom <mm> Same, bottom edge (default: 20)
      --no-home-x/-y/-z        Disable homing that axis before/after writing
      --laser                  Laser mode (uses M4/M5 unless --pen-up/-down override)
      --dry-run                Never lower the pen / turn on the laser

      --self-test               Run internal self-check and exit
    """);
}
