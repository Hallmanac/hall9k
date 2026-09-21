using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Learning;

/// <summary>
/// The one read model the learning slice needs: <c>h9k learn list</c> filters it and
/// <c>h9k learn show</c> renders it. One row per lesson, carrying exactly what the list filters
/// on (<see cref="Scope"/>, <see cref="ScopeId"/>, <see cref="Status"/>,
/// <see cref="RecordedAt"/>) alongside what the detail view needs — the same single-document
/// shape <see cref="Decision.DecisionDetails"/> uses, and for the same reason.
/// </summary>
public sealed class LearningDetails
{
    public Guid Id { get; set; }
    public KnowledgeScope Scope { get; set; } = KnowledgeScope.Unknown;
    /// <summary>The project this lesson applies to, or the owner whose habit it is — read under <see cref="Scope"/>.</summary>
    public Guid ScopeId { get; set; }
    public string Statement { get; set; } = string.Empty;
    public RecordedProvenance? Provenance { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public LearningStatus Status { get; set; } = LearningStatus.Unknown;
    public string? RetireReason { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

public sealed class LearningDetailsProjection : SingleStreamProjection<LearningDetails, Guid>
{
    public LearningDetails Create(IEvent<LearningRecorded> @event) => new()
    {
        Id = @event.Data.Id,
        Scope = @event.Data.Scope,
        ScopeId = @event.Data.ScopeId,
        Statement = @event.Data.Statement,
        Provenance = @event.Data.Provenance,
        RecordedAt = @event.Data.RecordedAt,
        Status = LearningStatus.Active,
    };

    /// <summary>
    /// The out-of-order counterpart of <see cref="Create"/> — see
    /// <c>DecisionDetailsProjection.Apply(IEvent{DecisionRecorded}, DecisionDetails)</c>'s own doc
    /// for the shape this handles and why the ending wins whichever order the two events landed in.
    /// </summary>
    public void Apply(IEvent<LearningRecorded> @event, LearningDetails view)
    {
        view.Id = @event.Data.Id;
        view.Scope = @event.Data.Scope;
        view.ScopeId = @event.Data.ScopeId;
        view.Statement = @event.Data.Statement;
        view.Provenance = @event.Data.Provenance;
        view.RecordedAt = @event.Data.RecordedAt;
        view.Status = view.RetiredAt is null ? LearningStatus.Active : LearningStatus.Retired;
    }

    public void Apply(IEvent<LearningRetired> @event, LearningDetails view)
    {
        view.RetireReason = @event.Data.Reason;
        view.RetiredAt = @event.Data.RetiredAt;
        view.Status = LearningStatus.Retired;
    }
}
