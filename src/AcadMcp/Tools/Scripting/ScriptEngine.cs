using System.Collections;
using System.Collections.Concurrent;
using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AcadMcp.Infrastructure;
using AcadMcp.Server;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.Geometry;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp.Scripting;
using Microsoft.CodeAnalysis.Scripting;

namespace AcadMcp.Tools.Scripting;

/// <summary>Roslyn compilation with a hash-keyed cache, plus conversion of script results to JSON-friendly values.</summary>
internal static class ScriptEngine
{
    private const int MaxCacheEntries = 200;
    private static readonly ConcurrentDictionary<string, Script<object>> Cache = new();
    private static readonly Lazy<ScriptOptions> Options = new(BuildOptions, LazyThreadSafetyMode.ExecutionAndPublication);

    public static readonly string[] Imports =
    [
        "System", "System.Collections.Generic", "System.Linq", "System.Text", "System.IO",
        "Autodesk.AutoCAD.ApplicationServices", "Autodesk.AutoCAD.DatabaseServices",
        "Autodesk.AutoCAD.EditorInput", "Autodesk.AutoCAD.Geometry", "Autodesk.AutoCAD.Colors",
    ];

    public sealed record Compiled(Script<object> Script, bool FromCache, List<object>? Warnings);

    /// <summary>Compiles (or fetches from cache). Throws ToolException with diagnostics on errors. Thread-safe; call off the main thread.</summary>
    public static Compiled GetOrCompile(string code)
    {
        var key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(code)));
        if (Cache.TryGetValue(key, out var cached))
            return new Compiled(cached, true, null);

        var script = CSharpScript.Create<object>(code, Options.Value, typeof(ScriptGlobals));
        var diagnostics = script.Compile();

        static object Diag(Diagnostic d)
        {
            var span = d.Location.GetLineSpan();
            return new
            {
                id = d.Id,
                severity = d.Severity.ToString(),
                message = d.GetMessage(),
                line = span.StartLinePosition.Line + 1,
                column = span.StartLinePosition.Character + 1,
            };
        }

        var errors = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Error).ToList();
        if (errors.Count > 0)
            throw new ToolException(
                $"Erro de compilação ({errors.Count}): {errors[0].GetMessage()}",
                new { compilationErrors = errors.Select(Diag).ToList() });

        if (Cache.Count >= MaxCacheEntries) Cache.Clear();
        Cache[key] = script;

        var warnings = diagnostics.Where(d => d.Severity == DiagnosticSeverity.Warning).Select(Diag).ToList();
        return new Compiled(script, false, warnings.Count > 0 ? warnings : null);
    }

    /// <summary>Compiles a trivial script once in the background so the first real call is fast.</summary>
    public static void Warmup() => Task.Run(() =>
    {
        try { GetOrCompile("return 1;"); Logger.Info("Roslyn aquecido."); }
        catch (Exception ex) { Logger.Error("Falha ao aquecer Roslyn", ex); }
    });

    private static ScriptOptions BuildOptions()
    {
        // Seed assemblies plus everything they reference directly: enough for the AutoCAD API
        // and the BCL without dragging every assembly loaded in acad.exe into each compilation.
        Type[] seeds =
        [
            typeof(object), typeof(Enumerable), typeof(List<>), typeof(ConcurrentDictionary<,>), typeof(Regex),
            typeof(Console), typeof(JsonSerializer), typeof(System.Linq.Expressions.Expression),
            typeof(Microsoft.CSharp.RuntimeBinder.Binder), typeof(System.ComponentModel.Component),
            typeof(Database), typeof(Autodesk.AutoCAD.ApplicationServices.Document),
            typeof(Autodesk.AutoCAD.ApplicationServices.Application), typeof(ScriptGlobals),
        ];

        var byName = new Dictionary<string, Assembly>(StringComparer.OrdinalIgnoreCase);
        void Add(Assembly a)
        {
            if (a.IsDynamic || string.IsNullOrEmpty(a.Location)) return;
            var name = a.GetName().Name ?? a.Location;
            if (!byName.TryGetValue(name, out var existing) || existing.GetName().Version < a.GetName().Version)
                byName[name] = a;
        }

        foreach (var seed in seeds.Select(t => t.Assembly).Distinct())
        {
            Add(seed);
            foreach (var reference in seed.GetReferencedAssemblies())
            {
                try { Add(Assembly.Load(reference)); }
                catch { /* optional reference not available in this process */ }
            }
        }

        Logger.Info($"execute_csharp: {byName.Count} assemblies referenciadas.");
        return ScriptOptions.Default
            .WithReferences(byName.Values.Select(a => MetadataReference.CreateFromFile(a.Location)))
            .WithImports(Imports)
            .WithEmitDebugInformation(true)
            .WithFilePath("execute_csharp.csx")
            .WithFileEncoding(Encoding.UTF8)
            .WithOptimizationLevel(OptimizationLevel.Debug)
            .WithAllowUnsafe(false);
    }

    // ------------------------------------------------------------------ results

    /// <summary>Converts an arbitrary script return value into JSON-friendly data. Main thread only.</summary>
    public static object? ToJson(object? v, Transaction tr, int depth = 0)
    {
        if (v is null) return null;
        if (depth > 6) return v.ToString();

        switch (v)
        {
            case string or bool or char or decimal:
            case byte or sbyte or short or ushort or int or uint or long or ulong or float:
                return v;
            case double d:
                return double.IsFinite(d) ? d : d.ToString();
            case Enum e:
                return e.ToString();
            case ObjectId id:
                return id.IsNull ? null : new { handle = id.Handle.ToString(), type = id.ObjectClass.DxfName };
            case Handle h:
                return h.ToString();
            case Point3d p:
                return Fmt.Pt(p);
            case Point2d p2:
                return Fmt.Pt(p2);
            case Vector3d vec:
                return Fmt.Vec(vec);
            case Entity ent:
                return ent.IsDisposed ? ent.ToString() : EntityInfo.Summary(tr, ent);
            case DBObject obj:
                return new { handle = obj.Handle.ToString(), type = obj.GetRXClass().DxfName };
            case IDictionary dict:
            {
                var result = new Dictionary<string, object?>();
                foreach (DictionaryEntry kv in dict)
                {
                    if (result.Count >= 1000) break;
                    result[kv.Key.ToString() ?? ""] = ToJson(kv.Value, tr, depth + 1);
                }
                return result;
            }
            case IEnumerable seq:
            {
                var list = new List<object?>();
                foreach (var item in seq)
                {
                    if (list.Count >= 1000) { list.Add("... (truncado em 1000 itens)"); break; }
                    list.Add(ToJson(item, tr, depth + 1));
                }
                return list;
            }
        }

        var type = v.GetType();
        // AutoCAD/other framework objects: their property graphs are huge or unsafe to walk.
        if (type.Namespace?.StartsWith("Autodesk", StringComparison.Ordinal) == true ||
            type.Namespace?.StartsWith("System", StringComparison.Ordinal) == true)
            return v.ToString();

        // User POCOs / anonymous types / records: walk public properties.
        var props = type.GetProperties(BindingFlags.Public | BindingFlags.Instance)
            .Where(p => p.CanRead && p.GetIndexParameters().Length == 0)
            .Take(100);
        var map = new Dictionary<string, object?>();
        foreach (var p in props)
        {
            try { map[JsonNamingPolicy.CamelCase.ConvertName(p.Name)] = ToJson(p.GetValue(v), tr, depth + 1); }
            catch (Exception ex) { map[p.Name] = $"<erro: {ex.GetBaseException().Message}>"; }
        }
        return map;
    }
}
