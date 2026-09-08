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

        Console.WriteLine("Self-test OK");
    }

    private static void Assert(bool condition, string message)
    {
        if (!condition) throw new Exception("Self-test failed: " + message);
    }
}
