using AcadMcp.Infrastructure;
using AcadMcp.Server;
using Autodesk.AutoCAD.Runtime;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

[assembly: ExtensionApplication(typeof(AcadMcp.Plugin))]
[assembly: CommandClass(typeof(AcadMcp.Commands))]

namespace AcadMcp;

public sealed class Plugin : IExtensionApplication
{
    public void Initialize()
    {
        try
        {
            Logger.Info($"AcadMcp {typeof(Plugin).Assembly.GetName().Version} carregado em {typeof(Plugin).Assembly.Location}" +
                        $" (runtime {Environment.Version}, STJ reflection={System.Text.Json.JsonSerializer.IsReflectionEnabledByDefault})");
            Config.Load();
            // SynchronizationContext.Current may still be null this early at startup; the first
            // Idle is guaranteed to run on the main thread with a context installed.
            AcApp.Idle += OnFirstIdle;
        }
        catch (System.Exception ex)
        {
            Logger.Error("Falha no Initialize", ex);
        }
    }

    private static void OnFirstIdle(object? sender, EventArgs e)
    {
        AcApp.Idle -= OnFirstIdle;
        try
        {
            if (!MainThread.Capture())
            {
                Logger.Error("SynchronizationContext nulo no Idle; servidor não iniciado.");
                Commands.Message("\nAcadMcp: não foi possível capturar a thread principal. Veja o log.");
                return;
            }

            if (Config.Current.AutoStart)
                Commands.StartServer();

            // First Roslyn compilation takes seconds; do it in the background now.
            Tools.Scripting.ScriptEngine.Warmup();
        }
        catch (System.Exception ex)
        {
            Logger.Error("Falha no auto-start", ex);
        }
    }

    public void Terminate()
    {
        try
        {
            // acad.exe is shutting down: give Kestrel a short, bounded chance to close sockets.
            Task.Run(McpHost.StopAsync).Wait(TimeSpan.FromSeconds(3));
        }
        catch (System.Exception ex)
        {
            Logger.Error("Falha no Terminate", ex);
        }
    }
}
