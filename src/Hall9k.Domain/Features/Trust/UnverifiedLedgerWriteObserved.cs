namespace Hall9k.Domain.Features.Trust;

/// <summary>
/// One sweep's own sighting of a ledger write it could not verify (idea 202383dc, T1 criterion 3:
/// "an unverifiable writer's files and envelopes are ignored and the writer is named") — persisted
/// so <c>h9k status</c> can name the writer without ever walking the ledger itself
/// (<c>Hall9k.Daemon.Messaging.MessageSweepEngine</c> is the only writer of this event, off the
/// same <c>TrustChain.UnverifiedWrites</c> it already computes once per tick). <see cref="Kind"/>
/// and <see cref="Identifier"/> mirror <c>Hall9k.Connectors.Trust.UnverifiedLedgerWrite</c>
/// exactly: for a vouch or revocation, <see cref="Identifier"/> is the node id the write names
/// under <see cref="RootFingerprint"/>; for a membership write, <see cref="Identifier"/> is the
/// same fingerprint as <see cref="RootFingerprint"/> — there is no separate node id to name.
/// </summary>
public sealed record UnverifiedLedgerWriteObserved(
    Guid ProjectId, string Kind, string Identifier, string RootFingerprint, string Reason, DateTimeOffset At);
