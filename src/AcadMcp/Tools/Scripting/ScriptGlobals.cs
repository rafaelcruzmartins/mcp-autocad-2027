using System.Text;
using AcadMcp.Infrastructure;
using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;

namespace AcadMcp.Tools.Scripting;

/// <summary>
/// Globals visible to execute_csharp scripts. Members are used directly by name in the script
/// (Doc, Db, Ed, Tr, Print, ...). Must be public for Roslyn.
/// </summary>
public sealed class ScriptGlobals
{
    private const int MaxOutputChars = 200_000;
    private readonly StringBuilder _output = new();
    private readonly DocScope _scope;

    internal ScriptGlobals(DocScope scope) => _scope = scope;

    /// <summary>Active document.</summary>
    public Document Doc => _scope.Doc;

    /// <summary>Active document database.</summary>
    public Database Db => _scope.Db;

    /// <summary>Active document editor.</summary>
    public Editor Ed => _scope.Ed;

    /// <summary>Open transaction (committed by the tool on success, aborted on exception). Do not Commit/Dispose it.</summary>
    public Transaction Tr => _scope.Tr;

    internal string Output => _output.ToString();

    /// <summary>Appends a line to the script output returned to the client.</summary>
    public void Print(object? value)
    {
        if (_output.Length >= MaxOutputChars) return;
        _output.AppendLine(value?.ToString() ?? "null");
        if (_output.Length >= MaxOutputChars) _output.AppendLine("... [saída truncada]");
    }

    /// <summary>ObjectId from a hexadecimal handle.</summary>
    public ObjectId Id(string handle) => Acad.IdFromHandle(Db, handle);

    /// <summary>Opens an object by handle inside <see cref="Tr"/>.</summary>
    public T Open<T>(string handle, OpenMode mode = OpenMode.ForRead) where T : DBObject =>
        (T)Tr.GetObject(Id(handle), mode);

    /// <summary>Model space record.</summary>
    public BlockTableRecord ModelSpace(OpenMode mode = OpenMode.ForRead) =>
        (BlockTableRecord)Tr.GetObject(SymbolUtilityServices.GetBlockModelSpaceId(Db), mode);

    /// <summary>Current space (model or active layout) record.</summary>
    public BlockTableRecord CurrentSpace(OpenMode mode = OpenMode.ForRead) =>
        (BlockTableRecord)Tr.GetObject(Db.CurrentSpaceId, mode);

    /// <summary>Appends a new entity to the current space (with DB defaults) and returns its handle.</summary>
    public string Add(Entity entity)
    {
        entity.SetDatabaseDefaults(Db);
        var space = CurrentSpace(OpenMode.ForWrite);
        space.AppendEntity(entity);
        Tr.AddNewlyCreatedDBObject(entity, true);
        return entity.Handle.ToString();
    }

    /// <summary>Degrees → radians.</summary>
    public double Rad(double degrees) => degrees * Math.PI / 180.0;

    /// <summary>Radians → degrees.</summary>
    public double Deg(double radians) => radians * 180.0 / Math.PI;
}
