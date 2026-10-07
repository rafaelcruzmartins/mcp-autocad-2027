using System.ComponentModel;
using AcadMcp.Infrastructure;
using AcadMcp.Server;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AcadMcp.Tools;

[McpServerToolType]
public static class QueryTools
{
    private static readonly Dictionary<string, string[]> TypeAliases = new(StringComparer.OrdinalIgnoreCase)
    {
        ["polyline"] = ["LWPOLYLINE", "POLYLINE"],
        ["pline"] = ["LWPOLYLINE", "POLYLINE"],
        ["block"] = ["INSERT"],
        ["blockreference"] = ["INSERT"],
        ["dimension"] = ["DIMENSION", "ARC_DIMENSION", "LARGE_RADIAL_DIMENSION"],
        ["dim"] = ["DIMENSION", "ARC_DIMENSION", "LARGE_RADIAL_DIMENSION"],
        ["text"] = ["TEXT"],
        ["mtext"] = ["MTEXT"],
        ["anytext"] = ["TEXT", "MTEXT"],
        ["leader"] = ["LEADER", "MULTILEADER"],
    };

    [McpServerTool(Name = "query_entities", ReadOnly = true, Idempotent = true)]
    [Description("Busca entidades com filtros combináveis (tipo, layer, nome de bloco, região retangular). " +
                 "Retorna handle, tipo DXF, layer, geometria resumida e bounds. Paginado por limit/offset.")]
    public static Task<CallToolResult> QueryEntities(
        [Description("Tipos DXF (LINE, LWPOLYLINE, CIRCLE, ARC, TEXT, MTEXT, INSERT, DIMENSION, HATCH...) ou aliases (polyline, block, dimension, anytext). Vazio = todos.")]
        string[]? types = null,
        [Description("Layers; aceita curingas * e ?. Vazio = todos.")]
        string[]? layers = null,
        [Description("Só referências (INSERT) destes blocos; nome efetivo, aceita curingas.")]
        string[]? blockNames = null,
        [Description("Região retangular em WCS para filtrar pelos bounds da entidade.")]
        Rect? region = null,
        [Description("'crossing' (bounds tocam a região, padrão) ou 'window' (bounds totalmente dentro).")]
        string regionMode = "crossing",
        [Description("Onde buscar: model (padrão), current, paper (todos os layouts), all, ou o nome de um layout.")]
        string space = "model",
        [Description("Incluir geometria resumida (padrão true). false = resposta mais curta.")]
        bool includeGeometry = true,
        [Description("Máximo de entidades retornadas (padrão 200, máx. 5000).")]
        int limit = 200,
        [Description("Pular as N primeiras entidades encontradas (paginação).")]
        int offset = 0) =>
        ToolInvoker.Read("query_entities", new { types, layers, blockNames, region, regionMode, space, includeGeometry, limit, offset }, s =>
        {
            limit = Math.Clamp(limit, 1, 5000);
            offset = Math.Max(0, offset);

            HashSet<string>? typeSet = null;
            if (types is { Length: > 0 })
            {
                typeSet = new(StringComparer.OrdinalIgnoreCase);
                foreach (var t in types.Where(t => !string.IsNullOrWhiteSpace(t)))
                {
                    if (TypeAliases.TryGetValue(t.Trim(), out var expanded)) typeSet.UnionWith(expanded);
                    else typeSet.Add(t.Trim());
                }
            }
            if (blockNames is { Length: > 0 })
            {
                typeSet ??= new(StringComparer.OrdinalIgnoreCase);
                typeSet.Add("INSERT");
            }

            var layerMatch = Acad.Wildcard(layers);
            var blockMatch = Acad.Wildcard(blockNames);
            var filterBlocks = blockNames is { Length: > 0 };
            var window = regionMode.Equals("window", StringComparison.OrdinalIgnoreCase);
            if (!window && !regionMode.Equals("crossing", StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("regionMode deve ser 'crossing' ou 'window'.");

            double minX = 0, minY = 0, maxX = 0, maxY = 0;
            if (region is not null)
            {
                minX = Math.Min(region.Min.X, region.Max.X); maxX = Math.Max(region.Min.X, region.Max.X);
                minY = Math.Min(region.Min.Y, region.Max.Y); maxY = Math.Max(region.Min.Y, region.Max.Y);
            }

            var spaces = Acad.ResolveSpaces(s, space);
            var results = new List<object>();
            var matched = 0;
            var multiSpace = spaces.Count > 1;

            foreach (var (btrId, layoutName) in spaces)
            {
                var btr = (BlockTableRecord)s.Tr.GetObject(btrId, OpenMode.ForRead);
                foreach (ObjectId id in btr)
                {
                    var cls = id.ObjectClass;
                    if (typeSet is not null && !typeSet.Contains(cls.DxfName) && !typeSet.Contains(cls.Name)) continue;

                    if (s.Tr.GetObject(id, OpenMode.ForRead) is not Entity e) continue;
                    if (!layerMatch(e.Layer)) continue;
                    if (filterBlocks && (e is not BlockReference br || !blockMatch(Acad.EffectiveBlockName(s.Tr, br)))) continue;

                    if (region is not null)
                    {
                        if (!EntityInfo.TryExtents(e, out var ext)) continue;
                        var inside = window
                            ? ext.MinPoint.X >= minX && ext.MaxPoint.X <= maxX && ext.MinPoint.Y >= minY && ext.MaxPoint.Y <= maxY
                            : ext.MaxPoint.X >= minX && ext.MinPoint.X <= maxX && ext.MaxPoint.Y >= minY && ext.MinPoint.Y <= maxY;
                        if (!inside) continue;
                    }

                    matched++;
                    if (matched <= offset || results.Count >= limit) continue;

                    var summary = EntityInfo.Summary(s.Tr, e, includeGeometry);
                    if (multiSpace) summary["space"] = layoutName;
                    results.Add(summary);
                }
            }

            return new
            {
                searched = spaces.Select(x => x.Layout),
                totalMatched = matched,
                offset,
                returned = results.Count,
                truncated = offset + results.Count < matched,
                entities = results,
            };
        });

    [McpServerTool(Name = "get_entity_properties", ReadOnly = true, Idempotent = true)]
    [Description("Todas as propriedades de uma entidade pelo handle: propriedades gerais, geometria completa, atributos de bloco, propriedades dinâmicas, XData e chaves do dicionário de extensão.")]
    public static Task<CallToolResult> GetEntityProperties(
        [Description("Handle hexadecimal da entidade (ex.: \"2A3F\").")] string handle) =>
        ToolInvoker.Read("get_entity_properties", new { handle }, s =>
            EntityInfo.Details(s.Tr, Acad.OpenEntity(s, handle, OpenMode.ForRead)));

    [McpServerTool(Name = "get_selection", ReadOnly = true, Idempotent = true)]
    [Description("Entidades atualmente selecionadas pelo usuário no AutoCAD (pickfirst / grips).")]
    public static Task<CallToolResult> GetSelection(
        [Description("Máximo de entidades retornadas (padrão 500).")] int limit = 500) =>
        ToolInvoker.Read("get_selection", new { limit }, s =>
        {
            var res = s.Ed.SelectImplied();
            if (res.Status != PromptStatus.OK || res.Value is null)
                return new { count = 0, returned = 0, entities = new List<object>(), note = "Nada selecionado no AutoCAD." };

            var ids = res.Value.GetObjectIds();
            var entities = ids.Take(Math.Clamp(limit, 1, 5000))
                .Select(id => s.Tr.GetObject(id, OpenMode.ForRead))
                .OfType<Entity>()
                .Select(e => (object)EntityInfo.Summary(s.Tr, e))
                .ToList();
            return new { count = ids.Length, returned = entities.Count, entities, note = (string?)null };
        });

    [McpServerTool(Name = "get_block_attributes", ReadOnly = true, Idempotent = true)]
    [Description("Todas as referências de um bloco (inclusive variações de bloco dinâmico) com posição, layout e atributos.")]
    public static Task<CallToolResult> GetBlockAttributes(
        [Description("Nome da definição de bloco.")] string blockName,
        [Description("Máximo de referências retornadas (padrão 1000).")] int limit = 1000) =>
        ToolInvoker.Read("get_block_attributes", new { blockName, limit }, s =>
        {
            var bt = (BlockTable)s.Tr.GetObject(s.Db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(blockName))
                throw new ArgumentException($"Bloco '{blockName}' não existe no desenho. Use list_blocks.");
            var btr = (BlockTableRecord)s.Tr.GetObject(bt[blockName], OpenMode.ForRead);

            var refIds = btr.GetBlockReferenceIds(true, false).Cast<ObjectId>().ToList();
            if (btr.IsDynamicBlock)
                foreach (ObjectId anonId in btr.GetAnonymousBlockIds())
                    refIds.AddRange(((BlockTableRecord)s.Tr.GetObject(anonId, OpenMode.ForRead))
                        .GetBlockReferenceIds(true, false).Cast<ObjectId>());

            var refs = new List<object>();
            foreach (var id in refIds.Take(Math.Clamp(limit, 1, 100000)))
            {
                var br = (BlockReference)s.Tr.GetObject(id, OpenMode.ForRead);
                refs.Add(new
                {
                    handle = br.Handle.ToString(),
                    space = Acad.OwnerLayoutName(s.Tr, br),
                    layer = br.Layer,
                    position = Fmt.Pt(br.Position),
                    rotationDeg = Acad.ToDeg(br.Rotation),
                    scale = new { x = Fmt.R(br.ScaleFactors.X), y = Fmt.R(br.ScaleFactors.Y), z = Fmt.R(br.ScaleFactors.Z) },
                    attributes = EntityInfo.AttributeMap(s.Tr, br),
                });
            }
            return new { blockName = btr.Name, totalReferences = refIds.Count, returned = refs.Count, references = refs };
        });
}
