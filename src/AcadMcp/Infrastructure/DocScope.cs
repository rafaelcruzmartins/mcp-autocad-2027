using Autodesk.AutoCAD.ApplicationServices;
using Autodesk.AutoCAD.DatabaseServices;
using Autodesk.AutoCAD.EditorInput;
using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Infrastructure;

/// <summary>
/// Everything a tool body needs, already set up: the active document, a document lock and an
/// open transaction. Must be used on the AutoCAD main thread (see <see cref="MainThread"/>).
/// </summary>
internal sealed class DocScope
{
    public Document Doc { get; }
    public Database Db { get; }
    public Editor Ed { get; }
    public Transaction Tr { get; }

    private DocScope(Document doc, Transaction tr)
    {
        Doc = doc;
        Db = doc.Database;
        Ed = doc.Editor;
        Tr = tr;
    }

    /// <summary>
    /// Locks the active document, opens a transaction and runs <paramref name="body"/>.
    /// Commits on success (unless <paramref name="commit"/> is false) and aborts on any exception.
    /// For writes, the lock carries <paramref name="undoName"/> so the whole call is a single undo group.
    /// </summary>
    public static T Run<T>(string undoName, bool write, Func<DocScope, T> body, bool commit = true)
    {
        var doc = AcApp.DocumentManager.MdiActiveDocument
                  ?? throw new InvalidOperationException("Nenhum desenho aberto no AutoCAD.");

        if (write && !string.IsNullOrEmpty(doc.CommandInProgress))
            throw new InvalidOperationException(
                $"O AutoCAD está executando o comando '{doc.CommandInProgress}'. Finalize ou cancele (Esc) e tente de novo.");

        using var docLock = write
            ? doc.LockDocument(DocumentLockMode.Write, undoName, undoName, false)
            : doc.LockDocument(DocumentLockMode.Read, null, null, false);

        T result;
        using (var tr = doc.TransactionManager.StartTransaction())
        {
            result = body(new DocScope(doc, tr));
            // Committing is also the cheapest way to close a read-only transaction.
            if (commit) tr.Commit();
            else tr.Abort();
        }

        if (write && commit)
            doc.TransactionManager.FlushGraphics();

        return result;
    }
}
