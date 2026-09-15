using JasperFx.Events;
using Marten.Events.Aggregation;

namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// <c>h9k status</c>'s own read view of one project's unverifiable ledger writers (idea 202383dc,
/// T1 criterion 3) — written only by <c>Hall9k.Daemon.Messaging.MessageSweepEngine</c>, off the
/// same <c>TrustChain.UnverifiedWrites</c> its own message sweep already computes once per tick,
/// never by a live ledger walk from <c>h9k status</c> itself.
/// </summary>
public sealed class UnverifiedLedgerWriteDetails
{
    public Guid Id { get; set; }
    public Guid ProjectId { get; set; }
    public string Kind { get; set; } = string.Empty;
    public string Identifier { get; set; } = string.Empty;
    public string RootFingerprint { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; set; }
    public DateTimeOffset LastSeenAt { get; set; }
}

public sealed class UnverifiedLedgerWriteDetailsProjection : SingleStreamProjection<UnverifiedLedgerWriteDetails, Guid>
{
    public UnverifiedLedgerWriteDetails Create(IEvent<UnverifiedLedgerWriteObserved> @event) => new()
    {
        Id = @event.StreamId,
        ProjectId = @event.Data.ProjectId,
        Kind = @event.Data.Kind,
        Identifier = @event.Data.Identifier,
        RootFingerprint = @event.Data.RootFingerprint,
        Reason = @event.Data.Reason,
        FirstSeenAt = @event.Data.At,
        LastSeenAt = @event.Data.At,
    };

    public void Apply(IEvent<UnverifiedLedgerWriteObserved> @event, UnverifiedLedgerWriteDetails view)
    {
        view.Reason = @event.Data.Reason;
        view.LastSeenAt = @event.Data.At;
    }
}
