using Hall9k.Domain.Features.Project;

namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// The two numbers a failure streak's backoff is built from: <see cref="Base"/>, the wait after
/// the first failed courier, and <see cref="Ceiling"/>, the longest the wait ever grows to.
/// </summary>
public readonly record struct CourierBackoffWaits(TimeSpan Base, TimeSpan Ceiling)
{
    /// <summary>
    /// The shortest base wait there is, whatever the project's own courier max wait says: a
    /// project that sets <c>--courier-max-wait 0</c> wants its batching wait gone, not a failing
    /// courier respawned on every ten-second sweep tick.
    /// </summary>
    public static readonly TimeSpan MinimumBase = TimeSpan.FromSeconds(60);

    /// <summary>
    /// The base wait for a project: its courier max wait, but never less than
    /// <see cref="MinimumBase"/>.
    /// </summary>
    public static TimeSpan BaseFor(TimeSpan courierMaxWait) =>
        courierMaxWait > MinimumBase ? courierMaxWait : MinimumBase;

    /// <summary>
    /// The waits for one project. The ceiling is the daemon's configured one but never less than
    /// the base, so a ceiling set below a project's long max wait cannot make the backoff shrink.
    /// </summary>
    public static CourierBackoffWaits For(int? courierMaxWaitSeconds, TimeSpan configuredCeiling)
    {
        TimeSpan baseWait = BaseFor(TimeSpan.FromSeconds(courierMaxWaitSeconds ?? ProjectAggregate.DefaultCourierMaxWaitSeconds));
        return new CourierBackoffWaits(baseWait, configuredCeiling > baseWait ? configuredCeiling : baseWait);
    }

    /// <summary>
    /// The wait after <paramref name="consecutiveFailures"/> failed couriers in a row:
    /// <c>min(Base x 2^(n-1), Ceiling)</c>, and <see cref="Base"/> itself when there are none.
    /// </summary>
    public TimeSpan Step(int consecutiveFailures)
    {
        if (consecutiveFailures <= 1)
        {
            return Base;
        }

        // Computed in doubles and compared against the ceiling before it is ever turned back into
        // a TimeSpan, so a streak of hundreds of failures cannot overflow the tick count.
        double doubled = Base.Ticks * Math.Pow(2, Math.Min(consecutiveFailures - 1, 60));
        return doubled >= Ceiling.Ticks ? Ceiling : TimeSpan.FromTicks((long)doubled);
    }
}
