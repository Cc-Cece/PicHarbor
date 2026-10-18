namespace PicHarbor.Core.Transfer;

/// <summary>Cooperative pause at safe file boundaries, independently cancellable by stopping the task.</summary>
public sealed class TransferPauseGate
{
    private readonly object sync = new();
    private TaskCompletionSource? resume;
    private bool paused;

    /// <summary>Raised when a pause is requested, acknowledged, or resumed.</summary>
    public event Action? StateChanged;
    /// <summary>Whether a pause has been requested.</summary>
    public bool IsPauseRequested { get { lock (sync) return resume is not null; } }
    /// <summary>Whether execution has reached a safe pause boundary.</summary>
    public bool IsPaused { get { lock (sync) return paused; } }

    /// <summary>Requests suspension at the next checkpoint.</summary>
    public void Pause()
    {
        lock (sync)
        {
            if (resume is not null) return;
            resume = new(TaskCreationOptions.RunContinuationsAsynchronously);
        }
        StateChanged?.Invoke();
    }

    /// <summary>Releases the checkpoint, retaining the task's existing state.</summary>
    public void Resume()
    {
        TaskCompletionSource? pending;
        lock (sync)
        {
            pending = resume;
            resume = null;
            paused = false;
        }
        pending?.TrySetResult();
        if (pending is not null) StateChanged?.Invoke();
    }

    /// <summary>Waits at a safe boundary. Stop cancellation interrupts even a paused task.</summary>
    public async Task WaitAsync(CancellationToken cancellationToken = default,
        Func<CancellationToken, Task>? keepAlive = null)
    {
        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            Task? wait;
            bool changed = false;
            lock (sync)
            {
                wait = resume?.Task;
                if (wait is not null && !paused) { paused = true; changed = true; }
            }
            if (changed) StateChanged?.Invoke();
            if (wait is null) return;
            if (keepAlive is null)
                await wait.WaitAsync(cancellationToken).ConfigureAwait(false);
            else
            {
                try { await wait.WaitAsync(TimeSpan.FromSeconds(20), cancellationToken).ConfigureAwait(false); }
                catch (TimeoutException)
                {
                    // Only the task's owning worker sends keep-alives, between files.
                    // Never interleave FTP control commands with an active transfer.
                    if (IsPauseRequested) await keepAlive(cancellationToken).ConfigureAwait(false);
                }
            }
        }
    }
}
