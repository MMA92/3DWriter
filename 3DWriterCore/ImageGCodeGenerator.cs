using System.Globalization;
using System.Text;
using SixLabors.ImageSharp;
using SixLabors.ImageSharp.PixelFormats;
using SixLabors.ImageSharp.Processing;

namespace WriterCore;

/// <summary>
/// Converts a raster image into GCode by rastering it row by row (boustrophedon: alternating
/// scan direction per row to minimize travel), drawing/lasering each contiguous run of "on"
/// pixels as one G1 segment. Same pen-up/down, laser, homing and bed/blocked-zone handling as
/// <see cref="GCodeGenerator"/>, just driven by pixels instead of font strokes/shapes.
/// Two ways to turn an image into that on/off bitmap: <see cref="LoadBitmap"/> (filled, with
/// optional Floyd-Steinberg dithering) and <see cref="LoadEdgeBitmap"/> (Sobel edge outline).
/// </summary>
public static class ImageGCodeGenerator
{
    public sealed record Result(string GCode, bool OutOfBounds);

    /// <summary>
    /// Pure raster-to-GCode step, decoupled from image decoding so it's directly testable.
    /// <paramref name="bitmap"/>[row,col] true = pixel should be drawn. Each pixel is
    /// <paramref name="pixelSizeMm"/> mm square, placed starting at (<paramref name="offsetX"/>,
    /// <paramref name="offsetY"/>) - defaults to the text offset (s.OffsetX/Y) if not given - in the
    /// same top-left/Y-down design space as text and shapes.
    /// </summary>
    public static Result Generate(bool[,] bitmap, double pixelSizeMm, WriterSettings s, double? offsetX = null, double? offsetY = null)
    {
        double ox = offsetX ?? s.OffsetX, oy = offsetY ?? s.OffsetY;
        int rows = bitmap.GetLength(0), cols = bitmap.GetLength(1);
        int fTravel = (int)(s.TravelSpeed * 60);
        int fDraw = (int)(s.DrawSpeed * 60);
        int fZ = (int)(s.ZSpeed * 60);
        bool outOfBounds = false;
        bool firstTravel = true;

        var g = new StringBuilder();
        void Line(string l) => g.Append(l).Append("\r\n");
        static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

        Line("; Generated with 3DWriter-cli (image mode)");
        Line($"; Pixel size: {F(pixelSizeMm)}mm, grid: {cols}x{rows}");
        Line($"; Bed: {F(s.BedWidth)} x {F(s.BedHeight)}");
        Line($"; Offset: {F(ox)} x {F(oy)}");
        Line($"; Tool offset: {F(s.ToolOffsetX)} x {F(s.ToolOffsetY)}");
        Line($"; Draw mode: {(s.LaserMode ? "Laser" : "Pen")}");

        if (s.HomeX || s.HomeY || s.HomeZ)
            Line($"G28 {(s.HomeX ? "X" : "")} {(s.HomeY ? "Y" : "")} {(s.HomeZ ? "Z" : "")} F{fTravel}");

        Line("G21");
        if (s.LaserMode)
        {
            Line("M452"); Line("M3 S255"); Line("G90"); Line("G21"); Line(s.PenUp);
        }
        else
        {
            Line($"G0 Z{F(s.InitialClearance)} F{fTravel}");
        }

        EmitRaster(bitmap, pixelSizeMm, ox, oy, s, Line, fTravel, fDraw, fZ, ref firstTravel, ref outOfBounds, strokes: null);

        if (s.LaserMode)
        {
            Line("G0 F3000"); Line(s.PenUp); Line("G91"); Line("G90"); Line("M3 S0"); Line("M5 S0"); Line("M18");
        }
        else
        {
            Line($"G0 Z{F(s.InitialClearance)} F{fZ}");
        }

        if (s.HomeX || s.HomeY)
            Line($"G0 {(s.HomeX ? "X0" : "")} {(s.HomeY ? $"Y{F(s.BedHeight)}" : "")} F{fTravel}");

        return new Result(g.ToString(), outOfBounds);
    }

