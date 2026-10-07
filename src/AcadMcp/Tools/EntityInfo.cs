using AcadMcp.Infrastructure;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Tools;

/// <summary>Converts entities into compact, unit-explicit dictionaries for tool results.</summary>
internal static class EntityInfo
{
    private const int MaxVerticesInSummary = 100;

    public static Dictionary<string, object?> Summary(Transaction tr, Entity e, bool includeGeometry = true)
    {
        var d = new Dictionary<string, object?>
        {
            ["handle"] = e.Handle.ToString(),
            ["type"] = e.GetRXClass().DxfName,
            ["layer"] = e.Layer,
        };
        if (includeGeometry)
        {
            var g = Geometry(tr, e, full: false);
            if (g.Count > 0) d["geometry"] = g;
        }
        d["bounds"] = Bounds(e);
        return d;
    }

    public static Dictionary<string, object?> Details(Transaction tr, Entity e)
    {
        var d = new Dictionary<string, object?>
        {
            ["handle"] = e.Handle.ToString(),
            ["type"] = e.GetRXClass().DxfName,
            ["className"] = e.GetRXClass().Name,
            ["space"] = Acad.OwnerLayoutName(tr, e),
            ["layer"] = e.Layer,
            ["color"] = Acad.ColorText(e.Color),
            ["linetype"] = e.Linetype,
            ["linetypeScale"] = e.LinetypeScale,
            ["lineweight"] = Acad.LineWeightText(e.LineWeight),
            ["transparency"] = TransparencyText(e),
            ["visible"] = e.Visible,
            ["geometry"] = Geometry(tr, e, full: true),
            ["bounds"] = Bounds(e),
        };

        if (e is BlockReference br)
        {
            d["attributes"] = AttributeList(tr, br);
            if (br.IsDynamicBlock) d["dynamicProperties"] = DynamicProperties(br);
        }

        d["xdata"] = XData(e);
        d["extensionDictionary"] = ExtensionDictionaryKeys(tr, e);
        return d;
    }

    public static object? Bounds(Entity e) => TryExtents(e, out var ext) ? Fmt.Ext(ext.MinPoint, ext.MaxPoint) : null;

    public static bool TryExtents(Entity e, out Extents3d ext)
    {
        try
        {
            ext = e.GeometricExtents;
            return true;
        }
        catch
        {
            ext = default;
            return false;
        }
    }

    private static string? TransparencyText(Entity e)
    {
        try
        {
            var t = e.Transparency;
            if (t.IsByLayer) return "ByLayer";
            if (t.IsByBlock) return "ByBlock";
            // Alpha 255 = opaque; AutoCAD UI shows 0-90 %.
            return $"{Math.Round((255 - t.Alpha) / 2.55)}%";
        }
        catch { return null; }
    }

