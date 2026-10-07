using System.ComponentModel;
using AcadMcp.Infrastructure;
using AcadMcp.Server;
using Autodesk.AutoCAD.Colors;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AcadMcp.Tools;

[McpServerToolType]
public static class ModifyTools
{
    [McpServerTool(Name = "create_layer")]
    [Description("Cria um layer ou atualiza as propriedades de um existente. Parâmetros omitidos não são alterados.")]
    public static Task<CallToolResult> CreateLayer(
        [Description("Nome do layer.")] string name,
        [Description("Cor: ACI 1-255, #RRGGBB, 'R,G,B' ou nome.")] string? color = null,
        [Description("Linetype (carregado de acadiso.lin se preciso).")] string? linetype = null,
        [Description("Espessura em mm (ex.: 0.35) ou Default.")] string? lineweight = null,
        [Description("Descrição.")] string? description = null,
        [Description("Plotável.")] bool? plottable = null,
        [Description("Ligado (false = off).")] bool? on = null,
        [Description("Congelado.")] bool? frozen = null,
        [Description("Bloqueado.")] bool? locked = null,
        [Description("Tornar o layer corrente.")] bool setCurrent = false) =>
        ToolInvoker.Write("create_layer", new { name, color, linetype, lineweight, description, plottable, on, frozen, locked, setCurrent }, s =>
        {
            var id = Acad.EnsureLayer(s, name, out var created);
            var l = (LayerTableRecord)s.Tr.GetObject(id, OpenMode.ForWrite);

            if (color is not null)
            {
                var c = Acad.ParseColor(color);
                if (c.IsByLayer || c.IsByBlock) throw new ArgumentException("A cor de um layer não pode ser ByLayer/ByBlock.");
                l.Color = c;
            }
            if (linetype is not null) l.LinetypeObjectId = Acad.EnsureLinetype(s, linetype);
            if (lineweight is not null) l.LineWeight = Acad.ParseLineWeight(lineweight);
            if (description is not null) l.Description = description;
            if (plottable is not null) l.IsPlottable = plottable.Value;
            if (on is not null) l.IsOff = !on.Value;
            if (locked is not null) l.IsLocked = locked.Value;
            if (frozen is not null)
            {
                if (frozen.Value && (id == s.Db.Clayer || setCurrent))
                    throw new ArgumentException("Não é possível congelar o layer corrente.");
                l.IsFrozen = frozen.Value;
            }
            if (setCurrent)
            {
                if (l.IsFrozen) throw new ArgumentException("Não é possível tornar corrente um layer congelado.");
                s.Db.Clayer = id;
            }

            var ltype = (LinetypeTableRecord)s.Tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead);
            return new
            {
                name = l.Name,
                created,
                isCurrent = s.Db.Clayer == id,
                color = Acad.ColorText(l.Color),
                linetype = ltype.Name,
                lineweight = Acad.LineWeightText(l.LineWeight),
                plottable = l.IsPlottable,
                on = !l.IsOff,
                frozen = l.IsFrozen,
                locked = l.IsLocked,
                description = l.Description,
            };
        });

    [McpServerTool(Name = "set_entity_properties")]
    [Description("Altera propriedades gerais de entidades por handle. Parâmetros omitidos não são alterados.")]
    public static Task<CallToolResult> SetEntityProperties(
        [Description("Handles das entidades.")] string[] handles,
        [Description(CreateTools.LayerDesc)] string? layer = null,
        [Description("Cor: ByLayer, ByBlock, ACI 1-255, #RRGGBB, 'R,G,B' ou nome.")] string? color = null,
        [Description("Linetype (ByLayer, ByBlock ou nome).")] string? linetype = null,
        [Description("Espessura: ByLayer, ByBlock, Default ou mm.")] string? lineweight = null,
        [Description("Escala do linetype da entidade.")] double? linetypeScale = null,
        [Description("Transparência: ByLayer, ByBlock ou 0-90 (%).")] string? transparency = null) =>
        ToolInvoker.Write("set_entity_properties", new { handles, layer, color, linetype, lineweight, linetypeScale, transparency }, s =>
        {
            var ids = Acad.IdsFromHandles(s.Db, handles);
            var notes = new List<string>();
            Transparency? tr = transparency is null ? null : ParseTransparency(transparency);
            var style = Style.Of(layer, color, linetype, lineweight);

            foreach (var id in ids)
            {
                var e = Acad.OpenEntity(s, id, OpenMode.ForWrite);
                Acad.ApplyStyle(s, e, style, notes);
                if (linetypeScale is > 0) e.LinetypeScale = linetypeScale.Value;
                if (tr is not null) e.Transparency = tr.Value;
            }

            var updated = ids.Select(id => (object)EntityInfo.Summary(s.Tr, (Entity)s.Tr.GetObject(id, OpenMode.ForRead), includeGeometry: false)).ToList();
            return new { count = ids.Count, notes = notes.Distinct().ToList() is { Count: > 0 } n ? n : null, entities = updated };
        });

    [McpServerTool(Name = "move_entities", Destructive = false)]
    [Description("Move entidades por um vetor de deslocamento (WCS, unidades do desenho).")]
    public static Task<CallToolResult> MoveEntities(
        [Description("Handles das entidades.")] string[] handles,
        [Description("Deslocamento (x, y, z).")] P3 displacement) =>
        ToolInvoker.Write("move_entities", new { handles, displacement }, s =>
        {
            var ids = Acad.IdsFromHandles(s.Db, handles);
            TransformAll(s, ids, Matrix3d.Displacement(displacement.ToVector()));
            return new { count = ids.Count, handles = ids.Select(i => i.Handle.ToString()) };
        });

    [McpServerTool(Name = "copy_entities")]
    [Description("Copia entidades por um deslocamento. Com count > 1 cria um arranjo linear (cópia i deslocada i × displacement).")]
    public static Task<CallToolResult> CopyEntities(
        [Description("Handles das entidades de origem.")] string[] handles,
        [Description("Deslocamento de cada cópia.")] P3 displacement,
        [Description("Número de cópias (padrão 1, máx. 1000).")] int count = 1) =>
        ToolInvoker.Write("copy_entities", new { handles, displacement, count }, s =>
        {
            if (count is < 1 or > 1000) throw new ArgumentException("count deve estar entre 1 e 1000.");
            var ids = Acad.IdsFromHandles(s.Db, handles);
            var copies = new List<object>();
            for (var i = 1; i <= count; i++)
            {
                var clones = CloneAll(s, ids);
                TransformAll(s, clones, Matrix3d.Displacement(displacement.ToVector() * i));
                copies.Add(clones.Select(c => c.Handle.ToString()).ToList());
            }
            return new { sourceCount = ids.Count, copies = count, newHandles = copies };
        });

    [McpServerTool(Name = "rotate_entities")]
    [Description("Rotaciona entidades em torno de um ponto base (eixo Z, ângulo em graus, anti-horário positivo).")]
    public static Task<CallToolResult> RotateEntities(
        [Description("Handles das entidades.")] string[] handles,
        [Description("Ponto base da rotação.")] P3 basePoint,
        [Description("Ângulo em graus.")] double angle) =>
        ToolInvoker.Write("rotate_entities", new { handles, basePoint, angle }, s =>
        {
            var ids = Acad.IdsFromHandles(s.Db, handles);
            TransformAll(s, ids, Matrix3d.Rotation(Acad.ToRad(angle), Vector3d.ZAxis, basePoint.ToPoint()));
            return new { count = ids.Count, handles = ids.Select(i => i.Handle.ToString()) };
        });

    [McpServerTool(Name = "scale_entities")]
    [Description("Escala entidades uniformemente em relação a um ponto base.")]
    public static Task<CallToolResult> ScaleEntities(
        [Description("Handles das entidades.")] string[] handles,
        [Description("Ponto base.")] P3 basePoint,
        [Description("Fator de escala (> 0).")] double factor) =>
        ToolInvoker.Write("scale_entities", new { handles, basePoint, factor }, s =>
        {
            if (factor <= 0) throw new ArgumentException("factor deve ser > 0.");
            var ids = Acad.IdsFromHandles(s.Db, handles);
            TransformAll(s, ids, Matrix3d.Scaling(factor, basePoint.ToPoint()));
            return new { count = ids.Count, handles = ids.Select(i => i.Handle.ToString()) };
        });

    [McpServerTool(Name = "mirror_entities")]
    [Description("Espelha entidades em relação à linha point1-point2 (no plano XY). Por padrão mantém os originais e cria cópias espelhadas. " +
                 "Textos ficam espelhados como em MIRRTEXT=1; para MIRRTEXT=0 use run_command com MIRROR.")]
    public static Task<CallToolResult> MirrorEntities(
        [Description("Handles das entidades.")] string[] handles,
        [Description("Primeiro ponto da linha de espelhamento.")] P3 point1,
        [Description("Segundo ponto da linha de espelhamento.")] P3 point2,
        [Description("Apagar os originais (espelhar no lugar). Padrão false.")] bool eraseSource = false) =>
        ToolInvoker.Write("mirror_entities", new { handles, point1, point2, eraseSource }, s =>
        {
            var p1 = point1.ToPoint();
            var dir = point2.ToPoint() - p1;
            dir = new Vector3d(dir.X, dir.Y, 0);
            if (dir.IsZeroLength()) throw new ArgumentException("point1 e point2 coincidem no plano XY.");
            // Reflect across the vertical plane through the line, so normals stay +Z (like MIRROR).
            var normal = new Vector3d(-dir.Y, dir.X, 0).GetNormal();
            var m = Matrix3d.Mirroring(new Plane(p1, normal));

            var ids = Acad.IdsFromHandles(s.Db, handles);
            var targets = eraseSource ? ids : CloneAll(s, ids);
            TransformAll(s, targets, m);
            return new
            {
                count = targets.Count,
                handles = targets.Select(i => i.Handle.ToString()),
                originalsKept = !eraseSource,
            };
        });

    [McpServerTool(Name = "erase_entities", Destructive = true)]
    [Description("Apaga entidades por handle (pode ser desfeito com U no AutoCAD).")]
    public static Task<CallToolResult> EraseEntities(
        [Description("Handles das entidades.")] string[] handles) =>
        ToolInvoker.Write("erase_entities", new { handles }, s =>
        {
            var ids = Acad.IdsFromHandles(s.Db, handles);
            var erased = new List<object>();
            foreach (var id in ids)
            {
                var e = Acad.OpenEntity(s, id, OpenMode.ForWrite);
                erased.Add(new { handle = e.Handle.ToString(), type = e.GetRXClass().DxfName, layer = e.Layer });
                e.Erase();
            }
            return new { count = erased.Count, erased };
        });

    private static void TransformAll(DocScope s, IEnumerable<ObjectId> ids, Matrix3d m)
    {
        foreach (var id in ids)
        {
            var e = Acad.OpenEntity(s, id, OpenMode.ForWrite);
            try { e.TransformBy(m); }
            catch (Autodesk.AutoCAD.Runtime.Exception ex)
            {
                throw new InvalidOperationException(
                    $"Não foi possível transformar {e.GetRXClass().DxfName} {e.Handle}: {ex.ErrorStatus}.", ex);
            }
        }
    }

    /// <summary>Deep-clones entities into their own owners (keeps attributes, xdata...), preserving input order.</summary>
    private static List<ObjectId> CloneAll(DocScope s, List<ObjectId> ids)
    {
        var map = new Dictionary<ObjectId, ObjectId>();
        foreach (var group in ids.GroupBy(id => s.Tr.GetObject(id, OpenMode.ForRead).OwnerId))
        {
            var mapping = new IdMapping();
            s.Db.DeepCloneObjects(new ObjectIdCollection(group.ToArray()), group.Key, mapping, false);
            foreach (var id in group)
                map[id] = mapping[id].Value;
        }
        return ids.Select(id => map[id]).ToList();
    }

    private static Transparency ParseTransparency(string input)
    {
        var v = input.Trim().TrimEnd('%');
        if (v.Equals("bylayer", StringComparison.OrdinalIgnoreCase)) return new Transparency(TransparencyMethod.ByLayer);
        if (v.Equals("byblock", StringComparison.OrdinalIgnoreCase)) return new Transparency(TransparencyMethod.ByBlock);
        if (!int.TryParse(v, out var pct) || pct is < 0 or > 90)
            throw new ArgumentException($"Transparência inválida '{input}'. Use ByLayer, ByBlock ou 0-90.");
        return new Transparency((byte)Math.Round(255 * (100 - pct) / 100.0));
    }
}
