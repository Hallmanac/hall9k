namespace Hall9k.Domain.Features.PrReviewPreflight;

/// <summary>
/// A pre-flight still genuinely in flight (<see cref="PrReviewPreflightDetails.CompletedAt"/> and
/// <see cref="PrReviewPreflightDetails.AbandonedAt"/> both null) for the exact head oid a fresh
/// claim was just dispatched against, whose recorded <see cref="PrReviewPreflightDetails.DispatchingRunId"/>
/// names an earlier, now-superseded claim (independent pre-PR review, cycle 7, conformance lens,
/// RunLauncher.cs:1935): a long daemon restart can expire a claim's lease while its detached
/// pre-flight process keeps running, and the next claim reclaims the task under a fresh run id
/// with nothing of its own to reach a verdict — the running pre-flight is the only thing that
/// ever will. Appended by <c>RunLauncher.EnsurePrReviewPreflightSafeAsync</c>'s own in-flight
/// branch the moment it finds this mismatch, so <see cref="RunSupervisor.CompletePreflightAsync"/>
/// and <see cref="RunSupervisor.AbandonPreflightAsync"/> — both guarded on the task's live claim
/// still naming <see cref="PrReviewPreflightDetails.DispatchingRunId"/> — see the reclaiming run
/// as the one they were dispatched for once this pre-flight finally reaches a verdict, instead of
/// finding a stale mismatch and leaving the reclaimed task <c>Claimed</c> with nothing left running
/// to release it.
/// </summary>
public sealed record PrReviewPreflightReclaimed(Guid Id, Guid DispatchingRunId, DateTimeOffset ReclaimedAt);
