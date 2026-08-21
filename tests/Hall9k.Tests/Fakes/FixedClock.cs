namespace Hall9k.Tests.Fakes;

/// <summary>A clock stopped at one instant, so an "observed at" assertion can be exact.</summary>
public sealed class FixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
