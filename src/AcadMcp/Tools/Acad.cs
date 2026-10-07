using System.Text.RegularExpressions;
using AcadMcp.Infrastructure;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using AcRx = Autodesk.AutoCAD.Runtime;
using Color = Autodesk.AutoCAD.Colors.Color;

namespace AcadMcp.Tools;

/// <summary>Shared AutoCAD helpers for tool bodies: handles, styles, symbol tables, spaces.</summary>
internal static class Acad
{
    public const double Deg = Math.PI / 180.0;

    public static double ToRad(double degrees) => degrees * Deg;
    public static double ToDeg(double radians) => Math.Round(radians / Deg, 6);

    // ---------------------------------------------------------------- handles

    public static ObjectId IdFromHandle(Database db, string? handle)
    {
        if (string.IsNullOrWhiteSpace(handle))
            throw new ArgumentException("Handle vazio.");

        long value;
        try { value = Convert.ToInt64(handle.Trim(), 16); }
        catch { throw new ArgumentException($"Handle inválido '{handle}' (esperado hexadecimal, ex.: \"2A3F\")."); }

        ObjectId id;
        try { id = db.GetObjectId(false, new Handle(value), 0); }
        catch { throw new ArgumentException($"Nenhum objeto com handle '{handle}' neste desenho."); }

        if (id.IsNull || id.IsErased)
            throw new ArgumentException($"O objeto com handle '{handle}' foi apagado.");
        return id;
    }

    /// <summary>Resolves all handles or fails listing every bad one (calls are atomic).</summary>
    public static List<ObjectId> IdsFromHandles(Database db, IEnumerable<string>? handles)
    {
        var list = handles?.Where(h => !string.IsNullOrWhiteSpace(h)).Distinct(StringComparer.OrdinalIgnoreCase).ToList() ?? [];
        if (list.Count == 0) throw new ArgumentException("Informe ao menos um handle.");

        var ids = new List<ObjectId>(list.Count);
        var errors = new List<string>();
        foreach (var h in list)
        {
            try { ids.Add(IdFromHandle(db, h)); }
            catch (ArgumentException ex) { errors.Add(ex.Message); }
        }
        if (errors.Count > 0) throw new ArgumentException(string.Join(" ", errors));
        return ids;
    }

    public static Entity OpenEntity(DocScope s, ObjectId id, OpenMode mode)
    {
        DBObject obj;
        try { obj = s.Tr.GetObject(id, mode, false); }
        catch (AcRx.Exception ex) when (ex.ErrorStatus == AcRx.ErrorStatus.OnLockedLayer)
        {
            throw new InvalidOperationException(
                $"A entidade {id.Handle} está em um layer bloqueado. Desbloqueie o layer antes de modificá-la.");
        }
        return obj as Entity ?? throw new ArgumentException(
            $"O handle {id.Handle} é um {id.ObjectClass.DxfName}, não uma entidade gráfica.");
    }

    public static Entity OpenEntity(DocScope s, string handle, OpenMode mode) =>
        OpenEntity(s, IdFromHandle(s.Db, handle), mode);

    // ---------------------------------------------------------------- spaces

    /// <summary>The space the user is working in (model space or the active paper layout).</summary>
    public static BlockTableRecord CurrentSpace(DocScope s, OpenMode mode = OpenMode.ForWrite) =>
        (BlockTableRecord)s.Tr.GetObject(s.Db.CurrentSpaceId, mode);

    /// <summary>
    /// Resolves a space selector: "model" (default), "current", "paper" (all paper layouts),
    /// "all", or a layout name.
    /// </summary>
    public static List<(ObjectId BtrId, string Layout)> ResolveSpaces(DocScope s, string? space)
    {
        var sel = string.IsNullOrWhiteSpace(space) ? "model" : space.Trim();
        var bt = (BlockTable)s.Tr.GetObject(s.Db.BlockTableId, OpenMode.ForRead);
        var layouts = new List<(ObjectId, string)>();
        foreach (ObjectId id in bt)
        {
            var btr = (BlockTableRecord)s.Tr.GetObject(id, OpenMode.ForRead);
            if (!btr.IsLayout) continue;
            var layout = (Layout)s.Tr.GetObject(btr.LayoutId, OpenMode.ForRead);
            layouts.Add((id, layout.LayoutName));
        }

        var msId = bt[BlockTableRecord.ModelSpace];
        List<(ObjectId, string)> result = sel.ToLowerInvariant() switch
        {
            "model" => layouts.Where(l => l.Item1 == msId).ToList(),
            "current" => layouts.Where(l => l.Item1 == s.Db.CurrentSpaceId).ToList(),
            "paper" => layouts.Where(l => l.Item1 != msId).ToList(),
            "all" => layouts,
            _ => layouts.Where(l => l.Item2.Equals(sel, StringComparison.OrdinalIgnoreCase)).ToList(),
        };
        if (result.Count == 0)
            throw new ArgumentException(
                $"Espaço/layout '{sel}' não encontrado. Use model, current, paper, all ou um destes layouts: {string.Join(", ", layouts.Select(l => l.Item2))}.");
        return result;
    }

