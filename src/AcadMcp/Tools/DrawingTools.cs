using System.ComponentModel;
using AcadMcp.Infrastructure;
using AcadMcp.Server;
using Autodesk.AutoCAD.DatabaseServices;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Tools;

[McpServerToolType]
public static class DrawingTools
{
    [McpServerTool(Name = "get_drawing_info", ReadOnly = true, Idempotent = true)]
    [Description("Informações do desenho ativo: arquivo, unidades, limites, extents, layer/layout correntes e contagem de entidades por tipo (model space e paper spaces).")]
    public static Task<CallToolResult> GetDrawingInfo() =>
        ToolInvoker.Read("get_drawing_info", null, s =>
        {
            var db = s.Db;
            var bt = (BlockTable)s.Tr.GetObject(db.BlockTableId, OpenMode.ForRead);
            var msId = bt[BlockTableRecord.ModelSpace];

            var modelCounts = new SortedDictionary<string, int>();
            var paperCounts = new SortedDictionary<string, int>();
            var blockDefs = 0;
            foreach (ObjectId btrId in bt)
            {
                var btr = (BlockTableRecord)s.Tr.GetObject(btrId, OpenMode.ForRead);
                if (!btr.IsLayout)
                {
                    if (!btr.IsAnonymous && !btr.IsFromExternalReference) blockDefs++;
                    continue;
                }
                var target = btrId == msId ? modelCounts : paperCounts;
                foreach (ObjectId id in btr)
                {
                    // ObjectClass is metadata only: no need to open each entity.
                    var name = id.ObjectClass.DxfName;
                    target[name] = target.TryGetValue(name, out var n) ? n + 1 : 1;
                }
            }

            var clayer = (LayerTableRecord)s.Tr.GetObject(db.Clayer, OpenMode.ForRead);
            var layers = (LayerTable)s.Tr.GetObject(db.LayerTableId, OpenMode.ForRead);

            return new
            {
                file = new
                {
                    name = s.Doc.Name,
                    // An unnamed drawing reports its template as Filename; that is not where it lives.
                    path = s.Doc.IsNamedDrawing ? db.Filename : null,
                    isNamedDrawing = s.Doc.IsNamedDrawing,
                    hasUnsavedChanges = Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0,
                    version = db.OriginalFileVersion.ToString(),
                },
                units = Fmt.Units(db),
                limits = new { min = Fmt.Pt(db.Limmin), max = Fmt.Pt(db.Limmax) },
                modelSpaceExtents = Fmt.Ext(db.Extmin, db.Extmax) ?? (object)"vazio",
                currentLayer = clayer.Name,
                currentLayout = LayoutManager.Current.CurrentLayout,
                tileMode = db.TileMode,
                layerCount = layers.Cast<ObjectId>().Count(),
                blockDefinitionCount = blockDefs,
                entityCounts = new
                {
                    modelSpace = modelCounts,
                    modelSpaceTotal = modelCounts.Values.Sum(),
                    paperSpaces = paperCounts,
                    paperSpacesTotal = paperCounts.Values.Sum(),
                },
            };
        });

    [McpServerTool(Name = "list_layers", ReadOnly = true, Idempotent = true)]
    [Description("Lista os layers com estado (on/frozen/locked), cor, linetype, espessura, plotável e descrição.")]
    public static Task<CallToolResult> ListLayers() =>
        ToolInvoker.Read("list_layers", null, s =>
        {
            var lt = (LayerTable)s.Tr.GetObject(s.Db.LayerTableId, OpenMode.ForRead);
            var layers = new List<object>();
            foreach (ObjectId id in lt)
            {
                var l = (LayerTableRecord)s.Tr.GetObject(id, OpenMode.ForRead);
                var ltype = (LinetypeTableRecord)s.Tr.GetObject(l.LinetypeObjectId, OpenMode.ForRead);
                layers.Add(new
                {
                    name = l.Name,
                    isCurrent = id == s.Db.Clayer,
                    on = !l.IsOff,
                    frozen = l.IsFrozen,
                    locked = l.IsLocked,
                    color = Acad.ColorText(l.Color),
                    linetype = ltype.Name,
                    lineweight = Acad.LineWeightText(l.LineWeight),
                    plottable = l.IsPlottable,
                    description = string.IsNullOrEmpty(l.Description) ? null : l.Description,
                    isXref = l.IsDependent,
                });
            }
            return new { count = layers.Count, layers };
        });

