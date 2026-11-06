using Hall9k.Cli.Infrastructure;
using Hall9k.Domain.Features.Courier;

namespace Hall9k.Cli.Orchestrator;

/// <summary>
/// What one project's courier line reads from: the most recent run if it is still in flight, the
/// last delivery, the failure streak and day-cap documents, and the registered window's launch time
/// (a window launched after the one the last failed courier addressed has reset the streak).
/// </summary>
/// <param name="InFlight">The most recent courier run for this project, when it is still running (<c>CompletedAt</c> null), null otherwise.</param>
/// <param name="LastDeliveredAt">When a courier for this project last actually delivered, across every run ever recorded, null if none ever has.</param>
/// <param name="Streak">The project's failure streak document, if one exists.</param>
/// <param name="DayCounter">The project's day spawn counter document, if one exists.</param>
/// <param name="PresenceRegistered">Whether the project's orchestrator presence on this node is registered.</param>
/// <param name="LaunchedAt">When the registered window launched, or the last time it re-registered with a changed address.</param>
/// <param name="ElevationBlocked">Whether the live window is elevated and the running daemon is not (<see cref="CourierElevationCheck.Blocks"/>).</param>
public sealed record CourierStatusFacts(
    CourierRunDetails? InFlight,
    DateTimeOffset? LastDeliveredAt,
    CourierFailureStreak? Streak,
    CourierDaySpawnCounter? DayCounter,
    bool PresenceRegistered,
    DateTimeOffset? LaunchedAt,
    bool ElevationBlocked = false)
{
    /// <summary>The failure streak as far as this line is concerned: running, and the project's presence registered.</summary>
    public CourierFailureStreak? RunningStreak =>
        PresenceRegistered && CourierFailureStreak.IsRunning(Streak, LaunchedAt) ? Streak : null;
}

/// <summary>
/// The feed courier's own line on <c>h9k status</c> (idea 89471598, piece 3): whether one is in
/// flight right now for this project, when one last actually delivered, and whether couriers keep
/// failing or the day cap has stopped them. Null when this project has never had a courier run at
/// all and nothing is wrong, the same "a quiet pane says nothing" posture
/// <see cref="OrchestratorPresenceLine"/> holds for a project that has never registered a window.
/// <para>
/// One branch per state, in precedence order: in flight, then blocked by elevation, then day cap
/// spent, then failing. The
/// winner appends the failed count when a streak is also running, so a state hidden by a
/// higher-precedence one is still counted. A further state is one more branch in
/// <see cref="Describe"/>.
/// </para>
/// </summary>
public static class CourierStatusLine
{
    public static string? Describe(CourierStatusFacts facts, DateTimeOffset now)
    {
        CourierFailureStreak? failing = facts.RunningStreak;

        // One branch per state that can win, in precedence order; failing is the last of them and
        // is the only one whose line carries no delivery text.
        string? state = facts.InFlight is { } running
            ? $"in flight (dispatched {DurationFormat.Short(Elapsed(running.DispatchedAt, now))} ago)"
            : facts.ElevationBlocked
                ? CourierElevationFix.BlockedState
                : DayCapSpent(facts, now)
                    ? DayCapText(now)
                    : null;

        if (state is null)
        {
            return failing is not null
                ? $"courier: {FailingText(failing, now)}"
                : facts.LastDeliveredAt is null
                    ? null
                    : $"courier: idle; {DeliveredText(facts.LastDeliveredAt, now)}";
        }

        // A state that outranks a running streak still counts it.
        string streakCount = failing is null ? string.Empty : $", {failing.Count} failed in a row";
        return $"courier: {state}{streakCount}; {DeliveredText(facts.LastDeliveredAt, now)}";
    }

    /// <summary>Where the day rolls, in local time, with the UTC midnight it really is named beside it.</summary>
    public static string DayCapText(DateTimeOffset now) =>
        $"day cap spent, rolls over at {CourierDaySpawnCounter.RollsOverAt(DateOnly.FromDateTime(now.UtcDateTime)).ToLocalTime():HH:mm} (00:00 UTC)";

    private static bool DayCapSpent(CourierStatusFacts facts, DateTimeOffset now) =>
        CourierDaySpawnCounter.CapHeldBackOn(facts.DayCounter, DateOnly.FromDateTime(now.UtcDateTime));

    /// <summary>The failing state: the count, when it began, when the next try is, and the last reason.</summary>
    public static string FailingText(CourierFailureStreak streak, DateTimeOffset now) =>
        $"failing, {streak.Count} in a row since {Since(streak.StartedAt, now)}, "
        + $"{NextTry(streak.NextAttemptAt, now)}, last: {Reason(streak)}";

    /// <summary>The streak's last failure reason made safe to print on one line.</summary>
    public static string Reason(CourierFailureStreak streak) =>
        streak.LastFailureReason.IsBlank()
            ? CourierFailureStreak.NoReasonGiven
            : ExternalText.OneLine(streak.LastFailureReason);

    /// <summary>
    /// The clock time alone when the streak began today (local time), with the date when it began
    /// on an earlier day.
    /// </summary>
    private static string Since(DateTimeOffset startedAt, DateTimeOffset now)
    {
        DateTimeOffset local = startedAt.ToLocalTime();
        return local.Date == now.ToLocalTime().Date ? $"{local:HH:mm}" : $"{local:yyyy-MM-dd HH:mm}";
    }

    /// <summary>
    /// Never a zero or negative duration: once the next-attempt time has passed the next sweep tick
    /// is what spawns, so that is what the line says.
    /// </summary>
    private static string NextTry(DateTimeOffset nextAttemptAt, DateTimeOffset now)
    {
        TimeSpan remaining = nextAttemptAt - now;
        return remaining <= TimeSpan.Zero ? "next try at the next sweep" : $"next try in {Countdown(remaining)}";
    }

    /// <summary>Whole seconds under a minute and whole minutes above it, both rounded up so a pending wait never reads as zero.</summary>
    private static string Countdown(TimeSpan remaining)
    {
        int seconds = (int)Math.Ceiling(remaining.TotalSeconds);
        if (seconds < 60)
        {
            return $"{seconds}s";
        }

        int minutes = (seconds + 59) / 60;
        return minutes < 60 ? $"{minutes}m" : $"{minutes / 60}h{minutes % 60:00}m";
    }

    private static string DeliveredText(DateTimeOffset? lastDeliveredAt, DateTimeOffset now) =>
        lastDeliveredAt is { } deliveredAt
            ? $"last delivered {deliveredAt.ToLocalTime():yyyy-MM-dd HH:mm} ({DurationFormat.Short(Elapsed(deliveredAt, now))} ago)"
            : "never delivered";

    /// <summary>Clamped at zero — the identical reason <see cref="OrchestratorPresenceLine"/>'s own copy of this clamp exists.</summary>
    private static TimeSpan Elapsed(DateTimeOffset since, DateTimeOffset now) =>
        now > since ? now - since : TimeSpan.Zero;
}
