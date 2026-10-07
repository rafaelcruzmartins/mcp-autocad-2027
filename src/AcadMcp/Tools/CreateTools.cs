using System.ComponentModel;
using AcadMcp.Infrastructure;
using AcadMcp.Server;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AcadMcp.Tools;

/// <summary>Entity creation tools. Everything goes into the current space (model or active layout).</summary>
[McpServerToolType]
public static class CreateTools
{
    internal const string LayerDesc = "Layer (criado se não existir). Omitido = layer corrente.";
    internal const string ColorDesc = "Cor: ByLayer, ByBlock, ACI 1-255, #RRGGBB, 'R,G,B' ou nome. Omitida = corrente.";
    internal const string LinetypeDesc = "Linetype (carregado de acadiso.lin se preciso). Omitido = corrente.";
    internal const string LineweightDesc = "Espessura: ByLayer, ByBlock, Default ou mm (ex.: 0.35). Omitida = corrente.";

    /// <summary>Uniform result for created entities: summary + space + notes (e.g. auto-created layer).</summary>
    private static Dictionary<string, object?> Result(DocScope s, Entity e, List<string> notes)
    {
        var info = EntityInfo.Summary(s.Tr, e);
        info["space"] = Acad.OwnerLayoutName(s.Tr, e);
        if (notes.Count > 0) info["notes"] = notes;
        return info;
    }

    [McpServerTool(Name = "create_line")]
    [Description("Cria uma linha entre dois pontos (WCS, unidades do desenho).")]
    public static Task<CallToolResult> CreateLine(
        [Description("Ponto inicial.")] P3 start,
        [Description("Ponto final.")] P3 end,
        [Description(LayerDesc)] string? layer = null,
        [Description(ColorDesc)] string? color = null,
        [Description(LinetypeDesc)] string? linetype = null,
        [Description(LineweightDesc)] string? lineweight = null) =>
        ToolInvoker.Write("create_line", new { start, end, layer, color, linetype, lineweight }, s =>
        {
            if (start.ToPoint().IsEqualTo(end.ToPoint()))
                throw new ArgumentException("start e end são o mesmo ponto.");
            var notes = new List<string>();
            var line = new Line(start.ToPoint(), end.ToPoint());
            Acad.AddToCurrentSpace(s, line, Style.Of(layer, color, linetype, lineweight), notes);
            return Result(s, line, notes);
        });

    [McpServerTool(Name = "create_polyline")]
    [Description("Cria uma polilinha 2D (LWPOLYLINE) a partir de vértices, com bulge opcional para trechos em arco.")]
    public static Task<CallToolResult> CreatePolyline(
        [Description("Vértices em ordem (mínimo 2).")] PolyVertex[] vertices,
        [Description("Fechar a polilinha.")] bool closed = false,
        [Description("Elevação Z da polilinha.")] double elevation = 0,
        [Description("Largura constante (0 = sem largura).")] double constantWidth = 0,
        [Description(LayerDesc)] string? layer = null,
        [Description(ColorDesc)] string? color = null,
        [Description(LinetypeDesc)] string? linetype = null,
        [Description(LineweightDesc)] string? lineweight = null) =>
        ToolInvoker.Write("create_polyline", new { vertices, closed, elevation, constantWidth, layer, color, linetype, lineweight }, s =>
        {
            if (vertices is null || vertices.Length < 2)
                throw new ArgumentException("Informe ao menos 2 vértices.");
            var notes = new List<string>();
            var pl = new Polyline(vertices.Length);
            for (var i = 0; i < vertices.Length; i++)
                pl.AddVertexAt(i, new Point2d(vertices[i].X, vertices[i].Y), vertices[i].Bulge, constantWidth, constantWidth);
            pl.Closed = closed;
            pl.Elevation = elevation;
            Acad.AddToCurrentSpace(s, pl, Style.Of(layer, color, linetype, lineweight), notes);
            return Result(s, pl, notes);
        });

