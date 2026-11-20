using System.Diagnostics;

namespace Mvec;

internal sealed class IdleDeadline : IAsyncDisposable
{
    private readonly object sync = new();
    private readonly TimeSpan timeout;
    private readonly CancellationTokenSource listener;
    private readonly Timer? timer;
    private long idleSince = Stopwatch.GetTimestamp();
    private int active;
    private bool retiring;

    public IdleDeadline(TimeSpan timeout, CancellationTokenSource listener)
    {
        this.timeout = timeout;
        this.listener = listener;
        if (timeout == Timeout.InfiniteTimeSpan)
            return;
        timer = new Timer(_ => Expire());
        timer.Change(timeout, Timeout.InfiniteTimeSpan);
    }

    public void Enter()
    {
        if (timer is not null)
            lock (sync) active++;
    }

    public void Leave()
    {
        if (timer is null)
            return;
        lock (sync)
        {
            if (--active != 0 || retiring)
                return;
            idleSince = Stopwatch.GetTimestamp();
            timer.Change(timeout, Timeout.InfiniteTimeSpan);
        }
    }

    private void Expire()
    {
        lock (sync)
        {
            if (active != 0 || retiring)
                return;
            var remaining = timeout - Stopwatch.GetElapsedTime(idleSince);
            if (remaining > TimeSpan.Zero)
            {
                timer!.Change(remaining, Timeout.InfiniteTimeSpan);
                return;
            }
            retiring = true;
        }
        // Stop accepting first; existing requests and their replies still drain.
        listener.Cancel();
    }

    public ValueTask DisposeAsync() => timer?.DisposeAsync() ?? ValueTask.CompletedTask;
}