    private static Dictionary<string, object?> Geometry(Transaction tr, Entity e, bool full)
    {
        var g = new Dictionary<string, object?>();
        try
        {
            switch (e)
            {
                case Line l:
                    g["start"] = Fmt.Pt(l.StartPoint);
                    g["end"] = Fmt.Pt(l.EndPoint);
                    g["length"] = Fmt.R(l.Length);
                    g["angleDeg"] = Acad.ToDeg(l.Angle);
                    break;

                case Arc a:
                    g["center"] = Fmt.Pt(a.Center);
                    g["radius"] = Fmt.R(a.Radius);
                    g["startAngleDeg"] = Acad.ToDeg(a.StartAngle);
                    g["endAngleDeg"] = Acad.ToDeg(a.EndAngle);
                    g["totalAngleDeg"] = Acad.ToDeg(a.TotalAngle);
                    g["length"] = Fmt.R(a.Length);
                    g["startPoint"] = Fmt.Pt(a.StartPoint);
                    g["endPoint"] = Fmt.Pt(a.EndPoint);
                    break;

                case Circle c:
                    g["center"] = Fmt.Pt(c.Center);
                    g["radius"] = Fmt.R(c.Radius);
                    g["diameter"] = Fmt.R(c.Diameter);
                    g["circumference"] = Fmt.R(c.Circumference);
                    g["area"] = Fmt.R(c.Area);
                    break;

                case Polyline p:
                    g["closed"] = p.Closed;
                    g["vertexCount"] = p.NumberOfVertices;
                    g["elevation"] = Fmt.R(p.Elevation);
                    g["length"] = Fmt.R(p.Length);
                    if (p.Closed) g["area"] = Fmt.R(p.Area);
                    var n = full ? p.NumberOfVertices : Math.Min(p.NumberOfVertices, MaxVerticesInSummary);
                    g["vertices"] = Enumerable.Range(0, n).Select(i =>
                    {
                        var pt = p.GetPoint2dAt(i);
                        var bulge = p.GetBulgeAt(i);
                        return bulge == 0
                            ? (object)new { x = Fmt.R(pt.X), y = Fmt.R(pt.Y) }
                            : new { x = Fmt.R(pt.X), y = Fmt.R(pt.Y), bulge = Fmt.R(bulge) };
                    }).ToList();
                    if (n < p.NumberOfVertices) g["verticesTruncated"] = true;
                    break;

                case Polyline2d or Polyline3d:
                    var curve = (Curve)e;
                    g["closed"] = curve.Closed;
                    g["length"] = Fmt.R(curve.GetDistanceAtParameter(curve.EndParam));
                    var pts = new List<object>();
                    // Polyline2d/3d enumerate their vertex ObjectIds.
                    foreach (ObjectId vId in (System.Collections.IEnumerable)e)
                    {
                        if (!full && pts.Count >= MaxVerticesInSummary) { g["verticesTruncated"] = true; break; }
                        var v = tr.GetObject(vId, OpenMode.ForRead);
                        if (v is Vertex2d v2) pts.Add(Fmt.Pt(v2.Position));
                        else if (v is PolylineVertex3d v3) pts.Add(Fmt.Pt(v3.Position));
                    }
                    g["vertices"] = pts;
                    break;

                case DBText t when e is not AttributeDefinition:
                    g["text"] = t.TextString;
                    g["position"] = Fmt.Pt(t.Position);
                    if (t.Justify != AttachmentPoint.BaseLeft) g["alignmentPoint"] = Fmt.Pt(t.AlignmentPoint);
                    g["height"] = Fmt.R(t.Height);
                    g["rotationDeg"] = Acad.ToDeg(t.Rotation);
                    g["justify"] = t.Justify.ToString();
                    g["style"] = t.TextStyleName;
                    break;

                case AttributeDefinition ad:
                    g["tag"] = ad.Tag;
                    g["prompt"] = ad.Prompt;
                    g["default"] = ad.TextString;
                    g["position"] = Fmt.Pt(ad.Position);
                    g["height"] = Fmt.R(ad.Height);
                    g["constant"] = ad.Constant;
                    g["invisible"] = ad.Invisible;
                    break;

                case MText m:
                    g["text"] = m.Text;
                    if (full) g["contents"] = m.Contents;
                    g["location"] = Fmt.Pt(m.Location);
                    g["height"] = Fmt.R(m.TextHeight);
                    g["width"] = Fmt.R(m.Width);
                    g["rotationDeg"] = Acad.ToDeg(m.Rotation);
                    g["attachment"] = m.Attachment.ToString();
                    g["style"] = m.TextStyleName;
                    break;

                // Table derives from BlockReference: must come first.
                case Table tb:
                    g["position"] = Fmt.Pt(tb.Position);
                    g["rows"] = tb.Rows.Count;
                    g["columns"] = tb.Columns.Count;
                    g["width"] = Fmt.R(tb.Width);
                    g["height"] = Fmt.R(tb.Height);
                    if (full)
                        g["cells"] = Enumerable.Range(0, tb.Rows.Count)
                            .Select(r => Enumerable.Range(0, tb.Columns.Count).Select(c => tb.Cells[r, c].TextString).ToList())
                            .ToList();
                    break;

                case BlockReference br:
                    g["name"] = Acad.EffectiveBlockName(tr, br);
                    if (br.IsDynamicBlock) g["anonymousName"] = br.Name;
                    g["position"] = Fmt.Pt(br.Position);
                    g["rotationDeg"] = Acad.ToDeg(br.Rotation);
                    g["scale"] = new { x = Fmt.R(br.ScaleFactors.X), y = Fmt.R(br.ScaleFactors.Y), z = Fmt.R(br.ScaleFactors.Z) };
                    g["isDynamic"] = br.IsDynamicBlock;
                    var attrs = AttributeMap(tr, br);
                    if (attrs.Count > 0) g["attributes"] = attrs;
                    break;

                case Dimension dim:
                    g["measurement"] = Fmt.R(dim.Measurement);
                    g["textOverride"] = string.IsNullOrEmpty(dim.DimensionText) ? null : dim.DimensionText;
                    g["style"] = dim.DimensionStyleName;
                    g["textPosition"] = Fmt.Pt(dim.TextPosition);
                    switch (dim)
                    {
                        case RotatedDimension rd:
                            g["kind"] = "linear";
                            g["point1"] = Fmt.Pt(rd.XLine1Point);
                            g["point2"] = Fmt.Pt(rd.XLine2Point);
                            g["dimLinePoint"] = Fmt.Pt(rd.DimLinePoint);
                            g["rotationDeg"] = Acad.ToDeg(rd.Rotation);
                            break;
                        case AlignedDimension ad2:
                            g["kind"] = "aligned";
                            g["point1"] = Fmt.Pt(ad2.XLine1Point);
                            g["point2"] = Fmt.Pt(ad2.XLine2Point);
                            g["dimLinePoint"] = Fmt.Pt(ad2.DimLinePoint);
                            break;
                        case RadialDimension rad:
                            g["kind"] = "radial";
                            g["center"] = Fmt.Pt(rad.Center);
                            g["chordPoint"] = Fmt.Pt(rad.ChordPoint);
                            break;
                        case DiametricDimension dd:
                            g["kind"] = "diametric";
                            g["chordPoint"] = Fmt.Pt(dd.ChordPoint);
                            g["farChordPoint"] = Fmt.Pt(dd.FarChordPoint);
                            break;
                        default:
                            g["kind"] = dim.GetRXClass().Name;
                            break;
                    }
                    break;

                case Hatch h:
                    g["pattern"] = h.PatternName;
                    g["loops"] = h.NumberOfLoops;
                    try { g["area"] = Fmt.R(h.Area); } catch { /* invalid boundary */ }
                    break;

                case Ellipse el:
                    g["center"] = Fmt.Pt(el.Center);
                    g["majorAxis"] = Fmt.Vec(el.MajorAxis);
                    g["radiusRatio"] = Fmt.R(el.RadiusRatio);
                    g["startAngleDeg"] = Acad.ToDeg(el.StartAngle);
                    g["endAngleDeg"] = Acad.ToDeg(el.EndAngle);
                    break;

                case Spline sp:
                    g["degree"] = sp.Degree;
                    g["closed"] = sp.Closed;
                    g["fitPoints"] = sp.NumFitPoints;
                    g["controlPoints"] = sp.NumControlPoints;
                    g["length"] = Fmt.R(sp.GetDistanceAtParameter(sp.EndParam));
                    break;

                case DBPoint pt:
                    g["position"] = Fmt.Pt(pt.Position);
                    break;


                case Viewport vp:
                    g["center"] = Fmt.Pt(vp.CenterPoint);
                    g["width"] = Fmt.R(vp.Width);
                    g["height"] = Fmt.R(vp.Height);
                    g["viewCenter"] = Fmt.Pt(vp.ViewCenter);
                    g["customScale"] = Fmt.R(vp.CustomScale);
                    g["locked"] = vp.Locked;
                    break;

                case Leader ld:
                    g["vertices"] = Enumerable.Range(0, ld.NumVertices).Select(i => Fmt.Pt(ld.VertexAt(i))).ToList();
                    break;

                case Region rg:
                    g["area"] = Fmt.R(rg.Area);
                    g["perimeter"] = Fmt.R(rg.Perimeter);
                    break;
            }
        }
        catch (Exception ex)
        {
            g["geometryError"] = ex.Message;
        }
        return g;
    }

