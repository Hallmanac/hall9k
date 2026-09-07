namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A human reviewer opened their own review lap on this pr-review task (<c>h9k pr review</c>,
/// Decisions Log #149). Provenance rather than a state change: the task is already Claimed —
/// either by the machine review this lap is reading the report of, or by the lap's own
/// interactive claim moments earlier — and this records who opened the lap, which run it is
/// riding on, and where the read-only worktree they are reading in actually is.
/// <para>
/// <see cref="RunId"/> is the run the lap rides on, which is the run <c>h9k pr approve</c> /
/// <c>h9k pr request-changes</c> will append <c>PrReviewDelivered</c> to: the machine review's
/// own already-parked run when one exists, or the lap's own freshly-dispatched run when the
/// reviewer got here before any automated pass did.
/// </para>
/// <para>
/// <see cref="WorktreePath"/> is empty exactly when <c>--no-worktree</c> skipped the checkout
/// (the reviewer is testing against a deployed environment instead) — an honest absence, never
/// a path nothing was checked out at. That same emptiness is what tells the finalize step there
/// is no checkout of this lap's own to release.
/// </para>
/// <para>
/// It is also the discriminator the daemon's own startup adoption needs. A lap-dispatched run
/// carries the ceiling-exempt <see cref="Guid.Empty"/> node sentinel with no agent process of
/// its own ever recorded, so adoption's "dispatched but never started" arm would otherwise fail
/// a lap a reviewer is sitting in the middle of, purely because the daemon restarted
/// (<c>RunSupervisor.AdoptOrphansAsync</c>).
/// </para>
/// </summary>
public sealed record PullRequestReviewLapOpened(
    Guid Id,
    Guid RunId,
    string WorktreePath,
    string PullRequestUrl,
    DateTimeOffset OpenedAt,
    Guid OpenedByOwnerId);
