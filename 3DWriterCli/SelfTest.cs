using System.Linq;
using WriterCore;

namespace WriterCliApp;

/// <summary>Minimal assert-based self-check. Run with --self-test.</summary>
public static class SelfTest
{
    public static void Run()
    {
        string fontsDir = Path.Combine(AppContext.BaseDirectory, "fonts");
        var font = FontData.Load(Path.Combine(fontsDir, "cursive.cmf"));
        var settings = new WriterSettings();

        var result = GCodeGenerator.Generate("AB", "cursive", font, settings);
        Assert(result.GCode.Contains("G21"), "expected G21 (units) in output");
        Assert(result.GCode.Contains("G28"), "expected G28 (homing) in output");
        Assert(result.GCode.Split("G1 ").Length > 2, "expected at least one draw move for 'AB'");

        bool threw = false;
        try { GCodeGenerator.Generate("§", "cursive", font, settings); }
        catch (UnsupportedCharacterException) { threw = true; }
        Assert(threw, "expected UnsupportedCharacterException for a character not in the font");

        var spaceResult = GCodeGenerator.Generate("  ", "cursive", font, settings);
        Assert(!spaceResult.GCode.Contains("G1 "), "space-only text should produce no draw strokes");

        RunShapeTests(font, settings);
        RunImageTests();
        RunWarmupScribbleTests(font, settings);
        Console.WriteLine("Self-test OK");
    }

    /// <summary>The warmup scribble should draw a dense zigzag, pen held down throughout, spliced
    /// in roughly at warmupProgress through the job (not right at the start or dangling at the very
    /// end), only at a safe pen-up boundary, and still respect the blocked-zone check.</summary>
    private static void RunWarmupScribbleTests(FontData font, WriterSettings settings)
    {
        var placed = new WriterSettings { OffsetX = 45, OffsetY = 45 };
        const string text = "ABCDEFGHIJ"; // enough glyphs/strokes to have many safe splice points
        var scribble = (x: 120.0, y: 120.0, size: 10.0);

        var without = GCodeGenerator.Generate(text, "cursive", font, placed);
        var with = GCodeGenerator.Generate(text, "cursive", font, placed, warmupScribble: scribble);
        var withLines = with.GCode.Split("\r\n");

        Assert(with.GCode.Length > without.GCode.Length, "warmup scribble should add lines to the job");
        Assert(with.GCode.Split("G1 ").Length > without.GCode.Split("G1 ").Length + 8,
            "warmup scribble should emit several zigzag draw moves");

        // wgx(120) = 120 + ToolOffsetX(-10) = 110 - the scribble's travel-to-start move, a unique marker.
        int scribbleIndex = Array.FindIndex(withLines, l => l.StartsWith("G0 X110"));
        Assert(scribbleIndex >= 0, "expected to find the scribble's travel-to-start move in the output");
        double position = (double)scribbleIndex / withLines.Length;
        Assert(position is > 0.25 and < 0.9, $"scribble should land roughly mid-job (60% default), not at the very start/end - was at {position:P0}");

        bool blocked = false;
        try { GCodeGenerator.Generate(text, "cursive", font, placed, warmupScribble: (0, 0, 5)); }
        catch (BlockedAreaException) { blocked = true; }
        Assert(blocked, "a scribble placed in the bed-edge margin should raise BlockedAreaException");
    }

    /// <summary>Covers the pure raster-to-GCode path (no file decoding) - a 2-pixel-wide dark
    /// stripe should draw exactly one run per row, and an all-light bitmap should draw nothing.</summary>
    private static void RunImageTests()
    {
        var settings = new WriterSettings { OffsetX = 50, OffsetY = 50 };

        var stripe = new bool[2, 3];
        stripe[0, 1] = true; // single dark pixel in the middle of row 0
        var result = ImageGCodeGenerator.Generate(stripe, 1.0, settings);
        Assert(result.GCode.Contains("G21"), "expected G21 (units) in image output");
        Assert(result.GCode.Split("G1 ").Length == 2, "one dark pixel should produce exactly one draw move");
        Assert(!result.OutOfBounds, "a small bitmap near the offset shouldn't be out of bounds");

        var blank = new bool[2, 2];
        var blankResult = ImageGCodeGenerator.Generate(blank, 1.0, settings);
        Assert(!blankResult.GCode.Contains("G1 "), "an all-light bitmap should produce no draw moves");

        bool blocked = false;
        var edgeBitmap = new bool[1, 1];
        edgeBitmap[0, 0] = true;
        try { ImageGCodeGenerator.Generate(edgeBitmap, 1.0, new WriterSettings { OffsetX = 0, OffsetY = 0 }); }
        catch (BlockedAreaException) { blocked = true; }
        Assert(blocked, "a pixel in the bed-edge margin should raise BlockedAreaException");

        // A long solid run (wide filled area) should get cut into multiple shorter draw moves
        // (each with its own fresh pen-up/down) once it exceeds maxStrokeLengthMm, instead of
        // one long unbroken G1 - this is what avoids paper tearing on long strokes.
        var wideRow = new bool[1, 20];
        for (int c = 0; c < 20; c++) wideRow[0, c] = true; // one 20px-wide run, 1mm/px = 20mm long
        var chunked = ImageGCodeGenerator.Generate(wideRow, 1.0, settings, maxStrokeLengthMm: 5);
        var unchunked = ImageGCodeGenerator.Generate(wideRow, 1.0, settings, maxStrokeLengthMm: 0);
        Assert(chunked.GCode.Split("G1 ").Length > unchunked.GCode.Split("G1 ").Length,
            "a 20mm run capped at 5mm chunks should produce more draw moves than uncapped");
    }