    [McpServerTool(Name = "create_circle")]
    [Description("Cria um círculo por centro e raio.")]
    public static Task<CallToolResult> CreateCircle(
        [Description("Centro.")] P3 center,
        [Description("Raio (> 0).")] double radius,
        [Description(LayerDesc)] string? layer = null,
        [Description(ColorDesc)] string? color = null,
        [Description(LinetypeDesc)] string? linetype = null,
        [Description(LineweightDesc)] string? lineweight = null) =>
        ToolInvoker.Write("create_circle", new { center, radius, layer, color, linetype, lineweight }, s =>
        {
            if (radius <= 0) throw new ArgumentException("radius deve ser > 0.");
            var notes = new List<string>();
            var c = new Circle(center.ToPoint(), Vector3d.ZAxis, radius);
            Acad.AddToCurrentSpace(s, c, Style.Of(layer, color, linetype, lineweight), notes);
            return Result(s, c, notes);
        });

    [McpServerTool(Name = "create_arc")]
    [Description("Cria um arco por centro, raio e ângulos inicial/final em graus (sentido anti-horário a partir do eixo X).")]
    public static Task<CallToolResult> CreateArc(
        [Description("Centro.")] P3 center,
        [Description("Raio (> 0).")] double radius,
        [Description("Ângulo inicial em graus.")] double startAngle,
        [Description("Ângulo final em graus (o arco vai de startAngle a endAngle no sentido anti-horário).")] double endAngle,
        [Description(LayerDesc)] string? layer = null,
        [Description(ColorDesc)] string? color = null,
        [Description(LinetypeDesc)] string? linetype = null,
        [Description(LineweightDesc)] string? lineweight = null) =>
        ToolInvoker.Write("create_arc", new { center, radius, startAngle, endAngle, layer, color, linetype, lineweight }, s =>
        {
            if (radius <= 0) throw new ArgumentException("radius deve ser > 0.");
            var notes = new List<string>();
            var arc = new Arc(center.ToPoint(), Vector3d.ZAxis, radius, Acad.ToRad(startAngle), Acad.ToRad(endAngle));
            var h = Acad.AddToCurrentSpace(s, arc, Style.Of(layer, color, linetype, lineweight), notes);
            return Result(s, arc, notes);
        });

    [McpServerTool(Name = "create_text")]
    [Description("Cria texto de linha única (TEXT).")]
    public static Task<CallToolResult> CreateText(
        [Description("Conteúdo do texto.")] string text,
        [Description("Ponto de inserção (o ponto de alinhamento, se 'justify' não for Left).")] P3 position,
        [Description("Altura. Omitida = altura fixa do estilo ou TEXTSIZE.")] double? height = null,
        [Description("Rotação em graus.")] double rotation = 0,
        [Description("Estilo de texto. Omitido = corrente.")] string? style = null,
        [Description("Justificação: Left, Center, Right, Middle, TopLeft, TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")]
        string justify = "Left",
        [Description(LayerDesc)] string? layer = null,
        [Description(ColorDesc)] string? color = null) =>
        ToolInvoker.Write("create_text", new { text, position, height, rotation, style, justify, layer, color }, s =>
        {
            var notes = new List<string>();
            var styleId = Acad.TextStyleId(s, style);
            var styleRec = (TextStyleTableRecord)s.Tr.GetObject(styleId, OpenMode.ForRead);
            var t = new DBText
            {
                TextString = text,
                Position = position.ToPoint(),
                TextStyleId = styleId,
                Height = height ?? (styleRec.TextSize > 0 ? styleRec.TextSize : s.Db.Textsize),
                Rotation = Acad.ToRad(rotation),
            };
            Acad.AddToCurrentSpace(s, t, Style.Of(layer, color, null, null), notes);
            // AddToCurrentSpace applied DB defaults; re-assert the requested style/height.
            t.TextStyleId = styleId;
            t.Height = height ?? (styleRec.TextSize > 0 ? styleRec.TextSize : s.Db.Textsize);

            var just = ParseTextJustify(justify);
            if (just != AttachmentPoint.BaseLeft)
            {
                t.Justify = just;
                t.AlignmentPoint = position.ToPoint();
                t.AdjustAlignment(s.Db);
            }
            return Result(s, t, notes);
        });

