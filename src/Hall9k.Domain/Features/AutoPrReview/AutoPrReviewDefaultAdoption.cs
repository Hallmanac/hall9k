namespace Hall9k.Domain.Features.AutoPrReview;

/// <summary>
/// The moment auto-pr-review's on-by-default behaviour first ran on this install (Decisions Log
/// #161) — written once, by the first daemon start that finds no row, and never recomputed.
/// Mutable telemetry, not an event (the <c>TaskLease</c>/<c>RunActivity</c>/
/// <c>CleanBaseGateVerdict</c> convention, Decisions Log #7): it is one recorded observation of
/// when this install crossed the flip, not a fact worth a stream of its own.
/// <para>
/// It exists for the no-backfill guard. A project registered before the flip has a registration
/// timestamp far older than the day this behaviour arrived, so registration alone would let
/// every review request that had been sitting open for weeks mint a task at once — which is
/// precisely what happened on the Windows node at 16:03 EDT on 2026-09-08, when opting
/// arx-platform in minted four tasks in one sweep, two of them for August requests (#1568,
/// #1556). <see cref="AdoptedAt"/> is the honest floor for those projects: this install had no
/// on-by-default behaviour before it, so nothing before it was ever a signal this install
/// declined to act on.
/// </para>
/// <para>
/// Keyed by node id, because a node is an install (PLAN.md §6.1, one machine = one node) and two
/// nodes can share one database (<c>RunSupervisor</c>'s own sentinel-run adoption already turns
/// on that being possible). Each install's own first run is therefore its own cutoff, rather
/// than one install's flip silently backdating another's. What keeps the two from minting
/// duplicates for one pull request is not this row and not
/// <see cref="ObservedReviewRequest"/> either (independent pre-PR review, cycle 1, adversarial
/// lens: an earlier version of this comment claimed it was): it is
/// <c>AutoPrReviewEngine.CreateOneAsync</c>'s own dedup on the task's canonical external
/// reference, which every task both installs write lands in.
/// </para>
/// </summary>
public sealed class AutoPrReviewDefaultAdoption
{
    /// <summary>The node this cutoff belongs to — a node is an install.</summary>
    public Guid Id { get; set; }

    /// <summary>When this install first ran the on-by-default behaviour. Never rewritten once recorded.</summary>
    public DateTimeOffset AdoptedAt { get; set; }
}

/// <summary>
/// The no-backfill guard itself (Decisions Log #161): which review requests are new enough for
/// this install to have been the one that let them through, and which are older than the
/// behaviour that would have acted on them.
/// </summary>
public static class AutoPrReviewCutoff
{
    /// <summary>
    /// The instant a project's own requests start counting: its registration, or this install's
    /// own adoption of the on-by-default behaviour, whichever is later. A project registered
    /// after the flip is bounded by its registration (the platform knew nothing of its
    /// repository before that); a project that predates the flip is bounded by the flip.
    /// </summary>
    public static DateTimeOffset For(DateTimeOffset registeredAt, DateTimeOffset adoptedAt) =>
        registeredAt > adoptedAt ? registeredAt : adoptedAt;

    /// <summary>
    /// Whether a request GitHub recorded at <paramref name="requestedAt"/> may mint and start a
    /// task on its own. A request whose own timestamp could not be read at all (null) never
    /// does: there is nothing to prove it postdates the cutoff, and the conservative answer is
    /// the one that leaves the operator a row to act on rather than the one that starts an
    /// agent session on an unobserved fact — the same discipline
    /// <c>AutoPrReviewEngine.IsGenuineReRequestAsync</c> already applies to a missing timestamp.
    /// <para>
    /// Inclusive of the cutoff instant itself: GitHub's own <c>createdAt</c> timestamps are
    /// second-resolution, and a request made in the same second a project was registered is a
    /// new request, not a stale one.
    /// </para>
    /// </summary>
    public static bool StartsOnItsOwn(DateTimeOffset? requestedAt, DateTimeOffset cutoff) =>
        requestedAt is { } observed && observed >= cutoff;
}
