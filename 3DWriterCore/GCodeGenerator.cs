using System.Globalization;
using System.Text;

namespace WriterCore;

public sealed class UnsupportedCharacterException : Exception
{
    public UnsupportedCharacterException(char c)
        : base($"Character '{c}' is not part of the selected font's character set.") { }
}

/// <summary>
/// Ported from Form1.cs render_stuff() (original WinForms app), stripped of the
/// PictureBox preview and all UI/settings-dialog plumbing. GCode math and structure
/// kept 1:1 for output parity; ported: the original also special-cased 'ä'/'ö'/'ü' by
/// hand-copying A/O/U strokes, which only ever matched the standard ASCII map and would
/// corrupt/misalign glyphs against the actual (untracked) scriptsUMLAUTS.cmf, which
/// already ships proper glyphs for those characters in its own char map. Dropped -
/// any font that lists a character in its own CharMap now just works, no special-casing
/// needed. Add per-character hacks back only if a font needs a glyph it can't encode itself.
/// </summary>
public static class GCodeGenerator
{
    /// <summary>A single drawn stroke in bed-space mm (top-left origin, Y down) - for GUI preview only, not used for GCode.</summary>
    public readonly record struct Stroke(double X1, double Y1, double X2, double Y2);

    public sealed record Result(string GCode, bool OutOfBounds, IReadOnlyList<Stroke> Strokes);

    public static Result Generate(string text, string fontName, FontData font, WriterSettings s)
    {
        var strokes = new List<Stroke>();
        double charHeight = font.Height * s.Scale;
        double lineSpacing = s.LineSpacing * s.Scale;
        double letterSpacing = s.LetterSpacing * s.Scale;

        int fTravel = (int)(s.TravelSpeed * 60);
        int fDraw = (int)(s.DrawSpeed * 60);
        int fZ = (int)(s.ZSpeed * 60);

        double accumX = 0, accumY = 0;
        double lastX = 0, lastY = 0;
        bool firstMove = true;
        bool outOfBounds = false;

        var g = new StringBuilder();
        void Line(string l) => g.Append(l).Append("\r\n");
        static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

        Line("; Generated with 3DWriter-cli");
        Line($"; Font: {fontName}");
        Line($"; FontScale: {F(charHeight)}mm ({F(s.Scale)})");
        Line($"; Bed: {F(s.BedWidth)} x {F(s.BedHeight)}");
        Line($"; Offset: {F(s.OffsetX)} x {F(s.OffsetY)}");
        Line($"; Draw mode: {(s.LaserMode ? "Laser" : "Pen")}");
        Line($"; Pen Up: {s.PenUp}");
        Line($"; Pen Down: {s.PenDown}");
        Line($"; Home: {(s.HomeX ? "X" : "")}{(s.HomeY ? "Y" : "")}{(s.HomeZ ? "Z" : "")}");
        Line($"; Dry run: {(s.DryRun ? "ON" : "OFF")}");

        if (s.HomeX || s.HomeY || s.HomeZ)
            Line($"G28 {(s.HomeX ? "X" : "")} {(s.HomeY ? "Y" : "")} {(s.HomeZ ? "Z" : "")} F{fTravel}");

        Line("G21"); // millimeters
        if (s.LaserMode)
        {
            Line("M452"); // laser print mode
            Line("M3 S255");
            Line("G90");
            Line("G21");
            Line(s.PenUp); // laser off before any moves
        }
        else
        {
            Line($"G0 Z{s.PenUp} F{fTravel}"); // pen up before any moves
        }

        foreach (var rawLine in text.Replace("\r\n", "\n").Split('\n'))
        {
            foreach (char ch in rawLine)
            {
                int cnum = font.CharMap.IndexOf(ch);
                if (cnum == -1) throw new UnsupportedCharacterException(ch);

                var glyph = font.Chars[cnum];
                int width = (int)Math.Round(glyph[1], MidpointRounding.ToEven); // "realwidth"

                if (cnum != 0) // index 0 is space - no strokes to draw
                {
                    for (int b = 0; b < glyph.Length / 4; b++)
                    {
                        double x1 = glyph[b * 4 + 3] * s.Scale;
                        double y1 = glyph[b * 4 + 4] * s.Scale;
                        double x2 = glyph[b * 4 + 5] * s.Scale;
                        double y2 = glyph[b * 4 + 6] * s.Scale;

                        strokes.Add(new Stroke(
                            accumX + x1 + s.OffsetX, s.OffsetY + accumY + y1,
                            accumX + x2 + s.OffsetX, s.OffsetY + accumY + y2));

                        double gx1 = accumX + x1 + s.OffsetX;
                        double gy1 = (charHeight - y1) + (s.BedHeight - s.OffsetY) - accumY - charHeight;
                        // Pen-lift test uses font-local Y (no accumY) - matches the original
                        // app's behaviour exactly, including its quirk of not distinguishing
                        // Y positions that coincide font-locally but differ across lines.
                        double testY1 = (charHeight - y1) + (s.BedHeight - s.OffsetY);

                        if (lastX == gx1 && lastY == testY1)
                        {
                            Line($"G1 X{F(gx1)} Y{F(gy1)} F{(firstMove ? fTravel : fDraw)}");
                            firstMove = false;
                        }
                        else
                        {
                            Line(s.LaserMode ? s.PenUp : $"G0 Z{s.PenUp} F{fZ}");
                            Line($"G0 X{F(gx1)} Y{F(gy1)} F{fTravel}");
                            Line(s.LaserMode ? (s.DryRun ? s.PenUp : s.PenDown) : $"G0 Z{(s.DryRun ? s.PenUp : s.PenDown)} F{fZ}");
                        }

                        if (gx1 > s.BedWidth || gx1 < 0) outOfBounds = true;
                        if (gy1 > s.BedHeight || gy1 < 0) outOfBounds = true;

                        double gx2 = accumX + x2 + s.OffsetX;
                        double gy2 = (charHeight - y2) + (s.BedHeight - s.OffsetY) - accumY - charHeight;
                        Line($"G1 X{F(gx2)} Y{F(gy2)} F{fDraw}");

                        lastX = gx2;
                        lastY = (charHeight - y2) + (s.BedHeight - s.OffsetY);
                    }
                }

                accumX += width * s.Scale + letterSpacing;
            }
            accumX = 0;
            accumY += charHeight + lineSpacing;
        }

        if (s.LaserMode)
        {
            Line("G0 F3000");
            Line(s.PenUp); // laser off
            Line("G91");
            Line("G90");
            Line("M3 S0");
            Line("M5 S0");
            Line("M18"); // disable steppers
        }
        else
        {
            Line($"G0 Z{s.PenUp} F{fZ}"); // raise pen
        }

        if (s.HomeX || s.HomeY)
            Line($"G0 {(s.HomeX ? "X0" : "")} {(s.HomeY ? "Y0" : "")} F{fTravel}");

        return new Result(g.ToString(), outOfBounds, strokes);
    }
}
