namespace Hall9k.Tests.Fakes;

/// <summary>A clock that stands still until the test moves it, so sweeps a settle period apart need no real wait.</summary>
public sealed class SettableClock(DateTimeOffset start) : TimeProvider
{
    private DateTimeOffset _now = start;

    public override DateTimeOffset GetUtcNow() => _now;

    public void Advance(TimeSpan by) => _now += by;
}
