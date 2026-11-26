using System.Collections.Concurrent;

namespace DeskRecall.Platform.VirtualDesktops;

/// <summary>
/// Eigener STA-Thread für COM-Aufrufe in die Shell (explorer.exe). So blockiert ein hängender
/// Explorer nie die Oberfläche: Aufrufe haben eine Zeitbegrenzung.
/// </summary>
public sealed class ComThread : IDisposable
{
    private readonly BlockingCollection<Action> _queue = new();
    // Vorrang für Benutzeraktionen (Desktop wechseln): wird vor wartenden Hintergrund-Abfragen ausgeführt.
    private readonly BlockingCollection<Action> _urgent = new();
    private readonly Thread _thread;

    public ComThread(string name)
    {
        _thread = new Thread(Run) { IsBackground = true, Name = name };
        _thread.SetApartmentState(ApartmentState.STA);
        _thread.Start();
    }

    private void Run()
    {
        var queues = new[] { _urgent, _queue };
        while (true)
        {
            Action action;
            try { BlockingCollection<Action>.TakeFromAny(queues, out action!); }
            catch (ArgumentException) { return; }        // beide Warteschlangen abgeschlossen
            catch (InvalidOperationException) { return; }
            try { action(); } catch { /* Fehler werden über die TaskCompletionSource gemeldet */ }
        }
    }

    public bool IsCurrent => Thread.CurrentThread == _thread;

    public T Invoke<T>(Func<T> func, TimeSpan? timeout = null, bool urgent = false)
    {
        if (IsCurrent) return func();
        var tcs = new TaskCompletionSource<T>(TaskCreationOptions.RunContinuationsAsynchronously);
        (urgent ? _urgent : _queue).Add(() =>
        {
            try { tcs.SetResult(func()); }
            catch (Exception ex) { tcs.SetException(ex); }
        });
        if (!((IAsyncResult)tcs.Task).AsyncWaitHandle.WaitOne(timeout ?? TimeSpan.FromSeconds(5)))
            throw new TimeoutException("Die Windows-Shell antwortet nicht (Zeitüberschreitung).");
        return tcs.Task.GetAwaiter().GetResult();
    }

    public void Invoke(Action action, TimeSpan? timeout = null, bool urgent = false) => Invoke(() => { action(); return true; }, timeout, urgent);

    public void Dispose() { _urgent.CompleteAdding(); _queue.CompleteAdding(); }
}
