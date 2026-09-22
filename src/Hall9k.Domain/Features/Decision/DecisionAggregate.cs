using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// One binding decision, from the moment it was recorded to whatever superseded it (idea
/// d805fd8b, piece 1). Small on purpose: the statement, where it applies, where it came from,
/// and its one ending. The rendered markdown PLAN.md §16 used to be is a projection of these
/// streams, not a source anyone edits.
/// </summary>
public sealed class DecisionAggregate
{
    private readonly List<Guid> supersedes = [];

    public Guid Id { get; private set; }
    public KnowledgeScope Scope { get; private set; } = KnowledgeScope.Unknown;
    /// <summary>The project this decision binds, or the owner whose habit it is — read under <see cref="Scope"/>.</summary>
    public Guid ScopeId { get; private set; }
    public string Statement { get; private set; } = string.Empty;
    /// <summary>The concrete failure that produced this rule, or null when there was none to record.</summary>
    public string? OriginIncident { get; private set; }
    /// <summary>What this decision replaced, recorded at birth.</summary>
    public IReadOnlyList<Guid> Supersedes => supersedes;
    /// <summary>The citation this decision answered to before this store existed, or null when it was recorded natively (idea d805fd8b, piece 3).</summary>
    public string? LegacyId { get; private set; }
    public RecordedProvenance? Provenance { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public DecisionStatus Status { get; private set; } = DecisionStatus.Unknown;
    /// <summary>What superseded this decision, or null when nothing did (or nothing was recorded that had).</summary>
    public Guid? SupersededByDecisionId { get; private set; }
    public string? SupersedeReason { get; private set; }
    public DateTimeOffset? SupersededAt { get; private set; }

    public void Apply(DecisionRecorded @event)
    {
        Id = @event.Id;
        Scope = @event.Scope;
        ScopeId = @event.ScopeId;
        Statement = @event.Statement;
        OriginIncident = @event.OriginIncident;
        supersedes.Clear();
        supersedes.AddRange(@event.Supersedes);
        LegacyId = @event.LegacyId;
        Provenance = @event.Provenance;
        RecordedAt = @event.RecordedAt;
        Status = DecisionStatus.Recorded;
    }

    public void Apply(DecisionSuperseded @event)
    {
        SupersededByDecisionId = @event.SupersededByDecisionId;
        SupersedeReason = @event.Reason;
        SupersededAt = @event.SupersededAt;
        Status = DecisionStatus.Superseded;
    }
}
