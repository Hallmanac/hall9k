namespace Hall9k.Domain.Features.Orchestrator;

/// <summary>
/// A project's own short courtesy lease over its feed cursor (idea 89471598, piece 3), held for
/// the few seconds <c>h9k orchestrator feed --drain</c> spends reading, printing, and advancing
/// the cursor by hand. The feed courier's own spawn gate treats a currently-held lease exactly
/// like a courier already running for that project — never spawning into the same moment a human
/// is reading the identical items — so the two never interleave a print with a drain that moves
/// the cursor out from under it. Advisory rather than a true mutex, the identical shape
/// <c>OrchestratorFeedCursor</c> already takes for the same reason: nothing here needs to survive
/// a crash, since a lease nobody ever releases simply expires on its own <see cref="HeldUntil"/>.
/// </summary>
public sealed class OrchestratorFeedDrainLease
{
    public Guid Id { get; set; }
    public DateTimeOffset HeldUntil { get; set; }

    /// <summary>
    /// How long <c>h9k orchestrator feed --drain</c> holds its own lease — generous next to how
    /// long that command actually takes (a handful of local queries and a print), short enough
    /// that a courier is never held off for long by a stale lease a crashed or Ctrl-C'd CLI
    /// invocation never released.
    /// </summary>
    public static readonly TimeSpan Duration = TimeSpan.FromSeconds(30);

    public static OrchestratorFeedDrainLease Held(Guid projectId, DateTimeOffset now) =>
        new() { Id = projectId, HeldUntil = now + Duration };

    /// <summary>Whether a manual drain is holding this project's lease right now — the feed courier's own gate reads this before every spawn.</summary>
    public static bool IsHeld(OrchestratorFeedDrainLease? lease, DateTimeOffset now) =>
        lease is not null && lease.HeldUntil > now;
}