    /// <summary>Covers the shape primitives (Box/Line/Triangle/Star/Arrow/Polygon), the
    /// polymorphic JSON converter (including the "no Type = Box" backward-compat default),
    /// and Generate() actually plotting a mixed shape list.</summary>
    private static void RunShapeTests(FontData font, WriterSettings settings)
    {
        Assert(new Box(0, 0, 10, 20).GetPoints().Count == 4, "box should have 4 corners");
        Assert(new Box(0, 0, 10, 20).Closed, "box should be closed");
        Assert(!new Line(0, 0, 10, 10).Closed, "line should be open");
        Assert(new Line(0, 0, 10, 10).GetPoints().Count == 2, "line should have 2 points");
        Assert(new Triangle(0, 0, 10, 10).GetPoints().Count == 3, "triangle should have 3 points");
        var starPts = new Star(0, 0, 10, 10).GetPoints();
        Assert(starPts.Count == 10, "5-point star should have 10 points (outer+inner)");
        Assert(starPts.Min(p => p.X) < 1 && starPts.Max(p => p.X) > 9, "star should span its full width (not a wedge)");
        Assert(starPts.Min(p => p.Y) < 1 && starPts.Max(p => p.Y) > 9, "star should span its full height (not a wedge)");
        Assert(new Arrow(0, 0, 10, 10).Closed, "arrow should be closed");

        var options = new System.Text.Json.JsonSerializerOptions
        {
            PropertyNameCaseInsensitive = true,
            Converters = { new ShapeJsonConverter() },
        };

        // Old boxes-only files have no "Type" field at all - must still load as Box.
        var legacy = System.Text.Json.JsonSerializer.Deserialize<List<IShape>>(
            "[{\"X\":1,\"Y\":2,\"Width\":3,\"Height\":4}]", options)!;
        Assert(legacy is [Box { X: 1, Y: 2, Width: 3, Height: 4 }], "missing Type should default to Box");

        var star = System.Text.Json.JsonSerializer.Deserialize<List<IShape>>(
            "[{\"Type\":\"Star\",\"X\":0,\"Y\":0,\"Width\":20,\"Height\":20}]", options)!;
        Assert(star is [Star], "Type:Star should deserialize as Star");
        // Regression guard: a Star loaded from JSON with no explicit "Points" field must still
        // draw a full 5-point star (10 vertices), not degenerate into a 4-point diamond.
        Assert(star[0].GetPoints().Count == 10, "JSON star with no Points field should still default to a 5-point star");

        string roundTripJson = System.Text.Json.JsonSerializer.Serialize(star, options);
        var roundTripped = System.Text.Json.JsonSerializer.Deserialize<List<IShape>>(roundTripJson, options)!;
        Assert(roundTripped is [Star], "saving then reloading a Star should still be a Star");
        Assert(roundTripped is [Star { Points: 5 }], "saved Star should record Points:5, not the JSON-missing-default 0");
        Assert(!roundTripJson.Contains("\"Closed\""), "Closed is derived from Type, shouldn't be written to JSON");

        // Positioned away from the bed edges - (0,0) would now trip the blocked-area check.
        var mixed = new List<IShape> { new Box(50, 50, 10, 10), new Line(70, 50, 70, 60) };
        var shapeResult = GCodeGenerator.Generate("", "cursive", font, settings, shapes: mixed);
        Assert(shapeResult.Strokes.Count == 5, "box (4 sides) + line (1 segment) should plot 5 strokes");

        bool blocked = false;
        try { GCodeGenerator.Generate("", "cursive", font, settings, shapes: new List<IShape> { new Box(0, 0, 10, 10) }); }
        catch (BlockedAreaException) { blocked = true; }
        Assert(blocked, "a box in the bed-edge margin should raise BlockedAreaException");

        // Regression: ToolOffset is hardware calibration, not part of the design - a box that
        // fits the bed in design space must not be flagged just because a large ToolOffset would
        // have pushed the GCode-space coordinate off-bed.
        var offsetSettings = new WriterSettings { ToolOffsetX = -50, ToolOffsetY = -50 };
        var onBed = GCodeGenerator.Generate("", "cursive", font, offsetSettings, shapes: new List<IShape> { new Box(20, 50, 10, 10) });
        Assert(!onBed.ShapesOutOfBounds, "a box that fits the bed in design space shouldn't be flagged due to ToolOffset alone");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Self-test failed: " + message);
    }
}