    [McpServerTool(Name = "list_blocks", ReadOnly = true, Idempotent = true)]
    [Description("Lista as definições de bloco (sem layouts), com atributos definidos, se é dinâmico/xref e quantas referências existem.")]
    public static Task<CallToolResult> ListBlocks(
        [Description("Incluir blocos anônimos (*U, *D...). Padrão false.")] bool includeAnonymous = false) =>
        ToolInvoker.Read("list_blocks", new { includeAnonymous }, s =>
        {
            var bt = (BlockTable)s.Tr.GetObject(s.Db.BlockTableId, OpenMode.ForRead);
            var blocks = new List<object>();
            foreach (ObjectId id in bt)
            {
                var btr = (BlockTableRecord)s.Tr.GetObject(id, OpenMode.ForRead);
                if (btr.IsLayout || (btr.IsAnonymous && !includeAnonymous)) continue;

                var attDefs = new List<object>();
                var entityCount = 0;
                foreach (ObjectId eid in btr)
                {
                    entityCount++;
                    if (eid.ObjectClass.DxfName == "ATTDEF" && s.Tr.GetObject(eid, OpenMode.ForRead) is AttributeDefinition ad)
                        attDefs.Add(new { tag = ad.Tag, prompt = ad.Prompt, @default = ad.TextString, constant = ad.Constant, invisible = ad.Invisible });
                }

                var refCount = btr.GetBlockReferenceIds(true, false).Count;
                if (btr.IsDynamicBlock)
                    foreach (ObjectId anonId in btr.GetAnonymousBlockIds())
                        refCount += ((BlockTableRecord)s.Tr.GetObject(anonId, OpenMode.ForRead)).GetBlockReferenceIds(true, false).Count;

                blocks.Add(new
                {
                    name = btr.Name,
                    origin = Fmt.Pt(btr.Origin),
                    entityCount,
                    referenceCount = refCount,
                    isDynamic = btr.IsDynamicBlock,
                    isXref = btr.IsFromExternalReference,
                    xrefPath = btr.IsFromExternalReference ? btr.PathName : null,
                    isAnonymous = btr.IsAnonymous,
                    units = btr.Units.ToString(),
                    attributes = attDefs.Count > 0 ? attDefs : null,
                    description = string.IsNullOrEmpty(btr.Comments) ? null : btr.Comments,
                });
            }
            return new { count = blocks.Count, blocks };
        });

    [McpServerTool(Name = "list_layouts", ReadOnly = true, Idempotent = true)]
    [Description("Lista os layouts (Model e paper spaces) com ordem, configuração de plotagem e quantidade de viewports.")]
    public static Task<CallToolResult> ListLayouts() =>
        ToolInvoker.Read("list_layouts", null, s =>
        {
            var dict = (DBDictionary)s.Tr.GetObject(s.Db.LayoutDictionaryId, OpenMode.ForRead);
            var current = LayoutManager.Current.CurrentLayout;
            var layouts = new List<(int Order, object Info)>();
            foreach (DBDictionaryEntry entry in dict)
            {
                var lay = (Layout)s.Tr.GetObject(entry.Value, OpenMode.ForRead);
                var btr = (BlockTableRecord)s.Tr.GetObject(lay.BlockTableRecordId, OpenMode.ForRead);
                var viewports = btr.Cast<ObjectId>().Count(i => i.ObjectClass.DxfName == "VIEWPORT");
                layouts.Add((lay.TabOrder, new
                {
                    name = lay.LayoutName,
                    tabOrder = lay.TabOrder,
                    isModel = lay.ModelType,
                    isCurrent = lay.LayoutName.Equals(current, StringComparison.OrdinalIgnoreCase),
                    plotDevice = lay.PlotConfigurationName,
                    paper = lay.CanonicalMediaName,
                    paperSize = new { width = Fmt.R(lay.PlotPaperSize.X), height = Fmt.R(lay.PlotPaperSize.Y), units = "mm" },
                    plotPaperUnits = lay.PlotPaperUnits.ToString(),
                    plotRotation = lay.PlotRotation.ToString(),
                    viewportCount = lay.ModelType ? 0 : Math.Max(0, viewports - 1), // first viewport is the paper space itself
                    entityCount = btr.Cast<ObjectId>().Count(),
                }));
            }
            return new { count = layouts.Count, current, layouts = layouts.OrderBy(l => l.Order).Select(l => l.Info) };
        });

    [McpServerTool(Name = "list_text_styles", ReadOnly = true, Idempotent = true)]
    [Description("Lista os estilos de texto com fonte, altura fixa, fator de largura e ângulo oblíquo.")]
    public static Task<CallToolResult> ListTextStyles() =>
        ToolInvoker.Read("list_text_styles", null, s =>
        {
            var table = (TextStyleTable)s.Tr.GetObject(s.Db.TextStyleTableId, OpenMode.ForRead);
            var styles = new List<object>();
            foreach (ObjectId id in table)
            {
                var ts = (TextStyleTableRecord)s.Tr.GetObject(id, OpenMode.ForRead);
                if (ts.IsShapeFile) continue;
                styles.Add(new
                {
                    name = ts.Name,
                    isCurrent = id == s.Db.Textstyle,
                    fontFile = ts.FileName,
                    bigFontFile = string.IsNullOrEmpty(ts.BigFontFileName) ? null : ts.BigFontFileName,
                    trueTypeFont = string.IsNullOrEmpty(ts.Font.TypeFace) ? null : ts.Font.TypeFace,
                    fixedHeight = ts.TextSize == 0 ? null : (double?)Fmt.R(ts.TextSize),
                    widthFactor = Fmt.R(ts.XScale),
                    obliqueDeg = Acad.ToDeg(ts.ObliquingAngle),
                    annotative = ts.Annotative == AnnotativeStates.True,
                });
            }
            return new { count = styles.Count, defaultTextHeight = Fmt.R(s.Db.Textsize), styles };
        });

