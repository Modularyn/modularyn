namespace Modularyn.Framework.Hosting;

/// <summary>
/// Admission and draining for work that uses modules. HTTP middleware and background
/// consumers share this gate. Hold a lease until the entire operation has finished.
/// </summary>
public sealed class ModuleWorkGate : IDisposable
{
    private readonly object sync = new();
    private bool accepting;
    private int active;
    private TaskCompletionSource drained = Completed();
    private CancellationTokenSource deadline = new();

    public bool IsAccepting { get { lock (sync) return accepting; } }
    public int ActiveCount { get { lock (sync) return active; } }

    public bool TryEnter(out ModuleWorkLease? lease)
    {
        lock (sync)
        {
            lease = null;
            if (!accepting) return false;
            if (active++ == 0) drained = new(TaskCreationOptions.RunContinuationsAsynchronously);
            lease = new(this, deadline.Token);
            return true;
        }
    }

    internal void Open(CancellationToken applicationStopping)
    {
        lock (sync)
        {
            if (active != 0) throw new InvalidOperationException("Work from the previous activation still requires cleanup.");
            if (deadline.IsCancellationRequested)
            {
                deadline.Dispose();
                deadline = new();
            }
            accepting = !applicationStopping.IsCancellationRequested;
        }
    }

    /// <summary>Atomically refuses new leases while existing leases drain.</summary>
    public void StopAccepting() { lock (sync) accepting = false; }

    internal Task DrainAsync(CancellationToken cancellationToken)
    {
        lock (sync) return drained.Task.WaitAsync(cancellationToken);
    }

    internal void CancelRemaining()
    {
        // Callback failures cannot prevent notification of the remaining work or module cleanup.
        try { deadline.Cancel(); }
        catch (AggregateException) { }
    }

    internal void Exit()
    {
        lock (sync)
        {
            if (--active == 0) drained.TrySetResult();
        }
    }

    public void Dispose()
    {
        StopAccepting();
        CancelRemaining();
        deadline.Dispose();
    }

    private static TaskCompletionSource Completed()
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        completion.SetResult();
        return completion;
    }
}

public sealed class ModuleWorkLease : IDisposable
{
    private ModuleWorkGate? owner;
    internal ModuleWorkLease(ModuleWorkGate owner, CancellationToken cancellationToken)
    {
        this.owner = owner;
        CancellationToken = cancellationToken;
    }

    /// <summary>Cancelled when the drain deadline expires; normal draining leaves work running.</summary>
    public CancellationToken CancellationToken { get; }
    public void Dispose() => Interlocked.Exchange(ref owner, null)?.Exit();
}
