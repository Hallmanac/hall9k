namespace Hall9k.Domain.Features.Courier;

/// <summary>
/// One project's own courier spawn count for the current UTC day (idea 89471598, piece 3) — a
/// purely local, derived, mechanical-bookkeeping document, the identical shape
/// <c>OrchestratorFeedCursor</c> already takes for the same reason: nothing here could ever be
/// replayed from anywhere else, so an event stream would only be ceremony around a number. Keyed
/// by the project's own id, one row per project, reset the moment a tick observes the UTC date
/// has rolled over since <see cref="Day"/>.
/// </summary>
public sealed class CourierDaySpawnCounter
{
    public Guid Id { get; set; }
    public DateOnly Day { get; set; }
    public int Count { get; set; }

    /// <summary>
    /// Whether this day's cap-reached state has already been logged once, so a busy project
    /// pinned at the cap for hours does not repeat the same log line every sweep tick.
    /// </summary>
    public bool CapHitLogged { get; set; }

    /// <summary>
    /// This project's count for <paramref name="today"/>, rolling the counter over to zero first
    /// if the stored day has passed — read-only, for the gate's own "how many so far today"
    /// question, never advancing anything.
    /// </summary>
    public static int CountFor(CourierDaySpawnCounter? existing, DateOnly today) =>
        existing is null || existing.Day != today ? 0 : existing.Count;

    /// <summary>
    /// The counter after one more spawn is recorded today, rolling over first exactly like
    /// <see cref="CountFor"/> does — the two must agree, since the gate reads one and the spawn
    /// path writes the other against the identical "today" instant.
    /// </summary>
    public static CourierDaySpawnCounter Incremented(CourierDaySpawnCounter? existing, Guid projectId, DateOnly today) =>
        existing is null || existing.Day != today
            ? new CourierDaySpawnCounter { Id = projectId, Day = today, Count = 1, CapHitLogged = false }
            : new CourierDaySpawnCounter { Id = projectId, Day = today, Count = existing.Count + 1, CapHitLogged = existing.CapHitLogged };
}
