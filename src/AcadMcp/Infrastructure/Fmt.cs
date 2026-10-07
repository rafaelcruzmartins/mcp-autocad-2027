using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.Json.Serialization.Metadata;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Infrastructure;

/// <summary>JSON options and small converters from AutoCAD types to plain, unit-tagged objects.</summary>
internal static class Fmt
{
    /// <summary>Options handed to the MCP SDK: tool argument binding and input schema generation.</summary>
    public static readonly JsonSerializerOptions Options = new()
    {
        // Required: the MCP SDK freezes these options (MakeReadOnly), which demands an explicit resolver.
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Options for tool results. NaN/Infinity are written as strings instead of failing the call.</summary>
    private static readonly JsonSerializerOptions OutputOptions = new()
    {
        TypeInfoResolver = new DefaultJsonTypeInfoResolver(),
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // Not on Options: it would add {"enum":["NaN",...]} to every number in the input schemas.
        NumberHandling = JsonNumberHandling.AllowNamedFloatingPointLiterals,
        ReferenceHandler = ReferenceHandler.IgnoreCycles,
        WriteIndented = false,
    };

    public static string Serialize(object? value) => JsonSerializer.Serialize(value, OutputOptions);

    public static double R(double v) => Math.Round(v, 6);

    public static object Pt(Point3d p) => new { x = R(p.X), y = R(p.Y), z = R(p.Z) };
    public static object Pt(Point2d p) => new { x = R(p.X), y = R(p.Y) };
    public static object Vec(Vector3d v) => new { x = R(v.X), y = R(v.Y), z = R(v.Z) };

    /// <summary>Extents, or null when the drawing/entity has none (e.g. an empty drawing).</summary>
    public static object? Ext(Point3d min, Point3d max)
    {
        if (min.X > max.X || min.Y > max.Y || Math.Abs(min.X) > 1e19 || Math.Abs(max.X) > 1e19)
            return null;
        return new { min = Pt(min), max = Pt(max) };
    }

    /// <summary>Drawing units as reported in every tool result.</summary>
    public static object Units(Database db) => new
    {
        insunits = db.Insunits.ToString(),
        lunits = LinearUnitsName(db.Lunits),
        measurement = db.Measurement.ToString(),
        note = "Coordenadas em unidades do desenho (insunits), WCS. Ângulos em graus.",
    };

    private static string LinearUnitsName(int lunits) => lunits switch
    {
        1 => "Scientific",
        2 => "Decimal",
        3 => "Engineering",
        4 => "Architectural",
        5 => "Fractional",
        _ => lunits.ToString(),
    };
}
