using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Tasks.Events;

/// <summary>
/// A human override of this task's own review-cycle caps (task: the review cycle caps become
/// settable at three levels): each cap resolves task &gt; project &gt; node &gt; compiled default,
/// independently of the other three, so a task that sets only one still inherits the rest from
/// the levels above. Every field is <see cref="Optional{T}"/> of a nullable int, the same
/// present-with-null-clears idiom <see cref="TaskRevised.EpicId"/> already uses: absent leaves
/// that cap alone, present with a value overrides it, present with null clears the override back
/// to the project or node.
/// <para>
/// Deliberately state-agnostic, unlike <see cref="TaskRevised"/>: a review cap is not part of the
/// readiness contract a Published task promises will not change out from under a node or a
/// running agent — it is meant to be set "at any time, including while the task's run is live"
/// (the takeover lever for a task observed grinding), and the daemon resolves the effective caps
/// fresh at every cap check, so a change here never disturbs a session already spawned. Setting a
/// cap at or below the cycles that track has run since its last human takeover grant (0, if it has
/// never had one — not the same as the absolute review cycle number the CLI prints, which never
/// resets) is exactly how a human hands a stuck run back: the very next cap check parks it, with no
/// new state or command beyond this one; 0 always parks immediately, since that count can never be
/// negative.
/// </para>
/// </summary>
public sealed record TaskReviewCapsOverridden(
    Guid Id,
    Optional<int?> MaxComplianceReviewCycles,
    Optional<int?> MaxAdversarialReviewCycles,
    Optional<int?> MaxFinalFullPassRounds,
    Optional<int?> LifetimeReviewCycleBudget,
    DateTimeOffset OverriddenAt,
    Guid OverriddenByOwnerId);
