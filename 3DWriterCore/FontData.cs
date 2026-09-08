using System.Globalization;

namespace WriterCore;

/// <summary>
/// Parses the .cmf font format: line 1 = character count (unused), line 2 = character
/// height in font units, line 3 = character map (index into this string = index into
/// Chars), remaining lines = "width,realwidth,strokeCount,x1,y1,x2,y2,..." per character.
/// </summary>
public sealed class FontData
{
    public double Height { get; private set; }
    public string CharMap { get; private set; } = "";
    public double[][] Chars { get; } = new double[250][];

    public static FontData Load(string cmfPath)
    {
        var font = new FontData();
        var lines = File.ReadAllLines(cmfPath);
        int charIndex = 0;

        for (int i = 0; i < lines.Length; i++)
        {
            switch (i)
            {
                case 0:
                    break; // character count - not needed, Chars is bounded by the array itself
                case 1:
                    font.Height = ParseDouble(lines[i]);
                    break;
                case 2:
                    font.CharMap = lines[i];
                    break;
                default:
                    var parts = lines[i].Split(',');
                    var values = new double[parts.Length];
                    for (int j = 0; j < parts.Length; j++)
                        values[j] = ParseDouble(parts[j]);
                    font.Chars[charIndex++] = values;
                    break;
            }
        }

        return font;
    }

    private static double ParseDouble(string s) =>
        double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : 0;
}
