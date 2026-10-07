using System.Diagnostics;
using AcadMcp.Infrastructure;
using ModelContextProtocol.Protocol;

namespace AcadMcp.Server;

/// <summary>An error with a clear message and optional structured details for the client.</summary>
internal sealed class ToolException(string message, object? details = null, Exception? inner = null)
    : Exception(message, inner)
{
    public object? Details { get; } = details;
}

/// <summary>
/// Single entry point for every MCP tool: dispatches to the AutoCAD main thread, opens the
/// document scope, logs call/params/duration/errors, and converts the outcome into a JSON
/// CallToolResult. Exceptions never escape (and never reach AutoCAD).
/// </summary>
internal static class ToolInvoker
{
    private static long _calls;
    private static long _errors;

    public static long Calls => Interlocked.Read(ref _calls);
    public static long Errors => Interlocked.Read(ref _errors);

    public static TimeSpan Timeout => TimeSpan.FromSeconds(Config.Current.CallTimeoutSeconds);

    /// <summary>Read-only tool: document locked for read, transaction committed (no changes expected).</summary>
    public static Task<CallToolResult> Read(string tool, object? args, Func<DocScope, object?> body) =>
        Run(tool, args, () => MainThread.InvokeAsync(
            () => DocScope.Run(tool, write: false, s => Tag(s, body)), Timeout));

    /// <summary>Modifying tool: one document lock + transaction = one undo group named after the tool.</summary>
    public static Task<CallToolResult> Write(string tool, object? args, Func<DocScope, object?> body, bool commit = true) =>
        Run(tool, args, () => MainThread.InvokeAsync(
            () => DocScope.Run($"MCP {tool}", write: true, s => Tag(s, body), commit), Timeout));

    /// <summary>Arbitrary main-thread work (no automatic document scope).</summary>
    public static Task<CallToolResult> Raw(string tool, object? args, Func<object?> body) =>
        Run(tool, args, () => MainThread.InvokeAsync(body, Timeout));

    /// <summary>
    /// Main-thread work inside a command context (ExecuteInCommandContextAsync). The whole call is
    /// bounded by the timeout because a command may sit waiting for user input.
    /// </summary>
    public static Task<CallToolResult> CommandContext(string tool, object? args, Func<Task<object?>> body) =>
        Run(tool, args, async () =>
        {
            var task = MainThread.InvokeInCommandContextAsync(body, Timeout);
            try { return await task.WaitAsync(Timeout).ConfigureAwait(false); }
            catch (TimeoutException) when (!task.IsCompleted)
            {
                throw new ToolException(
                    $"O comando não terminou em {Timeout.TotalSeconds:0}s. Provavelmente está aguardando entrada no AutoCAD: " +
                    "complete-o ou pressione Esc na linha de comando.");
            }
        });

    /// <summary>Fully custom dispatch (used by execute_csharp to compile off the main thread first).</summary>
    public static Task<CallToolResult> Custom(string tool, object? args, Func<Task<object?>> dispatch) =>
        Run(tool, args, dispatch);

    private static object? Tag(DocScope s, Func<DocScope, object?> body) =>
        new UnitsTagged(s.Db.Insunits.ToString(), body(s));

    private sealed record UnitsTagged(string Insunits, object? Value);

    private static async Task<CallToolResult> Run(string tool, object? args, Func<Task<object?>> dispatch)
    {
        var id = Interlocked.Increment(ref _calls);
        var argsJson = SafeSerialize(args);
        Logger.Info($"#{id} -> {tool} {Truncate(argsJson, 4000)}");
        var sw = Stopwatch.StartNew();

        try
        {
            var result = await dispatch().ConfigureAwait(false);
            sw.Stop();
            Logger.Info($"#{id} <- {tool} OK {sw.ElapsedMilliseconds} ms");

            object? units = null;
            if (result is UnitsTagged tagged)
            {
                units = new { drawing = tagged.Insunits, coordinates = "WCS", angles = "degrees" };
                result = tagged.Value;
            }
            return Ok(new { ok = true, tool, durationMs = sw.ElapsedMilliseconds, units, result });
        }
        catch (Exception ex)
        {
            sw.Stop();
            Interlocked.Increment(ref _errors);
            var root = ex is AggregateException { InnerException: { } inner } ? inner : ex;
            Logger.Error($"#{id} <- {tool} FALHOU {sw.ElapsedMilliseconds} ms {Truncate(argsJson, 4000)}", root);
            return Error(tool, sw.ElapsedMilliseconds, root);
        }
    }

    private static CallToolResult Ok(object payload) => new()
    {
        Content = [new TextContentBlock { Text = SafeSerialize(payload) }],
    };

    private static CallToolResult Error(string tool, long ms, Exception ex)
    {
        // For ToolException the interesting type/stack is the wrapped one (e.g. the script's exception).
        var shown = ex is ToolException { InnerException: { } inner } ? inner : ex;
        var payload = new
        {
            ok = false,
            tool,
            durationMs = ms,
            error = new
            {
                type = shown.GetType().FullName,
                message = ex.Message,
                // AutoCAD exceptions carry an ErrorStatus that is more useful than the message.
                errorStatus = shown is Autodesk.AutoCAD.Runtime.Exception acEx ? acEx.ErrorStatus.ToString() : null,
                details = (ex as ToolException)?.Details,
                stackTrace = shown is ToolException or ArgumentException ? null : shown.StackTrace,
            },
        };
        return new CallToolResult
        {
            IsError = true,
            Content = [new TextContentBlock { Text = SafeSerialize(payload) }],
        };
    }

    private static string SafeSerialize(object? value)
    {
        try { return Fmt.Serialize(value); }
        catch (Exception ex) { return $"{{\"ok\":false,\"serializationError\":{Fmt.Serialize(ex.Message)}}}"; }
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + $"... (+{s.Length - max} chars)";
}