    [McpServerTool(Name = "create_mtext")]
    [Description("Cria texto multilinha (MTEXT). Aceita códigos de formatação MText (\\P = nova linha).")]
    public static Task<CallToolResult> CreateMText(
        [Description("Conteúdo. Use \\P ou \\n para quebra de linha.")] string text,
        [Description("Ponto de inserção (relativo ao 'attachment').")] P3 location,
        [Description("Altura do texto. Omitida = TEXTSIZE ou altura do estilo.")] double? height = null,
        [Description("Largura da caixa (0 = sem quebra automática).")] double width = 0,
        [Description("Rotação em graus.")] double rotation = 0,
        [Description("Estilo de texto. Omitido = corrente.")] string? style = null,
        [Description("Ponto de fixação: TopLeft (padrão), TopCenter, TopRight, MiddleLeft, MiddleCenter, MiddleRight, BottomLeft, BottomCenter, BottomRight.")]
        string attachment = "TopLeft",
        [Description(LayerDesc)] string? layer = null,
        [Description(ColorDesc)] string? color = null) =>
        ToolInvoker.Write("create_mtext", new { text, location, height, width, rotation, style, attachment, layer, color }, s =>
        {
            var notes = new List<string>();
            var styleId = Acad.TextStyleId(s, style);
            var styleRec = (TextStyleTableRecord)s.Tr.GetObject(styleId, OpenMode.ForRead);
            var m = new MText();
            Acad.AddToCurrentSpace(s, m, Style.Of(layer, color, null, null), notes);
            m.Contents = text.Replace("\r\n", "\\P").Replace("\n", "\\P");
            m.Location = location.ToPoint();
            m.TextStyleId = styleId;
            m.TextHeight = height ?? (styleRec.TextSize > 0 ? styleRec.TextSize : s.Db.Textsize);
            m.Width = Math.Max(0, width);
            m.Rotation = Acad.ToRad(rotation);
            m.Attachment = ParseAttachment(attachment);
            return Result(s, m, notes);
        });