    /// <summary>
    /// Core raster loop, factored out so <see cref="GCodeGenerator.Generate"/> can emit an image
    /// as part of a combined text+shapes+image job (one shared header/footer/homing instead of
    /// each generator writing its own). <paramref name="strokes"/> is optional - pass a list to
    /// also collect design-space preview strokes (GUI canvas), like text/shapes already do.
    /// </summary>
    internal static void EmitRaster(
        bool[,] bitmap, double pixelSizeMm, double offsetX, double offsetY, WriterSettings s,
        Action<string> line, int fTravel, int fDraw, int fZ,
        ref bool firstTravel, ref bool outOfBounds,
        List<GCodeGenerator.Stroke>? strokes)
    {
        int rows = bitmap.GetLength(0), cols = bitmap.GetLength(1);
        static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

        double DesignX(int col) => offsetX + col * pixelSizeMm;
        double DesignY(int row) => offsetY + row * pixelSizeMm;
        (double gx, double gy) ToGCode(double x, double y) => (x + s.ToolOffsetX, s.BedHeight - y + s.ToolOffsetY);

        // Local copies: ref/out params can't be captured by the local EmitRun function below.
        bool localFirstTravel = firstTravel;
        bool localOutOfBounds = outOfBounds;

        for (int row = 0; row < rows; row++)
        {
            bool leftToRight = row % 2 == 0;
            int start = leftToRight ? 0 : cols - 1;
            int end = leftToRight ? cols : -1;
            int step = leftToRight ? 1 : -1;

            int runStart = -1;
            for (int col = start; col != end; col += step)
            {
                bool on = bitmap[row, col];
                if (on && runStart == -1) runStart = col;
                bool runEnds = !on || col + step == end;
                if (runStart != -1 && runEnds)
                {
                    int runEndCol = on ? col : col - step;
                    EmitRun(row, runStart, runEndCol);
                    runStart = -1;
                }
            }
        }

        void EmitRun(int row, int colA, int colB)
        {
            double xA = DesignX(colA), xB = DesignX(colB), y = DesignY(row);
            if (xA > s.BedWidth || xA < 0 || xB > s.BedWidth || xB < 0 || y > s.BedHeight || y < 0)
                localOutOfBounds = true;
            if (GCodeGenerator.IsInBlockedZone(xA, y, s) || GCodeGenerator.IsInBlockedZone(xB, y, s))
                throw new BlockedAreaException(s);

            strokes?.Add(new GCodeGenerator.Stroke(xA, y, xB, y));

            var (gxA, gyA) = ToGCode(xA, y);
            var (gxB, gyB) = ToGCode(xB, y);

            line(s.LaserMode ? s.PenUp : $"G0 Z{(localFirstTravel ? F(s.InitialClearance) : s.PenUp)} F{fZ}");
            line($"G0 X{F(gxA)} Y{F(gyA)} F{fTravel}");
            line(s.LaserMode ? (s.DryRun ? s.PenUp : s.PenDown) : $"G0 Z{(s.DryRun ? s.PenUp : s.PenDown)} F{fZ}");
            localFirstTravel = false;
            line($"G1 X{F(gxB)} Y{F(gyB)} F{fDraw}");
        }

        firstTravel = localFirstTravel;
        outOfBounds = localOutOfBounds;
    }

    /// <summary>Loads an image file, resizes it to the target mm grid, and returns its grayscale
    /// luminance (0=black..255=white) - the shared decode step for both bitmap modes below.</summary>
    private static (double[,] luma, int rows, int cols) LoadGrayscale(string imagePath, double widthMm, double pixelSizeMm)
    {
        if (pixelSizeMm <= 0) throw new ArgumentOutOfRangeException(nameof(pixelSizeMm), "must be > 0");

        int cols = Math.Max(1, (int)Math.Round(widthMm / pixelSizeMm));
        using var image = Image.Load<L8>(imagePath);
        int rows = Math.Max(1, (int)Math.Round(cols * (double)image.Height / image.Width));
        image.Mutate(ctx => ctx.Resize(cols, rows).Grayscale());

        var luma = new double[rows, cols];
        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
                luma[row, col] = image[col, row].PackedValue;

        return (luma, rows, cols);
    }

