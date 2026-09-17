namespace Hall9k.Domain.Features.Project.Events;

/// <summary>
/// Records this install's own local Project stream with the project's own wire key (idea 202383dc,
/// M2, Brian's ruling 2026-09-17): the 26-character ULID <c>h9k project join</c> reads back from
/// the ledger's own genesis commit (minted fresh there, or backfilled once by <c>h9k project
/// assign-key</c>) — never travels, node-scoped, a purely local mirror of a fact the ledger itself
/// already owns. <see cref="ProjectKey"/> is never reassigned once recorded here: the ledger's own
/// key is immutable the moment genesis (or the one-time backfill) writes it, so every later join
/// simply confirms the identical value.
/// </summary>
public sealed record ProjectKeyAssigned(Guid Id, string ProjectKey, DateTimeOffset AssignedAt);
