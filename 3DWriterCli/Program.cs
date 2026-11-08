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
string? imagePath = null;
double imageWidthMm = 100;
double imagePixelMm = 0.5;
byte imageThreshold = 128;
bool imageInvert = false;
bool imageDither = false;
string imageMode = "fill"; // fill | outline
int imageEdgeThreshold = 100;
double? imageOffsetX = null;
double? imageOffsetY = null;
bool warmup = false;
double warmupX = 20, warmupY = 10, warmupSize = 10; // default clear of the stock blocked margins (left 16/top 6)
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
        case "--image": imagePath = Next(); break;
        case "--image-width": imageWidthMm = NextD(); break;
        case "--image-resolution": imagePixelMm = NextD(); break;
        case "--image-threshold": imageThreshold = byte.Parse(Next(), CultureInfo.InvariantCulture); break;
        case "--image-invert": imageInvert = true; break;
        case "--image-dither": imageDither = true; break;
        case "--image-mode": imageMode = Next().ToLowerInvariant(); break;
        case "--image-edge-threshold": imageEdgeThreshold = (int)NextD(); break;
        case "--image-offset-x": imageOffsetX = NextD(); break;
        case "--image-offset-y": imageOffsetY = NextD(); break;
        case "--warmup-scribble": warmup = true; break;
        case "--warmup-x": warmupX = NextD(); break;
        case "--warmup-y": warmupY = NextD(); break;
        case "--warmup-size": warmupSize = NextD(); break;
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
if (text is null && imagePath is null)
{
    Console.Error.WriteLine("Provide text with --text \"...\" or --text-file <path>, and/or an image with --image <path>.\n");
    PrintHelp();
    return 1;
}
text ??= ""; // image-only job: still needs a font loaded below (harmless - no chars to render)

bool[,]? imageBitmap = null;
if (imagePath is not null)
{
    if (!File.Exists(imagePath)) { Console.Error.WriteLine($"Image not found: {imagePath}"); return 1; }
    if (imageMode is not ("fill" or "outline")) { Console.Error.WriteLine("--image-mode must be 'fill' or 'outline'"); return 1; }
    try
    {
        imageBitmap = imageMode == "outline"
            ? ImageGCodeGenerator.LoadEdgeBitmap(imagePath, imageWidthMm, imagePixelMm, imageEdgeThreshold, imageInvert)
            : ImageGCodeGenerator.LoadBitmap(imagePath, imageWidthMm, imagePixelMm, imageThreshold, imageInvert, imageDither);
    }
    catch (Exception ex) when (ex is not (UnsupportedCharacterException or BlockedAreaException))
    {
        Console.Error.WriteLine($"Failed to load image: {ex.Message}");
        return 1;
    }
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
    var result = GCodeGenerator.Generate(text, font, fontData, s, imageBitmap: imageBitmap, imagePixelSizeMm: imagePixelMm,
        imageOffsetX: imageOffsetX, imageOffsetY: imageOffsetY,
        warmupScribble: warmup ? (warmupX, warmupY, warmupSize) : null);
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

      --image <path>           Also render a raster image (png/jpg/bmp/...), combined with
                               --text in the same job if both are given
      --image-width <mm>       Physical output width, height follows aspect ratio (default: 100)
      --image-resolution <mm>  Pixel size in mm - smaller = finer detail, slower (default: 0.5)
      --image-mode <fill|outline> fill = rastered/filled (default), outline = Sobel edge detection
                               (sketch-like, fewer strokes, no filled areas)
      --image-threshold <0-255> Fill mode: grayscale cutoff, darker-than drawn (default: 128)
      --image-dither           Fill mode: Floyd-Steinberg dithering instead of a hard cutoff -
                               better for photos/gradients
      --image-edge-threshold <n> Outline mode: Sobel gradient cutoff, lower = more edges (default: 100)
      --image-invert           Draw light pixels / non-edges instead of dark/edges
      --image-offset-x <mm>    Image placement, independent of --offset-x/y (default: same as text)
      --image-offset-y <mm>    Same, Y axis

      --warmup-scribble        Draw a small zigzag first to get a ballpoint pen flowing before
                               the real job (pen stays down, lots of direction changes)
      --warmup-x <mm>          Scribble position, pick a spot nothing else uses (default: 20)
      --warmup-y <mm>          Same, Y axis (default: 10)
      --warmup-size <mm>       Scribble square size (default: 10)

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
