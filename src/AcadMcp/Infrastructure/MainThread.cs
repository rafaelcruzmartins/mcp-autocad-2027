using AcApp = Autodesk.AutoCAD.ApplicationServices.Application;

namespace AcadMcp.Infrastructure;

/// <summary>
/// Marshals work onto AutoCAD's main (UI) thread. The AutoCAD API is not thread-safe, so every
/// tool body runs through here. The SynchronizationContext is captured on the main thread
/// (first Application.Idle after load, where it is guaranteed to exist).
/// </summary>
internal static class MainThread
{
    private static SynchronizationContext? _context;
    private static int _threadId;

    public static bool IsReady => _context is not null;

    public static bool IsCurrent => _context is not null && Environment.CurrentManagedThreadId == _threadId;

    /// <summary>Must be called on the AutoCAD main thread.</summary>
    public static bool Capture()
    {
        var ctx = SynchronizationContext.Current;
        if (ctx is null) return false;
        _context = ctx;
        _threadId = Environment.CurrentManagedThreadId;
        Logger.Info($"Main thread capturada: T{_threadId}, contexto {ctx.GetType().FullName}");
        return true;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the main thread in application context. If the main thread
    /// does not pick it up within <paramref name="timeout"/>, the work is cancelled (never runs late).
    /// </summary>
    public static Task<T> InvokeAsync<T>(Func<T> work, TimeSpan timeout)
    {
        var ctx = _context ?? throw new InvalidOperationException(
            "A thread principal do AutoCAD ainda não foi capturada (o plugin terminou de carregar?).");

        if (IsCurrent)
        {
            try { return Task.FromResult(work()); }
            catch (Exception ex) { return Task.FromException<T>(ex); }
        }

        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        // 0 = pending, 1 = started, 2 = timed out before start.
        var state = 0;

        ctx.Post(_ =>
        {
            if (Interlocked.CompareExchange(ref state, 1, 0) != 0) return;
            try { tcs.TrySetResult(work()); }
            catch (Exception ex) { tcs.TrySetException(ex); }
        }, null);

        _ = Task.Delay(timeout).ContinueWith(_ =>
        {
            if (Interlocked.CompareExchange(ref state, 2, 0) == 0)
                tcs.TrySetException(new TimeoutException(
                    $"O AutoCAD não ficou disponível em {timeout.TotalSeconds:0}s (diálogo modal aberto ou thread ocupada). Nada foi executado."));
        }, TaskScheduler.Default);

        return tcs.Task;
    }

    /// <summary>
    /// Runs <paramref name="work"/> on the main thread inside a command context
    /// (DocumentManager.ExecuteInCommandContextAsync), needed e.g. for Editor.Command.
    /// </summary>
    public static Task<T> InvokeInCommandContextAsync<T>(Func<Task<T>> work, TimeSpan timeout)
    {
        return InvokeAsync(() =>
        {
            var inner = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
            var dm = AcApp.DocumentManager;
            if (dm.MdiActiveDocument is null)
                throw new InvalidOperationException("Nenhum desenho aberto no AutoCAD.");
            if (!dm.IsApplicationContext)
                throw new InvalidOperationException("O AutoCAD não está em contexto de aplicação; tente de novo em instantes.");

            dm.ExecuteInCommandContextAsync(async _ =>
            {
                try { inner.TrySetResult(await work()); }
                catch (Exception ex) { inner.TrySetException(ex); }
            }, null);
            return inner.Task;
        }, timeout).Unwrap();
    }
}