    [McpServerTool(Name = "create_dimension")]
    [Description("Cria cota. kind=linear: point1, point2, dimLinePoint, rotation (0 = horizontal, 90 = vertical). " +
                 "kind=aligned: point1, point2, dimLinePoint. kind=radial ou diametric: entityHandle de círculo/arco (ou center + radius) e angle em graus do ponto na circunferência.")]
    public static Task<CallToolResult> CreateDimension(
        [Description("linear, aligned, radial ou diametric.")] string kind,
        [Description("Primeiro ponto de origem (linear/aligned).")] P3? point1 = null,
        [Description("Segundo ponto de origem (linear/aligned).")] P3? point2 = null,
        [Description("Ponto por onde passa a linha de cota (linear/aligned).")] P3? dimLinePoint = null,
        [Description("Rotação da cota linear em graus (0 = horizontal, 90 = vertical).")] double rotation = 0,
        [Description("Handle do círculo/arco (radial/diametric).")] string? entityHandle = null,
        [Description("Centro (radial/diametric sem entityHandle).")] P3? center = null,
        [Description("Raio (radial/diametric sem entityHandle).")] double? radius = null,
        [Description("Ângulo em graus do ponto da circunferência onde a cota encosta (radial/diametric). Padrão 45.")] double angle = 45,
        [Description("Estilo de cota. Omitido = corrente.")] string? dimStyle = null,
        [Description("Texto que substitui a medida (use <> para incluir a medida).")] string? textOverride = null,
        [Description(LayerDesc)] string? layer = null) =>
        ToolInvoker.Write("create_dimension", new { kind, point1, point2, dimLinePoint, rotation, entityHandle, center, radius, angle, dimStyle, textOverride, layer }, s =>
        {
            var styleId = Acad.DimStyleId(s, dimStyle);
            Dimension dim;
            switch (kind.Trim().ToLowerInvariant())
            {
                case "linear":
                case "rotated":
                    Require(point1, point2, dimLinePoint);
                    dim = new RotatedDimension(Acad.ToRad(rotation), point1!.ToPoint(), point2!.ToPoint(), dimLinePoint!.ToPoint(), textOverride ?? "", styleId);
                    break;
                case "aligned":
                    Require(point1, point2, dimLinePoint);
                    dim = new AlignedDimension(point1!.ToPoint(), point2!.ToPoint(), dimLinePoint!.ToPoint(), textOverride ?? "", styleId);
                    break;
                case "radial":
                case "diametric":
                case "diameter":
                    Point3d c;
                    double r;
                    if (!string.IsNullOrWhiteSpace(entityHandle))
                    {
                        var ent = Acad.OpenEntity(s, entityHandle, OpenMode.ForRead);
                        (c, r) = ent switch
                        {
                            Arc a => (a.Center, a.Radius),
                            Circle ci => (ci.Center, ci.Radius),
                            _ => throw new ArgumentException($"entityHandle {entityHandle} não é círculo nem arco."),
                        };
                    }
                    else if (center is not null && radius is > 0)
                    {
                        (c, r) = (center.ToPoint(), radius.Value);
                    }
                    else throw new ArgumentException("Para radial/diametric informe entityHandle ou center + radius.");

                    var dir = new Vector3d(Math.Cos(Acad.ToRad(angle)), Math.Sin(Acad.ToRad(angle)), 0);
                    var chord = c + dir * r;
                    dim = kind.StartsWith("radial", StringComparison.OrdinalIgnoreCase)
                        ? new RadialDimension(c, chord, r * 0.5, textOverride ?? "", styleId)
                        : new DiametricDimension(c - dir * r, chord, r * 0.5, textOverride ?? "", styleId);
                    break;
                default:
                    throw new ArgumentException("kind deve ser linear, aligned, radial ou diametric.");
            }

            var notes = new List<string>();
            Acad.AddToCurrentSpace(s, dim, Style.Of(layer, null, null, null), notes);
            dim.DimensionStyle = styleId;
            dim.RecomputeDimensionBlock(true);
            return Result(s, dim, notes);
        });

