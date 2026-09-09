using System.Globalization;
using System.Text;

namespace WriterCore;

public sealed class UnsupportedCharacterException : Exception
{
    public UnsupportedCharacterException(char c)
        : base($"Character '{c}' is not part of the selected font's character set.") { }
}

public sealed class BlockedAreaException : Exception
{
    public BlockedAreaException()
        : base($"Text or a box is in a blocked area the pen can't reach (left {GCodeGenerator.BlockedMarginLeft}mm, " +
               $"right {GCodeGenerator.BlockedMarginRight}mm, top {GCodeGenerator.BlockedMarginTop}mm, bottom {GCodeGenerator.BlockedMarginBottom}mm " +
               "from the bed edges). Move it and try again.") { }
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
    // Margins (mm, from each bed edge) where the pen mount can't physically reach - shared with
    // the GUI so it can gray these zones out on the preview canvas using the same numbers.
    public const double BlockedMarginLeft = 16;
    public const double BlockedMarginRight = 30;
    public const double BlockedMarginTop = 6;
    public const double BlockedMarginBottom = 20;

    /// <summary>A single drawn stroke in bed-space mm (top-left origin, Y down) - for GUI preview only, not used for GCode.</summary>
    public readonly record struct Stroke(double X1, double Y1, double X2, double Y2);

    public sealed record Result(string GCode, bool OutOfBounds, IReadOnlyList<Stroke> Strokes);

    public static Result Generate(string text, string fontName, FontData font, WriterSettings s, IReadOnlyList<IShape>? shapes = null)
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
        bool firstTravel = true; // very first pen-up+travel of the whole job uses InitialClearance instead of PenUp for extra safety margin
        bool outOfBounds = false;

        var g = new StringBuilder();
        void Line(string l) => g.Append(l).Append("\r\n");
        static string F(double v) => v.ToString(CultureInfo.InvariantCulture);

        Line("; Generated with 3DWriter-cli");             // Zeile 1: nur Info, kein G-Code
        Line($"; Font: {fontName}");                        // Zeile 2
        Line($"; FontScale: {F(charHeight)}mm ({F(s.Scale)})"); // Zeile 3
        Line($"; Bed: {F(s.BedWidth)} x {F(s.BedHeight)}"); // Zeile 4
        Line($"; Offset: {F(s.OffsetX)} x {F(s.OffsetY)}"); // Zeile 5
        Line($"; Draw mode: {(s.LaserMode ? "Laser" : "Pen")}"); // Zeile 6
        Line($"; Pen Up: {s.PenUp}");                       // Zeile 7
        Line($"; Pen Down: {s.PenDown}");                   // Zeile 8
        Line($"; Home: {(s.HomeX ? "X" : "")}{(s.HomeY ? "Y" : "")}{(s.HomeZ ? "Z" : "")}"); // Zeile 9
        Line($"; Dry run: {(s.DryRun ? "ON" : "OFF")}");    // Zeile 10 - Ende Kommentarblock

        if (s.HomeX || s.HomeY || s.HomeZ)
            Line($"G28 {(s.HomeX ? "X" : "")} {(s.HomeY ? "Y" : "")} {(s.HomeZ ? "Z" : "")} F{fTravel}");
            // Zeile 11: G28 X Y Z - alle 3 Achsen homen (Referenzfahrt), mit Anfahrgeschw. fTravel

