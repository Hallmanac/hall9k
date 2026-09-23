using Hall9k.Domain.Infrastructure.Persistence;
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
/// <para>
/// It is also what the prompt feed reads (<see cref="Queries.LessonPromptFeed"/>, idea d805fd8b
/// piece 5), which is why <see cref="RecordedOnNodeId"/> lives here rather than being resolved
/// per caller: composing a dispatched session's lesson section has to be one indexed query over
/// these rows, not a stream replay per lesson.
/// </para>
/// </summary>
public sealed class LearningDetails
{
    public Guid Id { get; set; }
    public KnowledgeScope Scope { get; set; } = KnowledgeScope.Unknown;
    /// <summary>The project this lesson applies to, or the owner whose habit it is — read under <see cref="Scope"/>.</summary>
    public Guid ScopeId { get; set; }
    public string Statement { get; set; } = string.Empty;

    /// <summary>
    /// The lessons this one was merged out of, when it was recorded as a distillation
    /// (<see cref="LearningDecider.RecordDistilled"/>), and null for every lesson recorded on its
    /// own. Null rather than an empty list for the ordinary case on purpose: a distilled lesson
    /// with no citations is a state the decider refuses outright, so nothing downstream should
    /// have to read an empty list as if it meant something.
    /// </summary>
    public IReadOnlyList<Guid>? DistilledFrom { get; set; }

    public RecordedProvenance? Provenance { get; set; }
    public DateTimeOffset RecordedAt { get; set; }

    /// <summary>
    /// The node whose install actually appended this lesson, or null when the event carried no
    /// readable one (idea d805fd8b, piece 5). Deliberately not a field on
    /// <see cref="RecordedProvenance"/>, which carries only what its caller observed and leaves
    /// the node to <c>EventOriginStampingListener</c>'s event metadata: this property is that
    /// metadata projected onto the row, so a reader can tell this node's own agent from another
    /// node's without replaying every lesson's stream. Read through
    /// <see cref="EventRecordingNode"/>, so a replicated lesson answers with the node it came
    /// FROM rather than the node that applied it here.
    /// <para>
    /// This projection is Inline, which is why the node has to be on the event before the save
    /// reaches any session listener: the append site stamps it
    /// (<see cref="EventRecordingNode.StampAtAppend"/>, whose own doc carries the ordering and the
    /// review that found it), and <c>EventOriginStampingListener</c>'s later pass stamps the same
    /// value for the persisted metadata. Nothing here reads the listener's stamp directly, and a
    /// reader that assumes it could would project a null node onto every lesson this node records
    /// itself.
    /// </para>
    /// <para>
    /// Null on a row this projection materialised before this property existed. Honest while it
    /// lasts — such a lesson reads as recorded on a node nobody observed, and one recorded from a
    /// run therefore stops short of a prompt rather than being claimed as local — but not a state
    /// to leave standing: the projection is Inline and a lesson's only later event is its
    /// retirement, which never touches this field, so the row would never self-heal, and the
    /// event's own metadata says the node plainly. <see cref="LearningDetailsProjectionBackfill"/>
    /// replays those streams at daemon start, the same repair ideas and tasks already get.
    /// A lesson naming no run is unaffected either way, because that mark is decided by the
    /// absent run rather than by the node.
    /// </para>
    /// </summary>
    public Guid? RecordedOnNodeId { get; set; }

    public LearningStatus Status { get; set; } = LearningStatus.Unknown;
    public string? RetireReason { get; set; }
    public DateTimeOffset? RetiredAt { get; set; }
}

public sealed partial class LearningDetailsProjection : SingleStreamProjection<LearningDetails, Guid>
{
    public LearningDetails Create(IEvent<LearningRecorded> @event) => new()
    {
        Id = @event.Data.Id,
        Scope = @event.Data.Scope,
        ScopeId = @event.Data.ScopeId,
        Statement = @event.Data.Statement,
        DistilledFrom = @event.Data.DistilledFrom,
        Provenance = @event.Data.Provenance,
        RecordedAt = @event.Data.RecordedAt,
        RecordedOnNodeId = EventRecordingNode.Of(@event),
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
        view.DistilledFrom = @event.Data.DistilledFrom;
        view.Provenance = @event.Data.Provenance;
        view.RecordedAt = @event.Data.RecordedAt;
        view.RecordedOnNodeId = EventRecordingNode.Of(@event);
        view.Status = view.RetiredAt is null ? LearningStatus.Active : LearningStatus.Retired;
    }

    public void Apply(IEvent<LearningRetired> @event, LearningDetails view)
    {
        view.RetireReason = @event.Data.Reason;
        view.RetiredAt = @event.Data.RetiredAt;
        view.Status = LearningStatus.Retired;
    }
}
