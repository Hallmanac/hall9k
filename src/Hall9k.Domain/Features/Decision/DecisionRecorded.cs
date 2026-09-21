using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// A binding decision, recorded (idea d805fd8b, piece 1). The genesis event of a Decision
/// stream, one stream per decision, so the id is stable and citable forever: it is the citation
/// key PLAN.md §16's sequential numbers were never able to be, because a number that has to be
/// assigned at merge time forces a renumberer, a placeholder, and a tail conflict on every
/// stacked replay.
/// <para>
/// <see cref="OriginIncident"/> is AGENTS.md's own standing rule applied to the store: a rule
/// carries the concrete failure that created it, so a future reader knows why it exists and when
/// it might not apply. Null is the honest value for a decision that came out of a design walk
/// rather than a scar, never an invented incident.
/// </para>
/// <para>
/// <see cref="Supersedes"/> names what this decision replaces, recorded at birth. The other half
/// of the same fact, <see cref="DecisionSuperseded"/>, lands on each named decision's own stream
/// in the same transaction, so both directions are on the streams and neither is derived by
/// scanning the other's documents.
/// </para>
/// </summary>
public sealed record DecisionRecorded(
    Guid Id,
    KnowledgeScope Scope,
    Guid ScopeId,
    string Statement,
    string? OriginIncident,
    IReadOnlyList<Guid> Supersedes,
    RecordedProvenance Provenance,
    DateTimeOffset RecordedAt);
