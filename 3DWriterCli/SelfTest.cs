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
        Console.WriteLine("Self-test OK");
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
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Self-test failed: " + message);
    }
}