        Line("G21"); // millimeters                        // Zeile 12: Einheiten auf mm
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
            Line($"G0 Z{F(s.InitialClearance)} F{fTravel}"); // Zeile 13: einmaliger Extra-Hub direkt nach dem Homen (höher als das normale PenUp danach)
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
                            // Folge-Strich hängt nahtlos am letzten an (z.B. beim "W"): Stift bleibt
                            // unten, einfach zur nächsten Position weiterzeichnen (kein Pen-Up nötig).
                            Line($"G1 X{F(gx1)} Y{F(gy1)} F{(firstMove ? fTravel : fDraw)}");
                            firstMove = false;
                        }
                        else
                        {
                            // Neuer, nicht anschließender Strich - Stift muss "umgesetzt" werden:
                            Line(s.LaserMode ? s.PenUp : $"G0 Z{(firstTravel ? F(s.InitialClearance) : s.PenUp)} F{fZ}");     // Zeile 14/16: Stift hoch (PenUp, erste Anfahrt nach Homing: InitialClearance statt PenUp)
                            Line($"G0 X{F(gx1)} Y{F(gy1)} F{fTravel}");               // Zeile 15: zur Start-Position des Strichs fahren (Stift schwebt)
                            Line(s.LaserMode ? (s.DryRun ? s.PenUp : s.PenDown) : $"G0 Z{(s.DryRun ? s.PenUp : s.PenDown)} F{fZ}"); // Zeile 16: Stift runter (PenDown) - jetzt berührt er das Papier
                            firstTravel = false;
                        }

                        if (gx1 > s.BedWidth || gx1 < 0) outOfBounds = true;
                        if (gy1 > s.BedHeight || gy1 < 0) outOfBounds = true;

                        double gx2 = accumX + x2 + s.OffsetX;
                        double gy2 = (charHeight - y2) + (s.BedHeight - s.OffsetY) - accumY - charHeight;
                        Line($"G1 X{F(gx2)} Y{F(gy2)} F{fDraw}");
                        // Zeile 17/19: der eigentliche Zeichenstrich - Stift ist unten (aus Zeile 16),
                        // fährt mit Zeichengeschwindigkeit (fDraw) vom Strich-Start zum Strich-Ende.
                        // Nächste Schleifenrunde: wenn ihr Start == dieser Endpunkt, siehe Zeile 112-116
                        // oben (nahtloser Folge-Strich, erzeugt das doppelte G1 wie Zeile 17+18/19+20).

                        lastX = gx2;
                        lastY = (charHeight - y2) + (s.BedHeight - s.OffsetY);
                    }
                }

                accumX += width * s.Scale + letterSpacing;
            }
            accumX = 0;
            accumY += charHeight + lineSpacing;
        }

        foreach (var shape in shapes ?? Array.Empty<IShape>())
        {
            var pts = shape.GetPoints();
            if (pts.Count < 2) continue;
            int segments = shape.Closed ? pts.Count : pts.Count - 1; // closed: wraps last -> first

            for (int i = 0; i < segments; i++)
            {
                var p1 = pts[i];
                var p2 = pts[(i + 1) % pts.Count];
                strokes.Add(new Stroke(p1.X, p1.Y, p2.X, p2.Y));

                double gx1 = p1.X, gy1 = s.BedHeight - p1.Y;
                double gx2 = p2.X, gy2 = s.BedHeight - p2.Y;

                if (gx1 > s.BedWidth || gx1 < 0 || gy1 > s.BedHeight || gy1 < 0) outOfBounds = true;
                if (gx2 > s.BedWidth || gx2 < 0 || gy2 > s.BedHeight || gy2 < 0) outOfBounds = true;

                if (i == 0)
                {
                    Line(s.LaserMode ? s.PenUp : $"G0 Z{(firstTravel ? F(s.InitialClearance) : s.PenUp)} F{fZ}");
                    Line($"G0 X{F(gx1)} Y{F(gy1)} F{fTravel}");
                    Line(s.LaserMode ? (s.DryRun ? s.PenUp : s.PenDown) : $"G0 Z{(s.DryRun ? s.PenUp : s.PenDown)} F{fZ}");
                    firstTravel = false;
                }

                Line($"G1 X{F(gx2)} Y{F(gy2)} F{fDraw}");
            }

            Line(s.LaserMode ? s.PenUp : $"G0 Z{s.PenUp} F{fZ}");
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
            Line($"G0 Z{F(s.InitialClearance)} F{fZ}"); // raise pen to safety clearance, not just normal PenUp
        }

        if (s.HomeX || s.HomeY)
            Line($"G0 {(s.HomeX ? "X0" : "")} {(s.HomeY ? $"Y{F(s.BedHeight)}" : "")} F{fTravel}"); // present: bed drives forward (Y max) instead of back to Y0

        foreach (var st in strokes)
        {
            if (IsInBlockedZone(st.X1, st.Y1, s) || IsInBlockedZone(st.X2, st.Y2, s))
                throw new BlockedAreaException();
        }

        return new Result(g.ToString(), outOfBounds, strokes);
    }

    /// <summary>True if (x,y) - in the same top-left/Y-down bed-space mm as <see cref="Stroke"/> - falls
    /// within the pen's unreachable margin near a bed edge.</summary>
    public static bool IsInBlockedZone(double x, double y, WriterSettings s) =>
        x < BlockedMarginLeft || x > s.BedWidth - BlockedMarginRight ||
        y < BlockedMarginTop || y > s.BedHeight - BlockedMarginBottom;
}
