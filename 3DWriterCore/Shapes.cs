using System.Text.Json;
using System.Text.Json.Serialization;

namespace WriterCore;

/// <summary>A single 2D point in bed-space mm - the shared building block every shape below
/// reduces to.</summary>
public readonly record struct Point(double X, double Y);

/// <summary>Anything GCodeGenerator can plot alongside the text: its own closed or open
/// pen-up/move/pen-down/draw/pen-up outline, in the same top-left/Y-down mm space as text
/// offsets. Loaded from boxes JSON via <see cref="ShapeJsonConverter"/>.</summary>
public interface IShape
{
    IReadOnlyList<Point> GetPoints();
    bool Closed { get; }
}

/// <summary>An axis-aligned rectangle - also the JSON default when "Type" is omitted, so
/// existing boxes-only files (just X/Y/Width/Height, no discriminator) keep working.</summary>
public readonly record struct Box(double X, double Y, double Width, double Height) : IShape
{
    [JsonIgnore] public bool Closed => true; // fixed by shape type, not real state - don't round-trip it
    public IReadOnlyList<Point> GetPoints() => new[]
    {
        new Point(X, Y), new Point(X + Width, Y),
        new Point(X + Width, Y + Height), new Point(X, Y + Height),
    };
}

/// <summary>A single straight open segment from (X,Y) to (X2,Y2).</summary>
public readonly record struct Line(double X, double Y, double X2, double Y2) : IShape
{
    [JsonIgnore] public bool Closed => false;
    public IReadOnlyList<Point> GetPoints() => new[] { new Point(X, Y), new Point(X2, Y2) };
}

/// <summary>An isoceles triangle (apex at top-center) inscribed in the given bounding box.</summary>
public readonly record struct Triangle(double X, double Y, double Width, double Height) : IShape
{
    [JsonIgnore] public bool Closed => true;
    public IReadOnlyList<Point> GetPoints() => new[]
    {
        new Point(X + Width / 2, Y), new Point(X + Width, Y + Height), new Point(X, Y + Height),
    };
}

/// <summary>A regular star (default 5 points) inscribed in the given bounding box.</summary>
public readonly record struct Star(double X, double Y, double Width, double Height, int Points = 5) : IShape
{
    [JsonIgnore] public bool Closed => true;
    public IReadOnlyList<Point> GetPoints()
    {
        double cx = X + Width / 2, cy = Y + Height / 2, rx = Width / 2, ry = Height / 2;
        // Guards against System.Text.Json not applying the constructor's default value for
        // a missing "Points" property (leaving it 0) - 0 or fewer points still means "default".
        int n = Math.Max(Points > 0 ? Points : 5, 2) * 2;
        var pts = new Point[n];
        for (int i = 0; i < n; i++)
        {
            double angle = -Math.PI / 2 + i * 2 * Math.PI / n; // start pointing straight up, sweep the full circle
            double r = i % 2 == 0 ? 1.0 : 0.38; // alternate outer tip / inner notch
            pts[i] = new Point(cx + Math.Cos(angle) * rx * r, cy + Math.Sin(angle) * ry * r);
        }
        return pts;
    }
}

/// <summary>A simple rightward-pointing arrow silhouette inscribed in the given bounding box.</summary>
public readonly record struct Arrow(double X, double Y, double Width, double Height) : IShape
{
    [JsonIgnore] public bool Closed => true;
    public IReadOnlyList<Point> GetPoints()
    {
        double shaftTop = Y + Height * 0.35, shaftBottom = Y + Height * 0.65;
        double headX = X + Width * 0.6;
        return new[]
        {
            new Point(X, shaftTop), new Point(headX, shaftTop), new Point(headX, Y),
            new Point(X + Width, Y + Height / 2),
            new Point(headX, Y + Height), new Point(headX, shaftBottom), new Point(X, shaftBottom),
        };
    }
}

/// <summary>Any other outline as an explicit list of points, open or closed - the fallback
/// for shapes not covered above (and what a rotated non-Box shape becomes in the GUI).</summary>
public readonly record struct Polygon(IReadOnlyList<Point> Points, bool Closed = true) : IShape
{
    public IReadOnlyList<Point> GetPoints() => Points;
}

/// <summary>Reads/writes the polymorphic IShape union via a "Type" discriminator
/// ("Box"/"Rectangle", "Line", "Triangle", "Star", "Arrow", "Polygon" - case-insensitive).
/// A missing "Type" defaults to Box, so pre-existing boxes-only JSON files (no discriminator
/// at all) keep loading unchanged.</summary>
public sealed class ShapeJsonConverter : JsonConverter<IShape>
{
    public override IShape Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        using var doc = JsonDocument.ParseValue(ref reader);
        var root = doc.RootElement;

        string type = "box";
        foreach (var prop in root.EnumerateObject())
        {
            if (string.Equals(prop.Name, "Type", StringComparison.OrdinalIgnoreCase))
            {
                type = prop.Value.GetString() ?? "box";
                break;
            }
        }

        string json = root.GetRawText();
        return type.ToLowerInvariant() switch
        {
            "box" or "rectangle" => JsonSerializer.Deserialize<Box>(json, options),
            "line" => JsonSerializer.Deserialize<Line>(json, options),
            "triangle" => JsonSerializer.Deserialize<Triangle>(json, options),
            "star" => FixStarPoints(JsonSerializer.Deserialize<Star>(json, options)),
            "arrow" => JsonSerializer.Deserialize<Arrow>(json, options),
            "polygon" => JsonSerializer.Deserialize<Polygon>(json, options),
            _ => throw new JsonException($"Unknown shape Type '{type}'."),
        };
    }

    // System.Text.Json doesn't apply the constructor's default value for a JSON-missing
    // "Points" property (leaves it 0) - fix the data itself here so it round-trips clean.
    private static Star FixStarPoints(Star s) => s.Points > 0 ? s : s with { Points = 5 };

    public override void Write(Utf8JsonWriter writer, IShape value, JsonSerializerOptions options)
    {
        string type = value switch
        {
            Box => "Box", Line => "Line", Triangle => "Triangle",
            Star => "Star", Arrow => "Arrow", Polygon => "Polygon",
            _ => value.GetType().Name,
        };

        using var doc = JsonDocument.Parse(JsonSerializer.Serialize(value, value.GetType(), options));
        writer.WriteStartObject();
        writer.WriteString("Type", type);
        foreach (var prop in doc.RootElement.EnumerateObject()) prop.WriteTo(writer);
        writer.WriteEndObject();
    }
}
