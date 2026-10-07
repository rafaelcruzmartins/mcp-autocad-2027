using System.Net.Sockets;
using AcadMcp.Infrastructure;
using AcadMcp.Server;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp;

public sealed class Commands
{
    [CommandMethod("ACADMCP", "MCPSTART", CommandFlags.Modal)]
    public void McpStart()
    {
        if (!MainThread.IsReady) MainThread.Capture();
        StartServer();
    }

    [CommandMethod("ACADMCP", "MCPSTOP", CommandFlags.Modal)]
    public void McpStop()
    {
        if (!McpHost.IsRunning)
        {
            Message("\nAcadMcp: servidor já está parado.");
            return;
        }
        try
        {
            Task.Run(McpHost.StopAsync).Wait(TimeSpan.FromSeconds(10));
            Message("\nAcadMcp: servidor parado.");
        }
        catch (System.Exception ex)
        {
            Logger.Error("MCPSTOP falhou", ex);
            Message($"\nAcadMcp: erro ao parar: {ex.GetBaseException().Message}");
        }
    }

    [CommandMethod("ACADMCP", "MCPSTATUS", CommandFlags.Modal)]
    public void McpStatus()
    {
        var cfg = Config.Current;
        Message(McpHost.IsRunning
            ? $"\nAcadMcp: RODANDO em {McpHost.Url} desde {McpHost.StartedAt:HH:mm:ss}" +
              $"\n  chamadas: {ToolInvoker.Calls}, erros: {ToolInvoker.Errors}"
            : $"\nAcadMcp: PARADO (porta configurada {cfg.Port}). Use MCPSTART.");
        Message($"\n  config: {Config.FilePath}\n  log:    {Logger.CurrentFile}");
    }

    /// <summary>Starts Kestrel synchronously (bounded) so the result can be reported immediately.</summary>
    internal static void StartServer()
    {
        if (McpHost.IsRunning)
        {
            Message($"\nAcadMcp: já rodando em {McpHost.Url}");
            return;
        }

        var port = Config.Load().Port;
        try
        {
            // Kestrel startup does not need the UI thread, so blocking here cannot deadlock.
            if (!Task.Run(() => McpHost.StartAsync(port)).Wait(TimeSpan.FromSeconds(20)))
                throw new TimeoutException("Kestrel não iniciou em 20 s.");
            Message($"\nAcadMcp: servidor MCP em {McpHost.Url}");
        }
        catch (System.Exception ex)
        {
            var root = ex.GetBaseException();
            Logger.Error($"Falha ao iniciar na porta {port}", root);
            var hint = root is SocketException or IOException && root.Message.Contains("address", StringComparison.OrdinalIgnoreCase)
                ? $" A porta {port} já está em uso; mude 'Port' em {Config.FilePath} e rode MCPSTART."
                : " Veja o log.";
            Message($"\nAcadMcp: ERRO ao iniciar: {root.Message}.{hint}");
        }
    }

    internal static void Message(string text)
    {
        try { AcApp.DocumentManager.MdiActiveDocument?.Editor.WriteMessage(text); }
        catch { /* no document yet */ }
    }
}
