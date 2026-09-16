namespace Hall9k.Domain.Features.Trust;

/// <summary>One project's own record of one unverifiable ledger writer, keyed by
/// <see cref="UnverifiedLedgerWriteStreamId.For"/> — folds every sighting of the identical writer
/// into a single standing fact rather than one row per sweep tick that observed it.</summary>
public sealed class UnverifiedLedgerWriteAggregate
{
    public Guid Id { get; private set; }
    public Guid ProjectId { get; private set; }
    public string Kind { get; private set; } = string.Empty;
    public string Identifier { get; private set; } = string.Empty;
    public string RootFingerprint { get; private set; } = string.Empty;
    public string Reason { get; private set; } = string.Empty;
    public DateTimeOffset FirstSeenAt { get; private set; }
    public DateTimeOffset LastSeenAt { get; private set; }
    public bool Resolved { get; private set; }

    public void Apply(UnverifiedLedgerWriteObserved @event)
    {
        Id = UnverifiedLedgerWriteStreamId.For(@event.ProjectId, @event.Kind, @event.Identifier, @event.RootFingerprint);
        ProjectId = @event.ProjectId;
        Kind = @event.Kind;
        Identifier = @event.Identifier;
        RootFingerprint = @event.RootFingerprint;
        Reason = @event.Reason;
        if (FirstSeenAt == default)
        {
            FirstSeenAt = @event.At;
        }

        LastSeenAt = @event.At;
        Resolved = false;
    }

    public void Apply(UnverifiedLedgerWriteResolved @event)
    {
        Resolved = true;
    }
}
