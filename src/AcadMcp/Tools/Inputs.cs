using System.ComponentModel;
using Autodesk.AutoCAD.Geometry;

namespace AcadMcp.Tools;

/// <summary>Point or vector in WCS, drawing units. Z defaults to 0.</summary>
[Description("Ponto/vetor em WCS, unidades do desenho. z é opcional (padrão 0).")]
public sealed record P3(double X, double Y, double Z = 0)
{
    public Point3d ToPoint() => new(X, Y, Z);
    public Vector3d ToVector() => new(X, Y, Z);
}

[Description("Vértice de polilinha 2D. bulge = tan(ângulo do arco / 4) do segmento que começa neste vértice; 0 = segmento reto; positivo = anti-horário.")]
public sealed record PolyVertex(double X, double Y, double Bulge = 0);

[Description("Retângulo alinhado aos eixos em WCS (min = canto inferior esquerdo, max = superior direito).")]
public sealed record Rect(P3 Min, P3 Max);
