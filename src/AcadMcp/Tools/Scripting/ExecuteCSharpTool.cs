using System.ComponentModel;
using AcadMcp.Infrastructure;
using AcadMcp.Server;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace AcadMcp.Tools.Scripting;

[McpServerToolType]
public static class ExecuteCSharpTool
{
    private const string ToolDescription =
        "Compila (Roslyn, C# script) e executa código C# arbitrário na thread principal do AutoCAD, dentro de um lock do documento e de UMA transação já aberta. " +
        "Commit automático se terminar sem exceção; rollback se lançar exceção. A chamada inteira é um grupo de undo (U desfaz). " +
        "Globais: Doc (Document), Db (Database), Ed (Editor), Tr (Transaction aberta — NÃO chame Commit/Dispose), Print(object) acumula saída. " +
        "Helpers: Id(handle) -> ObjectId; Open<T>(handle, OpenMode) -> T; ModelSpace(mode); CurrentSpace(mode); Add(entity) -> handle (anexa ao espaço corrente); Rad(graus); Deg(radianos). " +
        "Usings já incluídos: System, System.Linq, System.Collections.Generic, System.Text, System.IO e Autodesk.AutoCAD.{ApplicationServices, DatabaseServices, EditorInput, Geometry, Colors}. " +
        "Use 'return valor;' no fim para devolver um resultado (ObjectId, Point3d, entidades, listas e objetos anônimos são convertidos para JSON). " +
        "Não use await (o código roda síncrono na thread do AutoCAD) nem loops infinitos (travam o AutoCAD). Para outras assemblies use #r \"caminho.dll\". " +
        "Scripts idênticos usam cache de compilação. Coordenadas em unidades do desenho (WCS), ângulos da API em radianos. " +
        "Exemplo: var c = new Circle(new Point3d(0,0,0), Vector3d.ZAxis, 50); var h = Add(c); Print($\"criado {h}\"); return h;";

    [McpServerTool(Name = "execute_csharp", Destructive = true, OpenWorld = true)]
    [Description(ToolDescription)]
    public static Task<CallToolResult> ExecuteCSharp(
        [Description("Código C# (script). Pode ter várias instruções e terminar com 'return ...;'.")] string code,
        [Description("false = sempre faz rollback no final (útil para consultas ou testes sem alterar o desenho). Padrão true.")] bool commit = true) =>
        ToolInvoker.Custom("execute_csharp", new { code, commit }, async () =>
        {
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("code está vazio.");

            // Compile off the main thread: it can take a second or two and must not freeze AutoCAD.
            var compiled = await Task.Run(() => ScriptEngine.GetOrCompile(code)).ConfigureAwait(false);

            return await MainThread.InvokeAsync(() =>
                DocScope.Run("MCP execute_csharp", write: true, s =>
                {
                    var globals = new ScriptGlobals(s);
                    object? returnValue;
                    try
                    {
                        var run = compiled.Script.RunAsync(globals);
                        if (!run.Wait(ToolInvoker.Timeout))
                            throw new TimeoutException(
                                "O script não terminou de forma síncrona. Não use await de operações que dependem da thread do AutoCAD.");
                        returnValue = run.Result.ReturnValue;
                    }
                    catch (Exception ex)
                    {
                        var inner = ex is AggregateException { InnerException: { } i } ? i : ex;
                        throw new ToolException(
                            $"Exceção no script ({inner.GetType().Name}): {inner.Message}. Transação revertida (rollback).",
                            new { output = globals.Output, rolledBack = true, scriptLine = ScriptLine(inner) },
                            inner);
                    }

                    return (object?)new
                    {
                        output = globals.Output,
                        returnValue = ScriptEngine.ToJson(returnValue, s.Tr),
                        committed = commit,
                        compiledFromCache = compiled.FromCache,
                        warnings = compiled.Warnings,
                        units = new { drawing = s.Db.Insunits.ToString(), coordinates = "WCS" },
                    };
                }, commit), ToolInvoker.Timeout).ConfigureAwait(false);
        });

    /// <summary>Line number inside the script from the stack trace (debug info is emitted).</summary>
    private static int? ScriptLine(Exception ex)
    {
        var trace = ex.StackTrace ?? "";
        var m = System.Text.RegularExpressions.Regex.Match(trace, @"execute_csharp\.csx:(?:line|linha) (\d+)");
        return m.Success ? int.Parse(m.Groups[1].Value) : null;
    }
}