    [McpServerTool(Name = "create_table")]
    [Description("Cria uma tabela (TABLE) a partir de uma matriz de strings (linhas x colunas). A primeira linha pode ser cabeçalho.")]
    public static Task<CallToolResult> CreateTable(
        [Description("Canto superior esquerdo da tabela.")] P3 position,
        [Description("Matriz de células: data[linha][coluna]. Linhas mais curtas são completadas com vazio.")] string[][] data,
        [Description("Altura de cada linha. Omitida = 2.5 × altura do texto.")] double? rowHeight = null,
        [Description("Largura de cada coluna. Um único valor para todas, ou um por coluna. Omitida = automática pelo texto.")] double[]? columnWidths = null,
        [Description("Altura do texto. Omitida = TEXTSIZE.")] double? textHeight = null,
        [Description("Formatar a primeira linha como cabeçalho.")] bool headerRow = true,
        [Description("Estilo de tabela. Omitido = corrente.")] string? tableStyle = null,
        [Description(LayerDesc)] string? layer = null) =>
        ToolInvoker.Write("create_table", new { position, data, rowHeight, columnWidths, textHeight, headerRow, tableStyle, layer }, s =>
        {
            if (data is null || data.Length == 0) throw new ArgumentException("data está vazia.");
            var rows = data.Length;
            var cols = data.Max(r => r?.Length ?? 0);
            if (cols == 0) throw new ArgumentException("data não tem colunas.");

            var th = textHeight ?? s.Db.Textsize;
            var rh = rowHeight ?? th * 2.5;

            var table = new Table();
            var notes = new List<string>();
            Acad.AddToCurrentSpace(s, table, Style.Of(layer, null, null, null), notes);

            if (!string.IsNullOrWhiteSpace(tableStyle))
            {
                var dict = (DBDictionary)s.Tr.GetObject(s.Db.TableStyleDictionaryId, OpenMode.ForRead);
                if (!dict.Contains(tableStyle)) throw new ArgumentException($"Estilo de tabela '{tableStyle}' não existe.");
                table.TableStyle = dict.GetAt(tableStyle);
            }
            else table.TableStyle = s.Db.Tablestyle;

            table.Position = position.ToPoint();
            table.SetSize(rows, cols);

            // New tables come with a merged title row from the style; we want a plain grid.
            try { table.UnmergeCells(CellRange.Create(table, 0, 0, 0, cols - 1)); } catch { /* not merged */ }

            for (var r = 0; r < rows; r++)
            {
                table.Rows[r].Height = rh;
                for (var c = 0; c < cols; c++)
                {
                    var cell = table.Cells[r, c];
                    cell.Style = headerRow && r == 0 ? "_HEADER" : "_DATA";
                    cell.TextHeight = th;
                    cell.TextString = (c < (data[r]?.Length ?? 0) ? data[r][c] : null) ?? "";
                }
            }

            for (var c = 0; c < cols; c++)
            {
                double width;
                if (columnWidths is { Length: > 0 })
                    width = columnWidths.Length == 1 ? columnWidths[0] : columnWidths[Math.Min(c, columnWidths.Length - 1)];
                else
                {
                    var longest = data.Max(r => c < (r?.Length ?? 0) ? (r![c]?.Length ?? 0) : 0);
                    width = Math.Max(th * 6, longest * th * 0.9 + th * 2);
                }
                table.Columns[c].Width = width;
            }

            table.GenerateLayout();
            return Result(s, table, notes);
        });

    private static void Require(params P3?[] points)
    {
        if (points.Any(p => p is null))
            throw new ArgumentException("Informe point1, point2 e dimLinePoint.");
    }

    internal static AttachmentPoint ParseTextJustify(string? justify) =>
        (justify ?? "Left").Trim().ToLowerInvariant() switch
        {
            "left" or "baseleft" => AttachmentPoint.BaseLeft,
            "center" or "basecenter" => AttachmentPoint.BaseCenter,
            "right" or "baseright" => AttachmentPoint.BaseRight,
            "middle" or "basemid" => AttachmentPoint.BaseMid,
            "topleft" => AttachmentPoint.TopLeft,
            "topcenter" => AttachmentPoint.TopCenter,
            "topright" => AttachmentPoint.TopRight,
            "middleleft" => AttachmentPoint.MiddleLeft,
            "middlecenter" => AttachmentPoint.MiddleCenter,
            "middleright" => AttachmentPoint.MiddleRight,
            "bottomleft" => AttachmentPoint.BottomLeft,
            "bottomcenter" => AttachmentPoint.BottomCenter,
            "bottomright" => AttachmentPoint.BottomRight,
            _ => throw new ArgumentException($"justify inválido '{justify}'."),
        };

    private static AttachmentPoint ParseAttachment(string? attachment) =>
        (attachment ?? "TopLeft").Trim().ToLowerInvariant() switch
        {
            "topleft" => AttachmentPoint.TopLeft,
            "topcenter" => AttachmentPoint.TopCenter,
            "topright" => AttachmentPoint.TopRight,
            "middleleft" => AttachmentPoint.MiddleLeft,
            "middlecenter" => AttachmentPoint.MiddleCenter,
            "middleright" => AttachmentPoint.MiddleRight,
            "bottomleft" => AttachmentPoint.BottomLeft,
            "bottomcenter" => AttachmentPoint.BottomCenter,
            "bottomright" => AttachmentPoint.BottomRight,
            _ => throw new ArgumentException($"attachment inválido '{attachment}'."),
        };
}
