namespace Hall9k.Tests.Fakes;

/// <summary>
/// A clock a test steps by hand: <see cref="Task.Delay(TimeSpan, TimeProvider, CancellationToken)"/>
/// registers a timer here and waits until <see cref="FireNextAsync"/> fires it, which moves the
/// clock to that timer's due time. A test therefore asserts the delay a loop asked for rather than
/// sleeping through it, and there is no real wait anywhere.
/// </summary>
public sealed class ManualTimeProvider(DateTimeOffset start) : TimeProvider
{
    private readonly object gate = new();
    private readonly List<ManualTimer> timers = [];
    private DateTimeOffset now = start;
    private TaskCompletionSource changed = NewSignal();

    public override DateTimeOffset GetUtcNow()
    {
        lock (gate)
        {
            return now;
        }
    }

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ManualTimer timer = new(this, callback, state);
        lock (gate)
        {
            timers.Add(timer);
        }

        timer.Change(dueTime, period);
        return timer;
    }

    /// <summary>The delay of the next timer to fire, waiting until a loop has asked for one.</summary>
    public async Task<TimeSpan> NextDelayAsync(CancellationToken cancellationToken)
    {
        while (true)
        {
            Task signal;
            lock (gate)
            {
                if (Earliest() is { } next)
                {
                    return next.DueAt!.Value - now;
                }

                signal = changed.Task;
            }

            await signal.WaitAsync(cancellationToken);
        }
    }

    /// <summary>Moves the clock to the next timer's due time and fires it, returning the delay that elapsed.</summary>
    public async Task<TimeSpan> FireNextAsync(CancellationToken cancellationToken)
    {
        TimeSpan delay = await NextDelayAsync(cancellationToken);
        ManualTimer timer;
        lock (gate)
        {
            timer = Earliest() ?? throw new InvalidOperationException("No timer is pending.");
            now = timer.DueAt!.Value;
            timer.DueAt = null;
        }

        timer.Fire();
        return delay;
    }

    private ManualTimer? Earliest() => timers.Where(timer => timer.DueAt is not null).MinBy(timer => timer.DueAt);

    private void Signal()
    {
        TaskCompletionSource previous;
        lock (gate)
        {
            previous = changed;
            changed = NewSignal();
        }

        previous.TrySetResult();
    }

    private static TaskCompletionSource NewSignal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback callback, object? state) : ITimer
    {
        public DateTimeOffset? DueAt { get; set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner.gate)
            {
                DueAt = dueTime == Timeout.InfiniteTimeSpan ? null : owner.now + dueTime;
            }

            owner.Signal();
            return true;
        }

        public void Fire() => callback(state);

        public void Dispose()
        {
            lock (owner.gate)
            {
                DueAt = null;
                owner.timers.Remove(this);
            }

            owner.Signal();
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return ValueTask.CompletedTask;
        }
    }
}
