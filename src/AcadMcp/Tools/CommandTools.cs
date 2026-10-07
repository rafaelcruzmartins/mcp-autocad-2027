using System.ComponentModel;
using AcadMcp.Server;
using Autodesk.AutoCAD.DatabaseServices;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Tools;

[McpServerToolType]
public static class CommandTools
{
    [McpServerTool(Name = "run_command", Destructive = true, OpenWorld = true)]
    [Description("Executa um comando do AutoCAD (último recurso; prefira as ferramentas tipadas ou execute_csharp). " +
                 "Com wait=true (padrão) roda via Editor.CommandAsync e espera terminar: passe cada resposta de prompt como um item de 'args', " +
                 "\"\" = Enter, pontos como \"x,y\". Ex.: command=\"_.LINE\", args=[\"0,0\",\"100,0\",\"\"]. " +
                 "Retorna os handles das entidades criadas. Use prefixo _. para nomes em inglês independentes de idioma. " +
                 "Se o comando ficar esperando entrada, a chamada expira; o usuário precisa completar ou pressionar Esc. " +
                 "Com wait=false a linha é enfileirada (SendStringToExecute) e a ferramenta retorna sem esperar.")]
    public static Task<CallToolResult> RunCommand(
        [Description("Nome do comando, ex.: \"_.ZOOM\", \"_.PURGE\", \"_.-LAYER\".")] string command,
        [Description("Respostas aos prompts, em ordem. \"\" = Enter.")] string[]? args = null,
        [Description("Esperar o comando terminar (padrão true).")] bool wait = true)
    {
        if (string.IsNullOrWhiteSpace(command))
            return ToolInvoker.Raw("run_command", null, () => throw new ArgumentException("command vazio."));

        if (!wait)
        {
            return ToolInvoker.Raw("run_command", new { command, args, wait }, () =>
            {
                var doc = AcApp.DocumentManager.MdiActiveDocument
                          ?? throw new InvalidOperationException("Nenhum desenho aberto no AutoCAD.");
                // On the command line a space acts as Enter, so "" args become consecutive spaces.
                var line = string.Join(" ", new[] { command }.Concat(args ?? []));
                doc.SendStringToExecute(line + "\n", true, false, true);
                return new { queued = true, line, note = "Enfileirado; o resultado não é aguardado." };
            });
        }

        return ToolInvoker.CommandContext("run_command", new { command, args, wait }, async () =>
        {
            var doc = AcApp.DocumentManager.MdiActiveDocument!;
            var db = doc.Database;
            var seedBefore = db.Handseed.Value;
            var currentSpace = db.CurrentSpaceId;

            object[] tokens = [command, .. (args ?? [])];
            await doc.Editor.CommandAsync(tokens);

            // Anything with a handle above the pre-command seed was created by the command.
            var created = new List<object>();
            using (var tr = db.TransactionManager.StartOpenCloseTransaction())
            {
                var space = (BlockTableRecord)tr.GetObject(db.CurrentSpaceId, OpenMode.ForRead);
                foreach (ObjectId id in space)
                {
                    if (id.Handle.Value >= seedBefore && !id.IsErased)
                        created.Add(new { handle = id.Handle.ToString(), type = id.ObjectClass.DxfName });
                }
                tr.Commit();
            }

            return new
            {
                command,
                args,
                createdCount = created.Count,
                created = created.Take(500),
                currentSpaceChanged = currentSpace != db.CurrentSpaceId,
                commandStillActive = string.IsNullOrEmpty(doc.CommandInProgress) ? null : doc.CommandInProgress,
            };
        });
    }
}