    /// <summary>
    /// Loads an image file and converts it to a dark/light "fill" bitmap at the given resolution -
    /// the decoding step shared by both standalone image rendering and the bitmap fed into
    /// <see cref="GCodeGenerator.Generate"/> for a combined text+image job. <paramref name="widthMm"/>
    /// is the physical output width; height follows the image's aspect ratio.
    /// </summary>
    /// <param name="dither">Floyd-Steinberg error diffusion instead of a hard per-pixel cutoff -
    /// turns grayscale gradients into a halftone-like dot pattern instead of flat black/white
    /// blobs, usually much more recognizable for photos (less so for already-flat line art).</param>
    public static bool[,] LoadBitmap(string imagePath, double widthMm, double pixelSizeMm, byte threshold, bool invert, bool dither = false)
    {
        var (luma, rows, cols) = LoadGrayscale(imagePath, widthMm, pixelSizeMm);
        var bitmap = new bool[rows, cols];

        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                double old = luma[row, col];
                bool dark = old < threshold;
                bitmap[row, col] = invert ? !dark : dark;

                if (!dither) continue;
                double error = old - (dark ? 0 : 255);
                if (col + 1 < cols) luma[row, col + 1] += error * 7 / 16;
                if (row + 1 < rows)
                {
                    if (col - 1 >= 0) luma[row + 1, col - 1] += error * 3 / 16;
                    luma[row + 1, col] += error * 5 / 16;
                    if (col + 1 < cols) luma[row + 1, col + 1] += error * 1 / 16;
                }
            }

        return bitmap;
    }

    /// <summary>
    /// Loads an image file and converts it to an "outline" bitmap: a Sobel edge-magnitude filter
    /// thresholded to on/off, so the pen draws roughly the image's contours instead of filling
    /// solid areas - far fewer, shorter strokes for line-art-like results, at the cost of losing
    /// filled regions entirely. Still rendered through the same row-raster/boustrophedon path as
    /// <see cref="LoadBitmap"/> (not true vector contour tracing), so a long diagonal edge comes
    /// out as a staircase of short horizontal segments rather than one smooth line.
    /// </summary>
    /// <param name="edgeThreshold">Sobel gradient magnitude (roughly 0-1020) above which a pixel
    /// counts as an edge. Lower = more (and fainter) edges detected, higher = only strong edges.</param>
    public static bool[,] LoadEdgeBitmap(string imagePath, double widthMm, double pixelSizeMm, int edgeThreshold, bool invert = false)
    {
        var (luma, rows, cols) = LoadGrayscale(imagePath, widthMm, pixelSizeMm);
        var bitmap = new bool[rows, cols];

        double At(int r, int c) => luma[Math.Clamp(r, 0, rows - 1), Math.Clamp(c, 0, cols - 1)];

        for (int row = 0; row < rows; row++)
            for (int col = 0; col < cols; col++)
            {
                double gx = -At(row - 1, col - 1) + At(row - 1, col + 1)
                            - 2 * At(row, col - 1) + 2 * At(row, col + 1)
                            - At(row + 1, col - 1) + At(row + 1, col + 1);
                double gy = -At(row - 1, col - 1) - 2 * At(row - 1, col) - At(row - 1, col + 1)
                            + At(row + 1, col - 1) + 2 * At(row + 1, col) + At(row + 1, col + 1);
                bool edge = Math.Sqrt(gx * gx + gy * gy) > edgeThreshold;
                bitmap[row, col] = invert ? !edge : edge;
            }

        return bitmap;
    }

    /// <summary>
    /// Loads an image file, converts it to a dark/light bitmap at the given resolution, and
    /// renders it standalone (own header/footer - not combined with text/shapes). See
    /// <see cref="GCodeGenerator.Generate"/>'s <c>imageBitmap</c> parameter to combine instead.
    /// </summary>
    public static Result GenerateFromFile(string imagePath, double widthMm, double pixelSizeMm, byte threshold, bool invert, WriterSettings s, bool dither = false) =>
        Generate(LoadBitmap(imagePath, widthMm, pixelSizeMm, threshold, invert, dither), pixelSizeMm, s);
}
