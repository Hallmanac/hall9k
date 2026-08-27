namespace Hall9k.Tests.Fakes;

/// <summary>
/// A clock that advances by a fixed <paramref name="step"/> on every read, so a polling loop's
/// elapsed-time math is driven by how many times it asked for the time rather than by how much
/// real wall-clock time actually passed — immune to a slow or loaded test runner.
/// </summary>
public sealed class SteppingClock(TimeSpan step) : TimeProvider
{
    private DateTimeOffset now = DateTimeOffset.UtcNow;

    public override DateTimeOffset GetUtcNow()
    {
        DateTimeOffset current = now;
        now += step;
        return current;
    }
}
