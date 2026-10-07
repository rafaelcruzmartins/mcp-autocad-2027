using System.ComponentModel;
using AcadMcp.Infrastructure;
using AcadMcp.Server;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AcadMcp.Tools;

[McpServerToolType]
public static class BlockTools
{
    [McpServerTool(Name = "insert_block")]
    [Description("Insere uma referência de bloco com atributos. Se o bloco não existir no desenho, informe sourceFile (DWG) para importá-lo.")]
    public static Task<CallToolResult> InsertBlock(
        [Description("Nome da definição de bloco.")] string name,
        [Description("Ponto de inserção.")] P3 position,
        [Description("Escala uniforme (padrão 1). Ignorada se scaleFactors for informado.")] double scale = 1,
        [Description("Escalas X/Y/Z independentes.")] P3? scaleFactors = null,
        [Description("Rotação em graus.")] double rotation = 0,
        [Description("Valores de atributos por TAG, ex.: {\"NUMERO\": \"101\", \"NOME\": \"Sala\"}. Tags omitidas usam o valor padrão.")]
        Dictionary<string, string>? attributes = null,
        [Description("Arquivo DWG de onde importar a definição, se o bloco não existir no desenho.")] string? sourceFile = null,
        [Description(CreateTools.LayerDesc)] string? layer = null,
        [Description(CreateTools.ColorDesc)] string? color = null) =>
        ToolInvoker.Write("insert_block", new { name, position, scale, scaleFactors, rotation, attributes, sourceFile, layer, color }, s =>
        {
            var notes = new List<string>();
            var bt = (BlockTable)s.Tr.GetObject(s.Db.BlockTableId, OpenMode.ForRead);
            if (!bt.Has(name))
            {
                if (string.IsNullOrWhiteSpace(sourceFile))
                    throw new ArgumentException($"Bloco '{name}' não existe no desenho. Use list_blocks ou informe sourceFile (DWG) para importar.");
                if (!File.Exists(sourceFile))
                    throw new ArgumentException($"sourceFile não encontrado: {sourceFile}");
                using var src = new Database(false, true);
                src.ReadDwgFile(sourceFile, FileOpenMode.OpenForReadAndAllShare, true, "");
                s.Db.Insert(name, src, true);
                notes.Add($"Definição '{name}' importada de {sourceFile}.");
                bt = (BlockTable)s.Tr.GetObject(s.Db.BlockTableId, OpenMode.ForRead);
            }

            var btrId = bt[name];
            var btr = (BlockTableRecord)s.Tr.GetObject(btrId, OpenMode.ForRead);
            if (btr.IsLayout) throw new ArgumentException($"'{name}' é um layout, não um bloco.");

            var sf = scaleFactors is null ? new Scale3d(scale) : new Scale3d(scaleFactors.X, scaleFactors.Y, scaleFactors.Z);
            if (sf.X == 0 || sf.Y == 0 || sf.Z == 0) throw new ArgumentException("Escala não pode ser 0.");

            var br = new BlockReference(position.ToPoint(), btrId);
            Acad.AddToCurrentSpace(s, br, Style.Of(layer, color, null, null), notes);
            br.ScaleFactors = sf;
            br.Rotation = Acad.ToRad(rotation);

            var values = new Dictionary<string, string>(attributes ?? [], StringComparer.OrdinalIgnoreCase);
            var used = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (ObjectId id in btr)
            {
                if (id.ObjectClass.DxfName != "ATTDEF") continue;
                var ad = (AttributeDefinition)s.Tr.GetObject(id, OpenMode.ForRead);
                if (ad.Constant) continue;

                var ar = new AttributeReference();
                ar.SetAttributeFromBlock(ad, br.BlockTransform);
                if (values.TryGetValue(ad.Tag, out var v))
                {
                    ar.TextString = v;
                    used.Add(ad.Tag);
                }
                if (ar.IsMTextAttribute) ar.UpdateMTextAttribute();
                else if (ar.Justify != AttachmentPoint.BaseLeft) ar.AdjustAlignment(s.Db);

                br.AttributeCollection.AppendAttribute(ar);
                s.Tr.AddNewlyCreatedDBObject(ar, true);
            }

            var unknown = values.Keys.Where(k => !used.Contains(k)).ToList();
            if (unknown.Count > 0) notes.Add($"Tags inexistentes no bloco (ignoradas): {string.Join(", ", unknown)}.");

            var info = EntityInfo.Summary(s.Tr, br);
            info["space"] = Acad.OwnerLayoutName(s.Tr, br);
            if (notes.Count > 0) info["notes"] = notes;
            return info;
        });

    [McpServerTool(Name = "set_block_attributes")]
    [Description("Altera valores de atributos em uma ou mais referências de bloco (por handle).")]
    public static Task<CallToolResult> SetBlockAttributes(
        [Description("Handles das referências de bloco (INSERT).")] string[] handles,
        [Description("Novos valores por TAG, ex.: {\"NUMERO\": \"102\"}.")] Dictionary<string, string> attributes) =>
        ToolInvoker.Write("set_block_attributes", new { handles, attributes }, s =>
        {
            if (attributes is null || attributes.Count == 0) throw new ArgumentException("attributes está vazio.");
            var values = new Dictionary<string, string>(attributes, StringComparer.OrdinalIgnoreCase);
            var ids = Acad.IdsFromHandles(s.Db, handles);
            var results = new List<object>();

            foreach (var id in ids)
            {
                if (Acad.OpenEntity(s, id, OpenMode.ForRead) is not BlockReference br)
                    throw new ArgumentException($"O handle {id.Handle} não é uma referência de bloco.");

                var changed = new List<object>();
                var found = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (ObjectId aid in br.AttributeCollection)
                {
                    if (aid.IsErased) continue;
                    var ar = (AttributeReference)s.Tr.GetObject(aid, OpenMode.ForRead);
                    if (!values.TryGetValue(ar.Tag, out var v)) continue;
                    found.Add(ar.Tag);

                    var old = ar.IsMTextAttribute ? ar.MTextAttribute.Text : ar.TextString;
                    if (old == v) continue;
                    try { ar.UpgradeOpen(); }
                    catch (Autodesk.AutoCAD.Runtime.Exception ex) when (ex.ErrorStatus == Autodesk.AutoCAD.Runtime.ErrorStatus.OnLockedLayer)
                    {
                        throw new InvalidOperationException($"O bloco {br.Handle} está em layer bloqueado.");
                    }
                    ar.TextString = v;
                    if (ar.IsMTextAttribute) ar.UpdateMTextAttribute();
                    else if (ar.Justify != AttachmentPoint.BaseLeft) ar.AdjustAlignment(s.Db);
                    changed.Add(new { tag = ar.Tag, from = old, to = v });
                }

                results.Add(new
                {
                    handle = br.Handle.ToString(),
                    block = Acad.EffectiveBlockName(s.Tr, br),
                    changed,
                    missingTags = values.Keys.Where(k => !found.Contains(k)).ToList() is { Count: > 0 } m ? m : null,
                });
            }
            return new { count = results.Count, results };
        });
}
