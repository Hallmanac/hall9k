using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Run.Events;

/// <summary>
/// What this pr-review run resolved the assignee's declared personas into, recorded at dispatch
/// before a single review session spawns (idea b9b09779, piece 1). The run's own record of what it
/// set out to do, so <c>h9k task show</c> and the findings report both read what actually
/// happened rather than re-deriving it from a registry that may have gained a persona since.
/// </summary>
/// <param name="Requested">Every persona the assignee is reviewed through, in the fixed order. Never empty: declaring none reads as the engineer.</param>
/// <param name="Ran">Those of <paramref name="Requested"/> the registry had a prompt for, and which this run therefore dispatched sessions for.</param>
/// <param name="Skipped">Those it had no prompt for. Named here, and named in the report, rather than silently ignored.</param>
/// <param name="FellBackToEngineer">
/// True when nothing the assignee declared could be run and the engineer's review stood in, so the
/// pull request was not left unreviewed. The one case where a review runs that nobody declared, so
/// it is recorded rather than inferred from <paramref name="Ran"/> disagreeing with
/// <paramref name="Requested"/>.
/// </param>
/// <param name="DriveDecisions">
/// For each persona whose review can stand the product up (idea b9b09779, piece 3 — the
/// designer today, QA once piece 2 lands), whether this run's own dispatch decided it would:
/// the project's drive setting as it stood at dispatch, and whether there was a run skill on the
/// ledger to drive with. Recorded here rather than re-resolved when the report is composed,
/// because both inputs can move while a review is in flight and the report must say what
/// happened, not what would be decided now. Empty for a run whose stream predates this, and for
/// every run whose personas cannot drive at all.
/// </param>
public sealed record PrReviewPersonasSelected(
    Guid Id,
    IReadOnlyList<ReviewPersona> Requested,
    IReadOnlyList<ReviewPersona> Ran,
    IReadOnlyList<ReviewPersona> Skipped,
    bool FellBackToEngineer,
    DateTimeOffset SelectedAt,
    IReadOnlyList<ReviewDriveDecision>? DriveDecisions = null);
