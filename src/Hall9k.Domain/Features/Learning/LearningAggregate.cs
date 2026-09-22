using Hall9k.Domain.Shared.ValueObjects;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// One run-earned lesson, from the moment it was recorded to its retirement (idea d805fd8b,
/// piece 1; backlog 55). Small on purpose: the statement, where it applies, where it came from,
/// and its one ending.
/// </summary>
public sealed class LearningAggregate
{
    public Guid Id { get; private set; }
    public KnowledgeScope Scope { get; private set; } = KnowledgeScope.Unknown;
    /// <summary>The project this lesson applies to, or the owner whose habit it is — read under <see cref="Scope"/>.</summary>
    public Guid ScopeId { get; private set; }
    public string Statement { get; private set; } = string.Empty;

    /// <summary>The lessons this one was merged out of, or null when it was recorded on its own; see <see cref="LearningRecorded.DistilledFrom"/>.</summary>
    public IReadOnlyList<Guid>? DistilledFrom { get; private set; }

    public RecordedProvenance? Provenance { get; private set; }
    public DateTimeOffset RecordedAt { get; private set; }
    public LearningStatus Status { get; private set; } = LearningStatus.Unknown;
    public string? RetireReason { get; private set; }
    public DateTimeOffset? RetiredAt { get; private set; }

    public void Apply(LearningRecorded @event)
    {
        Id = @event.Id;
        Scope = @event.Scope;
        ScopeId = @event.ScopeId;
        Statement = @event.Statement;
        DistilledFrom = @event.DistilledFrom;
        Provenance = @event.Provenance;
        RecordedAt = @event.RecordedAt;
        Status = LearningStatus.Active;
    }

    public void Apply(LearningRetired @event)
    {
        RetireReason = @event.Reason;
        RetiredAt = @event.RetiredAt;
        Status = LearningStatus.Retired;
    }
}