    [McpServerTool(Name = "list_dim_styles", ReadOnly = true, Idempotent = true)]
    [Description("Lista os estilos de cota com escala, altura de texto, tamanho de seta, casas decimais e estilo de texto.")]
    public static Task<CallToolResult> ListDimStyles() =>
        ToolInvoker.Read("list_dim_styles", null, s =>
        {
            var table = (DimStyleTable)s.Tr.GetObject(s.Db.DimStyleTableId, OpenMode.ForRead);
            var styles = new List<object>();
            foreach (ObjectId id in table)
            {
                var d = (DimStyleTableRecord)s.Tr.GetObject(id, OpenMode.ForRead);
                var txtStyle = d.Dimtxsty.IsNull ? null : ((TextStyleTableRecord)s.Tr.GetObject(d.Dimtxsty, OpenMode.ForRead)).Name;
                styles.Add(new
                {
                    name = d.Name,
                    isCurrent = id == s.Db.Dimstyle,
                    dimscale = Fmt.R(d.Dimscale),
                    textHeight = Fmt.R(d.Dimtxt),
                    arrowSize = Fmt.R(d.Dimasz),
                    decimals = d.Dimdec,
                    linearUnits = d.Dimlunit,
                    linearScale = Fmt.R(d.Dimlfac),
                    extensionOffset = Fmt.R(d.Dimexo),
                    extensionBeyond = Fmt.R(d.Dimexe),
                    textGap = Fmt.R(d.Dimgap),
                    textStyle = txtStyle,
                    annotative = d.Annotative == AnnotativeStates.True,
                });
            }
            return new { count = styles.Count, styles };
        });

    [McpServerTool(Name = "save_drawing")]
    [Description("Salva o desenho ativo. Sem 'path' faz QSAVE (exige desenho já nomeado). Com 'path' faz SAVEAS no formato DWG atual.")]
    public static Task<CallToolResult> SaveDrawing(
        [Description("Caminho completo .dwg para 'Salvar como'. Omita para salvar no arquivo atual.")] string? path = null,
        [Description("Sobrescrever se 'path' já existir. Padrão false.")] bool overwrite = false) =>
        ToolInvoker.CommandContext("save_drawing", new { path, overwrite }, async () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument!;
            var ed = doc.Editor;
            var oldFiledia = AcApp.GetSystemVariable("FILEDIA");
            AcApp.SetSystemVariable("FILEDIA", 0);
            try
            {
                if (string.IsNullOrWhiteSpace(path))
                {
                    if (!doc.IsNamedDrawing)
                        throw new ToolException("O desenho ainda não tem nome. Informe 'path' (ex.: C:\\Projetos\\planta.dwg).");
                    await ed.CommandAsync("_.QSAVE");
                }
                else
                {
                    var full = Path.GetFullPath(path);
                    if (!full.EndsWith(".dwg", StringComparison.OrdinalIgnoreCase)) full += ".dwg";
                    Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                    var exists = File.Exists(full);
                    if (exists && !overwrite && !full.Equals(doc.Database.Filename, StringComparison.OrdinalIgnoreCase))
                        throw new ToolException($"O arquivo '{full}' já existe. Use overwrite=true para substituir.");

                    // SAVEAS prompts: file format (Enter = current), file name, replace confirmation.
                    if (exists) await ed.CommandAsync("_.SAVEAS", "", full, "_Y");
                    else await ed.CommandAsync("_.SAVEAS", "", full);
                }
            }
            finally
            {
                AcApp.SetSystemVariable("FILEDIA", oldFiledia);
            }

            return new
            {
                saved = true,
                name = doc.Name,
                path = doc.Database.Filename,
                hasUnsavedChanges = Convert.ToInt32(AcApp.GetSystemVariable("DBMOD")) != 0,
            };
        });

    [McpServerTool(Name = "zoom_extents", Idempotent = true)]
    [Description("Zoom extents na viewport ativa (mostra todo o desenho).")]
    public static Task<CallToolResult> ZoomExtents() =>
        ToolInvoker.Raw("zoom_extents", null, () =>
        {
            if (AcApp.DocumentManager.MdiActiveDocument is null)
                throw new InvalidOperationException("Nenhum desenho aberto no AutoCAD.");
            dynamic acad = AcApp.AcadApplication;
            acad.ZoomExtents();
            return new { zoomed = true };
        });
}
