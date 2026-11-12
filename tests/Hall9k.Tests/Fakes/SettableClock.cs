namespace Hall9k.Tests.Fakes;

/// <summary>
/// A clock that stands still until the test moves it, so sweeps a settle period apart need no real wait. Its timestamp
/// counts awake time only: <see cref="Advance"/> moves both, <see cref="Suspend"/> moves the wall clock alone, the way a
/// node that slept sees it.
/// </summary>
public sealed class SettableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;
    private long _awakeTicks;

    public override DateTimeOffset GetUtcNow() => _now;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => _awakeTicks;

    public void Advance(TimeSpan by)
    {
        _now += by;
        _awakeTicks += by.Ticks;
    }

    public void Suspend(TimeSpan by) => _now += by;
}
