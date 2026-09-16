namespace Hall9k.Domain.Features.Message;

/// <summary>
/// This install's own permanent answer to "which eligible project is the legacy adopter"
/// (<c>Hall9k.Connectors.Messaging.LegacyMessageAdoption</c>'s own doc) — appended exactly once, the
/// first time <c>Hall9k.Daemon.Messaging.MessageSweepEngine</c> actually completes a flush with
/// adoption on, by whichever project that turns out to be. Before this event has ever been raised,
/// every reader falls back to the static "lowest eligible project id" guess; once raised, every
/// reader — <c>MessageOutbox.NextSeqAsync</c> at queue time and <c>MessageInbox.ReadFromAsync</c> at
/// read time alike — takes <see cref="ProjectId"/> as the answer instead, so a project the static
/// guess would have picked but whose trust chain read never actually succeeds can no longer disagree
/// with the project the sweep's own resilience fallback (independent pre-PR review, cycle 1, both
/// lenses) actually used.
/// </summary>
public sealed record LegacyMessageAdoptionAssigned(Guid ProjectId, DateTimeOffset At);