    public static string OwnerLayoutName(Transaction tr, Entity e)
    {
        try
        {
            if (tr.GetObject(e.OwnerId, OpenMode.ForRead) is BlockTableRecord { IsLayout: true } btr)
                return ((Layout)tr.GetObject(btr.LayoutId, OpenMode.ForRead)).LayoutName;
            if (tr.GetObject(e.OwnerId, OpenMode.ForRead) is BlockTableRecord owner)
                return $"(bloco {owner.Name})";
        }
        catch { /* fall through */ }
        return "?";
    }

    // ---------------------------------------------------------------- append + style

    /// <summary>Applies DB defaults + optional style, appends to the current space, returns the handle.</summary>
    public static string AddToCurrentSpace(DocScope s, Entity e, Style? style, List<string>? notes = null)
    {
        e.SetDatabaseDefaults(s.Db);
        ApplyStyle(s, e, style, notes);
        var space = CurrentSpace(s);
        space.AppendEntity(e);
        s.Tr.AddNewlyCreatedDBObject(e, true);
        return e.Handle.ToString();
    }

    public static void ApplyStyle(DocScope s, Entity e, Style? style, List<string>? notes = null)
    {
        if (style is null) return;
        if (!string.IsNullOrWhiteSpace(style.Layer))
        {
            e.LayerId = EnsureLayer(s, style.Layer, out var created);
            if (created) notes?.Add($"Layer '{style.Layer}' criado.");
        }
        if (!string.IsNullOrWhiteSpace(style.Color)) e.Color = ParseColor(style.Color);
        if (!string.IsNullOrWhiteSpace(style.Linetype)) e.LinetypeId = EnsureLinetype(s, style.Linetype);
        if (!string.IsNullOrWhiteSpace(style.Lineweight)) e.LineWeight = ParseLineWeight(style.Lineweight);
    }

    public static ObjectId EnsureLayer(DocScope s, string name, out bool created)
    {
        name = name.Trim();
        var lt = (LayerTable)s.Tr.GetObject(s.Db.LayerTableId, OpenMode.ForRead);
        if (lt.Has(name))
        {
            created = false;
            return lt[name];
        }
        try { SymbolUtilityServices.ValidateSymbolName(name, false); }
        catch { throw new ArgumentException($"Nome de layer inválido: '{name}'."); }

        lt.UpgradeOpen();
        // A new record has a null linetype until commit; set it so it is usable in this transaction.
        var rec = new LayerTableRecord { Name = name, LinetypeObjectId = SymbolUtilityServices.GetLinetypeContinuousId(s.Db) };
        var id = lt.Add(rec);
        s.Tr.AddNewlyCreatedDBObject(rec, true);
        created = true;
        return id;
    }

    public static ObjectId EnsureLinetype(DocScope s, string name)
    {
        name = name.Trim();
        var table = (LinetypeTable)s.Tr.GetObject(s.Db.LinetypeTableId, OpenMode.ForRead);
        if (table.Has(name)) return table[name];

        foreach (var file in new[] { "acadiso.lin", "acad.lin" })
        {
            try
            {
                s.Db.LoadLineTypeFile(name, file);
                table = (LinetypeTable)s.Tr.GetObject(s.Db.LinetypeTableId, OpenMode.ForRead);
                if (table.Has(name)) return table[name];
            }
            catch { /* try the next file */ }
        }
        throw new ArgumentException($"Linetype '{name}' não existe no desenho nem em acadiso.lin/acad.lin.");
    }

    public static ObjectId TextStyleId(DocScope s, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return s.Db.Textstyle;
        var table = (TextStyleTable)s.Tr.GetObject(s.Db.TextStyleTableId, OpenMode.ForRead);
        return table.Has(name) ? table[name]
            : throw new ArgumentException($"Estilo de texto '{name}' não existe. Use list_text_styles.");
    }

    public static ObjectId DimStyleId(DocScope s, string? name)
    {
        if (string.IsNullOrWhiteSpace(name)) return s.Db.Dimstyle;
        var table = (DimStyleTable)s.Tr.GetObject(s.Db.DimStyleTableId, OpenMode.ForRead);
        return table.Has(name) ? table[name]
            : throw new ArgumentException($"Estilo de cota '{name}' não existe. Use list_dim_styles.");
    }

    // ---------------------------------------------------------------- colors / lineweights

