namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// One project's run of consecutive failed couriers, and the earliest moment the next one may
/// spawn. A purely local, derived document in the <see cref="CourierDaySpawnCounter"/> idiom, keyed
/// by the project's own id and deleted with it: it lives in the daemon's database rather than in
/// the daemon's memory so that a restart (<c>h9k update --restart</c> included) does not lose it,
/// and it carries every fact <c>h9k status</c> and <c>h9k doctor</c> print so the CLI never needs
/// the daemon's options to describe it.
/// <para>
/// <see cref="Count"/> zero is a legitimate state: a courier the daemon itself ended (stopped at
/// shutdown, or adopted as stranded after a crash) says nothing about whether delivery works, so it
/// neither counts nor resets, but it still holds the next spawn off for one base wait, and that
/// needs somewhere to live when no streak is running.
/// </para>
/// </summary>
public sealed class CourierFailureStreak
{
    /// <summary>The longest a stored failure reason runs, ellipsis included.</summary>
    public const int MaxReasonLength = 120;

    public const string NoReasonGiven = "no reason given";
    public const string EndedWithoutOutcomeLine = "ended without an outcome line";

    public Guid Id { get; set; }

    /// <summary>How many couriers in a row have failed, zero when none is running (see the type's own summary).</summary>
    public int Count { get; set; }

    /// <summary>When the first failure of this streak completed.</summary>
    public DateTimeOffset StartedAt { get; set; }

    /// <summary>When the most recent failure of this streak completed.</summary>
    public DateTimeOffset LastFailureAt { get; set; }

    /// <summary>Why the most recent failure failed, one line, already truncated for display.</summary>
    public string LastFailureReason { get; set; } = string.Empty;

    /// <summary>
    /// The highest feed sequence any courier of this streak carried. An urgent item above it has
    /// never been tried, which is what earns that item its one early attempt.
    /// </summary>
    public long HighestCarriedSequence { get; set; }

    /// <summary>The earliest time the next courier may spawn, the early attempt for an uncarried urgent item aside.</summary>
    public DateTimeOffset NextAttemptAt { get; set; }

    /// <summary>
    /// Whether a streak is running as far as <paramref name="launchedAt"/>, the launch time of the
    /// project's registered orchestrator window, can tell. A registration newer than the last
    /// failure resets the streak, because the delivery address has changed since; an unchanged
    /// same-process register appends no launch, so it never lands here as newer.
    /// </summary>
    public static bool IsRunning(CourierFailureStreak? streak, DateTimeOffset? launchedAt) =>
        streak is { Count: > 0 } && !WasResetByRegistration(streak, launchedAt);

    /// <summary>Whether the window registered after this streak's last failure, which resets it.</summary>
    public static bool WasResetByRegistration(CourierFailureStreak? streak, DateTimeOffset? launchedAt) =>
        streak is { Count: > 0 } && launchedAt is { } launched && launched > streak.LastFailureAt;

    /// <summary>The streak after one more failed courier completed at <paramref name="completedAt"/>.</summary>
    public static CourierFailureStreak AfterFailure(
        CourierFailureStreak? existing,
        Guid projectId,
        DateTimeOffset completedAt,
        string? reason,
        long carriedThroughSequence,
        CourierBackoffWaits waits)
    {
        int count = (existing?.Count ?? 0) + 1;
        return new CourierFailureStreak
        {
            Id = projectId,
            Count = count,
            StartedAt = existing is { Count: > 0 } running ? running.StartedAt : completedAt,
            LastFailureAt = completedAt,
            LastFailureReason = NormalizeReason(reason),
            HighestCarriedSequence = Math.Max(existing?.HighestCarriedSequence ?? 0, carriedThroughSequence),
            NextAttemptAt = completedAt + waits.Step(count),
        };
    }

    /// <summary>
    /// The streak after a courier the daemon itself ended: nothing but the next-attempt time
    /// changes, to the ending's own completion plus the current step (the base when no streak is
    /// running), so the gate never spawns at once after one.
    /// </summary>
    public static CourierFailureStreak AfterDaemonEnding(
        CourierFailureStreak? existing, Guid projectId, DateTimeOffset completedAt, CourierBackoffWaits waits)
    {
        int count = existing?.Count ?? 0;
        return new CourierFailureStreak
        {
            Id = projectId,
            Count = count,
            StartedAt = existing?.StartedAt ?? default,
            LastFailureAt = existing?.LastFailureAt ?? default,
            LastFailureReason = existing?.LastFailureReason ?? string.Empty,
            HighestCarriedSequence = existing?.HighestCarriedSequence ?? 0,
            NextAttemptAt = completedAt + waits.Step(count),
        };
    }

    /// <summary>
    /// The streak the first sweep tick after a daemon start leaves behind: the next attempt pulled
    /// in to no later than <paramref name="tickAt"/> plus the base wait, the count untouched. A
    /// restart that fixed the cause is confirmed within about a minute, and a crash loop still gets
    /// at most one courier per restart. The same instance when nothing needs pulling in.
    /// </summary>
    public static CourierFailureStreak PulledIn(CourierFailureStreak existing, DateTimeOffset tickAt, CourierBackoffWaits waits)
    {
        DateTimeOffset pulledTo = tickAt + waits.Base;
        return existing.NextAttemptAt <= pulledTo
            ? existing
            : new CourierFailureStreak
            {
                Id = existing.Id,
                Count = existing.Count,
                StartedAt = existing.StartedAt,
                LastFailureAt = existing.LastFailureAt,
                LastFailureReason = existing.LastFailureReason,
                HighestCarriedSequence = existing.HighestCarriedSequence,
                NextAttemptAt = pulledTo,
            };
    }

    /// <summary>Where a long reason is cut, one short of the limit to leave room for the ellipsis, and never between the halves of a surrogate pair.</summary>
    private static int CutLength(string collapsed) =>
        char.IsHighSurrogate(collapsed[MaxReasonLength - 2]) ? MaxReasonLength - 2 : MaxReasonLength - 1;

    /// <summary>
    /// A failure reason as it is stored and shown: newlines collapsed to single spaces, and cut to
    /// <see cref="MaxReasonLength"/> characters with an ellipsis when longer. A blank one reads as
    /// <see cref="NoReasonGiven"/>. The run's own <c>Outcome</c> is a separate field and is never
    /// touched by this.
    /// </summary>
    public static string NormalizeReason(string? reason)
    {
        string collapsed = string.Join(
            ' ', (reason ?? string.Empty).Split(['\r', '\n'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        return collapsed.Length switch
        {
            0 => NoReasonGiven,
            <= MaxReasonLength => collapsed,
            _ => string.Concat(collapsed.AsSpan(0, CutLength(collapsed)), "…"),
        };
    }
}