    public static Dictionary<string, string> AttributeMap(Transaction tr, BlockReference br)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (ObjectId id in br.AttributeCollection)
        {
            if (id.IsErased) continue;
            if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference ar)
                map[ar.Tag] = ar.IsMTextAttribute ? ar.MTextAttribute.Text : ar.TextString;
        }
        return map;
    }

    public static List<object> AttributeList(Transaction tr, BlockReference br)
    {
        var list = new List<object>();
        foreach (ObjectId id in br.AttributeCollection)
        {
            if (id.IsErased) continue;
            if (tr.GetObject(id, OpenMode.ForRead) is AttributeReference ar)
                list.Add(new
                {
                    tag = ar.Tag,
                    text = ar.IsMTextAttribute ? ar.MTextAttribute.Text : ar.TextString,
                    handle = ar.Handle.ToString(),
                    position = Fmt.Pt(ar.Position),
                    height = Fmt.R(ar.Height),
                    invisible = ar.Invisible,
                    isMText = ar.IsMTextAttribute,
                });
        }
        return list;
    }

    private static List<object> DynamicProperties(BlockReference br)
    {
        var list = new List<object>();
        foreach (DynamicBlockReferenceProperty p in br.DynamicBlockReferencePropertyCollection)
        {
            list.Add(new
            {
                name = p.PropertyName,
                value = ValueToJson(p.Value),
                units = p.UnitsType.ToString(),
                readOnly = p.ReadOnly,
                allowedValues = p.GetAllowedValues() is { Length: > 0 } av ? av.Select(ValueToJson).ToList() : null,
            });
        }
        return list;
    }

    private static List<object>? XData(Entity e)
    {
        using var rb = e.XData;
        if (rb is null) return null;
        var apps = new List<object>();
        string? app = null;
        var values = new List<object>();
        foreach (TypedValue tv in rb)
        {
            if (tv.TypeCode == (short)DxfCode.ExtendedDataRegAppName)
            {
                if (app is not null) apps.Add(new { app, values });
                app = tv.Value?.ToString();
                values = [];
            }
            else
            {
                values.Add(new { code = tv.TypeCode, value = ValueToJson(tv.Value) });
            }
        }
        if (app is not null) apps.Add(new { app, values });
        return apps;
    }

    private static List<string>? ExtensionDictionaryKeys(Transaction tr, Entity e)
    {
        if (e.ExtensionDictionary.IsNull || e.ExtensionDictionary.IsErased) return null;
        var dict = (DBDictionary)tr.GetObject(e.ExtensionDictionary, OpenMode.ForRead);
        return dict.Cast<DBDictionaryEntry>().Select(x => x.Key).ToList();
    }

    public static object? ValueToJson(object? v) => v switch
    {
        null => null,
        Point3d p => Fmt.Pt(p),
        Point2d p => Fmt.Pt(p),
        Vector3d vec => Fmt.Vec(vec),
        ObjectId id => id.IsNull ? null : id.Handle.ToString(),
        double d => Fmt.R(d),
        string or bool or int or short or long or float => v,
        _ => v.ToString(),
    };
}
