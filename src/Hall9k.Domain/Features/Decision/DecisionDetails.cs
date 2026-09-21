using Hall9k.Domain.Shared.ValueObjects;
using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Decision;

/// <summary>
/// The one read model the decision slice needs: <c>h9k decide list</c> filters it and
/// <c>h9k decide show</c> renders it. One row per decision, carrying exactly what the list
/// filters on (<see cref="Scope"/>, <see cref="ScopeId"/>, <see cref="Status"/>,
/// <see cref="RecordedAt"/>) alongside what the detail view needs, because decisions are few and
/// small — the same reasoning that keeps <c>IdeaDetails</c> a single document rather than a lean
/// row plus a detail document the way the Task slice's volume made worth buying.
/// <para>
/// Both directions of supersession live here as plain fields: <see cref="Supersedes"/> comes off
/// this decision's own <see cref="DecisionRecorded"/>, and <see cref="SupersededByDecisionId"/>
/// off the <see cref="DecisionSuperseded"/> that landed on this stream. So <c>show</c> reads two
/// documents by id rather than scanning every other decision's own list for a back-reference.
/// </para>
/// </summary>
public sealed class DecisionDetails
{
    public Guid Id { get; set; }
    public KnowledgeScope Scope { get; set; } = KnowledgeScope.Unknown;
    /// <summary>The project this decision binds, or the owner whose habit it is — read under <see cref="Scope"/>.</summary>
    public Guid ScopeId { get; set; }
    public string Statement { get; set; } = string.Empty;
    /// <summary>The concrete failure that produced this rule, or null when there was none to record.</summary>
    public string? OriginIncident { get; set; }
    /// <summary>What this decision replaced, recorded at birth.</summary>
    public List<Guid> Supersedes { get; set; } = [];
    public RecordedProvenance? Provenance { get; set; }
    public DateTimeOffset RecordedAt { get; set; }
    public DecisionStatus Status { get; set; } = DecisionStatus.Unknown;
    /// <summary>What superseded this decision, or null when nothing did (or nothing was recorded that had).</summary>
    public Guid? SupersededByDecisionId { get; set; }
    public string? SupersedeReason { get; set; }
    public DateTimeOffset? SupersededAt { get; set; }
}

public sealed class DecisionDetailsProjection : SingleStreamProjection<DecisionDetails, Guid>
{
    public DecisionDetails Create(IEvent<DecisionRecorded> @event) => new()
    {
        Id = @event.Data.Id,
        Scope = @event.Data.Scope,
        ScopeId = @event.Data.ScopeId,
        Statement = @event.Data.Statement,
        OriginIncident = @event.Data.OriginIncident,
        Supersedes = [.. @event.Data.Supersedes],
        Provenance = @event.Data.Provenance,
        RecordedAt = @event.Data.RecordedAt,
        Status = DecisionStatus.Recorded,
    };

    /// <summary>
    /// Only reached when a document for this stream already exists by the time the recording event
    /// is processed — never true in causal order, since <see cref="Create"/> claims it first, but
    /// reachable on a replicated stream whose supersession arrived before its own genesis did
    /// (the same out-of-order shape <c>IdeaDetailsProjection.Apply(IdeaCaptured)</c> already
    /// handles). Repopulates every field the recording is authoritative for and leaves
    /// <see cref="DecisionDetails.Status"/> alone when the supersession already moved it, because
    /// the ending is the later fact whichever order the two events landed in.
    /// </summary>
    public void Apply(IEvent<DecisionRecorded> @event, DecisionDetails view)
    {
        view.Id = @event.Data.Id;
        view.Scope = @event.Data.Scope;
        view.ScopeId = @event.Data.ScopeId;
        view.Statement = @event.Data.Statement;
        view.OriginIncident = @event.Data.OriginIncident;
        view.Supersedes = [.. @event.Data.Supersedes];
        view.Provenance = @event.Data.Provenance;
        view.RecordedAt = @event.Data.RecordedAt;
        view.Status = view.SupersededAt is null ? DecisionStatus.Recorded : DecisionStatus.Superseded;
    }

    public void Apply(IEvent<DecisionSuperseded> @event, DecisionDetails view)
    {
        view.SupersededByDecisionId = @event.Data.SupersededByDecisionId;
        view.SupersedeReason = @event.Data.Reason;
        view.SupersededAt = @event.Data.SupersededAt;
        view.Status = DecisionStatus.Superseded;
    }
}