    private static readonly Dictionary<string, short> ColorNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["red"] = 1, ["vermelho"] = 1, ["yellow"] = 2, ["amarelo"] = 2, ["green"] = 3, ["verde"] = 3,
        ["cyan"] = 4, ["ciano"] = 4, ["blue"] = 5, ["azul"] = 5, ["magenta"] = 6,
        ["white"] = 7, ["branco"] = 7, ["black"] = 7, ["gray"] = 8, ["grey"] = 8, ["cinza"] = 8,
    };

    /// <summary>ByLayer | ByBlock | ACI 1-255 | #RRGGBB | "R,G,B" | color name.</summary>
    public static Color ParseColor(string input)
    {
        var v = input.Trim();
        if (v.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByLayer, 256);
        if (v.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return Color.FromColorIndex(ColorMethod.ByBlock, 0);
        if (short.TryParse(v, out var aci) && aci is >= 1 and <= 255) return Color.FromColorIndex(ColorMethod.ByAci, aci);
        if (ColorNames.TryGetValue(v, out var named)) return Color.FromColorIndex(ColorMethod.ByAci, named);
        if (v.StartsWith('#') && v.Length == 7 && int.TryParse(v[1..], System.Globalization.NumberStyles.HexNumber, null, out var rgb))
            return Color.FromRgb((byte)(rgb >> 16), (byte)(rgb >> 8), (byte)rgb);
        var parts = v.Split(',', StringSplitOptions.TrimEntries);
        if (parts.Length == 3 && parts.All(p => byte.TryParse(p, out _)))
            return Color.FromRgb(byte.Parse(parts[0]), byte.Parse(parts[1]), byte.Parse(parts[2]));
        throw new ArgumentException(
            $"Cor inválida '{input}'. Use ByLayer, ByBlock, índice ACI 1-255, #RRGGBB, \"R,G,B\" ou nome (red, yellow, green, cyan, blue, magenta, white, gray).");
    }

    /// <summary>Round-trippable color text: ByLayer | ByBlock | ACI number | #RRGGBB.</summary>
    public static string ColorText(Color c) => c.ColorMethod switch
    {
        ColorMethod.ByLayer => "ByLayer",
        ColorMethod.ByBlock => "ByBlock",
        ColorMethod.ByAci or ColorMethod.Foreground => c.ColorIndex.ToString(),
        ColorMethod.ByColor => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}",
        _ => c.ToString(),
    };

    private static readonly int[] LineWeights =
        [0, 5, 9, 13, 15, 18, 20, 25, 30, 35, 40, 50, 53, 60, 70, 80, 90, 100, 106, 120, 140, 158, 200, 211];

    /// <summary>ByLayer | ByBlock | Default | mm (0.25) — snapped to the nearest valid AutoCAD lineweight.</summary>
    public static LineWeight ParseLineWeight(string input)
    {
        var v = input.Trim();
        if (v.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLayer;
        if (v.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByBlock;
        if (v.Equals("default", StringComparison.OrdinalIgnoreCase)) return LineWeight.ByLineWeightDefault;
        v = v.Replace("mm", "", StringComparison.OrdinalIgnoreCase).Trim();
        if (!double.TryParse(v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var mm) || mm < 0)
            throw new ArgumentException($"Espessura inválida '{input}'. Use ByLayer, ByBlock, Default ou mm (ex.: 0.25).");
        var hundredths = (int)Math.Round(mm * 100);
        var nearest = LineWeights.OrderBy(w => Math.Abs(w - hundredths)).First();
        return (LineWeight)nearest;
    }

    public static string LineWeightText(LineWeight lw) => lw switch
    {
        LineWeight.ByLayer => "ByLayer",
        LineWeight.ByBlock => "ByBlock",
        LineWeight.ByLineWeightDefault => "Default",
        _ => ((int)lw / 100.0).ToString("0.00", System.Globalization.CultureInfo.InvariantCulture),
    };

    // ---------------------------------------------------------------- misc

    /// <summary>Case-insensitive AutoCAD-style wildcard (* and ?) matcher.</summary>
    public static Func<string, bool> Wildcard(IEnumerable<string>? patterns)
    {
        var list = patterns?.Where(p => !string.IsNullOrWhiteSpace(p)).ToList();
        if (list is null || list.Count == 0) return _ => true;
        var regexes = list.Select(p => new Regex(
            "^" + Regex.Escape(p.Trim()).Replace(@"\*", ".*").Replace(@"\?", ".") + "$",
            RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)).ToList();
        return name => regexes.Any(r => r.IsMatch(name));
    }

    public static string EffectiveBlockName(Transaction tr, BlockReference br)
    {
        var id = br.IsDynamicBlock ? br.DynamicBlockTableRecord : br.BlockTableRecord;
        return ((BlockTableRecord)tr.GetObject(id, OpenMode.ForRead)).Name;
    }
}

/// <summary>Optional style applied to created entities. Nulls keep the drawing defaults.</summary>
internal sealed record Style(string? Layer, string? Color, string? Linetype, string? Lineweight)
{
    public static Style? Of(string? layer, string? color, string? linetype, string? lineweight) =>
        layer is null && color is null && linetype is null && lineweight is null
            ? null
            : new Style(layer, color, linetype, lineweight);
}
